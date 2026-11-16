module Client.Tests.WorkQueueReleaseAcceptanceTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open NUnit.Framework
open Jarvis.WorkQueues
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.WorkQueueOperatorConversations
open Client.SourceEffectJournal
open Client.ReviewWorkflow
open Client.WorkQueueReceiptProducer
open Client.WorkQueueRoleAuthority
open Client.WorkQueueSchedulerHostPreview
open Client.AccountCapacityLedger

// WQ-11 offline acceptance smoke. All remote refs, project ledgers and journals
// are disposable. The WQ-02 fixture path below is explicitly NOT a wired
// scheduler or a production activation test.
let private ok = function
    | Ok value -> value
    | Error failure -> failwithf "WQ-11 acceptance: unexpected failure: %A" failure

let private git dir args =
    use p = new Process()
    p.StartInfo.FileName <- "git"
    p.StartInfo.WorkingDirectory <- dir
    p.StartInfo.UseShellExecute <- false
    p.StartInfo.RedirectStandardOutput <- true
    p.StartInfo.RedirectStandardError <- true
    for arg in args do p.StartInfo.ArgumentList.Add(arg)
    Assert.That(p.Start(), Is.True)
    let stdout = p.StandardOutput.ReadToEnd()
    let stderr = p.StandardError.ReadToEnd()
    Assert.That(p.WaitForExit(30000), Is.True, sprintf "git %A timed out" args)
    Assert.That(p.ExitCode, Is.EqualTo(0), stderr)
    stdout.Trim()

let private withTwoClones action =
    let root = Path.Combine(Path.GetTempPath(), "wq11-acceptance-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    try
        let bare = Path.Combine(root, "remote.git")
        let first = Path.Combine(root, "first")
        let second = Path.Combine(root, "second")
        git root [ "init"; "--bare"; "--initial-branch=main"; bare ] |> ignore
        git root [ "clone"; bare; first ] |> ignore
        File.WriteAllText(Path.Combine(first, "fixture.txt"), "WQ-11 disposable source")
        git first [ "add"; "fixture.txt" ] |> ignore
        git first [ "-c"; "user.name=WQ11 Fixture"; "-c"; "user.email=wq11@example.test"
                    "commit"; "-m"; "disposable source" ] |> ignore
        git first [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
        git root [ "clone"; bare; second ] |> ignore
        let projectId, ledgerId = Guid.NewGuid(), Guid.NewGuid()
        let binding dir : LedgerBinding =
            { RepositoryPath = dir
              RemoteName = "origin"
              ApprovedFetchUrl = bare
              ApprovedPushUrl = bare
              ProjectId = projectId
              LedgerId = ledgerId
              Epoch = 1 }
        let a, b = binding first, binding second
        LedgerStore.initialize a true |> ok |> ignore
        LedgerStore.fetch b |> ok |> ignore
        WorkQueueGitLedger.initialize a "Fixture" (Guid.NewGuid())
            (Map.ofList [ "Lead", set [ "planning"; "role:Lead" ] ]) |> ok |> ignore
        action a b
    finally
        // Cleanup failures are test failures, not silent success.
        if Directory.Exists(root) then Directory.Delete(root, true)

let private readThread binding kind subject =
    WorkQueueOperatorConversations.read binding kind subject |> ok

let private plan project id deps : WorkStage =
    { Id = id
      ProjectId = project
      SubjectId = "task-" + id
      Kind = WorkKind.Planning
      QueueId = "planning"
      StageGeneration = 1
      State = WorkState.Available
      Domain = DomainState.Undecided
      Gate = GateState.Clear
      ClaimGeneration = 0
      Dependencies = set deps
      WaivedDependencies = Map.empty
      AvailableAt = 1000L
      AvailableSequence = 1L
      SourceLineage = None
      Push = None
      IntegrationAuthorized = false }

let private launch project : Launch =
    { Id = "L"
      ProjectId = project
      RoleId = "Lead"
      RoleVersion = 1
      AccountTokenId = "disposable-token-L"
      State = LaunchState.ReservedUnregistered
      Lease = None
      Version = 0
      Credit = None
      PrimaryWorkId = None
      EverClaimed = false
      AncillaryMessages = Set.empty
      DispatchCount = 0 }

let private withSourceJournal action =
    let root = Path.Combine(Path.GetTempPath(), "wq11-journal-" + Guid.NewGuid().ToString("N"))
    let anchor = root + ".continuity.anchor"
    Directory.CreateDirectory(root) |> ignore
    try
        provisionTrustedHostRoot root anchor |> ok |> ignore
        let reopen () = openExistingAtHostRoot root anchor |> ok
        let digest (text: string) =
            SHA256.HashData(Encoding.UTF8.GetBytes text)
            |> Convert.ToHexString
            |> fun value -> value.ToLowerInvariant()
        let scope : SourceEffectScope =
            { ProjectName = "WQ11-disposable"
              ProjectId = Guid.NewGuid()
              LedgerId = Guid.NewGuid()
              LedgerEpoch = 1
              RepositoryId = digest "disposable-source"
              ApprovedRemoteFingerprint = digest "disposable-remote"
              SourceRef = "refs/heads/wq11/fixture"
              CheckoutId = "checkout-1"
              WorkId = "W"
              LaunchUuid = Guid.NewGuid()
              ClaimGeneration = 1 }
        action reopen scope digest
    finally
        if Directory.Exists(root) then Directory.Delete(root, true)
        if File.Exists(anchor) then File.Delete(anchor)

[<TestFixture>]
type WorkQueueReleaseAcceptanceTests() =
    [<Test>]
    member _.ManualTaskAndRoleConversationHasOneClaimAndDurableHandledReply() =
        withTwoClones (fun a b ->
            let taskId, roleId = Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N")
            WorkQueueOperatorConversations.post a "Operator" (Guid.NewGuid()) 0
                "Task" "T" taskId "What is the evidence?" |> ok |> ignore
            WorkQueueOperatorConversations.post b "Operator" (Guid.NewGuid()) 0
                "Role" "Lead" roleId "Please review." |> ok |> ignore
            WorkQueueOperatorConversations.claim a "WorkerA" (Guid.NewGuid()) 1
                "Role" "Lead" roleId |> ok |> ignore
            match WorkQueueOperatorConversations.claim b "WorkerB" (Guid.NewGuid()) 1
                    "Role" "Lead" roleId with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Competing claim did not conflict: %A" other)
            let handleOp, replyId = Guid.NewGuid(), Guid.NewGuid().ToString("N")
            let handled = WorkQueueOperatorConversations.handle b "WorkerA" handleOp 2
                              "Role" "Lead" roleId 1 replyId "Reviewed." |> ok
            let replay = WorkQueueOperatorConversations.handle a "WorkerA" handleOp 2
                             "Role" "Lead" roleId 1 replyId "Reviewed." |> ok
            Assert.That(replay.AlreadyApplied, Is.True)
            Assert.That(replay.LedgerSequence, Is.EqualTo(handled.LedgerSequence))
            let role = readThread a "Role" "Lead"
            Assert.That(role.Thread.Messages.[0].Status, Is.EqualTo("Handled"))
            Assert.That(role.Thread.Messages.[0].ReplyBody, Is.EqualTo("Reviewed."))
            let task = readThread b "Task" "T"
            Assert.That(task.Thread.Messages.[0].Status, Is.EqualTo("Pending"))
            let _, queue = WorkQueueGitLedger.load b |> ok
            Assert.That(queue.Works.IsEmpty, Is.True, "Manual conversation must not silently fabricate queue demand")
            Assert.That(queue.Messages.IsEmpty, Is.True))

    [<Test>]
    member _.CompetingProjectHeadIsVisibleThenFreshVersionCanRetryWithoutLosingEitherMessage() =
        withTwoClones (fun a b ->
            let firstId, secondId = Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N")
            let firstOp, secondOp = Guid.NewGuid(), Guid.NewGuid()
            let first = WorkQueueOperatorConversations.post a "Operator" firstOp 0
                            "Task" "T" firstId "First request." |> ok
            match WorkQueueOperatorConversations.post b "Operator" secondOp 0
                    "Task" "T" secondId "Second request." with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Stale project/thread head was not visible: %A" other)
            let original = WorkQueueOperatorConversations.inspectOperation b firstOp |> ok
            Assert.That(original.Operation.IsSome, Is.True)
            Assert.That(original.VerifiedGitHead, Is.Not.Empty)
            let uncommitted = WorkQueueOperatorConversations.inspectOperation b secondOp |> ok
            Assert.That(uncommitted.Operation.IsNone, Is.True)
            let latest = readThread b "Task" "T"
            Assert.That(latest.Thread.Version, Is.EqualTo(1))
            WorkQueueOperatorConversations.post b "Operator" secondOp latest.Thread.Version
                "Task" "T" secondId "Second request." |> ok |> ignore
            let replay = WorkQueueOperatorConversations.post a "Operator" firstOp 0
                             "Task" "T" firstId "First request." |> ok
            Assert.That(replay.AlreadyApplied, Is.True)
            Assert.That(replay.LedgerSequence, Is.EqualTo(first.LedgerSequence))
            match WorkQueueOperatorConversations.post a "Operator" firstOp 0
                    "Task" "T" firstId "Changed request." with
            | Error OperationIdReused -> ()
            | other -> Assert.Fail(sprintf "Altered original UUID must be rejected: %A" other)
            let final = readThread a "Task" "T"
            Assert.That(final.Thread.Version, Is.EqualTo(2))
            let ids = final.Thread.Messages |> Array.map (fun m -> m.Id) |> Set.ofArray
            Assert.That((ids = Set.ofList [ firstId; secondId ]), Is.True))

    [<Test>]
    member _.UnverifiedTaskReviewAndIntegrationProducerEvidenceIsNeverSilentlyIndexed() =
        withTwoClones (fun a b ->
            let project = a.ProjectId.ToString("D")
            let forged source origin : QueueProducerEvent =
                { ProjectId = project
                  SourceId = source
                  StageGeneration = 1
                  Origin = origin
                  HardPrerequisites = Set.empty }
            let inputs =
                [ forged "invented-task" (QueueProducerOrigin.TaskRequested "T")
                  forged "invented-review" (QueueProducerOrigin.FrozenReviewRequested(
                      "T", "R", String.replicate 40 "a", "refs/heads/source"))
                  forged "invented-integration" (QueueProducerOrigin.AcceptedIntegration(
                      "T", "R", String.replicate 40 "a", "refs/heads/main")) ]
            for input in inputs do
                match WorkQueueGitLedger.executeAt a "Fixture" (Guid.NewGuid()) 0 10L
                        (ProduceEvent input) with
                | Error(LedgerIntegrityError reason) ->
                    Assert.That(reason, Does.Contain("WQ-04"))
                | other -> Assert.Fail(sprintf "Unverified producer reached canonical project: %A" other)
            let _, state = WorkQueueGitLedger.load b |> ok
            Assert.That(state.Version, Is.EqualTo(0))
            Assert.That(state.Works.IsEmpty, Is.True)
            Assert.That(state.Receipts.IsEmpty, Is.True))

    [<Test>]
    member _.InertTwoCloneClaimCompletesPrerequisiteAndWakesDependentOnce_FixtureOnly() =
        withTwoClones (fun a b ->
            let project = a.ProjectId.ToString("D")
            let mutate binding version at effect =
                WorkQueueGitLedger.executeInertFixtureAt binding "Fixture"
                    (Guid.NewGuid()) version at effect |> ok
            mutate a 0 1000L (PublishStage (plan project "B" [])) |> ignore
            mutate b 1 1001L (PublishStage (plan project "D" [ "B" ])) |> ignore
            mutate a 2 1002L (AdmitProject (launch project)) |> ignore
            mutate b 3 1003L (DispatchOnce ("L", DispatchState.Submitted)) |> ignore
            let registerOp = Guid.NewGuid()
            let registration = WorkQueueGitLedger.executeInertFixtureAt a "Fixture"
                                   registerOp 4 1004L (InitializeAgent ("L", "session")) |> ok
            let registrationReplay = WorkQueueGitLedger.executeInertFixtureAt b "Fixture"
                                         registerOp 4 1004L (InitializeAgent ("L", "session")) |> ok
            Assert.That(registrationReplay.AlreadyApplied, Is.True)
            Assert.That(registrationReplay.Operation.OriginalResultHash,
                        Is.EqualTo(registration.Operation.OriginalResultHash))
            let claimOp = Guid.NewGuid()
            WorkQueueGitLedger.executeInertFixtureAt b "Fixture" claimOp 5 1005L
                (ClaimNext "L") |> ok |> ignore
            let _, claimed = WorkQueueGitLedger.load a |> ok
            Assert.That(claimed.Works.["B"].State, Is.EqualTo(WorkState.Claimed "L"))
            Assert.That(claimed.Works.["D"].State, Is.EqualTo(WorkState.Blocked))
            match WorkQueueGitLedger.executeInertFixtureAt a "Fixture" (Guid.NewGuid()) 5 1006L
                    (ClaimNext "L") with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Duplicate claim did not report CAS winner: %A" other)
            let completed = WorkQueueGitLedger.executeInertFixtureAt a "Fixture"
                                (Guid.NewGuid()) 6 2000L (CompleteOrdinary ("L", "B")) |> ok
            Assert.That(completed.AlreadyApplied, Is.False)
            let _, final = WorkQueueGitLedger.load b |> ok
            Assert.That(final.Works.["B"].State, Is.EqualTo(WorkState.Completed))
            Assert.That(final.Works.["D"].State, Is.EqualTo(WorkState.Available))
            Assert.That(final.Works.["D"].AvailableAt, Is.EqualTo(2000L))
            Assert.That(final.Works.["D"].AvailableSequence, Is.GreaterThan(claimed.NextAvailability)))

    [<Test>]
    member _.UnknownOriginalGitPushSurvivesRestartWithoutRedispatch() =
        withSourceJournal (fun reopen scope digest ->
            let original =
                { OriginalOperationId = Guid.NewGuid()
                  Scope = scope
                  Kind = GitPush
                  RequestSha256 = digest "original refs/heads/wq11/fixture push" }
            let first = reopen ()
            Assert.That(beginBeforeDispatch first original |> ok, Is.EqualTo(FirstDispatch))
            recordUncertain first original "original remote push response lost" |> ok |> ignore
            let restarted = reopen ()
            let observed = inspectForHandoff restarted scope [ original.OriginalOperationId ]
            Assert.That(observed.Records.Length, Is.EqualTo(1))
            Assert.That(observed.RecoveryBlocked, Is.True)
            Assert.That(observed.MissingOriginalOperations, Is.Empty)
            match observed.Records.Head.State with
            | Uncertain reason -> Assert.That(reason, Does.Contain("response lost"))
            | other -> Assert.Fail(sprintf "Lost acknowledgment became %A" other)
            let mutable redispatched = false
            match dispatchOnce restarted original (fun () -> redispatched <- true) with
            | Error reason -> Assert.That(reason, Does.Contain("RecoveryBlocked"))
            | other -> Assert.Fail(sprintf "Unknown original push was redispatched: %A" other)
            Assert.That(redispatched, Is.False))

    [<Test>]
    member _.MissingOriginalSourceEffectIsReportedAsMissingNotAsSuccess() =
        withSourceJournal (fun reopen scope _ ->
            let missing = Guid.NewGuid()
            let observed = inspectForHandoff (reopen ()) scope [ missing ]
            Assert.That(observed.RecoveryBlocked, Is.True)
            Assert.That(observed.AllKnownOperationsTerminal, Is.False)
            Assert.That(observed.MissingOriginalOperations = [ missing ], Is.True)
            Assert.That(observed.UninstrumentedRoutes, Is.Not.Empty)
            Assert.That(observed.BlockReasons, Is.Not.Empty))

    [<Test>]
    member _.OriginalCreateTaskReceiptReachesRealSchedulerPreviewWithDisposableCapOne() =
        withTwoClones (fun a b ->
            // Account and project are independent orphan authorities on the same
            // disposable bare Git remote; neither is a configured live account.
            let accountId, accountLedgerId = Guid.NewGuid(), Guid.NewGuid()
            let accountBinding (project: LedgerBinding) : AccountBinding =
                { RepositoryPath = project.RepositoryPath
                  RemoteName = project.RemoteName
                  ApprovedFetchUrl = project.ApprovedFetchUrl
                  ApprovedPushUrl = project.ApprovedPushUrl
                  AccountId = accountId
                  LedgerId = accountLedgerId
                  Epoch = 1 }
            let accountA, accountB = accountBinding a, accountBinding b
            AccountCapacityLedger.initialize accountA 1 |> ok |> ignore

            ReviewWorkflow.registerAgent a (Guid.NewGuid()) "Lead" |> ok |> ignore
            WorkQueueRoleAuthority.initializeCatalog a "Lead" (Guid.NewGuid()) |> ok |> ignore
            let role: RoleConfiguration =
                { RoleId = "Planner"; Version = 1; Enabled = true
                  Instructions = "Inspect genuine planning demand"
                  QueueSubscriptions = set [ "planning" ] }
            WorkQueueRoleAuthority.configure a "Lead" (Guid.NewGuid()) 0 0 role |> ok |> ignore

            // WQ02 original operation, WQ04 explicit producer, and immutable
            // origin are all real writes performed via the normal Git adapters.
            let taskId, sourceOp, producerOp =
                "wq11-wired-task", Guid.NewGuid(), Guid.NewGuid()
            let created = ReviewWorkflow.createTask a sourceOp taskId "Connected smoke task" |> ok
            Assert.That(created.Operation.Type, Is.EqualTo("CreateTask"))
            Assert.That(created.Operation.OperationId, Is.EqualTo(sourceOp.ToString("D")))
            let planned = WorkQueueReceiptProducer.preview b sourceOp |> ok
            Assert.That(planned.Status, Is.EqualTo("ReadyToProduce"))
            Assert.That(planned.SourceOperationKind, Is.EqualTo("CreateTask"))
            let published = WorkQueueReceiptProducer.produce b "Lead" producerOp sourceOp |> ok
            Assert.That(published.AlreadyApplied, Is.False)

            // A second checkout independently fetches canonical work and origin;
            // it has not been fed any in-memory producer projection.
            let projectHead, project = WorkQueueGitLedger.load a |> ok
            Assert.That(project.Works.Count, Is.EqualTo(1))
            let work = project.Works.[planned.StageId]
            Assert.That(work.Kind, Is.EqualTo(WorkKind.Planning))
            Assert.That(work.State, Is.EqualTo(WorkState.Available))
            let origin, originOid =
                WorkQueueGitLedger.readOriginAt a projectHead work.Id |> ok |> Option.get
            Assert.That(origin.SourceOperationId, Is.EqualTo(sourceOp.ToString("D")))
            Assert.That(origin.SourceOperationKind, Is.EqualTo("CreateTask"))
            Assert.That(origin.SourceRequestHash, Is.EqualTo(created.Operation.RequestHash))
            Assert.That(origin.WorkId, Is.EqualTo(work.Id))

            // WQ08 realReadPorts reads independently verified project/account
            // Git heads and versioned WQ04 role state. No fake preview ports.
            let beforeAccount = (AccountCapacityLedger.fetch accountA |> ok).Head
            let report = WorkQueueSchedulerHostPreview.preview accountB a "Planner"
            Assert.That(report.Mode, Is.EqualTo("ReadOnlyDryRun"))
            Assert.That(report.CanDispatch, Is.False)
            Assert.That(report.AccountHead, Is.EqualTo(Some beforeAccount))
            Assert.That(report.ProjectHead, Is.EqualTo(Some projectHead.Commit))
            Assert.That(report.MaxConcurrentAgents, Is.EqualTo(Some 1))
            Assert.That(report.OccupiedAgents, Is.EqualTo(Some 0))
            Assert.That(report.AvailableSlots, Is.EqualTo(Some 1))
            Assert.That(report.SelectedRoleVersion, Is.EqualTo(Some 1))
            Assert.That(report.EligibleWork, Does.Contain(work.Id))
            let candidate = report.WouldLaunch |> Option.get
            Assert.That(candidate.WorkId, Is.EqualTo(work.Id))
            Assert.That(candidate.SourceOperationId, Is.EqualTo(Some(sourceOp.ToString("D"))))
            Assert.That(candidate.SourceRequestHash, Is.EqualTo(Some created.Operation.RequestHash))
            Assert.That(candidate.OriginBlobOid, Is.EqualTo(Some originOid))
            Assert.That(report.Issues |> List.exists (fun x ->
                x.Code = "SourceReceiptMissing" || x.Code = "DemandUnknown"
                || x.Code = "ProjectUnknown" || x.Code = "AccountUnknown"), Is.False)
            Assert.That((AccountCapacityLedger.fetch accountB |> ok).Head, Is.EqualTo(beforeAccount))
            Assert.That((LedgerStore.fetch b |> ok).Commit, Is.EqualTo(projectHead.Commit))

            // The same genuinely disposable account authority can be filled.
            // Demand remains visible but a second slot no longer exists.
            let projectAuthority =
                { ProjectId = a.ProjectId; LedgerId = a.LedgerId
                  Epoch = a.Epoch; ApprovedFetchUrl = a.ApprovedFetchUrl
                  ApprovedPushUrl = a.ApprovedPushUrl }
            AccountCapacityLedger.reserve accountA (Guid.NewGuid()) "Fixture"
                (Guid.NewGuid()) projectAuthority "Planner" 1L |> ok |> ignore
            let full = WorkQueueSchedulerHostPreview.preview accountB a "Planner"
            Assert.That(full.OccupiedAgents, Is.EqualTo(Some 1))
            Assert.That(full.AvailableSlots, Is.EqualTo(Some 0))
            Assert.That(full.WouldLaunch |> Option.map _.WorkId, Is.EqualTo(Some work.Id))
            Assert.That(full.Issues |> List.exists (fun x -> x.Code = "CapacityFull"), Is.True)
            Assert.That(full.CanDispatch, Is.False)
            Assert.That(project.Launches.IsEmpty, Is.True)

            // Replaying both original UUIDs survives later Git heads; changing
            // the stable operation payload is not silently accepted as a retry.
            let sourceReplay =
                ReviewWorkflow.createTask b sourceOp taskId "Connected smoke task" |> ok
            Assert.That(sourceReplay.AlreadyApplied, Is.True)
            let producerReplay =
                WorkQueueReceiptProducer.produce a "Lead" producerOp sourceOp |> ok
            Assert.That(producerReplay.AlreadyApplied, Is.True)
            match ReviewWorkflow.createTask b sourceOp taskId "Changed payload" with
            | Error OperationIdReused -> ()
            | other -> Assert.Fail(sprintf "Changed CreateTask UUID was not rejected: %A" other)
            let _, finalProject = WorkQueueGitLedger.load b |> ok
            Assert.That(finalProject.Works.Count, Is.EqualTo(1))
            Assert.That(finalProject.Launches.IsEmpty, Is.True)
            Assert.That(git a.RepositoryPath [ "status"; "--porcelain" ], Is.Empty)
            Assert.That(git b.RepositoryPath [ "status"; "--porcelain" ], Is.Empty))
