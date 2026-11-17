module Client.Tests.WorkQueueVerifiedDispatchAdapterTests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open Jarvis.WorkQueues
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.ReviewWorkflow
open Client.WorkQueueRoleAuthority
open Client.WorkQueueReceiptProducer
open Client.WorkQueueCrossProjectPlanner
open Client.WorkQueueVerifiedDispatchAdapter
open Client.OriginalBrowserJournal
open Client.AccountCapacityLedger
open Client.AccountAdmissionAdapter

// A new, isolated WQ11 executor fixture: no production host/browser, timers,
// activated migration or project mutation outside disposable local bare remotes.
let private ok = function
    | Ok value -> value
    | Error error -> failwithf "Default-off dispatch fixture: %A" error

let private git directory arguments =
    use p = new Process()
    p.StartInfo.FileName <- "git"
    p.StartInfo.WorkingDirectory <- directory
    p.StartInfo.UseShellExecute <- false
    p.StartInfo.RedirectStandardOutput <- true
    p.StartInfo.RedirectStandardError <- true
    for a in arguments do p.StartInfo.ArgumentList.Add(a)
    Assert.That(p.Start(), Is.True)
    let output = p.StandardOutput.ReadToEnd()
    let error = p.StandardError.ReadToEnd()
    Assert.That(p.WaitForExit(30000), Is.True, sprintf "git %A timed out" arguments)
    Assert.That(p.ExitCode, Is.EqualTo 0, error)
    output.Trim()

let private withAuthorities action =
    let root = Path.Combine(Path.GetTempPath(), "wq11-dispatch-executor-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    let checkouts = ResizeArray<string>()
    try
        let pair label =
            let remote = Path.Combine(root, label + ".git")
            let first, second = Path.Combine(root, label + "-first"), Path.Combine(root, label + "-second")
            git root ["init"; "--bare"; "--initial-branch=main"; remote] |> ignore
            git root ["clone"; remote; first] |> ignore
            File.WriteAllText(Path.Combine(first, "fixture.txt"), "WQ11 executor " + label)
            git first ["add"; "fixture.txt"] |> ignore
            git first ["-c"; "user.name=WQ11 Test"; "-c"; "user.email=wq11@example.test";
                       "commit"; "-m"; "isolated fixture"] |> ignore
            git first ["push"; "-u"; "origin"; "HEAD"] |> ignore
            git root ["clone"; remote; second] |> ignore
            checkouts.Add(first)
            checkouts.Add(second)
            let projectId, ledgerId = Guid.NewGuid(), Guid.NewGuid()
            let binding path : LedgerBinding =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = remote; ApprovedPushUrl = remote
                  ProjectId = projectId; LedgerId = ledgerId; Epoch = 1 }
            binding first, binding second
        let a1, a2 = pair "project-a"
        let b1, b2 = pair "project-b"
        let ac1, ac2 = pair "account"
        let accountId, accountLedger = Guid.NewGuid(), Guid.NewGuid()
        let account (p: LedgerBinding) : AccountBinding =
            { RepositoryPath = p.RepositoryPath; RemoteName = "origin"
              ApprovedFetchUrl = p.ApprovedFetchUrl; ApprovedPushUrl = p.ApprovedPushUrl
              AccountId = accountId; LedgerId = accountLedger; Epoch = 1 }
        let account1, account2 = account ac1, account ac2
        for first, second in [a1, a2; b1, b2] do
            LedgerStore.initialize first true |> ok |> ignore
            LedgerStore.fetch second |> ok |> ignore
            WorkQueueGitLedger.initialize first "WQ11 Fake" (Guid.NewGuid())
                (Map.ofList ["Lead", set ["planning"; "role:Lead"]]) |> ok |> ignore
            ReviewWorkflow.registerAgent first (Guid.NewGuid()) "Lead" |> ok |> ignore
            WorkQueueRoleAuthority.initializeCatalog first "Lead" (Guid.NewGuid()) |> ok |> ignore
            let role: RoleConfiguration =
                { RoleId = "Lead"; Version = 1; Enabled = true
                  Instructions = "Disposable default-off Work only"
                  QueueSubscriptions = set ["planning"] }
            WorkQueueRoleAuthority.configure first "Lead" (Guid.NewGuid()) 0 0 role
            |> ok |> ignore
        AccountCapacityLedger.initialize account1 1 |> ok |> ignore
        let journalRoot = Path.Combine(root, "trusted-originals")
        Directory.CreateDirectory(journalRoot) |> ignore
        OriginalBrowserJournal.provisionTrustedHostRoot journalRoot |> ok |> ignore
        action (a1, a2) (b1, b2) (account1, account2) journalRoot
        for path in checkouts do
            Assert.That(git path ["status"; "--porcelain"], Is.Empty,
                        "Disposable source checkout was dirtied")
    finally
        if Directory.Exists root then Directory.Delete(root, true)

let private produce source receiver name =
    let sourceOperation, producerOperation = Guid.NewGuid(), Guid.NewGuid()
    ReviewWorkflow.createTask source sourceOperation name ("WQ11 " + name) |> ok |> ignore
    WQ04ProducerFixture.produce receiver "Lead" producerOperation sourceOperation
    |> ok |> ignore
    let head, project = WorkQueueGitLedger.load receiver |> ok
    let stage =
        project.Works |> Map.toSeq |> Seq.map snd
        |> Seq.filter (fun w -> w.SubjectId = name) |> Seq.exactlyOne
    let _, blob = WorkQueueGitLedger.readOriginAt receiver head stage.Id |> ok |> Option.get
    stage, blob

let private request () : AccountAdmissionRequest =
    { Actor = "WQ11 Fake-Only Executor"
      LaunchId = Guid.NewGuid()
      AdmissionOperationId = Guid.NewGuid()
      AbortOperationId = Guid.NewGuid()
      ReleaseOperationId = Guid.NewGuid()
      RoleId = "Lead"; RoleVersion = 1 }

let private pinned binding =
    let snap = LedgerStore.fetch binding |> ok
    let view = WorkQueueRoleAuthority.loadAt binding snap |> ok
    let authority = AccountAdmissionAdapter.projectAuthority binding
                    |> AccountCapacityLedger.projectAuthoritySha256
    let project: PlannerProject =
        { ProjectId = binding.ProjectId.ToString("D")
          LedgerId = binding.LedgerId.ToString("D")
          LedgerEpoch = binding.Epoch
          AuthoritySha256 = authority
          Head = snap.Commit; Version = view.Project.Version
          CatalogRevision = view.Catalog.Revision
          CatalogBlobOid = view.Proof.CatalogBlobOid
          CoverageComplete = view.Project.QueueCovered && view.Project.GraphCovered
                              && view.Project.MessageCovered && view.Project.SourceCovered }
    let current = view.Catalog.Current.["Lead"]
    let definition = view.Catalog.Versions.["Lead"].[current]
    let role: PlannerRole =
        { ProjectId = project.ProjectId; ProjectHead = project.Head
          CatalogBlobOid = project.CatalogBlobOid; RoleId = "Lead"
          Version = current; Enabled = definition.Enabled
          Queues = definition.QueueSubscriptions }
    project, role, snap, view

let private candidate binding (req: AccountAdmissionRequest) (originalId: Guid) workId =
    let project, _, snap, view = pinned binding
    let work = view.Project.Works.[workId]
    let origin, _ = WorkQueueGitLedger.readOriginAt binding snap work.Id |> ok |> Option.get
    let opt value = if String.IsNullOrWhiteSpace value then None else Some value
    let candidate: PlannerCandidate =
        { ProjectId = project.ProjectId; ProjectHead = project.Head
          ProjectVersion = project.Version
          WorkId = work.Id; WorkGeneration = work.StageGeneration
          QueueId = work.QueueId; AvailableAt = work.AvailableAt
          AvailableSequence = work.AvailableSequence
          BlocksOtherWork =
              view.Project.Works |> Map.exists (fun _ other ->
                  other.Dependencies.Contains work.Id
                  && not (other.WaivedDependencies.ContainsKey work.Id)
                  && other.State <> WorkState.Completed)
          RoleId = req.RoleId; RoleVersion = req.RoleVersion
          CatalogBlobOid = project.CatalogBlobOid
          Source =
              { Head = origin.SourceHead; OperationId = origin.SourceOperationId
                RequestHash = origin.SourceRequestHash
                SourceRef = opt origin.SourceRef; SourceCommit = opt origin.SourceCommit }
          LaunchId = req.LaunchId.ToString("D")
          OriginalBrowserOperationId = originalId.ToString("D") }
    candidate

let private observe journalRoot binding =
    let view = WorkQueueRoleAuthority.load binding |> ok
    view.Project.Launches |> Map.toList |> List.map (fun (_, launch) ->
        let journal = OriginalBrowserJournal.openExisting journalRoot |> ok
        let original = OriginalBrowserJournal.findByLaunch journal launch.Id |> ok
        let phase =
            match launch.State with
            | LaunchState.ReservedUnregistered -> PlannerLaunchPhase.ReservedUnregistered
            | LaunchState.SubmittedUnregistered _ -> PlannerLaunchPhase.SubmittedUnregistered
            | LaunchState.Registered _ -> PlannerLaunchPhase.Registered
            | LaunchState.DemandInvalidatedPendingTerminal _ ->
                PlannerLaunchPhase.DemandInvalidatedPendingTerminal
            | LaunchState.TerminalPendingRelease _ -> PlannerLaunchPhase.TerminalPendingRelease
        let browser =
            match original with
            | None -> PlannerBrowserEvidence.NotRecorded
            | Some entry ->
                match entry.Outcome with
                | None -> PlannerBrowserEvidence.Unknown "marker without settled outcome"
                | Some(OriginalBrowserOutcome.Accepted receipt) -> PlannerBrowserEvidence.Accepted receipt
                | Some(OriginalBrowserOutcome.DefinitiveNoEffect reason) ->
                    PlannerBrowserEvidence.DefinitiveNoEffect reason
                | Some(OriginalBrowserOutcome.Unknown reason) -> PlannerBrowserEvidence.Unknown reason
        { LaunchId = launch.Id; ProjectId = launch.ProjectId
          RoleId = launch.RoleId; RoleVersion = launch.RoleVersion
          AccountAdmissionOperationId = launch.AccountTokenId
          Phase = phase
          OriginalBrowserOperationId = original |> Option.map (fun e -> e.Intent.OperationId)
          OriginalBrowser = browser }: PlannerExistingLaunch)

let private ranking a b account journalRoot candidates =
    let pa, ra, _, _ = pinned a
    let pb, rb, _, _ = pinned b
    let actualAccount = AccountCapacityLedger.fetch account |> ok
    // WQ08's Activated marker is a disposable in-memory hypothetical *ranking
    // input only*. The real authority stays LegacyActive and this is never a
    // production admission grant or live migration.
    let simulatedAccount =
        { actualAccount with State = { actualAccount.State with MigrationPhase = "Activated" } }
    let ports: FakeOnlyCrossProjectPorts =
        { ReadAccount = fun () -> Ok simulatedAccount
          ReadProjects = fun () ->
              Ok { CompleteProjectSet = true; Projects = [pa; pb] }
          ReadRoles = fun () -> Ok [ra; rb]
          ReadCandidates = fun () -> Ok candidates
          ReadExistingLaunches = fun () -> Ok(observe journalRoot a @ observe journalRoot b) }
    WorkQueueCrossProjectPlanner.planFakeOnly ports

[<TestFixture>]
type WorkQueueVerifiedDispatchAdapterTests() =
    [<Test>]
    member _.DefaultOffTwoProjectBlockingFirstCapOneReplayAndUnknown() =
        withAuthorities (fun (a1, a2) (b1, b2) (account1, account2) journalRoot ->
            let aWork, _ = produce a1 a2 "executor-A"
            let bWork, _ = produce b1 b2 "executor-B"
            let downstream: WorkStage =
                { Id = "downstream-" + Guid.NewGuid().ToString("N")
                  ProjectId = b1.ProjectId.ToString("D")
                  SubjectId = "dependency"
                  Kind = WorkKind.FrozenReview; QueueId = "review"
                  StageGeneration = 1; State = WorkState.Blocked
                  Domain = DomainState.Undecided; Gate = GateState.Clear
                  ClaimGeneration = 0; Dependencies = Set.singleton bWork.Id
                  WaivedDependencies = Map.empty
                  AvailableAt = bWork.AvailableAt + 1L
                  AvailableSequence = bWork.AvailableSequence + 1L
                  SourceLineage = None; Push = None; IntegrationAuthorized = false }
            let _, before = WorkQueueGitLedger.load b1 |> ok
            WorkQueueGitLedger.executeInertFixture b1 "WQ11 Fake" (Guid.NewGuid())
                before.Version (PublishStage downstream) |> ok |> ignore
            let reqA, reqB = request (), request ()
            let originalA, originalB = Guid.NewGuid(), Guid.NewGuid()
            let ca = candidate a2 reqA originalA aWork.Id
            let cb = candidate b2 reqB originalB bWork.Id
            Assert.That(cb.BlocksOtherWork, Is.True)
            let plan = ranking a2 b2 account2 journalRoot [ca; cb]
            match plan.Decision with
            | PlannerDecision.WouldLaunch selected ->
                Assert.That(selected, Is.EqualTo cb)
            | other -> Assert.Fail(sprintf "WQ08 blocking-first failed: %A" other)
            let dispatchB, firstConnectB = Guid.NewGuid(), Guid.NewGuid()
            let mutable calls = 0
            let provider (intent: OriginalBrowserIntent) =
                calls <- calls + 1
                let reopened = OriginalBrowserJournal.openExisting journalRoot |> ok
                let marker = OriginalBrowserJournal.findOriginal reopened intent.OperationId |> ok
                Assert.That(marker.IsSome && marker.Value.Outcome.IsNone, Is.True,
                            "Fake callback ran before durable original marker.")
                FakeOriginalReply.Accepted
                    { LaunchId = intent.LaunchId; WorkId = intent.WorkId
                      Receipt = "fake-receipt"; SessionKey = "fake-session" }
            let unprovisioned =
                WorkQueueVerifiedDispatchAdapter.dispatchFakeOnly true plan account1 b1
                    "" cb reqB dispatchB firstConnectB provider
            Assert.That(unprovisioned.Phase, Is.EqualTo FakeDispatchPhase.RepairNeeded)
            Assert.That(unprovisioned.VerifiedSelection, Is.False)
            Assert.That(calls, Is.Zero)
            let disabled =
                WorkQueueVerifiedDispatchAdapter.dispatchFakeOnly false plan account1 b1
                    journalRoot cb reqB dispatchB firstConnectB provider
            Assert.That(disabled.Phase, Is.EqualTo FakeDispatchPhase.Disabled)
            Assert.That(calls, Is.Zero)
            Assert.That(AccountCapacityLedger.occupied (AccountCapacityLedger.fetch account2 |> ok).State, Is.Zero)
            let connected =
                WorkQueueVerifiedDispatchAdapter.dispatchFakeOnly true plan account1 b1
                    journalRoot cb reqB dispatchB firstConnectB provider
            Assert.That(connected.Phase, Is.EqualTo FakeDispatchPhase.Registered, connected.Detail)
            Assert.That(connected.VerifiedSelection, Is.True)
            Assert.That(connected.AccountHead.IsSome && connected.ProjectHead.IsSome
                        && connected.OriginBlobOid.IsSome, Is.True)
            Assert.That(calls, Is.EqualTo 1)
            let state = (WorkQueueGitLedger.load b2 |> ok) |> snd
            match state.Launches.[cb.LaunchId].State with
            | LaunchState.Registered _ -> ()
            | other -> Assert.Fail(sprintf "No WQ02 first-connect receipt: %A" other)
            Assert.That(AccountCapacityLedger.occupied (AccountCapacityLedger.fetch account2 |> ok).State, Is.EqualTo 1)
            let repeated =
                WorkQueueVerifiedDispatchAdapter.dispatchFakeOnly true plan account2 b2
                    journalRoot cb reqB dispatchB firstConnectB provider
            Assert.That(repeated.Phase, Is.EqualTo FakeDispatchPhase.Registered, repeated.Detail)
            Assert.That(calls, Is.EqualTo 1)
            // A replay of the same WQ03 operation is not a bearer permission.
            // The original account admission token and WQ02 first-connect
            // operation must still be present and match the exact UUIDs.
            let foreignAdmission = { reqB with AdmissionOperationId = Guid.NewGuid() }
            let replayWithForeignToken =
                WorkQueueVerifiedDispatchAdapter.dispatchFakeOnly true plan account2 b2
                    journalRoot cb foreignAdmission dispatchB firstConnectB provider
            Assert.That(replayWithForeignToken.Phase, Is.EqualTo FakeDispatchPhase.RepairNeeded)
            Assert.That(replayWithForeignToken.VerifiedSelection, Is.False)
            let replayWithForeignConnection =
                WorkQueueVerifiedDispatchAdapter.dispatchFakeOnly true plan account2 b2
                    journalRoot cb reqB dispatchB (Guid.NewGuid()) provider
            Assert.That(replayWithForeignConnection.Phase, Is.EqualTo FakeDispatchPhase.RepairNeeded)
            Assert.That(calls, Is.EqualTo 1)
            // Independent candidate cannot obtain the same global credit from
            // the old ranking; its project authority remains untouched.
            let staleA = ranking a2 b2 account2 journalRoot [ca]
            Assert.That(staleA.Decision, Is.EqualTo PlannerDecision.NoCapacity)
            let noSlot = WorkQueueVerifiedDispatchAdapter.dispatchFakeOnly true
                             (ranking a2 b2 account2 journalRoot [ca]) account1 a1
                             journalRoot ca reqA (Guid.NewGuid()) (Guid.NewGuid()) provider
            Assert.That(noSlot.Phase, Is.Not.EqualTo FakeDispatchPhase.Registered)
            Assert.That(calls, Is.EqualTo 1)
            Assert.That((WorkQueueGitLedger.load a2 |> ok |> snd).Launches.Count, Is.Zero)
            let claimB, terminalB = Guid.NewGuid(), Guid.NewGuid()
            let finished =
                WorkQueueVerifiedDispatchAdapter.completeFakeOnly true account2 b2
                    journalRoot cb reqB claimB terminalB
            Assert.That(finished.Phase, Is.EqualTo FakeDispatchPhase.TerminalReleased, finished.Detail)
            let again =
                WorkQueueVerifiedDispatchAdapter.completeFakeOnly true account1 b1
                    journalRoot cb reqB claimB terminalB
            Assert.That(again.Phase, Is.EqualTo FakeDispatchPhase.TerminalReleased, again.Detail)
            Assert.That(AccountCapacityLedger.occupied (AccountCapacityLedger.fetch account2 |> ok).State, Is.Zero)
            let afterTerminal =
                WorkQueueVerifiedDispatchAdapter.dispatchFakeOnly true plan account2 b2
                    journalRoot cb reqB dispatchB firstConnectB provider
            Assert.That(afterTerminal.Phase, Is.EqualTo FakeDispatchPhase.NotReady)
            Assert.That(calls, Is.EqualTo 1)
            let planA = ranking a2 b2 account2 journalRoot [candidate a2 reqA originalA aWork.Id]
            let dispatchA, firstConnectA = Guid.NewGuid(), Guid.NewGuid()
            let unknown (intent: OriginalBrowserIntent) =
                calls <- calls + 1
                FakeOriginalReply.Unknown "fake provider response lost"
            let held =
                WorkQueueVerifiedDispatchAdapter.dispatchFakeOnly true planA account1 a1
                    journalRoot ca reqA dispatchA firstConnectA unknown
            Assert.That(held.Phase, Is.EqualTo FakeDispatchPhase.RepairNeeded, held.Detail)
            let aState = WorkQueueGitLedger.load a2 |> ok |> snd
            Assert.That(aState.Launches.[ca.LaunchId].State,
                        Is.EqualTo(LaunchState.SubmittedUnregistered DispatchState.OutcomeUnknown))
            Assert.That(AccountCapacityLedger.occupied (AccountCapacityLedger.fetch account2 |> ok).State, Is.EqualTo 1)
            let reloaded =
                WorkQueueVerifiedDispatchAdapter.dispatchFakeOnly true planA account2 a2
                    journalRoot ca reqA dispatchA firstConnectA unknown
            Assert.That(reloaded.Phase, Is.EqualTo FakeDispatchPhase.RepairNeeded)
            Assert.That(reloaded.RepairAction.IsSome, Is.True)
            Assert.That(calls, Is.EqualTo 2))

    [<Test>]
    member _.DefinitiveNoEffectStaysHeldForManualRepair() =
        withAuthorities (fun (a1, a2) (b1, b2) (account1, account2) journalRoot ->
            let work, _ = produce a1 a2 "noeffect-A"
            let req = request ()
            let selected = candidate a2 req (Guid.NewGuid()) work.Id
            let plan = ranking a2 b2 account2 journalRoot [selected]
            let mutable calls = 0
            let noEffect (_: OriginalBrowserIntent) =
                calls <- calls + 1
                FakeOriginalReply.DefinitiveNoEffect "fake provider refused before send"
            let dispatch, connect = Guid.NewGuid(), Guid.NewGuid()
            let refused = WorkQueueVerifiedDispatchAdapter.dispatchFakeOnly true plan
                              account1 a1 journalRoot selected req dispatch connect noEffect
            Assert.That(refused.Phase, Is.EqualTo FakeDispatchPhase.RepairNeeded, refused.Detail)
            Assert.That(refused.RepairAction.Value, Does.Contain("pre-registration"))
            Assert.That(AccountCapacityLedger.occupied (AccountCapacityLedger.fetch account2 |> ok).State, Is.EqualTo 1)
            let replay = WorkQueueVerifiedDispatchAdapter.dispatchFakeOnly true plan
                             account2 a2 journalRoot selected req dispatch connect noEffect
            Assert.That(replay.Phase, Is.EqualTo FakeDispatchPhase.RepairNeeded)
            Assert.That(calls, Is.EqualTo 1)
            let journal = OriginalBrowserJournal.openExisting journalRoot |> ok
            let recorded = OriginalBrowserJournal.findByLaunch journal selected.LaunchId |> ok |> Option.get
            Assert.That(recorded.Outcome,
                        Is.EqualTo(Some(OriginalBrowserOutcome.DefinitiveNoEffect "fake provider refused before send")))
            Assert.That((WorkQueueGitLedger.load a2 |> ok |> snd).Launches.Count, Is.EqualTo 1)
            // No forged terminal or release may be inferred from a provider refusal.
            let notTerminal = WorkQueueVerifiedDispatchAdapter.completeFakeOnly true
                                  account1 a1 journalRoot selected req (Guid.NewGuid()) (Guid.NewGuid())
            Assert.That(notTerminal.Phase, Is.EqualTo FakeDispatchPhase.RepairNeeded)
            Assert.That(AccountCapacityLedger.occupied (AccountCapacityLedger.fetch account2 |> ok).State, Is.EqualTo 1))

    [<Test>]
    member _.ForeignFakeAcceptanceCannotForgeFirstConnectionOrRelease() =
        withAuthorities (fun (a1, a2) (b1, b2) (account1, account2) journalRoot ->
            let stage, _ = produce a1 a2 "foreign-provider-A"
            let req = request ()
            let selected = candidate a2 req (Guid.NewGuid()) stage.Id
            let plan = ranking a2 b2 account2 journalRoot [selected]
            let dispatchId, firstConnectId = Guid.NewGuid(), Guid.NewGuid()
            let mutable calls = 0
            let fake (_: OriginalBrowserIntent) =
                calls <- calls + 1
                FakeOriginalReply.Accepted
                    { LaunchId = Guid.NewGuid().ToString("D")
                      WorkId = selected.WorkId
                      Receipt = "foreign-accepted"; SessionKey = "foreign-session" }
            let refused =
                WorkQueueVerifiedDispatchAdapter.dispatchFakeOnly true plan account1 a1
                    journalRoot selected req dispatchId firstConnectId fake
            Assert.That(refused.Phase, Is.EqualTo FakeDispatchPhase.RepairNeeded, refused.Detail)
            let original = OriginalBrowserJournal.findByLaunch
                               (OriginalBrowserJournal.openExisting journalRoot |> ok)
                               selected.LaunchId |> ok |> Option.get
            match original.Outcome with
            | Some(OriginalBrowserOutcome.Unknown reason) ->
                Assert.That(reason, Does.Contain("identity"))
            | other -> Assert.Fail(sprintf "Foreign fake reply was accepted: %A" other)
            let state = WorkQueueGitLedger.load a2 |> ok |> snd
            Assert.That(state.Launches.[selected.LaunchId].State,
                        Is.EqualTo(LaunchState.SubmittedUnregistered DispatchState.OutcomeUnknown))
            Assert.That(AccountCapacityLedger.occupied (AccountCapacityLedger.fetch account2 |> ok).State, Is.EqualTo 1)
            let replay = WorkQueueVerifiedDispatchAdapter.dispatchFakeOnly true plan account2 a2
                             journalRoot selected req dispatchId firstConnectId fake
            Assert.That(replay.Phase, Is.EqualTo FakeDispatchPhase.RepairNeeded)
            Assert.That(calls, Is.EqualTo 1)
            let noTerminal = WorkQueueVerifiedDispatchAdapter.completeFakeOnly true account2 a2
                                 journalRoot selected req (Guid.NewGuid()) (Guid.NewGuid())
            Assert.That(noTerminal.Phase, Is.EqualTo FakeDispatchPhase.RepairNeeded)
            Assert.That(AccountCapacityLedger.occupied (AccountCapacityLedger.fetch account1 |> ok).State, Is.EqualTo 1))


    [<Test>]
    member _.TwoTrustedJournalHostsConcurrentSameLaunchHaveOneCanonicalOriginalSend() =
        withAuthorities (fun (a1, a2) (b1, b2) (account1, account2) journalRoot ->
            let foreignRoot = Path.Combine(Path.GetDirectoryName journalRoot, "trusted-originals-second")
            Directory.CreateDirectory(foreignRoot) |> ignore
            OriginalBrowserJournal.provisionTrustedHostRoot foreignRoot |> ok |> ignore
            let work, _ = produce a1 a2 "two-host-original-A"
            let req = request ()
            let selected = candidate a2 req (Guid.NewGuid()) work.Id
            let plan = ranking a2 b2 account2 journalRoot [selected]
            Assert.That(plan.Decision, Is.EqualTo(PlannerDecision.WouldLaunch selected))
            let dispatch, connect = Guid.NewGuid(), Guid.NewGuid()
            let callbacks = ConcurrentBag<string>()
            let provider (intent: OriginalBrowserIntent) =
                callbacks.Add(intent.OperationId)
                FakeOriginalReply.Accepted
                    { LaunchId = intent.LaunchId; WorkId = intent.WorkId
                      Receipt = "single-fake-receipt"; SessionKey = "single-fake-session" }
            use admissionReady = new CountdownEvent(2)
            use releaseAdmission = new ManualResetEventSlim(false)
            use claimReady = new CountdownEvent(2)
            use releaseClaim = new ManualResetEventSlim(false)
            let gate (ready: CountdownEvent) (release: ManualResetEventSlim) name () =
                ready.Signal() |> ignore
                if not (release.Wait(TimeSpan.FromSeconds(45.0))) then
                    failwith ("Timed out at WQ11 two-client " + name + " barrier")
            let dispatchFrom account project root =
                WorkQueueVerifiedDispatchAdapter.dispatchFakeOnlyWithGates
                    (gate admissionReady releaseAdmission "pre-admission")
                    (gate claimReady releaseClaim "pre-original-claim")
                    true plan account project root selected req dispatch connect provider
            let first = Task.Run(fun () -> dispatchFrom account1 a1 journalRoot)
            let second = Task.Run(fun () -> dispatchFrom account2 a2 foreignRoot)
            try
                Assert.That(admissionReady.Wait(TimeSpan.FromSeconds(45.0)), Is.True,
                            "Both independent clients must pass verified selection before WQ07 admission.")
                releaseAdmission.Set()
                Assert.That(claimReady.Wait(TimeSpan.FromSeconds(45.0)), Is.True,
                            "Both clients must replay the same WQ07/WQ02 admission and reach pre-send CAS.")
                releaseClaim.Set()
                Assert.That(Task.WaitAll([| first :> Task; second :> Task |], 90000), Is.True)
                let results = [first.Result; second.Result]
                Assert.That(callbacks.Count, Is.EqualTo 1,
                            "Two independently trusted SQLite roots must not each send the same original.")
                Assert.That(results |> List.filter (fun r -> r.Phase = FakeDispatchPhase.Registered)
                                    |> List.length, Is.EqualTo 1)
                Assert.That(results |> List.filter (fun r -> r.Phase = FakeDispatchPhase.RepairNeeded)
                                    |> List.length, Is.EqualTo 1)
                let winningRoot, losingRoot =
                    if first.Result.Phase = FakeDispatchPhase.Registered then journalRoot, foreignRoot
                    else foreignRoot, journalRoot
                let winningJournal = OriginalBrowserJournal.openExisting winningRoot |> ok
                let losingJournal = OriginalBrowserJournal.openExisting losingRoot |> ok
                Assert.That((OriginalBrowserJournal.findByLaunch winningJournal selected.LaunchId |> ok).IsSome,
                            Is.True, "Winning host has durable original intent.")
                Assert.That((OriginalBrowserJournal.findByLaunch losingJournal selected.LaunchId |> ok).IsNone,
                            Is.True, "Rejected foreign host must not mark an original.")
                let hostId = OriginalBrowserJournal.trustedHostId winningJournal |> ok
                let head = LedgerStore.fetch a2 |> ok
                let record, oid =
                    LedgerStore.readAt a2 head
                        ("records/wq11-original-hosts/" + req.LaunchId.ToString("N") + ".json")
                    |> ok
                Assert.That(record.IsSome && oid.IsSome, Is.True)
                Assert.That(record.Value, Does.Contain hostId)
                let sameHostReplay =
                    WorkQueueVerifiedDispatchAdapter.dispatchFakeOnly true plan account2 a2
                        winningRoot selected req dispatch connect provider
                Assert.That(sameHostReplay.Phase, Is.EqualTo FakeDispatchPhase.Registered,
                            sameHostReplay.Detail)
                let foreignReplay =
                    WorkQueueVerifiedDispatchAdapter.dispatchFakeOnly true plan account1 a1
                        losingRoot selected req dispatch connect provider
                Assert.That(foreignReplay.Phase, Is.EqualTo FakeDispatchPhase.RepairNeeded)
                let missingHost =
                    WorkQueueVerifiedDispatchAdapter.dispatchFakeOnly true plan account2 a2
                        "" selected req dispatch connect provider
                Assert.That(missingHost.Phase, Is.EqualTo FakeDispatchPhase.RepairNeeded)
                Assert.That(callbacks.Count, Is.EqualTo 1, "No replay or foreign host can resend.")
                Assert.That(AccountCapacityLedger.occupied (AccountCapacityLedger.fetch account1 |> ok).State,
                            Is.EqualTo 1, "One WQ07 credit remains held for the single registered launch.")
            finally
                releaseAdmission.Set()
                releaseClaim.Set())
