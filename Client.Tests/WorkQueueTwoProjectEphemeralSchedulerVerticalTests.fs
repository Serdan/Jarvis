module Client.Tests.WorkQueueTwoProjectEphemeralSchedulerVerticalTests

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open Jarvis.WorkQueues
open Client.LedgerStore
open Client.AccountCapacityLedger
open Client.AccountAdmissionAdapter
open Client.WorkQueueGitLedger
open Client.WorkQueueRoleAuthority
open Client.ReviewWorkflow
open Client.WorkQueueReceiptProducer
open Client.WorkQueueDurableLaunchIntent
open Client.WorkQueueNativeCredentialDispatchAdapter
open Client.WorkQueueNativeLifecycleCas
open Client.WorkQueueIntegrationSourceAuthority
open Client.WorkQueueReactiveWake
open Client.WorkQueueSchedulerHostLoop
open Client.WorkQueueSchedulerHeadBinding
open Client.OriginalBrowserJournal
open Client.WorkQueueCrossProjectPlanner

// Test-only, manually ticked WQ11 composition. The in-memory Activated
// projection below is NEVER written to the original WQ07 account. Production
// remains NO-GO until an independently approved migration/host bridge exists.
let private success = function
    | Ok value -> value
    | Error error -> failwithf "Two-project native Scheduler fixture: %A" error

let private git directory args =
    use proc = new Process()
    proc.StartInfo.FileName <- "git"
    proc.StartInfo.WorkingDirectory <- directory
    proc.StartInfo.UseShellExecute <- false
    proc.StartInfo.RedirectStandardOutput <- true
    proc.StartInfo.RedirectStandardError <- true
    for argument in args do proc.StartInfo.ArgumentList.Add(argument)
    Assert.That(proc.Start(), Is.True)
    let output = proc.StandardOutput.ReadToEnd()
    let error = proc.StandardError.ReadToEnd()
    Assert.That(proc.WaitForExit(30000), Is.True)
    Assert.That(proc.ExitCode, Is.Zero, error)
    output.Trim()

type private EphemeralSchedulerFixture =
    { Priority: LedgerBinding * LedgerBinding
      Other: LedgerBinding * LedgerBinding
      Account: AccountBinding * AccountBinding
      Historical: (LedgerBinding * LedgerBinding * (string * string) list) list
      JournalRoot: string
      Scratch: NativeDisposableAuthority
      Observer: Observer
      Root: string }

let private assertHistoricalUnchanged (f: EphemeralSchedulerFixture) =
    for first, second, originals in f.Historical do
        let writer = LedgerStore.fetch first |> success
        let reader = LedgerStore.fetch second |> success
        Assert.That(reader.Commit, Is.EqualTo writer.Commit,
                    "Both independent readers must converge on the original history")
        for path, expectedOid in originals do
            for binding, snapshot in [first, writer; second, reader] do
                let bytes, oid = LedgerStore.readAt binding snapshot path |> success
                Assert.That(oid, Is.EqualTo(Some expectedOid),
                            sprintf "Historical Git blob changed or disappeared: %s" path)
                Assert.That(bytes.IsSome, Is.True)
        let expectedAgent =
            originals |> List.find (fun (path, _) -> path.StartsWith("agents/")) |> fst
        let expectedReview =
            originals |> List.find (fun (path, _) -> path.EndsWith("/review.json")) |> fst
        Assert.That(git first.RepositoryPath
                        ["ls-tree"; "-r"; "--name-only"; writer.Commit; "agents"],
                    Is.EqualTo expectedAgent, "No persistent Agent may be added or deleted")
        Assert.That(git first.RepositoryPath
                        ["ls-tree"; "-r"; "--name-only"; writer.Commit; "tasks/historical-work/reviews"],
                    Is.EqualTo expectedReview, "No historical review may be added or deleted")

let private withAuthorities run =
    // Exact naming is required by the nominal native disposable issuer.
    let root = Path.Combine(Path.GetTempPath(), "work-native-cas-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    let checkouts = ResizeArray<string>()
    try
        let pair name =
            let remote = Path.Combine(root, name + ".git")
            let writer = Path.Combine(root, name + "-writer")
            let reader = Path.Combine(root, name + "-reader")
            git root ["init"; "--bare"; "--initial-branch=main"; remote] |> ignore
            git root ["clone"; remote; writer] |> ignore
            git writer ["config"; "user.name"; "Two Project Fixture"] |> ignore
            git writer ["config"; "user.email"; "scheduler@example.invalid"] |> ignore
            File.WriteAllText(Path.Combine(writer, "source.txt"), name)
            git writer ["add"; "source.txt"] |> ignore
            git writer ["commit"; "-m"; "disposable source"] |> ignore
            git writer ["push"; "-u"; "origin"; "HEAD"] |> ignore
            git root ["clone"; remote; reader] |> ignore
            checkouts.Add writer
            checkouts.Add reader
            remote, writer, reader
        let priorityRemote, priorityWriter, priorityReader = pair "project"
        let otherRemote, otherWriter, otherReader = pair "project-two"
        let accountRemote, accountWriter, accountReader = pair "account"
        let mkProject remote projectId ledgerId path : LedgerBinding =
            { RepositoryPath = path; RemoteName = "origin"
              ApprovedFetchUrl = remote; ApprovedPushUrl = remote
              ProjectId = projectId; LedgerId = ledgerId; Epoch = 1 }
        let accountId, accountLedgerId = Guid.NewGuid(), Guid.NewGuid()
        let mkAccount path : AccountBinding =
            { RepositoryPath = path; RemoteName = "origin"
              ApprovedFetchUrl = accountRemote; ApprovedPushUrl = accountRemote
              AccountId = accountId; LedgerId = accountLedgerId; Epoch = 1 }
        // Each authority has two independently configured clients, but three
        // independent bare Git remotes remain the only durable authorities.
        let priorityId, priorityLedger = Guid.NewGuid(), Guid.NewGuid()
        let otherId, otherLedger = Guid.NewGuid(), Guid.NewGuid()
        let priority = mkProject priorityRemote priorityId priorityLedger priorityWriter,
                       mkProject priorityRemote priorityId priorityLedger priorityReader
        let other = mkProject otherRemote otherId otherLedger otherWriter,
                    mkProject otherRemote otherId otherLedger otherReader
        let account = mkAccount accountWriter, mkAccount accountReader
        let priorityFirst, prioritySecond = priority
        let otherFirst, otherSecond = other
        let accountFirst, accountSecond = account
        let scratch = WorkQueueNativeLifecycleCas.authorizeFreshScratch priorityFirst accountFirst |> success
        let historical = ResizeArray<LedgerBinding * LedgerBinding * (string * string) list>()
        for first, second in [priority; other] do
            LedgerStore.initialize first true |> success |> ignore
            LedgerStore.fetch second |> success |> ignore
            WorkQueueGitLedger.initialize first "WQ11 native fixture" (Guid.NewGuid())
                (Map.ofList ["Lead", set ["planning"]]) |> success |> ignore
            // No registerAgent. The only agents/* rows are historical data seeded below.
            WorkQueueRoleAuthority.initializeCatalog first "WQ11 native fixture" (Guid.NewGuid())
            |> success |> ignore
            WorkQueueRoleAuthority.configure first "WQ11 native fixture"
                (Guid.NewGuid()) 0 0
                { RoleId = "Lead"; Version = 1; Enabled = true
                  Instructions = "Disposable Work-only execution"
                  QueueSubscriptions = set ["planning"] }
            |> success |> ignore
            // Seed representative legacy Agent and independently accepted review
            // records in the ORIGINAL disposable Git ledger, not an in-memory
            // replacement. All subsequent flows must preserve their blob OIDs.
            let agentPath = "agents/historical-agent.json"
            let reviewId = Guid.NewGuid().ToString("N")
            let reviewPath = sprintf "tasks/historical-work/reviews/%s/review.json" reviewId
            let agentBytes =
                """{"schemaVersion":1,"agentId":"historical-agent","version":7,"execution":"Stopped","unassignedDisposition":"Parked","currentAssignment":null,"stateSequence":42}"""
            let reviewBytes =
                sprintf """{"schemaVersion":1,"reviewId":"%s","taskId":"historical-work","status":"Accepted","version":3,"reviewerId":"historical-reviewer","reviewRef":"refs/heads/archived/frozen-review"}""" reviewId
            let seed: LedgerIntent =
                { OperationId = Guid.NewGuid(); Actor = "WQ11 historical-only fixture"
                  Kind = "SeedOriginalHistoricalRecords"
                  RequestJson = """{"schemaVersion":1,"purpose":"historical-preservation"}""" }
            let changes: LedgerFileChange list =
                [ { Path = agentPath; ExpectedBlobOid = None; Content = agentBytes }
                  { Path = reviewPath; ExpectedBlobOid = None; Content = reviewBytes } ]
            LedgerStore.transact first seed (fun _ -> Ok changes) |> success |> ignore
            let seeded = LedgerStore.fetch first |> success
            let originals =
                [agentPath; reviewPath]
                |> List.map (fun path ->
                    let bytes, oid = LedgerStore.readAt first seeded path |> success
                    Assert.That(bytes.IsSome, Is.True)
                    path, (oid |> Option.get))
            LedgerStore.fetch second |> success |> ignore
            historical.Add (first, second, originals)
        AccountCapacityLedger.initialize accountFirst 1 |> success |> ignore
        AccountCapacityLedger.fetch accountSecond |> success |> ignore
        let journalRoot = Path.Combine(root, "originals")
        Directory.CreateDirectory journalRoot |> ignore
        OriginalBrowserJournal.provisionTrustedHostRoot journalRoot |> success |> ignore
        let subscription = Map.ofList ["Lead", set ["planning"]]
        let approval =
            { AccountId = accountId
              ProjectIds = set [priorityId; otherId]
              RoleSubscriptions = Map.ofList [priorityId, subscription; otherId, subscription]
              ApprovedRoleCatalogs =
                [priorityFirst; otherFirst]
                |> List.map (fun b ->
                    let head = LedgerStore.fetch b |> success
                    let pinned = WorkQueueRoleAuthority.loadAt b head |> success
                    b.ProjectId, {BlobOid = pinned.Proof.CatalogBlobOid;
                                  Revision = pinned.Proof.CatalogRevision})
                |> Map.ofList
              AuthorityDescription = "Independent disposable-only test membership" }
        let scope : SchedulerHeadScope =
            { Account = accountSecond
              Projects = [{Alias = "Priority";Binding = prioritySecond}
                          {Alias = "Other";Binding = otherSecond}]
              Membership = Some approval }
        let f =
            { Priority = priority; Other = other; Account = account
              Historical = historical |> Seq.toList
              JournalRoot = journalRoot; Scratch = scratch
              Observer = Observer(scope, enabled = true); Root = root }
        run f
        assertHistoricalUnchanged f
        for path in checkouts do
            Assert.That(git path ["status"; "--porcelain"], Is.Empty,
                        "No source checkout can be modified by Git ledger CAS")
            Assert.That(git path ["rev-parse"; "HEAD"],
                        Is.EqualTo(git path ["rev-parse"; "origin/main"]))
    finally
        if Directory.Exists root then Directory.Delete(root, true)

let private context (f: EphemeralSchedulerFixture) =
    let priority, _ = f.Priority
    let other, _ = f.Other
    let _, account = f.Account
    let journal = OriginalBrowserJournal.openExisting f.JournalRoot |> success
    { Account = account; CompleteProjectSet = true
      Projects = [ { Binding = priority; OriginalJournal = Some journal }
                   { Binding = other; OriginalJournal = Some journal } ] }: DurableIntentContext

let private newPlanning source reader taskId =
    let creation = Guid.NewGuid()
    ReviewWorkflow.createTask source creation taskId ("WQ11 " + taskId) |> success |> ignore
    WorkQueueReceiptProducer.produce reader "Lead" (Guid.NewGuid()) creation
    |> success |> ignore
    let _, state = WorkQueueGitLedger.load reader |> success
    state.Works |> Map.toList |> List.map snd
    |> List.find (fun item -> item.SubjectId = taskId)

let private prepare f =
    match WorkQueueDurableLaunchIntent.prepareExplicit true (context f) "two-project disposable manual tick" with
    | DurableIntentOutcome.Prepared (intent, _, _) -> intent
    | other -> failwithf "No original durable WQ12 intent: %A" other

let private actor = "operator:" + String.replicate 64 "a"
let private sha = String.replicate 64 "b"

let private admission (original: DurablePlanningLaunchIntent) : AccountAdmissionRequest =
    { Actor = original.AdmissionActor
      LaunchId = Guid.Parse original.LaunchId
      AdmissionOperationId = Guid.Parse original.AdmissionOperationId
      AbortOperationId = Guid.Parse original.AbortOperationId
      ReleaseOperationId = Guid.Parse original.ReleaseOperationId
      RoleId = original.RoleId; RoleVersion = original.RoleVersion }

let private candidate (project: LedgerBinding) (original: DurablePlanningLaunchIntent) =
    let view = WorkQueueRoleAuthority.load project |> success
    let stage = view.Project.Works.[original.WorkId]
    { ProjectId = original.ProjectId; ProjectHead = original.ProjectHead
      ProjectVersion = original.ProjectVersion
      WorkId = original.WorkId; WorkGeneration = original.WorkGeneration
      QueueId = stage.QueueId; AvailableAt = stage.AvailableAt
      AvailableSequence = stage.AvailableSequence
      Priority = Contracts.priorityOf view.Project stage.Id
      BlocksOtherWork = view.Project.Works |> Map.exists (fun _ w ->
          w.Dependencies.Contains original.WorkId
          && w.State <> WorkState.Completed)
      RoleId = original.RoleId; RoleVersion = original.RoleVersion
      CatalogBlobOid = original.CatalogBlobOid
      Source =
        { Head = original.SourceHead; OperationId = original.SourceOperationId
          RequestHash = original.SourceRequestHash; SourceRef = None
          SourceCommit = None }
      LaunchId = original.LaunchId
      OriginalBrowserOperationId = original.OriginalBrowserOperationId }: PlannerCandidate

let private intent (report: Client.WorkQueueSchedulerHostPreview.SchedulerHostPreviewReport)
                   (original: DurablePlanningLaunchIntent) : WorkExecutorIntent =
    { WorkId = original.WorkId; WorkGeneration = original.WorkGeneration
      RoleId = original.RoleId; RoleVersion = original.RoleVersion
      ProjectHead = report.ProjectHead.Value; AccountHead = report.AccountHead.Value
      SourceOperationId = original.SourceOperationId
      SourceRequestHash = original.SourceRequestHash
      LaunchId = original.LaunchId
      OriginalBrowserOperationId = original.OriginalBrowserOperationId }

// This projection deliberately proves only a hypothetical disposable scheduler.
// The verified account head remains LegacyActive throughout. Never return this
// from a production port: there is no accepted WQ07 Activated CAS or provider.
let private fakeOnlyRead (account: AccountBinding) (project: LedgerBinding) () =
    let actual = WorkQueueReactiveWake.boundRead account project "Lead" ()
    { actual with
        AccountMigration = Some "Activated"
        Issues = actual.Issues |> List.filter (fun issue -> issue.Code <> "AccountNotActivated") }

let private host (f: EphemeralSchedulerFixture) project
                 (original: DurablePlanningLaunchIntent option)
                 execute projection enabled =
    let _, accountReader = f.Account
    let read = if projection then fakeOnlyRead accountReader project
               else WorkQueueReactiveWake.boundRead accountReader project "Lead"
    let ports: WorkWakePorts =
        { ReadVerified = read
          Plan = fun report ->
              match report.WouldLaunch, original with
              | Some selected, Some p when selected.WorkId = p.WorkId &&
                                           selected.WorkGeneration = p.WorkGeneration &&
                                           selected.RoleId = p.RoleId &&
                                           selected.RoleVersion = p.RoleVersion ->
                  Ok(Some (intent report p))
              | _ -> Ok None
          Execute = execute }
    let coordinator = WorkQueueReactiveWake.Coordinator(ports, enabled = enabled)
    let headReader () = f.Observer.ReadVerifiedHeads project.ProjectId
    new WorkQueueSchedulerHostLoop.Host(
        WorkQueueSchedulerHostLoop.productionPorts coordinator headReader ignore,
        enabled = enabled)

let private held (f: EphemeralSchedulerFixture) =
    let _, account = f.Account
    AccountCapacityLedger.fetch account |> success
    |> fun snapshot -> AccountCapacityLedger.occupied snapshot.State

let private dispatch f original executionId =
    let ctx = context f
    let ticket =
        match WorkQueueNativeCredentialDispatchAdapter.prepareDisposable true true
                  ctx original "WQ11 disposable coordinator" executionId with
        | NativeCredentialAdmission.Ready ticket -> ticket
        | other -> failwithf "No WQ07/WQ02 four-path admission: %A" other
    let project =
        ctx.Projects
        |> List.find (fun p -> p.Binding.ProjectId.ToString("D") = original.ProjectId)
        |> fun p -> p.Binding
    let head = LedgerStore.fetch project |> success
    let op = LedgerStore.getOperationAt project head (Guid.Parse original.AdmissionOperationId)
             |> success |> Option.get
    Assert.That(op.Paths, Has.Length.EqualTo 4)
    Assert.That(op.Paths, Does.Contain(WorkQueueGitLedger.credentialPath original.LaunchId))
    let verifier, _ = WorkQueueGitLedger.readCredentialAt project head original.LaunchId
                      |> success |> Option.get
    Assert.That(verifier.SecretSha256, Is.EqualTo ticket.Issued.VerifierSha256)
    let mutable sends = 0
    let mutable bearer = None
    let provider (_: OriginalBrowserIntent) (bootstrap: NativePrivateBootstrap) =
        sends <- sends + 1
        Assert.That(bootstrap.TakeOnceFor("foreign execution"), Is.EqualTo None)
        bearer <- bootstrap.TakeOnceFor(executionId)
        Assert.That(bearer.IsSome, Is.True)
        Assert.That(bootstrap.TakeOnceFor(executionId), Is.EqualTo None)
        NativeProviderReply.Accepted ("fake-only-" + executionId)
    let sent = WorkQueueNativeCredentialDispatchAdapter.submitDisposable true f.JournalRoot ticket provider
    Assert.That(sent.Phase, Is.EqualTo NativeDispatchPhase.AwaitFirstConnect, sent.Detail)
    let replay = WorkQueueNativeCredentialDispatchAdapter.submitDisposable true f.JournalRoot ticket provider
    Assert.That(replay.Phase, Is.EqualTo NativeDispatchPhase.NoResend)
    Assert.That(sends, Is.EqualTo 1)
    let connected = WorkQueueNativeCredentialDispatchAdapter.firstConnectDisposable
                        true f.JournalRoot ticket executionId bearer.Value
    Assert.That(connected.Phase, Is.EqualTo NativeDispatchPhase.Connected, connected.Detail)
    Assert.That((WorkQueueNativeCredentialDispatchAdapter.firstConnectDisposable
                    true f.JournalRoot ticket executionId bearer.Value).Phase,
                Is.EqualTo NativeDispatchPhase.Connected)
    ticket, bearer.Value, sends

[<TestFixture>]
type WorkQueueTwoProjectEphemeralSchedulerVerticalTests() =
    [<Test>]
    member _.OriginalGitHeadsAndProductionGatesAreNoGoWithoutAnyProviderCall() =
        withAuthorities (fun f ->
            let priorityWriter, priorityReader = f.Priority
            let otherWriter, otherReader = f.Other
            let accountWriter, accountReader = f.Account
            let b = newPlanning priorityWriter priorityReader "priority"
            let a = newPlanning otherWriter otherReader "other"
            let beforeAccount = (AccountCapacityLedger.fetch accountReader |> success).Head
            let beforeB = (LedgerStore.fetch priorityReader |> success).Commit
            let mutable effects = 0
            let executor _ =
                effects <- effects + 1
                WorkExecutorResult.Rejected "No production executor installed"
            let disabled = host f priorityReader None executor false false
            Assert.That(disabled.RunOnce [WorkWakeTrigger.WorkCreated],
                        Is.EqualTo SchedulerHostOutcome.Disabled)
            let actual = host f priorityReader None executor false true
            match actual.RunOnce [WorkWakeTrigger.ReviewAccepted; WorkWakeTrigger.IntegrationCommitted] with
            | SchedulerHostOutcome.Reconciled (WorkWakeResult.NeedsAttention ("AccountNotActivated", _)) -> ()
            | other -> Assert.Fail(sprintf "LegacyActive must block canonical Scheduler: %A" other)
            let original = prepare f
            Assert.That(original.WorkId, Is.EqualTo b.Id,
                        "Fresh two-project WQ12 ranking chooses higher-priority blocked-dependent source")
            Assert.That(original.WorkId, Is.Not.EqualTo a.Id)
            let ctx = context f
            Assert.That(WorkQueueNativeCredentialDispatchAdapter.prepareProduction ctx original,
                        Is.EqualTo NativeCredentialAdmission.HostBridgeUnavailable)
            Assert.That(WorkQueueNativeCredentialDispatchAdapter.prepareDisposable false true
                            ctx original "actor" "execution",
                        Is.EqualTo NativeCredentialAdmission.Disabled)
            Assert.That((AccountCapacityLedger.fetch accountWriter |> success).Head,
                        Is.EqualTo beforeAccount)
            Assert.That((WorkQueueGitLedger.load priorityReader |> success |> snd).Launches.Count,
                        Is.Zero)
            Assert.That((AccountCapacityLedger.fetch accountReader |> success).State.Tokens,
                        Is.Empty)
            Assert.That(effects, Is.Zero)
            Assert.That((AccountCapacityLedger.fetch accountReader |> success).State.MigrationPhase,
                        Is.EqualTo "LegacyActive")
            Assert.That((LedgerStore.fetch priorityReader |> success).Commit,
                        Is.Not.EqualTo beforeB,
                        "Only the explicit WQ12 durable intent may write before admission"))

    [<Test>]
    member _.ConcurrentOriginalWq07CapOneFromTwoClonesHasSingleCasWinner() =
        withAuthorities (fun f ->
            let priorityWriter, priorityReader = f.Priority
            let otherWriter, otherReader = f.Other
            let accountWriter, accountReader = f.Account
            let _ = newPlanning priorityWriter priorityReader "race-priority"
            let _ = newPlanning otherWriter otherReader "race-other"
            let request () : AccountAdmissionRequest =
                { Actor = "WQ11 disposable competing admission"
                  LaunchId = Guid.NewGuid()
                  AdmissionOperationId = Guid.NewGuid()
                  AbortOperationId = Guid.NewGuid()
                  ReleaseOperationId = Guid.NewGuid()
                  RoleId = "Lead"; RoleVersion = 1 }
            let firstRequest, secondRequest = request (), request ()
            let beforeWriter = AccountCapacityLedger.fetch accountWriter |> success
            let beforeReader = AccountCapacityLedger.fetch accountReader |> success
            Assert.That(beforeReader.Head, Is.EqualTo beforeWriter.Head)
            Assert.That(beforeWriter.State.Tokens, Is.Empty)
            // Both original WQ07 contenders MUST reach the lease-protected push
            // with the same verified pre-admission head. Their separate clones
            // then compete; a serial post-commit NoCapacity check is insufficient.
            use simultaneousLease = new Barrier(2)
            let compete account project (admission: AccountAdmissionRequest) =
                Task.Run(fun () ->
                    let mutable firstLease = true
                    let mutable observedParent = None
                    let hooks: AccountGitTestInterposition =
                        { BeforeLeasePush = fun operation parent ->
                            if operation = admission.AdmissionOperationId && firstLease then
                                firstLease <- false
                                observedParent <- Some parent
                                Assert.That(
                                    simultaneousLease.SignalAndWait(TimeSpan.FromSeconds 30.0),
                                    Is.True, "Two original WQ07 writers never reached the same CAS barrier")
                          GitCommand = fun _ -> AccountGitTestFault.PassThrough }
                    let result =
                        AccountCapacityLedger.withTestInterposition hooks (fun () ->
                            AccountAdmissionAdapter.admitInertFixture account project admission)
                    result, observedParent)
            let one = compete accountWriter priorityWriter firstRequest
            let two = compete accountReader otherReader secondRequest
            Assert.That(Task.WaitAll([| one :> Task; two :> Task |], TimeSpan.FromSeconds 90.0),
                        Is.True, "Competing original WQ07 admissions did not terminate")
            let oneOutcome, oneParent = one.Result
            let twoOutcome, twoParent = two.Result
            Assert.That(oneParent, Is.EqualTo(Some beforeWriter.Head))
            Assert.That(twoParent, Is.EqualTo(Some beforeWriter.Head))
            let outcomes = [oneOutcome; twoOutcome]
            Assert.That(
                outcomes |> List.filter (function ProjectAdmitted _ -> true | _ -> false) |> List.length,
                Is.EqualTo 1, sprintf "Exact one WQ07 winner required: %A" outcomes)
            match outcomes |> List.filter (function ProjectAdmitted _ -> false | _ -> true) with
            | [GlobalReservationRejected(AccountCapacityFull(1, 1))] -> ()
            | other -> Assert.Fail(sprintf "Expected original typed capacity-one loser: %A" other)
            let winnerRequest, winnerProject, loserRequest, loserProject =
                match oneOutcome with
                | ProjectAdmitted _ -> firstRequest, priorityReader, secondRequest, otherReader
                | _ -> secondRequest, otherReader, firstRequest, priorityReader
            let settledWriter = AccountCapacityLedger.fetch accountWriter |> success
            let settledReader = AccountCapacityLedger.fetch accountReader |> success
            Assert.That(settledReader.Head, Is.EqualTo settledWriter.Head)
            Assert.That(settledWriter.State.StateVersion,
                        Is.EqualTo(beforeWriter.State.StateVersion + 1L))
            Assert.That(settledWriter.State.NextSequence,
                        Is.EqualTo(beforeWriter.State.NextSequence + 1L))
            Assert.That(settledWriter.State.Tokens, Has.Length.EqualTo 1)
            Assert.That(settledWriter.State.Tokens.[0].LaunchId,
                        Is.EqualTo(winnerRequest.LaunchId.ToString("D")))
            Assert.That(settledWriter.State.MigrationPhase, Is.EqualTo "LegacyActive")
            let _, winnerReceipt =
                AccountCapacityLedger.readOperation accountReader winnerRequest.AdmissionOperationId
                |> success
            let _, loserReceipt =
                AccountCapacityLedger.readOperation accountWriter loserRequest.AdmissionOperationId
                |> success
            Assert.That(winnerReceipt.IsSome, Is.True)
            Assert.That(loserReceipt.IsNone, Is.True,
                        "Losing lease may not publish a WQ07 operation receipt")
            Assert.That((WorkQueueGitLedger.load winnerProject |> success |> snd).Launches.Count,
                        Is.EqualTo 1)
            Assert.That((WorkQueueGitLedger.load loserProject |> success |> snd).Launches.Count,
                        Is.Zero)
            for project, admission in [priorityReader, firstRequest; otherReader, secondRequest] do
                let verified = LedgerStore.fetch project |> success
                let path = WorkQueueGitLedger.credentialPath (admission.LaunchId.ToString("D"))
                let bytes, oid = LedgerStore.readAt project verified path |> success
                Assert.That(bytes.IsNone && oid.IsNone, Is.True,
                            "No losing or winning inert admission may issue a WQ02 four-path credential")
            for writer, reader in [f.Priority; f.Other] do
                let firstHead = (LedgerStore.fetch writer |> success).Commit
                let otherHead = (LedgerStore.fetch reader |> success).Commit
                Assert.That(otherHead, Is.EqualTo firstHead,
                            "Independent original project readers must converge")
            assertHistoricalUnchanged f)

    [<Test>]
    member _.ManualTickTwoProjectsRealCasCapOneNativeTerminalAndNextStop() =
        withAuthorities (fun f ->
            let priorityWriter, priorityReader = f.Priority
            let otherWriter, otherReader = f.Other
            let accountWriter, accountReader = f.Account
            let a = newPlanning otherWriter otherReader "ordinary"
            let b = newPlanning priorityWriter priorityReader "priority"
            // A later versioned review and Integration hard edge make B the
            // priority. These inert gates are NOT forged WQ05 Accepted receipts.
            let stageReview: WorkStage =
                { Id = "review-" + Guid.NewGuid().ToString("N")
                  ProjectId = priorityWriter.ProjectId.ToString("D")
                  SubjectId = "pending-review"; Kind = WorkKind.FrozenReview
                  QueueId = "review"; StageGeneration = 2; State = WorkState.Blocked
                  Domain = DomainState.Undecided; Gate = GateState.Clear
                  ClaimGeneration = 0; Dependencies = Set.singleton b.Id
                  WaivedDependencies = Map.empty
                  AvailableAt = b.AvailableAt + 1L; AvailableSequence = b.AvailableSequence + 1L
                  SourceLineage = None; Push = None; IntegrationAuthorized = false }
            let stageIntegration: WorkStage =
                { stageReview with
                    Id = "integration-" + Guid.NewGuid().ToString("N")
                    Kind = WorkKind.Integration; QueueId = "integration"
                    SubjectId = "pending-integration"
                    StageGeneration = 1; Dependencies = Set.singleton stageReview.Id
                    AvailableAt = stageReview.AvailableAt + 1L
                    AvailableSequence = stageReview.AvailableSequence + 1L }
            for stage in [stageReview; stageIntegration] do
                let _, queue = WorkQueueGitLedger.load priorityReader |> success
                WorkQueueGitLedger.executeInertFixture priorityWriter "WQ11 fake-only"
                    (Guid.NewGuid()) queue.Version (PublishStage stage) |> success |> ignore
            let priorityAt = LedgerStore.fetch priorityReader |> success
            match WorkQueueIntegrationSourceAuthority.inspectAt priorityReader priorityAt stageIntegration with
            | IntegrationSourceAuthorityDecision.IntegrationAuthorityUnavailable _ -> ()
            | other -> Assert.Fail(sprintf "Inert review gate cannot authorize integration: %A" other)
            let originalB = prepare f
            Assert.That(originalB.WorkId, Is.EqualTo b.Id)
            let mutable sendsB = 0
            let mutable ticketB = None
            let mutable bearerB = None
            let runB _ =
                let ticket, bearer, sends = dispatch f originalB "priority-execution"
                ticketB <- Some ticket; bearerB <- Some bearer; sendsB <- sends
                WorkExecutorResult.Applied
            let hostB = host f priorityReader (Some originalB) runB true true
            let hostA = host f otherReader None
                            (fun _ -> Assert.Fail "Other project cannot steal first tick"
                                      WorkExecutorResult.Rejected "wrong project") true true
            let stepB = hostB.RunOnce [WorkWakeTrigger.WorkCreated; WorkWakeTrigger.DependencyCommitted;
                                       WorkWakeTrigger.ReviewAccepted; WorkWakeTrigger.IntegrationCommitted]
            match stepB with
            | SchedulerHostOutcome.Reconciled (WorkWakeResult.Reconciled selected) ->
                Assert.That(selected.LaunchId, Is.EqualTo originalB.LaunchId)
            | other -> Assert.Fail(sprintf "Fake-only manually wired tick did not execute B: %A" other)
            Assert.That(sendsB, Is.EqualTo 1)
            assertHistoricalUnchanged f
            Assert.That(held f, Is.EqualTo 1)
            match hostA.RunOnce [WorkWakeTrigger.WorkCreated] with
            | SchedulerHostOutcome.Reconciled WorkWakeResult.NoCapacity -> ()
            | other -> Assert.Fail(sprintf "Cap-one must park A: %A" other)
            match WorkQueueDurableLaunchIntent.prepareExplicit true (context f) "competing clone" with
            | DurableIntentOutcome.NeedsAttention _ -> ()
            | other -> Assert.Fail(sprintf "No second original may be prepared at held cap: %A" other)
            match WorkQueueNativeCredentialDispatchAdapter.prepareDisposable true true
                      (context f) originalB "duplicate" "new-execution" with
            | NativeCredentialAdmission.Rejected reason ->
                Assert.That(reason, Does.Contain "already issued")
            | other -> Assert.Fail(sprintf "Second bearer after committed four-path CAS: %A" other)
            let atB = LedgerStore.fetch priorityReader |> success
            let _, beforeClaim = WorkQueueGitLedger.load priorityReader |> success
            let launch = beforeClaim.Launches.[originalB.LaunchId]
            let session : INativeWorkSessionJournal =
                { new INativeWorkSessionJournal with
                    member _.ReadAuthenticatedSession id =
                        if id <> originalB.LaunchId then Error "foreign launch"
                        else
                            match launch.State, launch.Lease with
                            | LaunchState.Registered (uuid, key), Some lease ->
                                Ok { AgentUuid = uuid; SessionKey = key
                                     LeaseGeneration = lease.Generation
                                     OriginalOperationId = originalB.OriginalBrowserOperationId }
                            | _ -> Error "no authenticated original"
                    member _.VerifyTerminalEvidence (id, session, evidence) =
                        if id = originalB.LaunchId && evidence = sha &&
                           session.OriginalOperationId = originalB.OriginalBrowserOperationId
                        then Ok ()
                        else Error "missing independent fake terminal evidence" }
            let bound binding account : NativeLifecycleBinding =
                { Project = binding; Account = account
                  DisposableAuthority = f.Scratch
                  OriginalJournalRoot = f.JournalRoot
                  Selected = candidate priorityReader originalB
                  Admission = admission originalB
                  DispatchOperationId = Guid.Parse originalB.DispatchOperationId
                  FirstConnectOperationId = Guid.Parse originalB.FirstConnectOperationId
                  SessionJournal = session }
            let bBound = bound priorityWriter accountWriter
            let bOther = bound priorityReader accountReader
            let claimOp = Guid.NewGuid()
            let claim = WorkQueueNativeLifecycleCas.transitionDisposable true bBound actor
                            claimOp beforeClaim.Version (NativeWorkAction.Claim 1) |> success
            Assert.That(claim.Status, Is.EqualTo "Applied")
            let staleOperation = Guid.NewGuid()
            let beforeStaleProject = LedgerStore.fetch priorityReader |> success
            let beforeStaleAccount = AccountCapacityLedger.fetch accountReader |> success
            let _, beforeStaleState = WorkQueueGitLedger.load priorityReader |> success
            let credentialPath = WorkQueueGitLedger.credentialPath originalB.LaunchId
            let _, credentialBefore =
                LedgerStore.readAt priorityReader beforeStaleProject credentialPath |> success
            Assert.That(credentialBefore.IsSome, Is.True)
            match WorkQueueNativeLifecycleCas.transitionDisposable true bOther actor
                      staleOperation beforeClaim.Version (NativeWorkAction.Claim 1) with
            | Error(StaleEntityVersion "Stale WQ-02 project version.") -> ()
            | other -> Assert.Fail(sprintf
                "Stale clone must fail on the exact original WQ02 version CAS, not preflight: %A" other)
            let afterStaleProject = LedgerStore.fetch priorityReader |> success
            let afterStaleAccount = AccountCapacityLedger.fetch accountReader |> success
            let _, afterStaleState = WorkQueueGitLedger.load priorityReader |> success
            let _, credentialAfter =
                LedgerStore.readAt priorityReader afterStaleProject credentialPath |> success
            let rejectedReceipt =
                LedgerStore.getOperationAt priorityReader afterStaleProject staleOperation |> success
            Assert.That(afterStaleProject.Commit, Is.EqualTo beforeStaleProject.Commit)
            Assert.That(afterStaleAccount.Head, Is.EqualTo beforeStaleAccount.Head)
            Assert.That(afterStaleState.Version, Is.EqualTo beforeStaleState.Version)
            Assert.That(afterStaleState.Receipts.Count, Is.EqualTo beforeStaleState.Receipts.Count)
            Assert.That(afterStaleState.Launches.Count, Is.EqualTo beforeStaleState.Launches.Count)
            Assert.That(credentialAfter, Is.EqualTo credentialBefore)
            Assert.That(rejectedReceipt.IsNone, Is.True,
                        "Rejected contender must not publish an original receipt")
            assertHistoricalUnchanged f
            let replay = WorkQueueNativeLifecycleCas.transitionDisposable true bOther actor
                            claimOp beforeClaim.Version (NativeWorkAction.Claim 1) |> success
            Assert.That(replay.Status, Is.EqualTo "IdempotentReplay")
            let snapshotAfterClaim = LedgerStore.fetch priorityReader |> success
            Assert.That(snapshotAfterClaim.Commit, Is.Not.EqualTo atB.Commit)
            let checkpointOp = Guid.NewGuid()
            let afterClaim = WorkQueueGitLedger.load priorityReader |> success |> snd
            WorkQueueNativeLifecycleCas.transitionDisposable true bOther actor
                checkpointOp afterClaim.Version (NativeWorkAction.Checkpoint(1,sha))
            |> success |> ignore
            assertHistoricalUnchanged f
            let terminalOp = Guid.NewGuid()
            let afterCheckpoint = WorkQueueGitLedger.load priorityWriter |> success |> snd
            let withoutTerminal: INativeWorkSessionJournal =
                { new INativeWorkSessionJournal with
                    member _.ReadAuthenticatedSession id = session.ReadAuthenticatedSession id
                    member _.VerifyTerminalEvidence (_,_,_) = Error "terminal proof absent" }
            match WorkQueueNativeLifecycleCas.transitionDisposable true
                      { bBound with SessionJournal = withoutTerminal }
                      actor (Guid.NewGuid()) afterCheckpoint.Version (NativeWorkAction.Complete(1,sha)) with
            | Error _ -> ()
            | Ok _ -> Assert.Fail "Terminal without independent evidence was committed"
            WorkQueueNativeLifecycleCas.transitionDisposable true bBound actor terminalOp
                afterCheckpoint.Version (NativeWorkAction.Complete(1,sha))
            |> success |> ignore
            let terminalHead = LedgerStore.fetch priorityReader |> success
            let terminalRecord = WorkQueueGitLedger.readTerminalAt priorityReader
                                    terminalHead originalB.LaunchId |> success
            Assert.That(terminalRecord.IsSome, Is.True)
            Assert.That(held f, Is.EqualTo 1, "Completion alone never releases account credit")
            let wrong = { ProjectId = priorityReader.ProjectId
                          LaunchId = Guid.Parse originalB.LaunchId
                          AdmissionOperationId = Guid.Parse originalB.AdmissionOperationId
                          TerminalOperationId = Guid.NewGuid() }: ProjectTerminalLocator
            match AccountAdmissionAdapter.releaseTerminal accountReader priorityReader
                      "WQ11 fake-only" (Guid.NewGuid()) wrong with
            | Error _ -> ()
            | Ok _ -> Assert.Fail "Unverified terminal released global capacity"
            let locator = { wrong with TerminalOperationId = terminalOp }
            AccountAdmissionAdapter.releaseTerminal accountWriter priorityWriter
                "WQ11 fake-only" (Guid.Parse originalB.ReleaseOperationId) locator
            |> success |> ignore
            let replayRelease = AccountAdmissionAdapter.releaseTerminal
                                    accountReader priorityReader "WQ11 fake-only"
                                    (Guid.Parse originalB.ReleaseOperationId) locator |> success
            Assert.That(replayRelease.AlreadyApplied, Is.True)
            assertHistoricalUnchanged f
            Assert.That(held f, Is.Zero)
            let nowAvailable = WorkQueueGitLedger.load priorityReader |> success |> snd
            Assert.That(nowAvailable.Works.[b.Id].State, Is.EqualTo WorkState.Completed)
            Assert.That(nowAvailable.Works.[stageReview.Id].State, Is.EqualTo WorkState.Available)
            Assert.That(nowAvailable.Works.[stageIntegration.Id].State, Is.EqualTo WorkState.Blocked)
            // WQ05 review and Integration gates remain CLOSED after a Planning
            // terminal. Their acceptance is not inferred from synthetic hints.
            let originalA = prepare f
            Assert.That(originalA.WorkId, Is.EqualTo a.Id)
            let mutable sendsA = 0
            let mutable bearerA = None
            let runA _ =
                let _, bearer, sends = dispatch f originalA "other-execution"
                bearerA <- Some bearer; sendsA <- sends
                WorkExecutorResult.Applied
            let hostNext = host f otherReader (Some originalA) runA true true
            match hostNext.RunOnce [WorkWakeTrigger.CapacityReleased; WorkWakeTrigger.ManualReconcile] with
            | SchedulerHostOutcome.Reconciled (WorkWakeResult.Reconciled selected) ->
                Assert.That(selected.LaunchId, Is.EqualTo originalA.LaunchId)
            | other -> Assert.Fail(sprintf "Verified terminal did not wake other project: %A" other)
            Assert.That(sendsA, Is.EqualTo 1)
            Assert.That(held f, Is.EqualTo 1)
            let _, activeA = WorkQueueGitLedger.load otherReader |> success
            let stopNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1000L
            WorkQueueGitLedger.executeInertFixtureAt otherWriter "operator STOP"
                (Guid.NewGuid()) activeA.Version stopNow
                (StopStalled(originalA.LaunchId, 1, 500L)) |> success |> ignore
            let afterStop = WorkQueueGitLedger.load otherReader |> success |> snd
            match WorkQueueGitLedger.executeCredentialFixtureAt otherReader "fake-only"
                      (Guid.NewGuid()) afterStop.Version (stopNow+1L)
                      originalA.LaunchId bearerA.Value CredentialWorkAction.ClaimPrimary with
            | Error _ -> ()
            | Ok _ -> Assert.Fail "STOP did not revoke new Work claims"
            Assert.That(held f, Is.EqualTo 1, "Stopped unresolved execution retains capacity")
            Assert.That((AccountCapacityLedger.fetch accountReader |> success).State.MigrationPhase,
                        Is.EqualTo "LegacyActive")
            assertHistoricalUnchanged f
            Assert.That((f.Observer.ReadAll() |> success).OccupiedCapacity, Is.EqualTo 1))

    [<Test>]
    member _.UnknownFakeProviderKeepsOriginalAndGlobalTokenNoRetry() =
        withAuthorities (fun f ->
            let priorityWriter, priorityReader = f.Priority
            let otherWriter, otherReader = f.Other
            let _ = newPlanning priorityWriter priorityReader "unknown-source"
            let _ = newPlanning otherWriter otherReader "waiting-source"
            let original = prepare f
            let ticket =
                match WorkQueueNativeCredentialDispatchAdapter.prepareDisposable true true
                          (context f) original "unknown case" "unknown-execution" with
                | NativeCredentialAdmission.Ready ticket -> ticket
                | other -> failwithf "No four-path admission: %A" other
            let mutable count = 0
            let provider (_: OriginalBrowserIntent) (bootstrap: NativePrivateBootstrap) =
                count <- count + 1
                Assert.That(bootstrap.TakeOnceFor("unknown-execution").IsSome, Is.True)
                NativeProviderReply.Unknown
            let result = WorkQueueNativeCredentialDispatchAdapter.submitDisposable
                            true f.JournalRoot ticket provider
            Assert.That(result.Phase, Is.EqualTo NativeDispatchPhase.Retained)
            Assert.That((WorkQueueNativeCredentialDispatchAdapter.submitDisposable
                            true f.JournalRoot ticket provider).Phase,
                        Is.EqualTo NativeDispatchPhase.NoResend)
            Assert.That(count, Is.EqualTo 1)
            assertHistoricalUnchanged f
            Assert.That(held f, Is.EqualTo 1)
            match WorkQueueDurableLaunchIntent.prepareExplicit true (context f) "competing client" with
            | DurableIntentOutcome.NeedsAttention _ -> ()
            | other -> Assert.Fail(sprintf "Unknown original allowed another dispatch: %A" other)
            let _, state = WorkQueueGitLedger.load priorityReader |> success
            Assert.That(state.Launches.Count, Is.EqualTo 1)
            Assert.That(state.Launches.[original.LaunchId].State,
                        Is.EqualTo (LaunchState.SubmittedUnregistered DispatchState.OutcomeUnknown))
            Assert.That((WorkQueueGitLedger.load otherReader |> success |> snd).Launches.Count,
                        Is.Zero))
