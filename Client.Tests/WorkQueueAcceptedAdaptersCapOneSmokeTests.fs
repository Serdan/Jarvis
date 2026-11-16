module Client.Tests.WorkQueueAcceptedAdaptersCapOneSmokeTests

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
open Client.WorkQueuePracticalAdmission
open Client.WorkQueueCrossProjectPlanner
open Client.OriginalBrowserJournal
open Client.AccountCapacityLedger
open Client.AccountAdmissionAdapter

// WQ11: default-off, fake-provider-only cross-project adapter acceptance.
// No Scheduler registration, production browser transport, timer or live host.
let private ok = function
    | Ok value -> value
    | Error error -> failwithf "WQ11 accepted adapters: %A" error

let private git directory arguments =
    use runner = new Process()
    runner.StartInfo.FileName <- "git"
    runner.StartInfo.WorkingDirectory <- directory
    runner.StartInfo.UseShellExecute <- false
    runner.StartInfo.RedirectStandardOutput <- true
    runner.StartInfo.RedirectStandardError <- true
    for argument in arguments do runner.StartInfo.ArgumentList.Add(argument)
    Assert.That(runner.Start(), Is.True)
    let output = runner.StandardOutput.ReadToEnd()
    let error = runner.StandardError.ReadToEnd()
    Assert.That(runner.WaitForExit(30000), Is.True, sprintf "git %A timed out" arguments)
    Assert.That(runner.ExitCode, Is.EqualTo(0), error)
    output.Trim()

let private withAuthorities action =
    let root = Path.Combine(Path.GetTempPath(), "wq11-accepted-capone-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    let checkouts = ResizeArray<string>()
    try
        let pair label =
            let remote = Path.Combine(root, label + ".git")
            let first = Path.Combine(root, label + "-first")
            let second = Path.Combine(root, label + "-second")
            git root ["init"; "--bare"; "--initial-branch=main"; remote] |> ignore
            git root ["clone"; remote; first] |> ignore
            File.WriteAllText(Path.Combine(first, "fixture.txt"), "WQ11 isolated " + label)
            git first ["add"; "fixture.txt"] |> ignore
            git first ["-c"; "user.name=WQ11 Fixture"; "-c"; "user.email=wq11@example.test"
                       "commit"; "-m"; "isolated source"] |> ignore
            git first ["push"; "-u"; "origin"; "HEAD"] |> ignore
            git root ["clone"; remote; second] |> ignore
            checkouts.Add(first)
            checkouts.Add(second)
            let projectId, ledgerId = Guid.NewGuid(), Guid.NewGuid()
            let binding checkout : LedgerBinding =
                { RepositoryPath = checkout; RemoteName = "origin"
                  ApprovedFetchUrl = remote; ApprovedPushUrl = remote
                  ProjectId = projectId; LedgerId = ledgerId; Epoch = 1 }
            binding first, binding second
        let a1, a2 = pair "project-a"
        let b1, b2 = pair "project-b"
        let accountSource1, accountSource2 = pair "account"
        let accountId, accountLedger = Guid.NewGuid(), Guid.NewGuid()
        let account (source: LedgerBinding) : AccountBinding =
            { RepositoryPath = source.RepositoryPath; RemoteName = source.RemoteName
              ApprovedFetchUrl = source.ApprovedFetchUrl
              ApprovedPushUrl = source.ApprovedPushUrl
              AccountId = accountId; LedgerId = accountLedger; Epoch = 1 }
        let account1, account2 = account accountSource1, account accountSource2
        for first, second in [a1, a2; b1, b2] do
            LedgerStore.initialize first true |> ok |> ignore
            LedgerStore.fetch second |> ok |> ignore
            WorkQueueGitLedger.initialize first "Fixture" (Guid.NewGuid())
                (Map.ofList ["Lead", set ["planning"; "role:Lead"]]) |> ok |> ignore
            ReviewWorkflow.registerAgent first (Guid.NewGuid()) "Lead" |> ok |> ignore
            WorkQueueRoleAuthority.initializeCatalog first "Lead" (Guid.NewGuid()) |> ok |> ignore
            let role: RoleConfiguration =
                { RoleId = "Lead"; Version = 1; Enabled = true
                  Instructions = "WQ11 disposable planning only"
                  QueueSubscriptions = set ["planning"] }
            WorkQueueRoleAuthority.configure first "Lead" (Guid.NewGuid()) 0 0 role |> ok |> ignore
        AccountCapacityLedger.initialize account1 1 |> ok |> ignore
        let journalRoot = Path.Combine(root, "original-browser-host")
        Directory.CreateDirectory(journalRoot) |> ignore
        OriginalBrowserJournal.provisionTrustedHostRoot journalRoot |> ok |> ignore
        action (a1, a2) (b1, b2) (account1, account2) journalRoot
        for checkout in checkouts do
            Assert.That(git checkout ["status"; "--porcelain"], Is.Empty,
                        "WQ11 disposable source checkout was dirtied")
    finally
        if Directory.Exists(root) then Directory.Delete(root, true)

let private produce source receiver taskId =
    let sourceOp, publishOp = Guid.NewGuid(), Guid.NewGuid()
    let created = ReviewWorkflow.createTask source sourceOp taskId ("WQ11 " + taskId) |> ok
    let published = WorkQueueReceiptProducer.produce receiver "Lead" publishOp sourceOp |> ok
    Assert.That(published.AlreadyApplied, Is.False)
    let snapshot, project = WorkQueueGitLedger.load receiver |> ok
    let work =
        project.Works |> Map.toSeq |> Seq.map snd
        |> Seq.filter (fun stage -> stage.SubjectId = taskId)
        |> Seq.exactlyOne
    let origin, oid =
        WorkQueueGitLedger.readOriginAt receiver snapshot work.Id |> ok |> Option.get
    Assert.That(origin.SourceOperationId, Is.EqualTo(sourceOp.ToString("D")))
    Assert.That(origin.SourceRequestHash, Is.EqualTo(created.Operation.RequestHash))
    work, oid

let private request (): AccountAdmissionRequest =
    { Actor = "WQ11 Fake-Only Accepted Adapters"
      LaunchId = Guid.NewGuid()
      AdmissionOperationId = Guid.NewGuid()
      AbortOperationId = Guid.NewGuid()
      ReleaseOperationId = Guid.NewGuid()
      RoleId = "Lead"; RoleVersion = 1 }

let private mutate binding operation change =
    let _, state = WorkQueueGitLedger.load binding |> ok
    WorkQueueGitLedger.executeInertFixture binding "WQ11 Fake-Only"
        operation state.Version change |> ok

let private pinned binding =
    let snapshot = LedgerStore.fetch binding |> ok
    let view = WorkQueueRoleAuthority.loadAt binding snapshot |> ok
    let authority = AccountAdmissionAdapter.projectAuthority binding
                    |> AccountCapacityLedger.projectAuthoritySha256
    let project: PlannerProject =
        { ProjectId = binding.ProjectId.ToString("D")
          LedgerId = binding.LedgerId.ToString("D")
          LedgerEpoch = binding.Epoch; AuthoritySha256 = authority
          Head = snapshot.Commit; Version = view.Project.Version
          CatalogRevision = view.Catalog.Revision
          CatalogBlobOid = view.Proof.CatalogBlobOid
          CoverageComplete = view.Project.QueueCovered && view.Project.GraphCovered
                             && view.Project.MessageCovered && view.Project.SourceCovered }
    let version = view.Catalog.Current.["Lead"]
    let definition = view.Catalog.Versions.["Lead"].[version]
    let role: PlannerRole =
        { ProjectId = project.ProjectId; ProjectHead = project.Head
          CatalogBlobOid = project.CatalogBlobOid; RoleId = "Lead"
          Version = version; Enabled = definition.Enabled
          Queues = definition.QueueSubscriptions }
    project, role, snapshot, view

let private candidate binding (request: AccountAdmissionRequest) (originalId: Guid) workId =
    let project, _, snapshot, view = pinned binding
    let work = view.Project.Works.[workId]
    let origin, _ = WorkQueueGitLedger.readOriginAt binding snapshot work.Id |> ok |> Option.get
    let opt value = if String.IsNullOrWhiteSpace value then None else Some value
    let result: PlannerCandidate =
        { ProjectId = project.ProjectId; ProjectHead = project.Head
          ProjectVersion = project.Version
          WorkId = work.Id; WorkGeneration = work.StageGeneration
          QueueId = work.QueueId; AvailableAt = work.AvailableAt
          AvailableSequence = work.AvailableSequence
          BlocksOtherWork =
              view.Project.Works
              |> Map.toSeq |> Seq.exists (fun (_, other) -> other.Dependencies.Contains work.Id)
          RoleId = request.RoleId; RoleVersion = request.RoleVersion
          CatalogBlobOid = project.CatalogBlobOid
          Source =
              { Head = origin.SourceHead; OperationId = origin.SourceOperationId
                RequestHash = origin.SourceRequestHash
                SourceRef = opt origin.SourceRef; SourceCommit = opt origin.SourceCommit }
          LaunchId = request.LaunchId.ToString("D")
          OriginalBrowserOperationId = originalId.ToString("D") }
    result

[<TestFixture>]
type WorkQueueAcceptedAdaptersCapOneSmokeTests() =
    [<Test>]
    member _.TwoRealProjectsOneGlobalCapPinnedAdmissionAndOneShotOriginal() =
        withAuthorities (fun (a1, a2) (b1, b2) (account1, account2) journalRoot ->
            let aWork, aOid = produce a1 a2 "wq11-project-a"
            let bWork, bOid = produce b1 b2 "wq11-project-b"
            // A concrete downstream dependency makes B blocking demand, even
            // though its source task was published later than A's demand.
            let downstream: WorkStage =
                { Id = "downstream-" + Guid.NewGuid().ToString("N")
                  ProjectId = b1.ProjectId.ToString("D")
                  SubjectId = "disposable-downstream"
                  Kind = WorkKind.FrozenReview; QueueId = "review"
                  StageGeneration = 1; State = WorkState.Blocked
                  Domain = DomainState.Undecided; Gate = GateState.Clear
                  ClaimGeneration = 0; Dependencies = Set.singleton bWork.Id
                  WaivedDependencies = Map.empty
                  AvailableAt = bWork.AvailableAt + 1L
                  AvailableSequence = bWork.AvailableSequence + 1L
                  SourceLineage = None; Push = None; IntegrationAuthorized = false }
            mutate b1 (Guid.NewGuid()) (PublishStage downstream) |> ignore
            let reqA, reqB = request (), request ()
            let originalA, originalB = Guid.NewGuid(), Guid.NewGuid()

            let originals () = OriginalBrowserJournal.openExisting journalRoot |> ok
            let observe binding =
                let view = WorkQueueRoleAuthority.load binding |> ok
                view.Project.Launches |> Map.toList |> List.map (fun (_, launch) ->
                    let recorded = OriginalBrowserJournal.findByLaunch (originals ()) launch.Id |> ok
                    let phase =
                        match launch.State with
                        | LaunchState.ReservedUnregistered -> PlannerLaunchPhase.ReservedUnregistered
                        | LaunchState.SubmittedUnregistered _ -> PlannerLaunchPhase.SubmittedUnregistered
                        | LaunchState.Registered _ -> PlannerLaunchPhase.Registered
                        | LaunchState.DemandInvalidatedPendingTerminal _ ->
                            PlannerLaunchPhase.DemandInvalidatedPendingTerminal
                        | LaunchState.TerminalPendingRelease _ -> PlannerLaunchPhase.TerminalPendingRelease
                    let browser =
                        match recorded with
                        | None -> PlannerBrowserEvidence.NotRecorded
                        | Some entry ->
                            match entry.Outcome with
                            | None -> PlannerBrowserEvidence.Unknown "original marker without outcome"
                            | Some (OriginalBrowserOutcome.Accepted receipt) -> PlannerBrowserEvidence.Accepted receipt
                            | Some (OriginalBrowserOutcome.DefinitiveNoEffect reason) ->
                                PlannerBrowserEvidence.DefinitiveNoEffect reason
                            | Some (OriginalBrowserOutcome.Unknown reason) -> PlannerBrowserEvidence.Unknown reason
                    { LaunchId = launch.Id; ProjectId = launch.ProjectId
                      RoleId = launch.RoleId; RoleVersion = launch.RoleVersion
                      AccountAdmissionOperationId = launch.AccountTokenId; Phase = phase
                      OriginalBrowserOperationId = recorded |> Option.map (fun e -> e.Intent.OperationId)
                      OriginalBrowser = browser }: PlannerExistingLaunch)
            let ports candidates readLaunches =
                let pa, ra, _, _ = pinned a2
                let pb, rb, _, _ = pinned b2
                let realAccount = AccountCapacityLedger.fetch account2 |> ok
                // The real WQ07 fixture initializes LegacyActive. WQ08 correctly
                // holds such an unmigrated authority. Only this injected,
                // fake-only planner view models a hypothetical Activated phase;
                // WQ04 admissions and terminal releases use the real Git state.
                let account =
                    { realAccount with
                        State = { realAccount.State with MigrationPhase = "Activated" } }
                { ReadAccount = fun () -> Ok account
                  ReadProjects = fun () ->
                      Ok { CompleteProjectSet = true; Projects = [pb; pa] }
                  ReadRoles = fun () -> Ok [rb; ra]
                  ReadCandidates = fun () -> Ok candidates
                  ReadExistingLaunches = readLaunches }: FakeOnlyCrossProjectPorts
            let report candidates =
                WorkQueueCrossProjectPlanner.planFakeOnly
                    (ports candidates (fun () -> Ok(observe a2 @ observe b2)))
            let aCandidate = candidate a2 reqA originalA aWork.Id
            let bCandidate = candidate b2 reqB originalB bWork.Id
            Assert.That(aCandidate.BlocksOtherWork, Is.False)
            Assert.That(bCandidate.BlocksOtherWork, Is.True)
            let realAccount = AccountCapacityLedger.fetch account2 |> ok
            Assert.That(realAccount.State.MigrationPhase, Is.EqualTo("LegacyActive"))
            let unmodifiedPorts =
                { ports [aCandidate; bCandidate] (fun () -> Ok []) with
                    ReadAccount = fun () -> Ok realAccount }
            let unmigrated = WorkQueueCrossProjectPlanner.planFakeOnly unmodifiedPorts
            Assert.That(unmigrated.Decision, Is.EqualTo(PlannerDecision.NeedsAttention))
            Assert.That(unmigrated.Issues |> List.exists (fun e -> e.Code = "AccountNotActivated"), Is.True)
            let initial = report [aCandidate; bCandidate]
            Assert.That(initial.CanDispatch, Is.False)
            Assert.That(initial.Issues, Is.Empty)
            match initial.Decision with
            | PlannerDecision.WouldLaunch selected ->
                Assert.That(selected, Is.EqualTo(bCandidate))
            | other -> Assert.Fail(sprintf "Two-project blocking-first choice failed: %A" other)
            // Wrong pinned role and foreign/doubled one-shot UUIDs never become
            // an executable candidate, even with spare global capacity.
            let wrongRole = report [aCandidate; { bCandidate with RoleVersion = 9 }]
            Assert.That(wrongRole.Decision, Is.EqualTo(PlannerDecision.NeedsAttention))
            Assert.That(wrongRole.Issues |> List.exists (fun e -> e.Code = "RoleVersionMismatch"), Is.True)
            let duplicateBrowser = report [aCandidate; { bCandidate with OriginalBrowserOperationId = originalA.ToString("D") }]
            Assert.That(duplicateBrowser.Decision, Is.EqualTo(PlannerDecision.NeedsAttention))
            Assert.That(duplicateBrowser.Issues |> List.exists (fun e -> e.Code = "DuplicateBrowserIntent"), Is.True)

            let demandB: PracticalAdmissionDemand = { WorkId = bWork.Id; OriginBlobOid = bOid }
            let demandA: PracticalAdmissionDemand = { WorkId = aWork.Id; OriginBlobOid = aOid }
            match WorkQueuePracticalAdmission.admitExplicit true account1 b1 reqB demandB with
            | ProjectAdmitted _ -> ()
            | other -> Assert.Fail(sprintf "WQ04 genuine role/source admission failed: %A" other)
            let afterAdmission = WorkQueueRoleAuthority.load b2 |> ok
            let launchIdB = reqB.LaunchId.ToString("D")
            Assert.That(afterAdmission.Project.Launches.[launchIdB].Credit, Is.EqualTo(Some bWork.Id))
            Assert.That(afterAdmission.Project.Launches.[launchIdB].AccountTokenId,
                        Is.EqualTo(reqB.AdmissionOperationId.ToString("D")))
            let admissionHead = LedgerStore.fetch b2 |> ok
            let admissionReceipt, _ =
                WorkQueueGitLedger.readAdmissionAt b2 admissionHead launchIdB
                |> ok |> Option.get
            Assert.That(admissionReceipt.Kind, Is.EqualTo("AdmissionWon"))
            Assert.That(admissionReceipt.AdmissionOperationId,
                        Is.EqualTo(reqB.AdmissionOperationId.ToString("D")))
            let roleWitness, _ =
                LedgerStore.readAt b2 admissionHead (WorkQueueRoleAuthority.roleAdmissionPath launchIdB) |> ok
            Assert.That(roleWitness.IsSome, Is.True)
            let admissionOperation =
                LedgerStore.getOperationAt b2 admissionHead reqB.AdmissionOperationId
                |> ok |> Option.get
            let expectedPaths =
                [ WorkQueueGitLedger.RecordPath; WorkQueueGitLedger.admissionPath launchIdB
                  WorkQueueRoleAuthority.roleAdmissionPath launchIdB ]
            Assert.That((admissionOperation.Paths |> String.concat "|"),
                        Is.EqualTo(expectedPaths |> String.concat "|"))
            let held () = AccountCapacityLedger.occupied (AccountCapacityLedger.fetch account2 |> ok).State
            Assert.That(held(), Is.EqualTo(1))
            match WorkQueuePracticalAdmission.admitExplicit true account2 b2 reqB demandB with
            | ProjectAdmitted _ -> ()
            | other -> Assert.Fail(sprintf "Exact WQ04 replay rejected: %A" other)
            Assert.That((AccountCapacityLedger.fetch account1 |> ok).State.Tokens.Length, Is.EqualTo(1))
            match WorkQueuePracticalAdmission.admitExplicit true account1 a1 reqA demandA with
            | GlobalReservationRejected(AccountCapacityFull(1, 1)) -> ()
            | other -> Assert.Fail(sprintf "Cross-project cap-one admission was not blocked: %A" other)
            Assert.That(held(), Is.EqualTo(1))
            let missingJournal = report [aCandidate]
            Assert.That(missingJournal.Decision, Is.EqualTo(PlannerDecision.Unknown))
            Assert.That(missingJournal.Issues |> List.exists (fun e -> e.Code = "OriginalBrowserJournalMissing"), Is.True)
            let unprovisioned = Path.Combine(journalRoot, "not-provisioned")
            match OriginalBrowserJournal.openExisting unprovisioned with
            | Error _ -> ()
            | Ok _ -> Assert.Fail("Missing WQ03 journal silently provisioned a new store")

            let originalIntent (selected: PlannerCandidate) (account: AccountSnapshot) projectHead =
                { OperationId = selected.OriginalBrowserOperationId
                  LaunchId = selected.LaunchId
                  ProjectId = selected.ProjectId
                  WorkId = selected.WorkId
                  WorkGeneration = selected.WorkGeneration
                  RoleId = selected.RoleId; RoleVersion = selected.RoleVersion
                  RequestFingerprint = selected.Source.RequestHash
                  Provenance =
                      { AccountHead = account.Head; ProjectHead = projectHead
                        SourceRef = selected.Source.SourceRef
                        SourceCommit = selected.Source.SourceCommit } }: OriginalBrowserIntent
            let journalIntentB =
                originalIntent bCandidate (AccountCapacityLedger.fetch account2 |> ok)
                    (LedgerStore.fetch b2 |> ok).Commit
            let mutable providerCalls = 0
            let sendOnce store intent callback =
                match OriginalBrowserJournal.tryMarkOriginal store intent |> ok with
                | OriginalMarkResult.Existing _ -> None
                | OriginalMarkResult.Marked ->
                    // Force a separate open and read before the fake callback.
                    Assert.That((OriginalBrowserJournal.findOriginal (originals ()) intent.OperationId |> ok).IsSome, Is.True)
                    providerCalls <- providerCalls + 1
                    callback intent
            let fakeProvider (intent: OriginalBrowserIntent) =
                Assert.That(intent.LaunchId, Is.EqualTo(launchIdB))
                Assert.That(intent.WorkId, Is.EqualTo(bWork.Id))
                Some (intent.LaunchId, intent.WorkId, "disposable-session-" + Guid.NewGuid().ToString("N"))
            let result = sendOnce (originals ()) journalIntentB fakeProvider
            Assert.That(result.IsSome, Is.True)
            let session = result.Value |> fun (_, _, key) -> key
            OriginalBrowserJournal.recordOriginalOutcome (originals ()) journalIntentB
                (OriginalBrowserOutcome.Accepted "fake-only-provider-receipt") |> ok
            Assert.That((sendOnce (originals ()) journalIntentB fakeProvider).IsNone, Is.True)
            Assert.That(providerCalls, Is.EqualTo(1))
            let forged = { journalIntentB with LaunchId = reqA.LaunchId.ToString("D") }
            match OriginalBrowserJournal.tryMarkOriginal (originals ()) forged with
            | Error reason -> Assert.That(reason, Does.Contain("IdentityConflict"))
            | Ok _ -> Assert.Fail("Foreign launch UUID reused WQ03 original marker")

            let dispatchOp, connectOp = Guid.NewGuid(), Guid.NewGuid()
            mutate b2 dispatchOp (DispatchOnce(launchIdB, DispatchState.Submitted)) |> ignore
            let _, beforeConnect = WorkQueueGitLedger.load b1 |> ok
            // Session/launch/work identity came only from the fake callback.
            match result with
            | Some (id, work, sessionKey) when id = launchIdB && work = bWork.Id ->
                mutate b1 connectOp (InitializeAgent(id, sessionKey)) |> ignore
            | _ -> Assert.Fail("No matching fake first connection returned")
            let replay = WorkQueueGitLedger.executeInertFixture b2 "WQ11 Fake-Only"
                            connectOp beforeConnect.Version (InitializeAgent(launchIdB, session)) |> ok
            Assert.That(replay.AlreadyApplied, Is.True)
            Assert.That((WorkQueueRoleAuthority.load b2 |> ok).Project.Launches.Count, Is.EqualTo(1))
            mutate b1 (Guid.NewGuid()) (ClaimNext launchIdB) |> ignore
            let terminalOp = Guid.NewGuid()
            mutate b2 terminalOp (CompleteOrdinary(launchIdB, bWork.Id)) |> ignore
            let terminalHead, completed = WorkQueueGitLedger.load b1 |> ok
            Assert.That(completed.Works.[bWork.Id].State, Is.EqualTo(WorkState.Completed))
            Assert.That((WorkQueueGitLedger.readTerminalAt b1 terminalHead launchIdB |> ok).IsSome, Is.True)
            let locator: ProjectTerminalLocator =
                { ProjectId = b1.ProjectId; LaunchId = reqB.LaunchId
                  AdmissionOperationId = reqB.AdmissionOperationId
                  TerminalOperationId = terminalOp }
            let forgedTerminal = { locator with TerminalOperationId = Guid.NewGuid() }
            match AccountAdmissionAdapter.releaseTerminal account1 b1 "WQ11 Fake-Only"
                      (Guid.NewGuid()) forgedTerminal with
            | Error _ -> ()
            | Ok _ -> Assert.Fail("Unproven project terminal released the global cap")
            Assert.That(held(), Is.EqualTo(1))
            let released = AccountAdmissionAdapter.releaseTerminal account2 b2 "WQ11 Fake-Only"
                               reqB.ReleaseOperationId locator |> ok
            Assert.That(released.AlreadyApplied, Is.False)
            let releasedAgain = AccountAdmissionAdapter.releaseTerminal account1 b1 "WQ11 Fake-Only"
                                   reqB.ReleaseOperationId locator |> ok
            Assert.That(releasedAgain.AlreadyApplied, Is.True)
            Assert.That(held(), Is.Zero)

            let aFresh = candidate a2 reqA originalA aWork.Id
            let afterRelease = report [aFresh]
            match afterRelease.Decision with
            | PlannerDecision.WouldLaunch selected ->
                Assert.That(selected.WorkId, Is.EqualTo(aWork.Id))
            | other -> Assert.Fail(sprintf "Verified terminal did not restore project A demand: %A" other)
            match WorkQueuePracticalAdmission.admitExplicit true account1 a1 reqA demandA with
            | ProjectAdmitted _ -> ()
            | other -> Assert.Fail(sprintf "Other project's WQ04 demand failed after release: %A" other)
            Assert.That(held(), Is.EqualTo(1))
            let journalIntentA =
                originalIntent aFresh (AccountCapacityLedger.fetch account2 |> ok)
                    (LedgerStore.fetch a2 |> ok).Commit
            let unknownProvider (_: OriginalBrowserIntent) : (string * string * string) option = None
            let unknown = sendOnce (originals ()) journalIntentA unknownProvider
            Assert.That(unknown.IsNone, Is.True)
            OriginalBrowserJournal.recordOriginalOutcome (originals ()) journalIntentA
                (OriginalBrowserOutcome.Unknown "fake provider callback lost") |> ok
            let unknownOp = Guid.NewGuid()
            mutate a2 unknownOp
                (DispatchOnce(reqA.LaunchId.ToString("D"), DispatchState.OutcomeUnknown)) |> ignore
            Assert.That((sendOnce (originals ()) journalIntentA unknownProvider).IsNone, Is.True)
            Assert.That(providerCalls, Is.EqualTo(2))
            Assert.That((WorkQueueRoleAuthority.load a1 |> ok).Project.Launches.[reqA.LaunchId.ToString("D")].State,
                        Is.EqualTo(LaunchState.SubmittedUnregistered DispatchState.OutcomeUnknown))
            Assert.That(held(), Is.EqualTo(1))
            let blocked = report []
            Assert.That(blocked.Decision, Is.EqualTo(PlannerDecision.Unknown))
            Assert.That(blocked.Issues |> List.exists (fun e -> e.Code = "OriginalBrowserOutcomeUnknown"), Is.True)
            Assert.That(held(), Is.EqualTo(1)))
