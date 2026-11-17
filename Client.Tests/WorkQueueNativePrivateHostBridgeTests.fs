module Client.Tests.WorkQueueNativePrivateHostBridgeTests

open System
open System.Diagnostics
open System.IO
open NUnit.Framework
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.WorkQueueGitLedger
open Client.WorkQueueReceiptProducer
open Client.WorkQueueRoleAuthority
open Client.WorkQueueDurableLaunchIntent
open Client.WorkQueueNativeCredentialDispatchAdapter
open Client.WorkQueueNativePrivateHostBridge
open Client.OriginalBrowserJournal
open Client.AccountCapacityLedger
open Jarvis.WorkQueues

// No live host, public endpoint or real provider invocation. All Git authorities
// are fresh throwaway repositories with two independent clones.
[<TestFixture>]
type WorkQueueNativePrivateHostBridgeTests() =
    let ok = function Ok value -> value | Error e -> failwithf "Fixture: %A" e

    let git dir args =
        use proc = new Process()
        proc.StartInfo.FileName <- "git"
        proc.StartInfo.WorkingDirectory <- dir
        proc.StartInfo.UseShellExecute <- false
        proc.StartInfo.RedirectStandardOutput <- true
        proc.StartInfo.RedirectStandardError <- true
        for arg in args do proc.StartInfo.ArgumentList.Add(arg)
        Assert.That(proc.Start(), Is.True)
        let output = proc.StandardOutput.ReadToEnd()
        let error = proc.StandardError.ReadToEnd()
        Assert.That(proc.WaitForExit(30000), Is.True)
        Assert.That(proc.ExitCode, Is.EqualTo(0), error)
        output.Trim()

    let withAuthorities run =
        let root = Path.Combine(Path.GetTempPath(), "native-private-host-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try
            let pair name =
                let remote = Path.Combine(root, name + ".git")
                let first = Path.Combine(root, name + "-first")
                let second = Path.Combine(root, name + "-second")
                git root ["init"; "--bare"; "--initial-branch=main"; remote] |> ignore
                git root ["clone"; remote; first] |> ignore
                File.WriteAllText(Path.Combine(first, "witness.txt"), name)
                git first ["add"; "witness.txt"] |> ignore
                git first ["-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.test";
                           "commit"; "-m"; "fixture"] |> ignore
                git first ["push"; "-u"; "origin"; "HEAD"] |> ignore
                git root ["clone"; remote; second] |> ignore
                let projectId, ledgerId = Guid.NewGuid(), Guid.NewGuid()
                let binding path : LedgerBinding =
                    { RepositoryPath = path; RemoteName = "origin"
                      ApprovedFetchUrl = remote; ApprovedPushUrl = remote
                      ProjectId = projectId; LedgerId = ledgerId; Epoch = 1 }
                binding first, binding second
            let p1, p2 = pair "project"
            let q1, q2 = pair "second-project"
            let ca, cb = pair "account"
            let accountId, accountLedgerId = Guid.NewGuid(), Guid.NewGuid()
            let account (source: LedgerBinding) : AccountBinding =
                { RepositoryPath = source.RepositoryPath; RemoteName = source.RemoteName
                  ApprovedFetchUrl = source.ApprovedFetchUrl; ApprovedPushUrl = source.ApprovedPushUrl
                  AccountId = accountId; LedgerId = accountLedgerId; Epoch = 1 }
            let account1, account2 = account ca, account cb
            for first, second in [p1,p2; q1,q2] do
                LedgerStore.initialize first true |> ok |> ignore
                LedgerStore.fetch second |> ok |> ignore
                WorkQueueGitLedger.initialize first "private fake fixture" (Guid.NewGuid())
                    (Map.ofList ["Lead", set ["planning"]]) |> ok |> ignore
                ReviewWorkflow.registerAgent first (Guid.NewGuid()) "Lead" |> ok |> ignore
                WorkQueueRoleAuthority.initializeCatalog first "Lead" (Guid.NewGuid()) |> ok |> ignore
                WorkQueueRoleAuthority.configure first "Lead" (Guid.NewGuid()) 0 0
                    { RoleId = "Lead"; Version = 1; Enabled = true
                      Instructions = "Offline only"; QueueSubscriptions = set ["planning"] }
                |> ok |> ignore
            AccountCapacityLedger.initialize account1 1 |> ok |> ignore
            AccountCapacityLedger.fetch account2 |> ok |> ignore
            let journalRoot = Path.Combine(root, "original-journal")
            Directory.CreateDirectory journalRoot |> ignore
            OriginalBrowserJournal.provisionTrustedHostRoot journalRoot |> ok |> ignore
            run p1 p2 q1 q2 account1 account2 journalRoot
            for path in [p1.RepositoryPath; p2.RepositoryPath; q1.RepositoryPath; q2.RepositoryPath] do
                Assert.That(git path ["status"; "--porcelain"], Is.Empty)
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    let context account project secondProject : DurableIntentContext =
        { Account = account; CompleteProjectSet = true
          Projects = [ { Binding = project; OriginalJournal = None }
                       { Binding = secondProject; OriginalJournal = None } ] }

    let original first firstOther second account =
        let taskId = "private-bridge-" + Guid.NewGuid().ToString("N")
        let operation = Guid.NewGuid()
        ReviewWorkflow.createTask first operation taskId "Disposable planning source"
        |> ok |> ignore
        WQ04ProducerFixture.produce firstOther "Lead" (Guid.NewGuid()) operation
        |> ok |> ignore
        match WorkQueueDurableLaunchIntent.prepareExplicit true
                  (context account first second) "private fake" with
        | DurableIntentOutcome.Prepared (prepared, _, _) -> prepared
        | other -> failwithf "No original WQ03 prepared intent: %A" other

    let ticket = function
        | NativeCredentialAdmission.Ready value -> value
        | other -> failwithf "Expected disposable credential admission: %A" other

    [<Test>]
    member _.DefaultOffAndEnabledProductionBothFailClosedWithExactMissingInterfaces() =
        withAuthorities (fun p1 p2 q1 _ account1 account2 _ ->
            let prepared = original p1 p2 q1 account1
            let ctx = context account1 p1 q1
            let beforeProject = (LedgerStore.fetch p2 |> ok).Commit
            let beforeAccount = (AccountCapacityLedger.fetch account2 |> ok).Head
            Assert.That(WorkQueueNativePrivateHostBridge.inspectProduction false,
                        Is.EqualTo PrivateHostGate.Disabled)
            match WorkQueueNativePrivateHostBridge.inspectProduction true with
            | PrivateHostGate.HostBridgeUnavailable info ->
                Assert.That(info.Code, Is.EqualTo "HostBridgeUnavailable")
                Assert.That(info.MissingInterfaces, Is.EquivalentTo [
                    MissingPrivateHostInterface.ProviderPrivateExecutionBootstrap
                    MissingPrivateHostInterface.AuthenticatedProviderExecutionBinding
                    MissingPrivateHostInterface.CredentialAuthenticatedWorkFirstConnect ])
                Assert.That(info.BrowserInterface, Does.Contain("USER_SCRIPT"))
                Assert.That(info.WorkFirstConnectInterface, Does.Contain("executeCredentialFixtureAt"))
                Assert.That(info.IntegrationOwner, Does.Contain("Provider"))
            | other -> Assert.Fail(sprintf "Private provider not proven: %A" other)
            for _ in 1..2 do
                Assert.That(WorkQueueNativeCredentialDispatchAdapter.prepareProduction ctx prepared,
                            Is.EqualTo NativeCredentialAdmission.HostBridgeUnavailable)
            Assert.That((WorkQueueGitLedger.load p2 |> ok |> snd).Launches.IsEmpty, Is.True)
            Assert.That(WorkQueueGitLedger.readCredentialAt p2 (LedgerStore.fetch p2 |> ok)
                            prepared.LaunchId |> ok, Is.EqualTo None)
            Assert.That((LedgerStore.fetch p1 |> ok).Commit, Is.EqualTo beforeProject)
            Assert.That((AccountCapacityLedger.fetch account1 |> ok).Head,
                        Is.EqualTo beforeAccount)
            Assert.That((AccountCapacityLedger.fetch account2 |> ok).State.Tokens, Is.Empty))

    [<Test>]
    member _.TwoCloneFakeProviderConsumesOnlyMatchingExecutionOnceThenConnectReplayStop() =
        withAuthorities (fun p1 p2 q1 _ account1 account2 journal ->
            let prepared = original p1 p2 q1 account1
            let ctx = context account1 p1 q1
            let agentsBefore =
                git p1.RepositoryPath ["ls-tree"; "-r"; "--name-only";
                    (LedgerStore.fetch p1 |> ok).Commit; "agents"]
            let admitted =
                WorkQueueNativeCredentialDispatchAdapter.prepareDisposable
                    true true ctx prepared "fake-only" "host-execution-A" |> ticket
            let cloneHead = LedgerStore.fetch p2 |> ok
            let op = LedgerStore.getOperationAt p2 cloneHead
                        (Guid.Parse prepared.AdmissionOperationId) |> ok |> Option.get
            Assert.That(op.Paths, Has.Length.EqualTo 4)
            Assert.That(op.Paths, Does.Contain(WorkQueueGitLedger.credentialPath prepared.LaunchId))
            let mutable calls = 0
            let mutable privateBearer : string option = None
            let provider (_: OriginalBrowserIntent) (bootstrap: NativePrivateBootstrap) =
                calls <- calls + 1
                Assert.That(bootstrap.TakeOnceFor("other-tab"), Is.EqualTo None)
                Assert.That(bootstrap.TakeOnceFor("other-launch"), Is.EqualTo None)
                privateBearer <- bootstrap.TakeOnceFor("host-execution-A")
                Assert.That(privateBearer.IsSome, Is.True)
                Assert.That(bootstrap.TakeOnceFor("host-execution-A"), Is.EqualTo None)
                NativeProviderReply.Accepted "fake-provider-only"
            let submitted =
                WorkQueueNativeCredentialDispatchAdapter.submitDisposable true journal admitted provider
            Assert.That(submitted.Phase, Is.EqualTo NativeDispatchPhase.AwaitFirstConnect, submitted.Detail)
            let replay = WorkQueueNativeCredentialDispatchAdapter.submitDisposable true journal admitted provider
            Assert.That(replay.Phase, Is.EqualTo NativeDispatchPhase.NoResend)
            Assert.That(calls, Is.EqualTo 1)
            let bearer = privateBearer.Value
            let connect exec secret =
                WorkQueueNativeCredentialDispatchAdapter.firstConnectDisposable true journal
                    admitted exec secret
            Assert.That((connect "other-tab" bearer).Phase, Is.EqualTo NativeDispatchPhase.Retained)
            Assert.That((connect "host-execution-A"
                            (IssuedWorkCredential.Issue(prepared.LaunchId).RevealForPrivateDelivery())).Phase,
                        Is.EqualTo NativeDispatchPhase.Retained)
            Assert.That((connect "host-execution-A" bearer).Phase, Is.EqualTo NativeDispatchPhase.Connected)
            let connected = WorkQueueGitLedger.load p2 |> ok |> snd
            Assert.That((connect "host-execution-A" bearer).Phase, Is.EqualTo NativeDispatchPhase.Connected)
            Assert.That((WorkQueueGitLedger.load p1 |> ok |> snd).Version, Is.EqualTo connected.Version)
            // The original credential must not authorize a different launch.
            let beforeForeign = (LedgerStore.fetch p2 |> ok).Commit
            let foreignLaunchId = Guid.NewGuid().ToString("D")
            match WorkQueueGitLedger.executeCredentialFixtureAt p2 "fake-only" (Guid.NewGuid())
                    connected.Version (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                    foreignLaunchId bearer CredentialWorkAction.ClaimPrimary with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Foreign launch must reject bearer: %A" other)
            Assert.That((LedgerStore.fetch p1 |> ok).Commit, Is.EqualTo beforeForeign)
            Assert.That(git p1.RepositoryPath ["ls-tree"; "-r"; "--name-only";
                (LedgerStore.fetch p1 |> ok).Commit; "agents"], Is.EqualTo agentsBefore)
            let lease = connected.Launches.[prepared.LaunchId].Lease.Value
            WorkQueueGitLedger.executeInertFixtureAt p2 "fixture operator STOP" (Guid.NewGuid())
                connected.Version (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1000L)
                (StopStalled(prepared.LaunchId, lease.Generation, 500L)) |> ok |> ignore
            let stopped = WorkQueueGitLedger.load p1 |> ok |> snd
            match WorkQueueGitLedger.executeCredentialFixtureAt p2 "fake-only" (Guid.NewGuid())
                    stopped.Version (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                    prepared.LaunchId bearer CredentialWorkAction.ClaimPrimary with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "STOP must revoke future claims: %A" other)
            Assert.That((AccountCapacityLedger.fetch account2 |> ok).State.Tokens.Length, Is.EqualTo 1)
            match WorkQueueNativePrivateHostBridge.inspectProduction true with
            | PrivateHostGate.HostBridgeUnavailable _ -> ()
            | other -> Assert.Fail(sprintf "Fake success must not enable production: %A" other))

    [<Test>]
    member _.TwoCloneUnknownFakeSendDoesNotReissueOrFirstConnect() =
        withAuthorities (fun p1 p2 q1 _ account1 account2 journal ->
            let prepared = original p1 p2 q1 account1
            let admitted =
                WorkQueueNativeCredentialDispatchAdapter.prepareDisposable true true
                    (context account1 p1 q1) prepared "fake-only" "host-execution-U" |> ticket
            let mutable sends = 0
            let mutable privateBearer : string option = None
            let provider (_: OriginalBrowserIntent) (bootstrap: NativePrivateBootstrap) =
                sends <- sends + 1
                privateBearer <- bootstrap.TakeOnceFor("host-execution-U")
                Assert.That(privateBearer.IsSome, Is.True)
                NativeProviderReply.Unknown
            let outcome = WorkQueueNativeCredentialDispatchAdapter.submitDisposable true journal admitted provider
            Assert.That(outcome.Phase, Is.EqualTo NativeDispatchPhase.Retained)
            Assert.That((WorkQueueNativeCredentialDispatchAdapter.submitDisposable true journal admitted provider).Phase,
                        Is.EqualTo NativeDispatchPhase.NoResend)
            Assert.That(sends, Is.EqualTo 1)
            Assert.That((WorkQueueNativeCredentialDispatchAdapter.firstConnectDisposable true journal
                            admitted "host-execution-U" privateBearer.Value).Phase,
                        Is.EqualTo NativeDispatchPhase.Retained,
                        "Unknown original send cannot authenticate first connect")
            Assert.That((WorkQueueGitLedger.load p2 |> ok |> snd).Launches.[prepared.LaunchId].State,
                        Is.EqualTo (LaunchState.SubmittedUnregistered DispatchState.OutcomeUnknown))
            Assert.That((AccountCapacityLedger.fetch account2 |> ok).State.Tokens.Length, Is.EqualTo 1)
            let journalStore = OriginalBrowserJournal.openExisting journal |> ok
            match OriginalBrowserJournal.findByLaunch journalStore prepared.LaunchId |> ok with
            | Some row ->
                match row.Outcome with
                | Some(OriginalBrowserOutcome.Unknown _) -> ()
                | other -> Assert.Fail(sprintf "Unknown send evidence lost: %A" other)
            | None -> Assert.Fail "Original durable marker lost")
