module Client.Tests.WorkQueueNativeCredentialDispatchAdapterTests

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
open Client.OriginalBrowserJournal
open Client.AccountCapacityLedger
open Jarvis.WorkQueues

// Explicit disposable tests only: two real Git clones per project/account,
// fake private provider and original WQ03 host journal. No live browser/scheduler.
[<TestFixture>]
type WorkQueueNativeCredentialDispatchAdapterTests() =
    let ok = function Ok x -> x | Error e -> failwithf "Work-native fixture: %A" e

    let git dir args =
        use p = new Process()
        p.StartInfo.FileName <- "git"
        p.StartInfo.WorkingDirectory <- dir
        p.StartInfo.UseShellExecute <- false
        p.StartInfo.RedirectStandardOutput <- true
        p.StartInfo.RedirectStandardError <- true
        for arg in args do p.StartInfo.ArgumentList.Add arg
        Assert.That(p.Start(), Is.True)
        let output = p.StandardOutput.ReadToEnd()
        let error = p.StandardError.ReadToEnd()
        Assert.That(p.WaitForExit(30000), Is.True)
        Assert.That(p.ExitCode, Is.EqualTo 0, error)
        output.Trim()

    let withAuthorities run =
        let root = Path.Combine(Path.GetTempPath(), "work-native-credential-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let paths = ResizeArray<string>()
        try
            let pair label =
                let remote = Path.Combine(root, label + ".git")
                let a = Path.Combine(root, label + "a")
                let b = Path.Combine(root, label + "b")
                git root ["init"; "--bare"; "--initial-branch=main"; remote] |> ignore
                git root ["clone"; remote; a] |> ignore
                File.WriteAllText(Path.Combine(a, "source.txt"), label)
                git a ["add"; "source.txt"] |> ignore
                git a ["-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.test"; "commit"; "-m"; "fixture"]
                |> ignore
                git a ["push"; "-u"; "origin"; "HEAD"] |> ignore
                git root ["clone"; remote; b] |> ignore
                paths.Add a
                paths.Add b
                let projectId, ledgerId = Guid.NewGuid(), Guid.NewGuid()
                let binding path : LedgerBinding =
                    { RepositoryPath = path; RemoteName = "origin"
                      ApprovedFetchUrl = remote; ApprovedPushUrl = remote
                      ProjectId = projectId; LedgerId = ledgerId; Epoch = 1 }
                binding a, binding b
            let a1,a2 = pair "project-a"
            let b1,b2 = pair "project-b"
            let c1,c2 = pair "account"
            let accountId, accountLedgerId = Guid.NewGuid(), Guid.NewGuid()
            let account (binding: LedgerBinding) : AccountBinding =
                { RepositoryPath = binding.RepositoryPath; RemoteName = binding.RemoteName
                  ApprovedFetchUrl = binding.ApprovedFetchUrl; ApprovedPushUrl = binding.ApprovedPushUrl
                  AccountId = accountId; LedgerId = accountLedgerId; Epoch = 1 }
            let account1,account2 = account c1, account c2
            for first,second in [a1,a2; b1,b2] do
                LedgerStore.initialize first true |> ok |> ignore
                LedgerStore.fetch second |> ok |> ignore
                WorkQueueGitLedger.initialize first "credential native fixture" (Guid.NewGuid())
                    (Map.ofList ["Lead", set ["planning"]]) |> ok |> ignore
                ReviewWorkflow.registerAgent first (Guid.NewGuid()) "Lead" |> ok |> ignore
                WorkQueueRoleAuthority.initializeCatalog first "Lead" (Guid.NewGuid()) |> ok |> ignore
                WorkQueueRoleAuthority.configure first "Lead" (Guid.NewGuid()) 0 0
                    { RoleId = "Lead"; Version = 1; Enabled = true
                      Instructions = "Offline cooperative execution"
                      QueueSubscriptions = set ["planning"] }
                |> ok |> ignore
            AccountCapacityLedger.initialize account1 1 |> ok |> ignore
            AccountCapacityLedger.fetch account2 |> ok |> ignore
            let journalRoot = Path.Combine(root, "journal")
            Directory.CreateDirectory journalRoot |> ignore
            OriginalBrowserJournal.provisionTrustedHostRoot journalRoot |> ok |> ignore
            run (a1,a2) (b1,b2) (account1,account2) journalRoot
            for path in paths do
                Assert.That(git path ["status"; "--porcelain"], Is.Empty, "Source checkout dirtied")
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    let context account a b : DurableIntentContext =
        { Account = account; CompleteProjectSet = true
          Projects = [ { Binding = a; OriginalJournal = None }
                       { Binding = b; OriginalJournal = None } ] }

    let original a1 a2 b1 account =
        let taskId = "native-" + Guid.NewGuid().ToString("N")
        let sourceOp = Guid.NewGuid()
        ReviewWorkflow.createTask a1 sourceOp taskId "Native disposable planning" |> ok |> ignore
        WQ04ProducerFixture.produce a2 "Lead" (Guid.NewGuid()) sourceOp |> ok |> ignore
        let ctx = context account a1 b1
        match WorkQueueDurableLaunchIntent.prepareExplicit true ctx "native fixture" with
        | DurableIntentOutcome.Prepared (prepared,_,_) -> prepared
        | other -> failwithf "Original was not prepared: %A" other

    let ready = function
        | NativeCredentialAdmission.Ready ticket -> ticket
        | other -> failwithf "Expected original credential reservation: %A" other

    [<Test>]
    member _.DefaultOffAndMissingProviderPrivateBridgeNeverReserveOrSend() =
        withAuthorities (fun (a1,a2) (b1,_) (account,_) _ ->
            let ctx = context account a1 b1
            let p = original a1 a2 b1 account
            Assert.That(WorkQueueNativeCredentialDispatchAdapter.prepareProduction ctx p,
                        Is.EqualTo NativeCredentialAdmission.HostBridgeUnavailable)
            Assert.That(WorkQueueNativeCredentialDispatchAdapter.prepareDisposable false true ctx p "actor" "exec-1",
                        Is.EqualTo NativeCredentialAdmission.Disabled)
            Assert.That(WorkQueueNativeCredentialDispatchAdapter.prepareDisposable true false ctx p "actor" "exec-1",
                        Is.EqualTo NativeCredentialAdmission.HostBridgeUnavailable)
            Assert.That((WorkQueueGitLedger.load a1 |> ok |> snd).Launches.IsEmpty, Is.True)
            Assert.That((AccountCapacityLedger.fetch account |> ok).State.Tokens, Is.Empty))

    [<Test>]
    member _.TwoCloneOriginalCasOneShotDeliveryConnectReplayAndStop() =
        withAuthorities (fun (a1,a2) (b1,_) (account1, account2) journalRoot ->
            let p = original a1 a2 b1 account1
            let ctx = context account1 a1 b1
            let initialAgentFiles =
                let head = (LedgerStore.fetch a1 |> ok).Commit
                git a1.RepositoryPath ["ls-tree"; "-r"; "--name-only"; head; "agents"]
            let ticket =
                WorkQueueNativeCredentialDispatchAdapter.prepareDisposable true true ctx p "native" "exec-A"
                |> ready
            let before = LedgerStore.fetch a2 |> ok
            let _, admitted = WorkQueueGitLedger.load a2 |> ok
            Assert.That(admitted.Launches.Count, Is.EqualTo(1))
            let verifier, _ = WorkQueueGitLedger.readCredentialAt a2 before p.LaunchId
                              |> ok |> Option.get
            let op = LedgerStore.getOperationAt a2 before (Guid.Parse p.AdmissionOperationId)
                     |> ok |> Option.get
            Assert.That(op.Paths, Has.Length.EqualTo 4, "Verifier must be in original four-path CAS")
            Assert.That(op.Paths, Does.Contain(WorkQueueGitLedger.credentialPath p.LaunchId))
            Assert.That(verifier.SecretSha256, Is.EqualTo(ticket.Issued.VerifierSha256))
            let persisted, _ = LedgerStore.readAt a2 before (WorkQueueGitLedger.credentialPath p.LaunchId) |> ok
            Assert.That(persisted.Value, Does.Not.Contain(ticket.Issued.RevealForPrivateDelivery()))
            let mutable providerCalls = 0
            let mutable privateBearer = None
            let provider (_: OriginalBrowserIntent) (bootstrap: NativePrivateBootstrap) =
                providerCalls <- providerCalls + 1
                Assert.That(bootstrap.ToString(), Does.Not.Contain(ticket.Issued.RevealForPrivateDelivery()))
                Assert.That(bootstrap.TakeOnceFor("wrong-execution"), Is.EqualTo(None))
                privateBearer <- bootstrap.TakeOnceFor("exec-A")
                Assert.That(privateBearer.IsSome, Is.True)
                Assert.That(bootstrap.TakeOnceFor("exec-A"), Is.EqualTo(None))
                NativeProviderReply.Accepted "private-execution-receipt"
            let sent =
                WorkQueueNativeCredentialDispatchAdapter.submitDisposable true journalRoot ticket provider
            Assert.That(sent.Phase, Is.EqualTo NativeDispatchPhase.AwaitFirstConnect, sent.Detail)
            Assert.That(providerCalls, Is.EqualTo 1)
            let replay =
                WorkQueueNativeCredentialDispatchAdapter.submitDisposable true journalRoot ticket provider
            Assert.That(replay.Phase, Is.EqualTo NativeDispatchPhase.NoResend)
            Assert.That(providerCalls, Is.EqualTo 1)
            let bearer = privateBearer.Value
            let foreign =
                WorkQueueNativeCredentialDispatchAdapter.firstConnectDisposable true journalRoot ticket
                    "different-session" bearer
            Assert.That(foreign.Phase, Is.EqualTo NativeDispatchPhase.Retained)
            let wrong =
                WorkQueueNativeCredentialDispatchAdapter.firstConnectDisposable true journalRoot ticket
                    "exec-A" (IssuedWorkCredential.Issue(p.LaunchId).RevealForPrivateDelivery())
            Assert.That(wrong.Phase, Is.EqualTo NativeDispatchPhase.Retained)
            let connected =
                WorkQueueNativeCredentialDispatchAdapter.firstConnectDisposable true journalRoot ticket "exec-A" bearer
            Assert.That(connected.Phase, Is.EqualTo NativeDispatchPhase.Connected, connected.Detail)
            let stateAfter = WorkQueueGitLedger.load a2 |> ok |> snd
            let again =
                WorkQueueNativeCredentialDispatchAdapter.firstConnectDisposable true journalRoot ticket "exec-A" bearer
            Assert.That(again.Phase, Is.EqualTo NativeDispatchPhase.Connected, again.Detail)
            Assert.That((WorkQueueGitLedger.load a1 |> ok |> snd).Version, Is.EqualTo stateAfter.Version,
                        "Same first-connect UUID must not mutate on replay")
            let agentFilesAfter =
                let head = (LedgerStore.fetch a1 |> ok).Commit
                git a1.RepositoryPath ["ls-tree"; "-r"; "--name-only"; head; "agents"]
            Assert.That(agentFilesAfter, Is.EqualTo initialAgentFiles,
                        "Work-native launch may not create persistent agents")
            let launch = stateAfter.Launches.[p.LaunchId]
            Assert.That(launch.Lease.Value.Generation, Is.EqualTo 1)
            WorkQueueGitLedger.executeInertFixtureAt a2 "operator STOP" (Guid.NewGuid())
                stateAfter.Version (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1000L)
                (StopStalled(p.LaunchId, 1, 500L)) |> ok |> ignore
            let stopped = WorkQueueGitLedger.load a1 |> ok |> snd
            match WorkQueueGitLedger.executeCredentialFixtureAt a2 "native" (Guid.NewGuid())
                      stopped.Version (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                      p.LaunchId bearer CredentialWorkAction.ClaimPrimary with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "STOP did not revoke future credential action: %A" other)
            Assert.That((AccountCapacityLedger.fetch account2 |> ok).State.Tokens.Length, Is.EqualTo 1))

    [<Test>]
    member _.UnknownSendNeverReissuesOriginalOrReinvokesProvider() =
        withAuthorities (fun (a1,a2) (b1,_) (account1,account2) journalRoot ->
            let p = original a1 a2 b1 account1
            let ticket =
                WorkQueueNativeCredentialDispatchAdapter.prepareDisposable true true
                    (context account1 a1 b1) p "native" "exec-unknown"
                |> ready
            let mutable calls = 0
            let provider (_: OriginalBrowserIntent) (bootstrap: NativePrivateBootstrap) =
                calls <- calls + 1
                Assert.That(bootstrap.TakeOnceFor("exec-unknown").IsSome, Is.True)
                NativeProviderReply.Unknown
            let first =
                WorkQueueNativeCredentialDispatchAdapter.submitDisposable true journalRoot ticket provider
            Assert.That(first.Phase, Is.EqualTo NativeDispatchPhase.Retained, first.Detail)
            let second =
                WorkQueueNativeCredentialDispatchAdapter.submitDisposable true journalRoot ticket provider
            Assert.That(second.Phase, Is.EqualTo NativeDispatchPhase.NoResend, second.Detail)
            Assert.That(calls, Is.EqualTo 1)
            let journal = OriginalBrowserJournal.openExisting journalRoot |> ok
            match OriginalBrowserJournal.findByLaunch journal p.LaunchId |> ok with
            | Some row ->
                match row.Outcome with
                | Some(OriginalBrowserOutcome.Unknown _) -> ()
                | other -> Assert.Fail(sprintf "Unknown send not preserved: %A" other)
            | None -> Assert.Fail "Original one-shot journal marker disappeared"
            let state = WorkQueueGitLedger.load a2 |> ok |> snd
            Assert.That(state.Launches.Count, Is.EqualTo 1)
            Assert.That(state.Launches.[p.LaunchId].State,
                        Is.EqualTo(LaunchState.SubmittedUnregistered DispatchState.OutcomeUnknown))
            Assert.That((AccountCapacityLedger.fetch account2 |> ok).State.Tokens.Length, Is.EqualTo 1)
            Assert.That(WorkQueueNativeCredentialDispatchAdapter.prepareProduction (context account2 a2 b1) p,
                        Is.EqualTo NativeCredentialAdmission.HostBridgeUnavailable))

    [<Test>]
    member _.LostOriginalCredentialAdmissionAckPreservesOneBearerAndRejectsReissue() =
        withAuthorities (fun (a1,a2) (b1,_) (account1,account2) _ ->
            let p = original a1 a2 b1 account1
            let firstContext = context account1 a1 b1
            let result =
                WorkQueueGitLedger.withTestPublishedAckLoss
                    (fun op -> op = Guid.Parse p.AdmissionOperationId)
                    (fun () ->
                        WorkQueueNativeCredentialDispatchAdapter.prepareDisposable
                            true true firstContext p "native" "original-execution")
            let ticket =
                match result with
                | NativeCredentialAdmission.Retained (saved, why) ->
                    Assert.That(why, Does.Contain("OutcomeUnknown"))
                    saved
                | other -> failwithf "Lost project admission ACK not retained: %A" other
            let snapshot = LedgerStore.fetch a2 |> ok
            let verifier, _ =
                WorkQueueGitLedger.readCredentialAt a2 snapshot p.LaunchId |> ok |> Option.get
            Assert.That(verifier.SecretSha256, Is.EqualTo ticket.Issued.VerifierSha256)
            let replay =
                WorkQueueNativeCredentialDispatchAdapter.prepareDisposable true true
                    (context account2 a2 b1) p "new-actor" "different-execution"
            match replay with
            | NativeCredentialAdmission.Rejected why ->
                Assert.That(why, Does.Contain("already issued"))
            | other -> Assert.Fail(sprintf "Original lost ACK minted another credential: %A" other)
            let after = WorkQueueGitLedger.load a1 |> ok |> snd
            Assert.That(after.Launches.Count, Is.EqualTo 1)
            Assert.That((AccountCapacityLedger.fetch account2 |> ok).State.Tokens.Length, Is.EqualTo 1)
            Assert.That((LedgerStore.fetch a2 |> ok).Commit, Is.EqualTo snapshot.Commit,
                        "Rejected duplicate cannot commit another project operation"))
