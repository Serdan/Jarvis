module Client.Tests.WorkQueueDurableLaunchIntentTests

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open NUnit.Framework
open Jarvis.WorkQueues
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.WorkQueueGitLedger
open Client.WorkQueueReceiptProducer
open Client.WorkQueueRoleAuthority
open Client.WorkQueueDurableLaunchIntent
open Client.WorkCompositionGitLedger
open Client.AccountCapacityLedger
open Client.AccountAdmissionAdapter

let private success = function
    | Ok value -> value
    | Error error -> failwithf "WQ12 disposable Git fixture: %A" error

let private git directory arguments =
    use p = new Process()
    p.StartInfo.FileName <- "git"
    p.StartInfo.WorkingDirectory <- directory
    p.StartInfo.UseShellExecute <- false
    p.StartInfo.RedirectStandardOutput <- true
    p.StartInfo.RedirectStandardError <- true
    for arg in arguments do p.StartInfo.ArgumentList.Add(arg)
    Assert.That(p.Start(), Is.True)
    let output = p.StandardOutput.ReadToEnd()
    let error = p.StandardError.ReadToEnd()
    Assert.That(p.WaitForExit(30000), Is.True, sprintf "Disposable git timed out: %A" arguments)
    Assert.That(p.ExitCode, Is.Zero, sprintf "git %A: %s" arguments error)
    output.Trim()

let private withBindings test =
    let root = Path.Combine(Path.GetTempPath(), "wq12-original-intent-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    let checkouts = ResizeArray<string>()
    try
        let pair name =
            let remote = Path.Combine(root, name + ".git")
            let first, second = Path.Combine(root, name + "-first"), Path.Combine(root, name + "-second")
            git root ["init"; "--bare"; "--initial-branch=main"; remote] |> ignore
            git root ["clone"; remote; first] |> ignore
            File.WriteAllText(Path.Combine(first, "source.txt"), "WQ12 offline " + name)
            git first ["add"; "source.txt"] |> ignore
            git first ["-c"; "user.name=WQ12 Fixture"; "-c"; "user.email=wq12@example.test";
                       "commit"; "-m"; "disposable source"] |> ignore
            git first ["push"; "-u"; "origin"; "HEAD"] |> ignore
            git root ["clone"; remote; second] |> ignore
            checkouts.Add first
            checkouts.Add second
            let projectId, ledgerId = Guid.NewGuid(), Guid.NewGuid()
            let binding checkout : LedgerBinding =
                { RepositoryPath = checkout; RemoteName = "origin"
                  ApprovedFetchUrl = remote; ApprovedPushUrl = remote
                  ProjectId = projectId; LedgerId = ledgerId; Epoch = 1 }
            binding first, binding second
        let a1, a2 = pair "project-a"
        let b1, b2 = pair "project-b"
        let accountRoot1, accountRoot2 = pair "account"
        let accountId, ledgerId = Guid.NewGuid(), Guid.NewGuid()
        let account (binding: LedgerBinding) : AccountBinding =
            { RepositoryPath = binding.RepositoryPath; RemoteName = "origin"
              ApprovedFetchUrl = binding.ApprovedFetchUrl; ApprovedPushUrl = binding.ApprovedPushUrl
              AccountId = accountId; LedgerId = ledgerId; Epoch = 1 }
        let account1, account2 = account accountRoot1, account accountRoot2
        for one, two in [a1,a2; b1,b2] do
            LedgerStore.initialize one true |> success |> ignore
            LedgerStore.fetch two |> success |> ignore
            WorkQueueGitLedger.initialize one "WQ12 Fixture" (Guid.NewGuid())
                (Map.ofList ["Lead", set ["planning"]]) |> success |> ignore
            ReviewWorkflow.registerAgent one (Guid.NewGuid()) "Lead" |> success |> ignore
            WorkQueueRoleAuthority.initializeCatalog one "Lead" (Guid.NewGuid()) |> success |> ignore
            let role: RoleConfiguration =
                { RoleId = "Lead"; Version = 1; Enabled = true
                  Instructions = "Disposable read-only planning Work"
                  QueueSubscriptions = set ["planning"] }
            WorkQueueRoleAuthority.configure one "Lead" (Guid.NewGuid()) 0 0 role
            |> success |> ignore
        AccountCapacityLedger.initialize account1 1 |> success |> ignore
        AccountCapacityLedger.fetch account2 |> success |> ignore
        test (a1,a2) (b1,b2) (account1,account2)
        for checkout in checkouts do
            Assert.That(git checkout ["status"; "--porcelain"], Is.Empty)
    finally
        if Directory.Exists root then Directory.Delete(root, true)

let private context account first second : DurableIntentContext =
    { Account = account
      CompleteProjectSet = true
      Projects =
        [ { Binding = first; OriginalJournal = None }
          { Binding = second; OriginalJournal = None } ] }

let private producedTask source receiver taskId =
    let op = Guid.NewGuid()
    ReviewWorkflow.createTask source op taskId ("WQ12 " + taskId) |> success |> ignore
    WorkQueueReceiptProducer.produce receiver "Lead" (Guid.NewGuid()) op |> success |> ignore
    let snapshot, project = WorkQueueGitLedger.load receiver |> success
    let stage = project.Works |> Map.toList |> List.map snd
                |> List.find (fun work -> work.SubjectId = taskId)
    let origin, oid =
        WorkQueueGitLedger.readOriginAt receiver snapshot stage.Id
        |> success |> Option.get
    Assert.That(origin.SourceOperationId, Is.EqualTo(op.ToString("D")))
    Assert.That(origin.SourceRef, Is.Empty)
    Assert.That(origin.SourceCommit, Is.Empty)
    stage, oid

let private prepared = function
    | DurableIntentOutcome.Prepared (intent, _, _) -> intent
    | other -> failwithf "WQ12 expected prepared canonical intent, received: %A" other

[<TestFixture>]
type WorkQueueDurableLaunchIntentTests() =
    [<Test>]
    member _.CanonicalPlanningOriginIsPreparedOnceAcrossTwoClientsAndWq07CapOne() =
        withBindings (fun (a1,a2) (b1,b2) (account1,account2) ->
            let stage, originOid = producedTask a1 a2 "planning-one"
            let first = context account1 a1 b1
            let second = context account2 b2 a2
            Assert.That(WorkQueueDurableLaunchIntent.prepareExplicit false first "WQ12",
                        Is.EqualTo(DurableIntentOutcome.Disabled))
            let headBefore = (LedgerStore.fetch a1 |> success).Commit
            let original = WorkQueueDurableLaunchIntent.prepareExplicit true first "WQ12" |> prepared
            Assert.That(original.ProjectId, Is.EqualTo(a1.ProjectId.ToString("D")))
            Assert.That(original.WorkId, Is.EqualTo(stage.Id))
            Assert.That(original.OriginBlobOid, Is.EqualTo(originOid))
            Assert.That(original.ProjectHead, Is.EqualTo(headBefore))
            Assert.That(original.SourceOperationKind, Is.EqualTo("CreateTask"))
            Assert.That(original.SourceRef, Is.Empty)
            Assert.That(original.SourceCommit, Is.Empty)
            Assert.That(original.AdmissionActor,
                        Is.EqualTo("WQ12.Prepared:" + original.PrepareOperationId))
            let uuidNames =
                [ original.PrepareOperationId; original.LaunchId; original.OriginalBrowserOperationId
                  original.AdmissionOperationId; original.AbortOperationId
                  original.ReleaseOperationId; original.DispatchOperationId
                  original.FirstConnectOperationId ]
            Assert.That((uuidNames |> List.map Guid.Parse |> Set.ofList).Count, Is.EqualTo(8))
            Assert.That(AccountCapacityLedger.occupied (AccountCapacityLedger.fetch account1 |> success).State,
                        Is.Zero, "Preparing an original must never reserve WQ07 capacity")
            let replay = WorkQueueDurableLaunchIntent.prepareExplicit true second "OtherClient" |> prepared
            Assert.That(replay, Is.EqualTo(original))
            let path = WorkQueueDurableLaunchIntent.intentPath original.ProjectId stage.Id stage.StageGeneration
            let snapshot = LedgerStore.fetch a2 |> success
            Assert.That((LedgerStore.readAt a2 snapshot path |> success |> fst).IsSome, Is.True)

            let foreignRole = { original with RoleId = "Foreign" }
            match WorkQueueDurableLaunchIntent.admitPreparedExplicit true first foreignRole "WQ12" with
            | TokenRetained _ -> ()
            | other -> Assert.Fail(sprintf "Contradictory role bypassed original intent: %A" other)

            let incomplete = { first with CompleteProjectSet = false }
            match WorkQueueDurableLaunchIntent.admitPreparedExplicit true incomplete original "WQ12" with
            | TokenRetained _ -> ()
            | other -> Assert.Fail(sprintf "Incomplete global membership admitted a launch: %A" other)

            match WorkQueueDurableLaunchIntent.admitPreparedExplicit true first original "Client A" with
            | ProjectAdmitted _ -> ()
            | other -> Assert.Fail(sprintf "Original durable launch was not admitted: %A" other)
            let state = WorkQueueRoleAuthority.load a2 |> success
            Assert.That(state.Project.Launches.Count, Is.EqualTo(1))
            Assert.That(state.Project.Launches.[original.LaunchId].Credit, Is.EqualTo(Some stage.Id))
            let cap = AccountCapacityLedger.fetch account2 |> success
            Assert.That(AccountCapacityLedger.occupied cap.State, Is.EqualTo(1))
            Assert.That(cap.State.Tokens.Length, Is.EqualTo(1))
            Assert.That(cap.State.Tokens.[0].AdmissionOperationId,
                        Is.EqualTo(original.AdmissionOperationId))

            match WorkQueueDurableLaunchIntent.admitPreparedExplicit true second original "Client B" with
            | ProjectAdmitted _ -> ()
            | other -> Assert.Fail(sprintf "Exact original UUID replay was not idempotent: %A" other)
            Assert.That((AccountCapacityLedger.fetch account1 |> success).State.Tokens.Length, Is.EqualTo(1))
            let _ = producedTask b1 b2 "later-other-project"
            match WorkQueueDurableLaunchIntent.prepareExplicit true second "WQ12" with
            | DurableIntentOutcome.NeedsAttention _ -> ()
            | other -> Assert.Fail(sprintf "Held account cap issued a second original: %A" other)
            Assert.That((AccountCapacityLedger.fetch account2 |> success).State.Tokens.Length, Is.EqualTo(1)))

    [<Test>]
    member _.AccountHeldWithoutProjectAdmissionResumesSameOriginalFromSecondClient() =
        withBindings (fun (a1,a2) (b1,b2) (account1,account2) ->
            let stage, _ = producedTask a1 a2 "partial-wq07-original"
            let first = context account1 a1 b1
            let second = context account2 b2 a2
            let original =
                WorkQueueDurableLaunchIntent.prepareExplicit true first "Preparing client A"
                |> prepared
            // Model lost WQ04 project-CAS acknowledgment after WQ07's
            // original reservation was published. No project admission exists.
            let held =
                AccountCapacityLedger.reserve account1
                    (Guid.Parse original.AdmissionOperationId)
                    original.AdmissionActor
                    (Guid.Parse original.LaunchId)
                    (AccountAdmissionAdapter.projectAuthority a1)
                    original.RoleId (int64 original.RoleVersion)
                |> success
            Assert.That(held.AlreadyApplied, Is.False)
            Assert.That((WorkQueueRoleAuthority.load a1 |> success).Project.Launches.Count,
                        Is.Zero)
            let occupied = AccountCapacityLedger.fetch account2 |> success
            Assert.That(AccountCapacityLedger.occupied occupied.State, Is.EqualTo(1))
            Assert.That(occupied.State.Tokens.Length, Is.EqualTo(1))

            // Client B has a different caller identity and checkout. The
            // wrapper must re-use the original held token rather than trying
            // fresh global selection, which cannot pass while cap=1 is held.
            match WorkQueueDurableLaunchIntent.admitPreparedExplicit true second original "Client B" with
            | ProjectAdmitted _ -> ()
            | other -> Assert.Fail(sprintf "WQ07-only partial original did not recover: %A" other)
            let view = WorkQueueRoleAuthority.load a1 |> success
            Assert.That(view.Project.Launches.Count, Is.EqualTo(1))
            Assert.That(view.Project.Launches.[original.LaunchId].Credit, Is.EqualTo(Some stage.Id))
            let after = AccountCapacityLedger.fetch account1 |> success
            Assert.That(after.State.Tokens.Length, Is.EqualTo(1))
            Assert.That(AccountCapacityLedger.occupied after.State, Is.EqualTo(1))
            match WorkQueueDurableLaunchIntent.admitPreparedExplicit true first original "Client A again" with
            | ProjectAdmitted _ -> ()
            | other -> Assert.Fail(sprintf "Recovered original could not replay: %A" other))

    [<Test>]
    member _.HeldTokenWithDifferentOriginalActorCannotReuseAdmissionOperation() =
        withBindings (fun (a1,a2) (b1,b2) (account1,account2) ->
            let _, _ = producedTask a1 a2 "mismatched-wq07-actor"
            let first = context account1 a1 b1
            let second = context account2 b2 a2
            let original =
                WorkQueueDurableLaunchIntent.prepareExplicit true first "Preparing client A"
                |> prepared
            // A conflicting original WQ07 receipt must never be reinterpreted
            // as the canonical prepared request, even if its token fields match.
            AccountCapacityLedger.reserve account1
                (Guid.Parse original.AdmissionOperationId) "DifferentOriginalActor"
                (Guid.Parse original.LaunchId)
                (AccountAdmissionAdapter.projectAuthority a1)
                original.RoleId (int64 original.RoleVersion)
            |> success |> ignore
            match WorkQueueDurableLaunchIntent.admitPreparedExplicit true second original "Client B" with
            | GlobalReservationRejected AccountOperationIdReused -> ()
            | other -> Assert.Fail(sprintf "Conflicting original actor bypassed WQ07: %A" other)
            Assert.That((WorkQueueRoleAuthority.load a1 |> success).Project.Launches.Count,
                        Is.Zero)
            let after = AccountCapacityLedger.fetch account2 |> success
            Assert.That(after.State.Tokens.Length, Is.EqualTo(1))
            Assert.That(AccountCapacityLedger.occupied after.State, Is.EqualTo(1)))

    [<Test>]
    member _.ConcurrentProjectGitClientsShareOneCanonicalOriginalIdentity() =
        withBindings (fun (a1,a2) (b1,b2) (account1,account2) ->
            let stage, _ = producedTask a1 a2 "same-origin-race"
            let one = context account1 a1 b1
            let two = context account2 b2 a2
            let jobs =
                [| Task.Run(fun () -> WorkQueueDurableLaunchIntent.prepareExplicit true one "WQ12 A")
                   Task.Run(fun () -> WorkQueueDurableLaunchIntent.prepareExplicit true two "WQ12 B") |]
            jobs |> Array.map (fun job -> job :> Task) |> Task.WaitAll
            let persisted =
                WorkQueueDurableLaunchIntent.readPrepared one a1 stage.Id stage.StageGeneration |> prepared
            for task in jobs do
                match task.Result with
                | DurableIntentOutcome.Prepared (candidate, _, _) ->
                    Assert.That(candidate, Is.EqualTo(persisted))
                | DurableIntentOutcome.NeedsAttention reason ->
                    Assert.That(reason, Does.Contain("reconcile"),
                                "CAS losers must preserve original identity for reconciliation")
                | other -> Assert.Fail(sprintf "Concurrent CAS did not fail closed: %A" other)
            Assert.That((AccountCapacityLedger.fetch account1 |> success).State.Tokens, Is.Empty)
            let snapshot = LedgerStore.fetch a2 |> success
            let opCount = snapshot.Header.NextSequence
            let again = WorkQueueDurableLaunchIntent.prepareExplicit true two "WQ12 retry" |> prepared
            Assert.That(again, Is.EqualTo(persisted))
            Assert.That((LedgerStore.fetch a1 |> success).Header.NextSequence, Is.EqualTo(opCount)))

    [<Test>]
    member _.BlockedCompositionAndInvalidProjectMembershipNeverPrepareAnOriginal() =
        withBindings (fun (a1,a2) (b1,b2) (account1,_) ->
            let stage, _ = producedTask a1 a2 "composed-blocked"
            let spec title linked =
                { Title = title; Goal = title; Scope = "WQ12"
                  AcceptanceCriteria = set ["Verified prereq"]; Constraints = Set.empty
                  LinkedTaskId = linked }
            let mutations =
                [ WorkCompositionMutation.Create("root", WorkUnitKind.Project, None, spec "Root" None)
                  WorkCompositionMutation.Create("blocked", WorkUnitKind.Feature, Some "root",
                                                 spec "Blocked" (Some stage.SubjectId)) ]
            mutations |> List.iteri (fun version mutation ->
                WorkCompositionGitLedger.execute a1 "WQ12" (Guid.NewGuid()) version mutation
                |> success |> ignore)
            let valid = context account1 a1 b1
            match WorkQueueDurableLaunchIntent.prepareExplicit true valid "WQ12" with
            | DurableIntentOutcome.NeedsAttention text ->
                Assert.That(text, Does.Contain("composition"))
            | other -> Assert.Fail(sprintf "Composed task crossed unverified graph gate: %A" other)
            let missing = { valid with CompleteProjectSet = false }
            match WorkQueueDurableLaunchIntent.prepareExplicit true missing "WQ12" with
            | DurableIntentOutcome.NeedsAttention _ -> ()
            | other -> Assert.Fail(sprintf "Incomplete membership minted a launch: %A" other)
            Assert.That((AccountCapacityLedger.fetch account1 |> success).State.Tokens, Is.Empty)
            match WorkQueueDurableLaunchIntent.readPrepared valid a1 stage.Id stage.StageGeneration with
            | DurableIntentOutcome.NeedsAttention _ -> ()
            | other -> Assert.Fail(sprintf "Rejected demand persisted an original: %A" other))

    [<Test>]
    member _.ReplacedOriginalGitRecordCannotRecyclePreparedBrowserIdentity() =
        withBindings (fun (a1,a2) (b1,_) (account1,_) ->
            let stage, _ = producedTask a1 a2 "immutable-original"
            let approved = context account1 a1 b1
            let original = WorkQueueDurableLaunchIntent.prepareExplicit true approved "WQ12" |> prepared
            let path = WorkQueueDurableLaunchIntent.intentPath original.ProjectId stage.Id stage.StageGeneration
            let head = LedgerStore.fetch a2 |> success
            let _, oid = LedgerStore.readAt a2 head path |> success
            let replacement =
                { original with OriginalBrowserOperationId = Guid.NewGuid().ToString("D") }
            let change: LedgerRecordChange =
                { OperationId = Guid.NewGuid(); Actor = "WQ12 disposable tamper"
                  Path = path; ExpectedBlobOid = oid
                  Content = CanonicalCodec.encode replacement }
            LedgerStore.putRecord a2 change |> success |> ignore
            match WorkQueueDurableLaunchIntent.readPrepared approved a1 stage.Id stage.StageGeneration with
            | DurableIntentOutcome.NeedsAttention detail ->
                Assert.That(detail, Does.Contain("replaced"))
            | other -> Assert.Fail(sprintf "A replaced original was treated as immutable: %A" other)
            Assert.That((AccountCapacityLedger.fetch account1 |> success).State.Tokens, Is.Empty))
