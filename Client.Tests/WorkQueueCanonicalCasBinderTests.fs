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
open Client.WorkQueueReceiptProducer
open Client.WorkQueueRoleAuthority
open Client.WorkCompositionGitLedger

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

    // Real original WQ04 producer fixtures: authenticated Git role history and
    // task-linked composition predate publication; no fake graph-only writer.
    let seedOriginalRoles binding =
        WorkQueueRoleAuthority.initializeCatalog binding "Lead" (Guid.NewGuid()) |> ok |> ignore
        [ "Planner", "planning"; "Reviewer", "review"; "Integrator", "integration" ]
        |> List.iteri (fun revision (name, queue) ->
            let role: RoleConfiguration =
                { RoleId = name; Version = 1; Enabled = true
                  Instructions = "Original WQ04 fixture: " + name
                  QueueSubscriptions = Set.singleton queue }
            WorkQueueRoleAuthority.configure binding "Lead" (Guid.NewGuid()) revision 0 role
            |> ok |> ignore)

    let linkOriginalTask binding task =
        let apply mutation =
            let version = WorkCompositionGitLedger.load binding |> ok |> snd
            WorkCompositionGitLedger.execute binding "Lead" (Guid.NewGuid()) version.Version mutation
            |> ok |> ignore
        let graph = WorkCompositionGitLedger.load binding |> ok |> snd
        if graph.Version = 0 then
            let spec kind linked =
                { Title = kind; Goal = "Original source/role/graph fixture"
                  Scope = "Hermetic project CAS"; AcceptanceCriteria = Set.singleton "Exact receipt"
                  Constraints = Set.empty; LinkedTaskId = linked }
            apply (WorkCompositionMutation.Create("root", WorkUnitKind.Project, None, spec "Root" None))
            apply (WorkCompositionMutation.Create("module", WorkUnitKind.System, Some "root", spec "Module" None))
        let spec =
            { Title = task; Goal = "Original source/role/graph fixture"
              Scope = "Hermetic project CAS"; AcceptanceCriteria = Set.singleton "Exact receipt"
              Constraints = Set.empty; LinkedTaskId = Some task }
        apply (WorkCompositionMutation.Create("unit-" + task, WorkUnitKind.Feature, Some "module", spec))

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
          RoleScope =
              match selector with
              | TaskCreated _ -> NewRoleDemand("Planner", 1)
              | ReviewSubmitted _ -> NewRoleDemand("Reviewer", 1)
              | IntegrationState _ -> NewRoleDemand("Integrator", 1)
              | _ -> NoRoleTransition }

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
            seedOriginalRoles a
            linkOriginalTask a task
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
                  { request with RoleScope = NewRoleDemand("Reviewer", 1) } ] do
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
            seedOriginalRoles a
            linkOriginalTask a task
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

    [<Test>]
    member _.ExplicitReceiptEntryPointResolvesOriginalTaskAndReplaysWithoutNewHeadAssertions() =
        withClones (fun a b _ ->
            let task = "receipt-entrypoint-task"
            seedOriginalRoles a
            linkOriginalTask a task
            let created = ReviewWorkflow.createTask a (Guid.NewGuid()) task "Original task receipt" |> ok
            let sourceId = Guid.Parse created.Operation.OperationId
            let preview = WorkQueueReceiptProducer.preview b sourceId |> ok
            Assert.That(preview.Status, Is.EqualTo("ReadyToProduce"))
            Assert.That(preview.SourceOperationKind, Is.EqualTo("CreateTask"))
            Assert.That(preview.SubjectId, Is.EqualTo(task))
            Assert.That(preview.StageQueue, Is.EqualTo("planning"))
            Assert.That(preview.AccountCredit, Is.EqualTo("NotReserved"))
            let producerId = Guid.NewGuid()
            let produced = WorkQueueReceiptProducer.produce b "Lead" producerId sourceId |> ok
            Assert.That(produced.AlreadyApplied, Is.False)
            let after = WorkQueueReceiptProducer.preview a sourceId |> ok
            Assert.That(after.Status, Is.EqualTo("AlreadyProduced"))
            Assert.That(after.StageId, Is.EqualTo(preview.StageId))
            // Reconstructing the request against a newer Git head would be a
            // false UUID collision. The original durable producer receipt wins.
            let replay = WorkQueueReceiptProducer.produce a "Lead" producerId sourceId |> ok
            Assert.That(replay.AlreadyApplied, Is.True)
            Assert.That(replay.Operation.OperationId, Is.EqualTo(producerId.ToString("D")))
            match WorkQueueReceiptProducer.produce a "Other" producerId sourceId with
            | Error OperationIdReused -> ()
            | other -> Assert.Fail(sprintf "Cross-actor UUID replay was not rejected: %A" other)
            match WorkQueueReceiptProducer.produce a "Lead" (Guid.NewGuid()) sourceId with
            | Error _ -> ()
            | Ok _ -> Assert.Fail("A different operation ID published a duplicate task stage.")
            let snapshot, project = WorkQueueGitLedger.load a |> ok
            Assert.That(project.Works.Count, Is.EqualTo(1))
            let stage = project.Works.[preview.StageId]
            Assert.That(stage.Kind, Is.EqualTo(WorkKind.Planning))
            Assert.That(stage.State, Is.EqualTo(WorkState.Available))
            let origin, _ = WorkQueueGitLedger.readOriginAt a snapshot preview.StageId |> ok |> Option.get
            Assert.That(origin.SourceOperationId, Is.EqualTo(sourceId.ToString("D")))
            Assert.That(project.Launches.IsEmpty, Is.True, "Production launch is not activated.")
            // Explicit disposable WQ02 fixture only, not real account admission.
            let launch : Launch =
                { Id = "receipt-fixture-planner"; ProjectId = a.ProjectId.ToString("D")
                  RoleId = "Planner"; RoleVersion = 1
                  AccountTokenId = Guid.NewGuid().ToString("D")
                  State = LaunchState.ReservedUnregistered; Lease = None; Version = 0
                  Credit = None; PrimaryWorkId = None; EverClaimed = false
                  AncillaryMessages = Set.empty; DispatchCount = 0 }
            let apply mutation =
                let _, current = WorkQueueGitLedger.load a |> ok
                WorkQueueGitLedger.executeInertFixtureAt a "Lead" (Guid.NewGuid())
                    current.Version 101L mutation |> ok |> ignore
            apply (AdmitProject launch)
            apply (DispatchOnce(launch.Id, DispatchState.Submitted))
            apply (InitializeAgent(launch.Id, "receipt-planning-fixture-session"))
            apply (ClaimNext launch.Id)
            let _, claimed = WorkQueueGitLedger.load a |> ok
            Assert.That(claimed.Works.[preview.StageId].State, Is.EqualTo(WorkState.Claimed launch.Id))
            apply (CompleteOrdinary(launch.Id, preview.StageId))
            let _, doneState = WorkQueueGitLedger.load a |> ok
            Assert.That(doneState.Works.[preview.StageId].State, Is.EqualTo(WorkState.Completed))
        )

    [<Test>]
    member _.ExplicitReceiptReviewAndIntegrationRequireOriginalDependentStage() =
        withClones (fun a b _ ->
            let task = "receipt-review-task"
            seedOriginalRoles a
            linkOriginalTask a task
            ReviewWorkflow.registerAgent a (Guid.NewGuid()) "Owner" |> ok |> ignore
            ReviewWorkflow.registerAgent a (Guid.NewGuid()) "Reviewer" |> ok |> ignore
            let sourceSha = git a.RepositoryPath [ "rev-parse"; "HEAD" ]
            let sourceRef = "refs/heads/main"
            ReviewWorkflow.createTask a (Guid.NewGuid()) task task |> ok |> ignore
            let reviewId = Guid.NewGuid()
            let submittedReview =
                ReviewWorkflow.submitBranchReview a (Guid.NewGuid()) task "Owner" reviewId
                    sourceRef sourceSha "Review exact SHA" "task-bound" sourceRef |> ok
            let sourceId = Guid.Parse submittedReview.Operation.OperationId
            let reviewPreview = WorkQueueReceiptProducer.preview b sourceId |> ok
            Assert.That(reviewPreview.Status, Is.EqualTo("ReadyToProduce"))
            Assert.That(reviewPreview.StageQueue, Is.EqualTo("review"))
            WorkQueueReceiptProducer.produce b "Lead" (Guid.NewGuid()) sourceId |> ok |> ignore
            ReviewWorkflow.claimBranchReview a (Guid.NewGuid()) task reviewId "Reviewer" 1
                |> ok |> ignore
            let existing = WorkQueueReceiptProducer.preview b sourceId |> ok
            Assert.That(existing.Status, Is.EqualTo("AlreadyProduced"))
            ReviewWorkflow.decideBranchReview a (Guid.NewGuid()) task reviewId "Reviewer" 2 Accept
                |> ok |> ignore
            let integrationId = Guid.NewGuid()
            let submitted =
                ReviewWorkflow.submitIntegration a (Guid.NewGuid()) task "Owner" integrationId
                    sourceRef sourceSha (reviewId.ToString("N")) sourceRef |> ok
            let integrationSource = Guid.Parse submitted.Operation.OperationId
            let integrationPreview = WorkQueueReceiptProducer.preview b integrationSource |> ok
            Assert.That(integrationPreview.Status, Is.EqualTo("ReadyToProduce"))
            Assert.That(integrationPreview.StageQueue, Is.EqualTo("integration"))
            Assert.That(integrationPreview.PriorReviewStageId, Is.EqualTo(reviewPreview.StageId))
            WorkQueueReceiptProducer.produce b "Lead" (Guid.NewGuid()) integrationSource |> ok |> ignore
            let _, queue = WorkQueueGitLedger.load b |> ok
            let stage = queue.Works.[integrationPreview.StageId]
            Assert.That(stage.Kind, Is.EqualTo(WorkKind.Integration))
            Assert.That(stage.State, Is.EqualTo(WorkState.Blocked))
            Assert.That(stage.Dependencies, Does.Contain(reviewPreview.StageId))
        )

    [<Test>]
    member _.LateClaimedReviewIsVisibleAndCannotSilentlyBackfillAsUnclaimed() =
        withClones (fun a b _ ->
            let task = "receipt-late-review"
            seedOriginalRoles a
            linkOriginalTask a task
            ReviewWorkflow.registerAgent a (Guid.NewGuid()) "Owner" |> ok |> ignore
            ReviewWorkflow.registerAgent a (Guid.NewGuid()) "Reviewer" |> ok |> ignore
            let sourceSha = git a.RepositoryPath [ "rev-parse"; "HEAD" ]
            ReviewWorkflow.createTask a (Guid.NewGuid()) task task |> ok |> ignore
            let reviewId = Guid.NewGuid()
            let original =
                ReviewWorkflow.submitBranchReview a (Guid.NewGuid()) task "Owner" reviewId
                    "refs/heads/main" sourceSha "Late review" "task-bound" "refs/heads/main" |> ok
            let originalId = Guid.Parse original.Operation.OperationId
            ReviewWorkflow.claimBranchReview a (Guid.NewGuid()) task reviewId "Reviewer" 1
                |> ok |> ignore
            let preview = WorkQueueReceiptProducer.preview b originalId |> ok
            Assert.That(preview.SourceStatus, Is.EqualTo("Claimed"))
            Assert.That(preview.Status, Is.EqualTo("NeedsAttention"))
            Assert.That(preview.Blockers, Is.Not.Empty)
            Assert.That(preview.Recovery, Does.Contain("backup SHA"))
            match WorkQueueReceiptProducer.produce b "Lead" (Guid.NewGuid()) originalId with
            | Error _ -> ()
            | Ok _ -> Assert.Fail("Claimed review was backfilled as an unclaimed runnable review.")
            let _, queue = WorkQueueGitLedger.load b |> ok
            Assert.That(queue.Works.Count, Is.EqualTo(0))
        )

    [<Test>]
    member _.TwoClonesCannotDuplicateTheSameOriginalTaskStage() =
        withClones (fun a b _ ->
            let task = "receipt-race-task"
            seedOriginalRoles a
            linkOriginalTask a task
            let created = ReviewWorkflow.createTask a (Guid.NewGuid()) task task |> ok
            let originalId = Guid.Parse created.Operation.OperationId
            let left = Task.Run(fun () -> WorkQueueReceiptProducer.produce a "Lead" (Guid.NewGuid()) originalId)
            let right = Task.Run(fun () -> WorkQueueReceiptProducer.produce b "Lead" (Guid.NewGuid()) originalId)
            Task.WaitAll [| left :> Task; right :> Task |]
            let successful =
                [ left.Result; right.Result ]
                |> List.filter (function Ok _ -> true | Error _ -> false)
            Assert.That(successful.Length, Is.EqualTo(1))
            let _, queue = WorkQueueGitLedger.load b |> ok
            let stages = queue.Works |> Map.toList |> List.map snd
                         |> List.filter (fun stage -> stage.SubjectId = task)
            Assert.That(stages.Length, Is.EqualTo(1))
        )

    [<Test>]
    member _.OriginalProducerRejectsMissingRoleMissingGraphAndStaleRoleWithoutAnyPartialCAS() =
        withClones (fun a b _ ->
            let task = "original-gates-regression"
            seedOriginalRoles a
            let created = ReviewWorkflow.createTask a (Guid.NewGuid()) task task |> ok
            let original = requestFor b created (TaskCreated task)
            let before = LedgerStore.fetch b |> ok
            let deny expected result =
                match result with
                | Error(InvalidBinding reason) when expected = "invalid" -> ()
                | Error(StaleEntityVersion reason) when expected = "stale" -> ()
                | other -> Assert.Fail(sprintf "Original CAS should deny %s: %A" expected other)
            deny "invalid" (WorkQueueCanonicalCasBinder.publishPractical
                                b "Lead" (Guid.NewGuid())
                                { original with RoleScope = NoRoleTransition })
            deny "invalid" (WorkQueueCanonicalCasBinder.publishPractical
                                b "Lead" (Guid.NewGuid()) original)
            let after = LedgerStore.fetch b |> ok
            Assert.That(after.Commit, Is.EqualTo(before.Commit),
                        "Neither unbound role nor graph may produce Available or an orphan origin")
            linkOriginalTask a task
            let oldRole = (WorkQueueRoleAuthority.load a |> ok).Catalog
            let updated =
                { oldRole.Versions.["Planner"].[1] with
                    Version = 2; Instructions = "Revised original planner demand" }
            WorkQueueRoleAuthority.configure a "Lead" (Guid.NewGuid())
                oldRole.Revision 1 updated |> ok |> ignore
            let staleRole = requestFor b created (TaskCreated task)
            let freshBefore = LedgerStore.fetch b |> ok
            deny "stale" (WorkQueueCanonicalCasBinder.publishPractical
                              b "Lead" (Guid.NewGuid()) staleRole)
            Assert.That((LedgerStore.fetch b |> ok).Commit, Is.EqualTo(freshBefore.Commit))
            let accepted = { staleRole with RoleScope = NewRoleDemand("Planner", 2) }
            WorkQueueCanonicalCasBinder.publishPractical b "Lead" (Guid.NewGuid()) accepted
            |> ok |> ignore
            let head, queue = WorkQueueGitLedger.load a |> ok
            let stage = queue.Works |> Map.toSeq |> Seq.map snd
                        |> Seq.find (fun item -> item.SubjectId = task)
            Assert.That(stage.State, Is.EqualTo(WorkState.Available))
            let origin, _ = WorkQueueGitLedger.readOriginAt a head stage.Id |> ok |> Option.get
            Assert.That(origin.RoleId, Is.EqualTo("Planner"))
            Assert.That(origin.RoleVersion, Is.EqualTo(2))
            Assert.That(origin.SourceOperationId, Is.EqualTo(created.Operation.OperationId))
            Assert.That((WorkCompositionGitLedger.readStageLinkAt a head stage.Id |> ok).IsSome,
                        Is.True, "Original stage, source, role and composition must commit atomically")
        )
