module Client.Tests.WorkQueueCanonicalCasBinderTests

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open NUnit.Framework
open Jarvis.WorkQueues
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.WorkQueueGitLedger
open Client.WorkQueueCanonicalSourceEvidence
open Client.WorkQueueCanonicalCasBinder
open Client.WorkQueueRoleAuthority

[<TestFixture>]
type WorkQueueCanonicalCasBinderTests() =
    let git directory args =
        use runner = new Process()
        runner.StartInfo.FileName <- "git"
        runner.StartInfo.WorkingDirectory <- directory
        runner.StartInfo.RedirectStandardOutput <- true
        runner.StartInfo.RedirectStandardError <- true
        args |> List.iter runner.StartInfo.ArgumentList.Add
        if not (runner.Start()) then failwith "Git fixture did not start."
        let output = runner.StandardOutput.ReadToEnd()
        let stderr = runner.StandardError.ReadToEnd()
        if not (runner.WaitForExit(20000)) then
            runner.Kill(true)
            failwith "Git fixture timed out."
        if runner.ExitCode <> 0 then failwithf "Git fixture command failed: %s" stderr
        output.Trim()

    let ok = function
        | Ok value -> value
        | Error error -> failwithf "Unexpected fixture error: %A" error

    let denied = function
        | Error error ->
            Assert.That(string error, Does.Contain("IncompleteCoverage / RecoveryBlocked"))
        | Ok _ -> Assert.Fail("An inert WQ04 binder published a Git operation.")

    let gaps assessment =
        match assessment with
        | RecoveryBlocked reasons -> reasons

    let hasSourceGap issues =
        issues |> List.exists (function SourceEffectsUncovered _ -> true | _ -> false)
    let hasRoleGap issues =
        issues |> List.exists (function RoleAdmissionUncovered _ -> true | _ -> false)
    let hasRoleEvidenceRejection issues =
        issues |> List.exists (function RoleEvidenceRejected _ -> true | _ -> false)
    let hasEventRejection issues =
        issues |> List.exists (function CanonicalEventRejected _ -> true | _ -> false)
    let hasStaleHead issues =
        issues |> List.exists (function CanonicalHeadChanged _ -> true | _ -> false)
    let hasCasGap issues =
        issues |> List.exists (function SameCasProducerUnproved _ -> true | _ -> false)

    let withClones work =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-wq04-binder-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try
            let bare = Path.Combine(root, "remote.git")
            let first = Path.Combine(root, "first")
            let second = Path.Combine(root, "second")
            git root ["init"; "--bare"; "--initial-branch=main"; bare] |> ignore
            git root ["clone"; bare; first] |> ignore
            git first ["config"; "user.name"; "Fixture"] |> ignore
            git first ["config"; "user.email"; "fixture@example.test"] |> ignore
            File.WriteAllText(Path.Combine(first, "source.txt"), "pinned")
            git first ["add"; "source.txt"] |> ignore
            git first ["commit"; "-m"; "source fixture"] |> ignore
            git first ["push"; "-u"; "origin"; "HEAD"] |> ignore
            git root ["clone"; bare; second] |> ignore
            let projectId, ledgerId = Guid.NewGuid(), Guid.NewGuid()
            let binding path =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = bare; ApprovedPushUrl = bare
                  ProjectId = projectId; LedgerId = ledgerId; Epoch = 1 }
            let a, b = binding first, binding second
            LedgerStore.initialize a true |> ok |> ignore
            ReviewWorkflow.registerAgent a (Guid.NewGuid()) "Lead" |> ok |> ignore
            let subscriptions =
                Map.ofList [ "Planner", set [ "planning" ]
                             "Reviewer", set [ "review" ]
                             "Integrator", set [ "integration" ] ]
            WorkQueueGitLedger.initialize a "Lead" (Guid.NewGuid()) subscriptions |> ok |> ignore
            let taskId = "binder-original"
            ReviewWorkflow.createTask a (Guid.NewGuid()) taskId taskId |> ok |> ignore
            let _, taskRecord, _ = ReviewWorkflow.getTask a taskId |> ok
            let closure = ReviewWorkflow.closeTaskAs a "Lead" (Guid.NewGuid()) taskId taskRecord.Version |> ok
            let snapshot = LedgerStore.fetch b |> ok
            let _, project = WorkQueueGitLedger.load b |> ok
            let original =
                { SnapshotHead = snapshot.Commit
                  SnapshotHighWater = snapshot.Header.NextSequence - 1L
                  OriginalOperationId = Guid.Parse closure.Operation.OperationId
                  OriginalRequestHash = closure.Operation.RequestHash }
            let request =
                { SourceAnchor = original
                  SourceEvent = TaskTransition(taskId, null, "CloseTask", 0)
                  ExpectedProjectVersion = project.Version
                  RoleScope = NoRoleTransition }
            work a b request
        finally
            try Directory.Delete(root, true) with _ -> ()

    let requestFor binding (write: LedgerWrite) selector =
        let snapshot = LedgerStore.fetch binding |> ok
        let project = WorkQueueGitLedger.loadAt binding snapshot |> ok
        { SourceAnchor =
            { SnapshotHead = snapshot.Commit
              SnapshotHighWater = snapshot.Header.NextSequence - 1L
              OriginalOperationId = Guid.Parse write.Operation.OperationId
              OriginalRequestHash = write.Operation.RequestHash }
          SourceEvent = selector
          ExpectedProjectVersion = project.Version
          RoleScope = NoRoleTransition }

    [<Test>]
    member _.ValidOriginalCanonicalEventStillCannotProduceOrClaim() =
        withClones (fun _ b request ->
            let original = LedgerStore.fetch b |> ok
            let evidence =
                WorkQueueCanonicalSourceEvidence.read b request.SourceAnchor request.SourceEvent |> ok
            match evidence.Coverage with
            | IncompleteCoverage explanation ->
                Assert.That(explanation, Does.Contain("No atomic source Git"))
            let missing = WorkQueueCanonicalCasBinder.inspect b request |> gaps
            Assert.That(hasSourceGap missing, Is.True)
            Assert.That(hasCasGap missing, Is.True)
            denied (WorkQueueCanonicalCasBinder.attemptUnderCas b "Lead" (Guid.NewGuid()) request)
            let after = LedgerStore.fetch b |> ok
            Assert.That(after.Commit, Is.EqualTo(original.Commit))
            Assert.That(after.Header.NextSequence, Is.EqualTo(original.Header.NextSequence))
        )

    [<Test>]
    member _.ForgedOperationAndMatchingCallerHighWaterRemainDenied() =
        withClones (fun _ b request ->
            let all = [
                { request with SourceAnchor = { request.SourceAnchor with OriginalOperationId = Guid.NewGuid() } }
                { request with SourceAnchor = { request.SourceAnchor with OriginalRequestHash = String.replicate 64 "0" } }
                { request with SourceEvent = TaskTransition("forged-task", null, "CloseTask", 0) }
                { request with SourceEvent = TaskTransition("binder-original", null, "ReopenTask", 0) }
            ]
            for forged in all do
                let missing = WorkQueueCanonicalCasBinder.inspect b forged |> gaps
                Assert.That(hasEventRejection missing, Is.True)
                Assert.That(hasCasGap missing, Is.True)
                denied (WorkQueueCanonicalCasBinder.attemptUnderCas b "Lead" (Guid.NewGuid()) forged)
            let snapshot = LedgerStore.fetch b |> ok
            Assert.That(snapshot.Commit, Is.EqualTo(request.SourceAnchor.SnapshotHead))
        )

    [<Test>]
    member _.TwoClonesCannotReuseStaleOriginalHighWaterOrPretendTheirRoleRevisionIsAuthority() =
        withClones (fun a b request ->
            let forgedRole = { request with RoleScope = NewRoleDemand("Reviewer", 999) }
            let pinnedRole = { request with RoleScope = PinnedLaunchRole("fake-launch", "Reviewer", 1) }
            for attempted in [ forgedRole; pinnedRole ] do
                let missing = WorkQueueCanonicalCasBinder.inspect b attempted |> gaps
                Assert.That(hasRoleGap missing, Is.True)
                denied (WorkQueueCanonicalCasBinder.attemptUnderCas b "Lead" (Guid.NewGuid()) attempted)
            // The other clone commits a real canonical operation; even a
            // previously valid original event and asserted role revision do
            // not carry authority across its changed Git CAS snapshot.
            ReviewWorkflow.createTask a (Guid.NewGuid()) "independent-advance" "Independent" |> ok |> ignore
            let missing = WorkQueueCanonicalCasBinder.inspect b pinnedRole |> gaps
            Assert.That(hasStaleHead missing, Is.True)
            Assert.That(hasRoleGap missing, Is.True)
            denied (WorkQueueCanonicalCasBinder.attemptUnderCas b "Lead" (Guid.NewGuid()) pinnedRole)
        )

    [<Test>]
    member _.RacedTwoCloneBinderAttemptsNeverPublishReceiptOrProjectMutation() =
        withClones (fun a b request ->
            let before = LedgerStore.fetch a |> ok
            let operationA, operationB = Guid.NewGuid(), Guid.NewGuid()
            let first = Task.Run(fun () ->
                WorkQueueCanonicalCasBinder.attemptUnderCas a "Lead" operationA request)
            let second = Task.Run(fun () ->
                WorkQueueCanonicalCasBinder.attemptUnderCas b "Lead" operationB request)
            Task.WaitAll [| first :> Task; second :> Task |]
            denied first.Result
            denied second.Result
            let snapshot = LedgerStore.fetch b |> ok
            Assert.That(snapshot.Commit, Is.EqualTo(before.Commit))
            Assert.That(snapshot.Header.NextSequence, Is.EqualTo(before.Header.NextSequence))
            let storedA = LedgerStore.getOperationAt b snapshot operationA |> ok
            let storedB = LedgerStore.getOperationAt b snapshot operationB |> ok
            Assert.That(Option.isNone storedA, Is.True)
            Assert.That(Option.isNone storedB, Is.True)
        )

    [<Test>]
    member _.HistoricalRoleVersionIsEvidenceNotAnUntrustedClaimOrProducerPermit() =
        withClones (fun a b original ->
            let asCurrent (request: CanonicalBindRequest) =
                let snapshot = LedgerStore.fetch b |> ok
                let _, project = WorkQueueGitLedger.load b |> ok
                { request with
                    SourceAnchor =
                        { request.SourceAnchor with
                            SnapshotHead = snapshot.Commit
                            SnapshotHighWater = snapshot.Header.NextSequence - 1L }
                    ExpectedProjectVersion = project.Version }
            let desired = { original with RoleScope = NewRoleDemand("Reviewer", 1) }
            let absent = WorkQueueCanonicalCasBinder.inspect b desired |> gaps
            Assert.That(hasRoleEvidenceRejection absent, Is.True,
                        "Legacy subscriptions and a claimed version cannot substitute for historical role issuance.")
            WorkQueueRoleAuthority.initializeCatalog a "Lead" (Guid.NewGuid()) |> ok |> ignore
            let revision1 : RoleConfiguration =
                { RoleId = "Reviewer"; Version = 1; Enabled = true
                  Instructions = "Review immutable commits"
                  QueueSubscriptions = set ["review"] }
            WorkQueueRoleAuthority.configure a "Lead" (Guid.NewGuid()) 0 0 revision1 |> ok |> ignore
            let valid = asCurrent desired
            let assessed = WorkQueueCanonicalCasBinder.inspect b valid |> gaps
            Assert.That(hasRoleEvidenceRejection assessed, Is.False,
                        "A genuine originally issued enabled role version should pass only its read-only evidence check.")
            Assert.That(hasRoleGap assessed, Is.True)
            Assert.That(hasCasGap assessed, Is.True)
            // Even genuine, enabled historical-role evidence cannot turn a
            // read-only preflight into a WorkStage producer or ledger receipt.
            let beforeValidAttempt = LedgerStore.fetch b |> ok
            denied (WorkQueueCanonicalCasBinder.attemptUnderCas b "Lead" (Guid.NewGuid()) valid)
            let afterValidAttempt = LedgerStore.fetch b |> ok
            Assert.That(afterValidAttempt.Commit, Is.EqualTo(beforeValidAttempt.Commit))
            let fabricatedLaunch =
                { valid with RoleScope = PinnedLaunchRole("never-admitted", "Reviewer", 1) }
            Assert.That(hasRoleEvidenceRejection
                            (WorkQueueCanonicalCasBinder.inspect b fabricatedLaunch |> gaps), Is.True)
            let fabricatedRevision =
                { valid with RoleScope = NewRoleDemand("Reviewer", 42) }
            Assert.That(hasRoleEvidenceRejection
                            (WorkQueueCanonicalCasBinder.inspect b fabricatedRevision |> gaps), Is.True)
            // Clone A disables Reviewer at revision 2 after clone B's valid
            // pinned original-event/role snapshot. This must invalidate the
            // old assertion, and neither side may publish a queue receipt.
            WorkQueueRoleAuthority.configure a "Lead" (Guid.NewGuid()) 1 1
                { revision1 with Version = 2; Enabled = false } |> ok |> ignore
            let afterDisable = asCurrent desired
            Assert.That(hasRoleEvidenceRejection
                            (WorkQueueCanonicalCasBinder.inspect b afterDisable |> gaps), Is.True)
            Assert.That(hasRoleEvidenceRejection
                            (WorkQueueCanonicalCasBinder.inspect b
                                { afterDisable with RoleScope = NewRoleDemand("Reviewer", 2) } |> gaps), Is.True)
            Assert.That(hasStaleHead
                            (WorkQueueCanonicalCasBinder.inspect b valid |> gaps), Is.True)
            let before = LedgerStore.fetch b |> ok
            denied (WorkQueueCanonicalCasBinder.attemptUnderCas b "Lead" (Guid.NewGuid()) afterDisable)
            let after = LedgerStore.fetch a |> ok
            Assert.That(after.Commit, Is.EqualTo(before.Commit))
            Assert.That(after.Header.NextSequence, Is.EqualTo(before.Header.NextSequence))
        )

    [<Test>]
    member _.PracticalTaskProducerPublishesImmutableOriginAndRunsThroughFixtureClaim() =
        withClones (fun a b _ ->
            let task = "practical-task"
            let created = ReviewWorkflow.createTask a (Guid.NewGuid()) task "Practical" |> ok
            let request = requestFor b created (TaskCreated task)
            let publishedId = Guid.NewGuid()
            WorkQueueCanonicalCasBinder.publishPractical b "Lead" publishedId request |> ok |> ignore
            let snapshot, project = WorkQueueGitLedger.load b |> ok
            let work = project.Works |> Map.toList |> List.map snd
                       |> List.find (fun work -> work.SubjectId = task)
            Assert.That(work.Kind, Is.EqualTo(WorkKind.Planning))
            Assert.That(work.QueueId, Is.EqualTo("planning"))
            Assert.That(work.State, Is.EqualTo(WorkState.Available))
            Assert.That(work.AvailableSequence, Is.GreaterThan(0L))
            let origin, _ =
                WorkQueueGitLedger.readOriginAt b snapshot work.Id |> ok |> Option.get
            Assert.That(origin.SourceOperationId, Is.EqualTo(created.Operation.OperationId))
            Assert.That(origin.SourceOperationKind, Is.EqualTo("CreateTask"))
            Assert.That(origin.WorkId, Is.EqualTo(work.Id))
            Assert.That(origin.SourceHead, Is.EqualTo(request.SourceAnchor.SnapshotHead))
            Assert.That(origin.AccountCreditStatus, Is.EqualTo("NotReserved"))
            // An already applied UUID with original payload must replay once.
            // A competing UUID or stale pinned head cannot create second work.
            let replay =
                WorkQueueCanonicalCasBinder.publishPractical b "Lead" publishedId request
            match replay with
            | Ok _ -> ()
            | Error error -> Assert.Fail(sprintf "Original producer UUID replay failed: %A" error)
            for altered in
                [ { request with SourceEvent = TaskCreated "forged-task" }
                  { request with RoleScope = NewRoleDemand("Planner", 1) } ] do
                match WorkQueueCanonicalCasBinder.publishPractical b "Lead" publishedId altered with
                | Error OperationIdReused -> ()
                | other -> Assert.Fail(sprintf "Changed input reused producer UUID: %A" other)
            match WorkQueueCanonicalCasBinder.publishPractical a "Lead" (Guid.NewGuid()) request with
            | Error _ -> ()
            | Ok _ -> Assert.Fail("Stale clone published the same task twice.")
            let later = requestFor b created (TaskCreated task)
            match WorkQueueCanonicalCasBinder.publishPractical b "Lead" (Guid.NewGuid()) later with
            | Error _ -> ()
            | Ok _ -> Assert.Fail("Second operation published a duplicate stage.")
            let snapshotAfter, same = WorkQueueGitLedger.load b |> ok
            Assert.That(same.Works.Count, Is.EqualTo(1))
            Assert.That(snapshotAfter.Header.NextSequence, Is.EqualTo(snapshot.Header.NextSequence))
            // Versioned, Git-pinned practical role ranking is a read-only
            // feasibility result. It is not a launch/admission permission.
            WorkQueueRoleAuthority.initializeCatalog a "Lead" (Guid.NewGuid()) |> ok |> ignore
            let role : RoleConfiguration =
                { RoleId = "Planner"; Version = 1; Enabled = true
                  Instructions = "Plan queued tasks"
                  QueueSubscriptions = set [ "planning" ] }
            WorkQueueRoleAuthority.configure a "Lead" (Guid.NewGuid()) 0 0 role |> ok |> ignore
            let demand = WorkQueueRoleAuthority.selectPracticalDemand b "Planner" |> ok
            Assert.That(demand.Chosen.IsSome, Is.True)
            let chosen = demand.Chosen.Value
            Assert.That(chosen.WorkId, Is.EqualTo(work.Id))
            Assert.That(chosen.RoleId, Is.EqualTo("Planner"))
            Assert.That(chosen.RoleVersion, Is.EqualTo(1))
            // A real claim/completion, in an explicitly frozen disposable
            // WQ-02 fixture, demonstrates the projected planning work is valid.
            let launch : Launch =
                { Id = "practical-planner-launch"; ProjectId = b.ProjectId.ToString("D")
                  RoleId = "Planner"; RoleVersion = 1; AccountTokenId = Guid.NewGuid().ToString("D")
                  State = LaunchState.ReservedUnregistered; Lease = None; Version = 0
                  Credit = None; PrimaryWorkId = None; EverClaimed = false
                  AncillaryMessages = Set.empty; DispatchCount = 0 }
            let mutate mutation =
                let _, current = WorkQueueGitLedger.load b |> ok
                WorkQueueGitLedger.executeInertFixtureAt b "Lead" (Guid.NewGuid())
                    current.Version 100L mutation |> ok |> ignore
            mutate (AdmitProject launch)
            mutate (DispatchOnce(launch.Id, DispatchState.Submitted))
            mutate (InitializeAgent(launch.Id, "practical-fixture-session"))
            mutate (ClaimNext launch.Id)
            let _, claimed = WorkQueueGitLedger.load b |> ok
            Assert.That(claimed.Works.[work.Id].State, Is.EqualTo(WorkState.Claimed launch.Id))
            mutate (CompleteOrdinary(launch.Id, work.Id))
            let _, completed = WorkQueueGitLedger.load b |> ok
            Assert.That(completed.Works.[work.Id].State, Is.EqualTo(WorkState.Completed))
        )

    [<Test>]
    member _.PracticalReviewAndIntegrationPreserveImmutableHardDependency() =
        withClones (fun a b _ ->
            let task = "practical-review"
            ReviewWorkflow.registerAgent a (Guid.NewGuid()) "Owner" |> ok |> ignore
            ReviewWorkflow.registerAgent a (Guid.NewGuid()) "Reviewer" |> ok |> ignore
            let sourceSha = git a.RepositoryPath [ "rev-parse"; "HEAD" ]
            let sourceRef = "refs/heads/main"
            ReviewWorkflow.createTask a (Guid.NewGuid()) task task |> ok |> ignore
            let reviewId = Guid.NewGuid()
            let submittedReview =
                ReviewWorkflow.submitBranchReview a (Guid.NewGuid()) task "Owner" reviewId
                    sourceRef sourceSha "Review immutable commit" "task-bound" sourceRef |> ok
            let originalReview =
                ReviewWorkflow.getBranchReviewAt b (LedgerStore.fetch b |> ok) task reviewId |> ok
            let reviewRequest =
                requestFor b submittedReview
                    (ReviewSubmitted(task, reviewId, originalReview.ReviewRef, 1, sourceSha, sourceRef))
            WorkQueueCanonicalCasBinder.publishPractical b "Lead" (Guid.NewGuid()) reviewRequest
                |> ok |> ignore
            let snapshot, queue = WorkQueueGitLedger.load b |> ok
            let reviewStage =
                queue.Works |> Map.toList |> List.map snd
                |> List.find (fun item -> item.Kind = WorkKind.FrozenReview)
            Assert.That(reviewStage.SubjectId, Is.EqualTo(task))
            let reviewOrigin, _ =
                WorkQueueGitLedger.readOriginAt b snapshot reviewStage.Id |> ok |> Option.get
            Assert.That(reviewOrigin.SourceCommit, Is.EqualTo(sourceSha))
            ReviewWorkflow.claimBranchReview a (Guid.NewGuid()) task reviewId "Reviewer" 1
                |> ok |> ignore
            ReviewWorkflow.decideBranchReview a (Guid.NewGuid()) task reviewId "Reviewer" 2 Accept
                |> ok |> ignore
            let integrationId = Guid.NewGuid()
            let submitted =
                ReviewWorkflow.submitIntegration a (Guid.NewGuid()) task "Owner" integrationId
                    sourceRef sourceSha (reviewId.ToString("N")) sourceRef |> ok
            let integrationRequest =
                requestFor b submitted
                    (IntegrationState(task, integrationId, reviewId, sourceSha, sourceRef, "Pending"))
            WorkQueueCanonicalCasBinder.publishPractical b "Lead" (Guid.NewGuid()) integrationRequest
                |> ok |> ignore
            let _, nextQueue = WorkQueueGitLedger.load b |> ok
            let integration =
                nextQueue.Works |> Map.toList |> List.map snd
                |> List.find (fun item -> item.Kind = WorkKind.Integration)
            Assert.That(integration.State, Is.EqualTo(WorkState.Blocked))
            Assert.That(integration.Dependencies, Does.Contain(reviewStage.Id))
            Assert.That(integration.SubjectId, Is.EqualTo(task))
            Assert.That(nextQueue.Works.Count, Is.EqualTo(2))
        )
