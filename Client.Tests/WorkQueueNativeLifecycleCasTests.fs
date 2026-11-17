module Client.Tests.WorkQueueNativeLifecycleCasTests

open System
open System.Diagnostics
open System.IO
open NUnit.Framework
open Jarvis.WorkQueues
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.ReviewWorkflow
open Client.WorkQueueRoleAuthority
open Client.WorkQueueReceiptProducer
open Client.WorkQueueCrossProjectPlanner
open Client.WorkQueueVerifiedDispatchAdapter
open Client.WorkQueueNativeLifecycleCas
open Client.OriginalBrowserJournal
open Client.AccountCapacityLedger
open Client.AccountAdmissionAdapter
open Client.WorkQueueToolFacade

// Real disposable bare Git remotes and two independently configured clones.
// The provider is fake and the authenticated execution port is test-injected;
// no live browser/MCP/scheduler is registered or called.
let private ok = function
    | Ok value -> value
    | Error error -> failwithf "Work-native verified fixture failed: %A" error

let private git root args =
    use p = new Process()
    p.StartInfo.FileName <- "git"
    p.StartInfo.WorkingDirectory <- root
    p.StartInfo.UseShellExecute <- false
    p.StartInfo.RedirectStandardOutput <- true
    p.StartInfo.RedirectStandardError <- true
    for a in args do p.StartInfo.ArgumentList.Add a
    Assert.That(p.Start(), Is.True)
    let output = p.StandardOutput.ReadToEnd()
    let error = p.StandardError.ReadToEnd()
    Assert.That(p.WaitForExit(30000), Is.True, sprintf "Git command timed out: %A" args)
    Assert.That(p.ExitCode, Is.EqualTo(0), error)
    output.Trim()

let private fixture action =
    let root = Path.Combine(Path.GetTempPath(), "work-native-cas-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try
        let pair label =
            let bare = Path.Combine(root, label + ".git")
            let a = Path.Combine(root, label + "-a")
            let b = Path.Combine(root, label + "-b")
            git root ["init"; "--bare"; "--initial-branch=main"; bare] |> ignore
            git root ["clone"; bare; a] |> ignore
            File.WriteAllText(Path.Combine(a, "fixture.txt"), "work-native CAS " + label)
            git a ["add"; "fixture.txt"] |> ignore
            git a ["-c"; "user.name=Native Test"; "-c"; "user.email=native@example.test";
                   "commit"; "-m"; "fixture"] |> ignore
            git a ["push"; "-u"; "origin"; "HEAD"] |> ignore
            git root ["clone"; bare; b] |> ignore
            a, b, bare
        let pA, pB, pRemote = pair "project"
        let aA, aB, aRemote = pair "account"
        let projectId, ledgerId = Guid.NewGuid(), Guid.NewGuid()
        let accountId, accountLedger = Guid.NewGuid(), Guid.NewGuid()
        let project path : LedgerBinding =
            { RepositoryPath = path; RemoteName = "origin"
              ApprovedFetchUrl = pRemote; ApprovedPushUrl = pRemote
              ProjectId = projectId; LedgerId = ledgerId; Epoch = 1 }
        let account path : AccountBinding =
            { RepositoryPath = path; RemoteName = "origin"
              ApprovedFetchUrl = aRemote; ApprovedPushUrl = aRemote
              AccountId = accountId; LedgerId = accountLedger; Epoch = 1 }
        let pa, pb, aa, ab = project pA, project pB, account aA, account aB
        // Issued before either ledger exists. No existing Frozen project can
        // obtain the nominal scratch capability from an enabled boolean.
        let scratchAuthority =
            WorkQueueNativeLifecycleCas.authorizeFreshScratch pa aa |> ok
        LedgerStore.initialize pa true |> ok |> ignore
        LedgerStore.fetch pb |> ok |> ignore
        WorkQueueGitLedger.initialize pa "Native Test" (Guid.NewGuid())
            (Map.ofList ["Lead", set ["planning"; "role:Lead"]]) |> ok |> ignore
        ReviewWorkflow.registerAgent pa (Guid.NewGuid()) "Lead" |> ok |> ignore
        WorkQueueRoleAuthority.initializeCatalog pa "Lead" (Guid.NewGuid()) |> ok |> ignore
        let role: RoleConfiguration =
            { RoleId = "Lead"; Version = 1; Enabled = true
              Instructions = "Fake Work-only native fixture"
              QueueSubscriptions = set ["planning"] }
        WorkQueueRoleAuthority.configure pa "Lead" (Guid.NewGuid()) 0 0 role
        |> ok |> ignore
        AccountCapacityLedger.initialize aa 1 |> ok |> ignore
        let sourceId = Guid.NewGuid()
        ReviewWorkflow.createTask pa sourceId "native-plan" "Native Work CAS fixture" |> ok |> ignore
        WQ04ProducerFixture.produce pb "Lead" (Guid.NewGuid()) sourceId |> ok |> ignore
        let _, state = WorkQueueGitLedger.load pa |> ok
        let work = state.Works |> Map.toList |> List.map snd |> List.exactlyOne
        let req: AccountAdmissionRequest =
            { Actor = "WQ11 Fake-Only Executor"
              LaunchId = Guid.NewGuid()
              AdmissionOperationId = Guid.NewGuid()
              AbortOperationId = Guid.NewGuid()
              ReleaseOperationId = Guid.NewGuid()
              RoleId = "Lead"; RoleVersion = 1 }
        let originalId = Guid.NewGuid()
        let snap = LedgerStore.fetch pb |> ok
        let view = WorkQueueRoleAuthority.loadAt pb snap |> ok
        let origin, _ = WorkQueueGitLedger.readOriginAt pb snap work.Id |> ok |> Option.get
        let authority =
            AccountAdmissionAdapter.projectAuthority pb
            |> AccountCapacityLedger.projectAuthoritySha256
        let pp: PlannerProject =
            { ProjectId = projectId.ToString("D"); LedgerId = ledgerId.ToString("D")
              LedgerEpoch = 1; AuthoritySha256 = authority
              Head = snap.Commit; Version = view.Project.Version
              CatalogRevision = view.Catalog.Revision
              CatalogBlobOid = view.Proof.CatalogBlobOid
              CoverageComplete = true }
        let pr: PlannerRole =
            { ProjectId = pp.ProjectId; ProjectHead = pp.Head
              CatalogBlobOid = view.Proof.CatalogBlobOid
              RoleId = req.RoleId; Version = req.RoleVersion
              Enabled = true; Queues = set ["planning"] }
        let selected: PlannerCandidate =
            { ProjectId = pp.ProjectId; ProjectHead = snap.Commit
              ProjectVersion = view.Project.Version
              WorkId = work.Id; WorkGeneration = work.StageGeneration
              QueueId = work.QueueId; AvailableAt = work.AvailableAt
              AvailableSequence = work.AvailableSequence
              BlocksOtherWork = false
              RoleId = req.RoleId; RoleVersion = req.RoleVersion
              CatalogBlobOid = view.Proof.CatalogBlobOid
              Source =
                { Head = origin.SourceHead
                  OperationId = origin.SourceOperationId
                  RequestHash = origin.SourceRequestHash
                  SourceRef = if origin.SourceRef = "" then None else Some origin.SourceRef
                  SourceCommit = if origin.SourceCommit = "" then None else Some origin.SourceCommit }
              LaunchId = req.LaunchId.ToString("D")
              OriginalBrowserOperationId = originalId.ToString("D") }
        let actualAccount = AccountCapacityLedger.fetch ab |> ok
        let simulatedAccount =
            { actualAccount with State = { actualAccount.State with MigrationPhase = "Activated" } }
        let ports: FakeOnlyCrossProjectPorts =
            { ReadAccount = fun () -> Ok simulatedAccount
              ReadProjects = fun () -> Ok { CompleteProjectSet = true; Projects = [pp] }
              ReadRoles = fun () -> Ok [pr]
              ReadCandidates = fun () -> Ok [selected]
              ReadExistingLaunches = fun () -> Ok [] }
        let plan = WorkQueueCrossProjectPlanner.planFakeOnly ports
        Assert.That(plan.Decision, Is.EqualTo(PlannerDecision.WouldLaunch selected))
        let journalRoot = Path.Combine(root, "originals")
        Directory.CreateDirectory journalRoot |> ignore
        OriginalBrowserJournal.provisionTrustedHostRoot journalRoot |> ok |> ignore
        let dispatchId, connectId = Guid.NewGuid(), Guid.NewGuid()
        let fake (intent: OriginalBrowserIntent) =
            FakeOriginalReply.Accepted
                { LaunchId = intent.LaunchId
                  WorkId = intent.WorkId; Receipt = "read-only fake receipt"
                  SessionKey = "native-fixture-session" }
        let connected =
            WorkQueueVerifiedDispatchAdapter.dispatchFakeOnly true plan aa pa
                journalRoot selected req dispatchId connectId fake
        Assert.That(connected.Phase, Is.EqualTo FakeDispatchPhase.Registered, connected.Detail)
        let _, admitted = WorkQueueGitLedger.load pb |> ok
        let launch = admitted.Launches.[selected.LaunchId]
        let uuid, sessionKey =
            match launch.State with
            | LaunchState.Registered (uuid, sessionKey) -> uuid, sessionKey
            | other -> failwithf "No registered original execution: %A" other
        let lease = launch.Lease |> Option.get
        let sha = String.replicate 64 "a"
        let trusted: INativeWorkSessionJournal =
            { new INativeWorkSessionJournal with
                member _.ReadAuthenticatedSession(id) =
                    if id = selected.LaunchId then
                        Ok { AgentUuid = uuid; SessionKey = sessionKey
                             LeaseGeneration = lease.Generation
                             OriginalOperationId = selected.OriginalBrowserOperationId }
                    else Error "foreign execution"
                member _.VerifyTerminalEvidence(id, session, evidence) =
                    if id = selected.LaunchId &&
                       session.AgentUuid = uuid &&
                       session.SessionKey = sessionKey &&
                       evidence = sha then Ok ()
                    else Error "terminal receipt not authenticated" }
        let bound project account journal : NativeLifecycleBinding =
            { Project = project; Account = account
              DisposableAuthority = scratchAuthority
              OriginalJournalRoot = journal
              Selected = selected; Admission = req
              DispatchOperationId = dispatchId
              FirstConnectOperationId = connectId
              SessionJournal = trusted }
        action pa pb aa ab selected req bound sha
        for checkout in [pA; pB; aA; aB] do
            Assert.That(git checkout ["status"; "--porcelain"], Is.Empty,
                        "No disposable source checkout may be dirtied by ledger writes.")
    finally
        if Directory.Exists root then Directory.Delete(root, true)

let private actor = "operator:" + String.replicate 64 "a"

[<TestFixture>]
type WorkQueueNativeLifecycleCasTests() =
    [<Test>]
    member _.TwoCloneSingleClaimOriginalReplayAndEvidenceBoundCheckpointTerminal() =
        fixture (fun pa pb aa ab selected req bound sha ->
            let op = Guid.NewGuid()
            let original = WorkQueueGitLedger.load pa |> ok |> snd
            let v = original.Version
            let bA = bound pa aa ""
            let disabled =
                WorkQueueNativeLifecycleCas.transitionDisposable false bA actor op v (Claim 1)
            match disabled with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Default-off must refuse: %A" other)
            let gitBefore = (LedgerStore.fetch pa |> ok).Commit
            Assert.That((LedgerStore.fetch pb |> ok).Commit, Is.EqualTo gitBefore)

            let enabledA = bound pa aa (Path.GetDirectoryName pa.RepositoryPath |> fun d -> Path.Combine(d, "originals"))
            let enabledB = bound pb ab enabledA.OriginalJournalRoot
            let claimed =
                WorkQueueNativeLifecycleCas.transitionDisposable true enabledA actor op v (Claim 1)
                |> ok
            Assert.That(claimed.Status, Is.EqualTo "Applied")
            Assert.That(claimed.Applied, Is.True)
            let newer = WorkQueueGitLedger.load pb |> ok |> snd
            Assert.That(newer.Works.[selected.WorkId].State,
                        Is.EqualTo (WorkState.Claimed selected.LaunchId))
            Assert.That(newer.Works.[selected.WorkId].ClaimGeneration, Is.EqualTo 1)
            Assert.That(newer.Launches.[selected.LaunchId].PrimaryWorkId, Is.EqualTo(Some selected.WorkId))
            let loser =
                WorkQueueNativeLifecycleCas.transitionDisposable true enabledB actor
                    (Guid.NewGuid()) v (Claim 1)
            match loser with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Stale clone must lose Git CAS: %A" other)
            let replay =
                WorkQueueNativeLifecycleCas.transitionDisposable true enabledB actor op v (Claim 1)
                |> ok
            Assert.That(replay.Status, Is.EqualTo "IdempotentReplay")
            match WorkQueueNativeLifecycleCas.transitionDisposable true enabledB actor
                    op v (Claim 2) with
            | Error OperationIdReused -> ()
            | other ->
                Assert.Fail(sprintf "Altered claimed generation under original UUID must collide: %A" other)

            let checkpointOp = Guid.NewGuid()
            let checkpointVersion = (WorkQueueGitLedger.load pa |> ok |> snd).Version
            let checkpoint =
                WorkQueueNativeLifecycleCas.transitionDisposable true enabledB actor
                    checkpointOp checkpointVersion (Checkpoint(1, sha)) |> ok
            Assert.That(checkpoint.Applied, Is.True)
            let replayCheckpoint =
                WorkQueueNativeLifecycleCas.transitionDisposable true enabledA actor
                    checkpointOp checkpointVersion (Checkpoint(1, sha)) |> ok
            Assert.That(replayCheckpoint.Status, Is.EqualTo "IdempotentReplay")
            match WorkQueueNativeLifecycleCas.transitionDisposable true enabledA actor
                    checkpointOp checkpointVersion (Checkpoint(1, String.replicate 64 "b")) with
            | Error OperationIdReused -> ()
            | other -> Assert.Fail(sprintf "Foreign SHA replay must be rejected: %A" other)

            let completeOp = Guid.NewGuid()
            let completeVersion = (WorkQueueGitLedger.load pb |> ok |> snd).Version
            let withheldTerminal: INativeWorkSessionJournal =
                { new INativeWorkSessionJournal with
                    member _.ReadAuthenticatedSession id =
                        enabledB.SessionJournal.ReadAuthenticatedSession id
                    member _.VerifyTerminalEvidence(_, _, _) =
                        Error "terminal evidence not independently recorded" }
            let withMissingTerminal = { enabledB with SessionJournal = withheldTerminal }
            let beforeTerminal = (LedgerStore.fetch pb |> ok).Commit
            match WorkQueueNativeLifecycleCas.transitionDisposable true withMissingTerminal actor
                    (Guid.NewGuid()) completeVersion (Complete(1, sha)) with
            | Error(InvalidBinding why) when why.Contains "terminal evidence" -> ()
            | other -> Assert.Fail(sprintf "Unverified terminal must not commit: %A" other)
            Assert.That((LedgerStore.fetch pa |> ok).Commit, Is.EqualTo beforeTerminal)
            Assert.That((WorkQueueGitLedger.load pb |> ok |> snd).Works.[selected.WorkId].State,
                        Is.EqualTo (WorkState.Claimed selected.LaunchId))
            let completed =
                WorkQueueNativeLifecycleCas.transitionDisposable true enabledB actor
                    completeOp completeVersion (Complete(1, sha)) |> ok
            Assert.That(completed.Applied, Is.True)
            let again =
                WorkQueueNativeLifecycleCas.transitionDisposable true enabledA actor
                    completeOp completeVersion (Complete(1, sha)) |> ok
            Assert.That(again.Status, Is.EqualTo "IdempotentReplay")
            let head, completedState = WorkQueueGitLedger.load pa |> ok
            Assert.That(completedState.Works.[selected.WorkId].State, Is.EqualTo WorkState.Completed)
            Assert.That(completedState.Works.[selected.WorkId].Domain,
                        Is.EqualTo DomainState.Undecided)
            Assert.That(completedState.Launches.[selected.LaunchId].State,
                        Is.EqualTo(LaunchState.TerminalPendingRelease
                                       ("terminal:" + completeOp.ToString("D"))))
            let terminal = WorkQueueGitLedger.readTerminalAt pa head selected.LaunchId |> ok
            Assert.That(terminal.IsSome, Is.True)
            let terminalReceipt = terminal.Value |> fst
            Assert.That(terminalReceipt.TerminalOperationId,
                        Is.EqualTo(completeOp.ToString("D")))
            Assert.That(AccountCapacityLedger.occupied (AccountCapacityLedger.fetch aa |> ok).State,
                        Is.EqualTo 1, "WQ07 release must be separately verified, never eager.")
            let view = WorkQueueToolFacade.readWithBinding pb "List" "Terminal" "" |> ok
            Assert.That(view.Work |> List.exists (fun row -> row.WorkId = selected.WorkId),
                        Is.True)
            Assert.That(view.Work |> List.forall (fun row -> not row.ClaimEnabled), Is.True))

    [<Test>]
    member _.ForeignRoleSessionAndMissingHostProofFailClosedWithoutGitWrites() =
        fixture (fun pa pb aa ab selected req bound sha ->
            let before = LedgerStore.fetch pa |> ok
            let v = (WorkQueueGitLedger.load pa |> ok |> snd).Version
            let journal = Path.Combine(Path.GetDirectoryName pa.RepositoryPath, "originals")
            let valid = bound pb ab journal
            let malformed = { valid with OriginalJournalRoot = "" }
            let foreign = { valid with Selected = { selected with RoleVersion = 27 } }
            let foreignAdmission = { valid with Admission = { req with AdmissionOperationId = Guid.NewGuid() } }
            let foreignPort: INativeWorkSessionJournal =
                { new INativeWorkSessionJournal with
                    member _.ReadAuthenticatedSession _ =
                        Ok { AgentUuid = "foreign"; SessionKey = "stolen"
                             LeaseGeneration = 1
                             OriginalOperationId = selected.OriginalBrowserOperationId }
                    member _.VerifyTerminalEvidence(_, _, _) = Ok () }
            let foreignSession = { valid with SessionJournal = foreignPort }
            let noTrustedTerminal: INativeWorkSessionJournal =
                { new INativeWorkSessionJournal with
                    member _.ReadAuthenticatedSession id = valid.SessionJournal.ReadAuthenticatedSession id
                    member _.VerifyTerminalEvidence(_, _, _) = Error "missing original terminal source evidence" }
            let cases =
                [malformed, Claim 1
                 foreign, Claim 1
                 foreignAdmission, Claim 1
                 foreignSession, Claim 1]
            for binding, action in cases do
                match WorkQueueNativeLifecycleCas.transitionDisposable true binding actor
                        (Guid.NewGuid()) v action with
                | Error _ -> ()
                | Ok applied -> Assert.Fail(sprintf "Unverified Work claim was applied: %A" applied)
            Assert.That((LedgerStore.fetch pb |> ok).Commit, Is.EqualTo before.Commit)
            let boundNoTerminal = { valid with SessionJournal = noTrustedTerminal }
            // A terminal journal could not be queried without a successful
            // original claim. Even future completion may not infer terminal.
            match WorkQueueNativeLifecycleCas.transitionDisposable true boundNoTerminal actor
                    (Guid.NewGuid()) v (Complete(1, sha)) with
            | Error _ -> ()
            | Ok applied -> Assert.Fail(sprintf "Unverified terminal was applied: %A" applied)
            Assert.That((LedgerStore.fetch pa |> ok).Commit, Is.EqualTo before.Commit))

    [<Test>]
    member _.FrozenProductionLikeBindingIsRejectedBeforeAnyGitMutation() =
        fixture (fun pa pb aa ab selected req bound sha ->
            let root = Path.GetDirectoryName pa.RepositoryPath
            // A fresh mirror of the fully initialized frozen ledger preserves
            // the Work, role, original host and operation receipts. Only the
            // remote authority differs: this is not the issued scratch ledger.
            let mirror = Path.Combine(root, "production-like.git")
            let checkout = Path.Combine(root, "production-like-client")
            git root ["clone"; "--mirror"; pa.ApprovedPushUrl; mirror] |> ignore
            git root ["clone"; mirror; checkout] |> ignore
            let production =
                { pa with RepositoryPath = checkout
                          ApprovedFetchUrl = mirror; ApprovedPushUrl = mirror }
            let before = LedgerStore.fetch production |> ok
            let _, state = WorkQueueGitLedger.load production |> ok
            Assert.That(state.Migration, Is.EqualTo(MigrationEpoch.Frozen))
            Assert.That(state.Works.[selected.WorkId].State, Is.EqualTo WorkState.Available)
            Assert.That(state.Launches.[selected.LaunchId].Credit,
                        Is.EqualTo(Some selected.WorkId))
            // No new authority can be issued after the source project and
            // account ledgers have entered their existing Frozen state.
            match WorkQueueNativeLifecycleCas.authorizeFreshScratch pa aa with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Already initialized ledger minted scratch authority: %A" other)
            let journal = Path.Combine(root, "originals")
            let otherwiseValid = bound production aa journal
            let v = state.Version
            match WorkQueueNativeLifecycleCas.transitionDisposable true otherwiseValid actor
                    (Guid.NewGuid()) v (Claim 1) with
            | Error(InvalidBinding reason) when reason.Contains "scratch authority" -> ()
            | other -> Assert.Fail(sprintf "Real Frozen binding must fail before CAS: %A" other)
            Assert.That((LedgerStore.fetch production |> ok).Commit, Is.EqualTo before.Commit)
            // Even the original approved scratch remote is protected when
            // caller has no opaque authority, despite all other evidence.
            let valid = bound pa aa journal
            let noAuthority =
                { valid with DisposableAuthority = Unchecked.defaultof<_> }
            let originalBefore = (LedgerStore.fetch pa |> ok).Commit
            match WorkQueueNativeLifecycleCas.transitionDisposable true noAuthority actor
                    (Guid.NewGuid()) v (Claim 1) with
            | Error(InvalidBinding reason) when reason.Contains "scratch authority" -> ()
            | other -> Assert.Fail(sprintf "Missing authority must fail closed: %A" other)
            Assert.That((LedgerStore.fetch pa |> ok).Commit, Is.EqualTo originalBefore))
