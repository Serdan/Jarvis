module AutomationChallengeWake20261122.NativeIngressTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open NUnit.Framework
open Common
open Client
open Client.ConsoleTui
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.ChallengeWorkflow
open Client.OrchestrationApi
open Client.WorkQueueSchedulerHostLoop
open Client.WorkQueueReactiveWake
open Client.AutomationChallengeWakeAdapter

[<TestFixture; NonParallelizable>]
type AuthenticatedNativeIngress() =
    let ok = function
        | Ok value -> value
        | Error error -> failwithf "Disposable source failed: %A" error

    let git cwd arguments =
        use p = new Process()
        p.StartInfo.FileName <- "git"
        p.StartInfo.WorkingDirectory <- cwd
        p.StartInfo.RedirectStandardOutput <- true
        p.StartInfo.RedirectStandardError <- true
        for arg in arguments do p.StartInfo.ArgumentList.Add arg
        if not (p.Start()) then failwith "Git unavailable"
        let output = p.StandardOutput.ReadToEnd()
        let error = p.StandardError.ReadToEnd()
        p.WaitForExit()
        if p.ExitCode <> 0 then failwithf "Disposable Git failed: %s" error
        output.Trim()

    let withBoundClients test =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-b1-native-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let priorXdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
        // PendingIntentJournal and LedgerBindingConfig must NEVER touch a
        // real client profile during the source-pinned disposable native test.
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(root, "config"))
        try
            let remote, a, b = Path.Combine(root, "remote.git"), Path.Combine(root, "a"), Path.Combine(root, "b")
            git root ["init"; "--bare"; remote] |> ignore
            git root ["clone"; remote; a] |> ignore
            git a ["config"; "user.name"; "B1 Native Fixture"] |> ignore
            git a ["config"; "user.email"; "b1@example.test"] |> ignore
            File.WriteAllText(Path.Combine(a, "source.txt"), "isolated")
            git a ["add"; "source.txt"] |> ignore
            git a ["commit"; "-m"; "fixture baseline"] |> ignore
            git a ["push"; "-u"; "origin"; "HEAD"] |> ignore
            git root ["clone"; remote; b] |> ignore
            git b ["config"; "user.name"; "B1 Native Fixture"] |> ignore
            git b ["config"; "user.email"; "b1@example.test"] |> ignore
            let projectId, ledgerId = Guid.NewGuid(), Guid.NewGuid()
            let binding path : LedgerBinding =
                { RepositoryPath = path; RemoteName = "origin"; ApprovedFetchUrl = remote
                  ApprovedPushUrl = remote; ProjectId = projectId; LedgerId = ledgerId; Epoch = 1 }
            let first, second = binding a, binding b
            LedgerStore.initialize first true |> ok |> ignore
            for agent in ["Lead"; "Agent 3"; "Intruder"] do
                ReviewWorkflow.registerAgent first (Guid.NewGuid()) agent |> ok |> ignore
            let taskId = "w17-registered-ingress"
            ReviewWorkflow.createTask first (Guid.NewGuid()) taskId taskId |> ok |> ignore
            let attemptId = Guid.NewGuid().ToString("N")
            ReviewWorkflow.assignTask first (Guid.NewGuid()) taskId (Guid.Parse attemptId) "Agent 3" |> ok |> ignore
            ReviewWorkflow.beginAttempt first (Guid.NewGuid()) taskId "Agent 3" 1 1 |> ok |> ignore
            let _, task, attempt = ReviewWorkflow.getTask first taskId |> ok
            let agent = (ReviewWorkflow.getAgentInbox first "Agent 3" |> ok).Agent
            let challenge =
                ChallengeWorkflow.create first (Guid.NewGuid()) "Agent 3" "Native ingress" taskId
                    [{ OwnerKind = ""; OwnerAgentId = "Lead"; SuggestedAgentId = ""; Description = "Receipt required" }]
                |> ok
            ChallengeWorkflow.blockAttempt second (Guid.NewGuid()) "Agent 3" taskId attemptId
                task.Version attempt.Value.Version agent.Version 1 "Awaiting native proof" [challenge.ChallengeId]
            |> ok |> ignore
            let rtA = Runtime(root, ConsoleTui(), FullTrust)
            let rtB = Runtime(root, ConsoleTui(), FullTrust)
            for name, runtime, pinned in ["a", rtA, first; "b", rtB, second] do
                let head = (LedgerStore.fetch pinned |> ok).Commit
                LedgerBindingConfig.bindExisting name runtime projectId ledgerId 1 "origin"
                    remote remote head true |> ok |> ignore
            test first second rtA rtB challenge
        finally
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", priorXdg)
            Directory.Delete(root, true)

    let resolveCommand project (original: Guid) (challenge: ChallengeCreated) =
        { ProjectName = project
          ActorId = "Lead"
          OperationId = original.ToString("D")
          Action = "Resolve"
          ChallengeId = challenge.ChallengeId
          ConditionId = challenge.ConditionIds.Head
          ExpectedVersion = 1
          Title = ""
          TaskId = ""
          AttemptId = ""
          DependencyChallengeIds = []
          ExpectedTaskVersion = 0
          ExpectedAttemptVersion = 0
          ExpectedAgentVersion = 0
          AssignmentGeneration = 0
          Conditions = []
          Evidence = "signed:disposable"
          Reason = ""
          Outcome = "Satisfied"
          NewOwnerAgentId = ""
          Subscribe = false }

    let execute (runtime: Runtime) actor (cmd: ChallengeWriteCommand) callback =
        let wire = JsonSerializer.Serialize<AgentCommand>(ChallengeWriteCommand cmd)
        Client.SignalR.Client.executeAuthenticatedCommandWithChallengeWake
            runtime wire actor None false callback
        |> fun pending -> pending.GetAwaiter().GetResult()

    let registrationPresent (program: string) =
        // The generated Client assembly is compiled by ProjectReference. These
        // source-anchored assertions additionally make callback removal from
        // its Program registration a RED test instead of silently succeeding
        // through a test-only injected Host.
        program.Contains("let challengeWakeEnabled = false")
        && program.Contains("let challengeCoordinator =")
        && program.Contains("let challengeHost, onChallengeCommit =")
        && program.Contains("Client.AutomationChallengeWakeAdapter.registerHost")
        && program.Contains("Client.receiveAuthenticatedCommandAndReplyWithChallengeWakeTiming")
        && program.Contains("false report onChallengeCommit")
        && program.Contains("true report onChallengeCommit")
        && program.IndexOf("OAuth.login", StringComparison.Ordinal)
           < program.IndexOf("let challengeHost, onChallengeCommit =", StringComparison.Ordinal)
        && program.IndexOf("do! dispatch.Complete()", StringComparison.Ordinal)
           < program.IndexOf("do! challengeHost.StopAsync()", StringComparison.Ordinal)

    [<Test>]
    member _.CompiledProgramOwnsCallbackRegistrationAndShutdownOrder() =
        let file = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "Client", "Program.fs"))
        let program = File.ReadAllText(file)
        Assert.That(typeof<Runtime>.Assembly.GetType("Program"), Is.Not.Null,
                    "A production-compiled Program is required, not a test-only source")
        Assert.That(registrationPresent program, Is.True, "B1 Program registration/lifecycle disappeared")
        let mutant = program.Replace("false report onChallengeCommit", "false report (fun _ _ -> ())")
        Assert.That(registrationPresent mutant, Is.False,
                    "Removing the registered callback must be detected")
        let shutdownMutant = program.Replace("do! challengeHost.StopAsync()", "do! System.Threading.Tasks.Task.CompletedTask")
        Assert.That(registrationPresent shutdownMutant, Is.False,
                    "Removing production Host shutdown must be detected")

    [<Test>]
    member _.RealPostJournalAuthenticatedSignalRCoreTwoClientsAndReplay() =
        withBoundClients (fun a b rtA rtB challenge ->
            let original = Guid.NewGuid()
            let cmd = resolveCommand "a" original challenge
            let mutable calls = 0
            let mutable accepted = 0
            let mutable headReads = 0
            let mutable schedulerWakes = 0
            let ports : SchedulerHostPorts =
                { ReadVerifiedHeads = fun () ->
                    headReads <- headReads + 1
                    Ok { AccountHead = "held"; ProjectHead = "held" }
                  Wake = fun signals ->
                    Assert.That((signals = [WorkWakeTrigger.DependencyCommitted]), Is.True)
                    schedulerWakes <- schedulerWakes + 1
                    Client.WorkQueueReactiveWake.WorkWakeResult.Disabled
                  Report = fun _ -> ()
                  Delay = fun _ _ -> System.Threading.Tasks.Task.CompletedTask }
            let registeredHost, registeredCommit =
                registerHost true ports
                    (fun project ->
                        if project <> "a" then failwith "foreign project"
                        LedgerBindingConfig.load "b" rtB)
                    ignore
            use host = registeredHost
            let onCommitted : Client.SignalR.Client.ChallengeWakePostCommit =
                fun originalCommand receipt ->
                    calls <- calls + 1
                    Assert.That(originalCommand.OperationId, Is.EqualTo(original.ToString("D")))
                    Assert.That(receipt.operation.OperationId, Is.EqualTo(original.ToString("D")))
                    Assert.That(receipt.alreadyApplied, Is.False)
                    Assert.That((LedgerStore.fetch b |> ok).Commit, Is.EqualTo(receipt.head),
                                "Callback must observe the durable remote commit")
                    registeredCommit originalCommand receipt
                    accepted <- accepted + 1
            let good = execute rtA "Lead" cmd onCommitted
            Assert.That(good.Error.IsNone, Is.True, sprintf "Authenticated write: %A" good.Error)
            Assert.That(calls, Is.EqualTo(1))
            Assert.That(accepted, Is.EqualTo(1))
            Assert.That(schedulerWakes, Is.Zero, "No synchronous scheduler execution")
            Assert.That(host.RunOnce [], Is.EqualTo(SchedulerHostOutcome.Reconciled Client.WorkQueueReactiveWake.WorkWakeResult.Disabled))
            Assert.That(schedulerWakes, Is.EqualTo(1))
            Assert.That(host.RunOnce [], Is.EqualTo(SchedulerHostOutcome.Dormant))

            // ACK-loss replay must reuse the original operation UUID, never
            // emit another callback or schedule a second hint.
            let replay = execute rtA "Lead" cmd onCommitted
            Assert.That(replay.Error.IsNone, Is.True, sprintf "Replay: %A" replay.Error)
            Assert.That(calls, Is.EqualTo(1))
            let duplicateOtherClient = execute rtB "Lead" { cmd with ProjectName = "b" } onCommitted
            // This may report AlreadyApplied or operation reuse depending on
            // the independently persisted client request journal. Neither
            // outcome grants a second wake or a different original UUID.
            Assert.That(calls, Is.EqualTo(1), sprintf "Second-client duplicate: %A" duplicateOtherClient.Error)
            let stale = execute rtB "Lead" { cmd with ProjectName = "b"; OperationId = Guid.NewGuid().ToString("D") } onCommitted
            Assert.That(stale.Error.IsSome, Is.True, "Two-client CAS loser must be rejected")
            let forged = execute rtA "Intruder" { cmd with OperationId = Guid.NewGuid().ToString("D") } onCommitted
            Assert.That(forged.Error.IsSome, Is.True, "Forged actor must be rejected before Git write")
            let invalid = execute rtA "Lead" { cmd with OperationId = "not-a-guid" } onCommitted
            Assert.That(invalid.Error.IsSome, Is.True, "Invalid original UUID must fail before commit")
            let precommit = execute rtA "Lead"
                                { cmd with OperationId = Guid.NewGuid().ToString("D"); ExpectedVersion = 999 } onCommitted
            Assert.That(precommit.Error.IsSome, Is.True, "Stale precommit input must fail")
            let foreign = execute rtA "Lead" { cmd with ProjectName = "foreign"; OperationId = Guid.NewGuid().ToString("D") } onCommitted
            Assert.That(foreign.Error.IsSome, Is.True, "Foreign checkout must fail closed")
            Assert.That(calls, Is.EqualTo(1))
            Assert.That(headReads, Is.EqualTo(1))
            Assert.That(schedulerWakes, Is.EqualTo(1))
            Assert.That((ReviewWorkflow.getAgentInbox b "Agent 3" |> ok).Agent.State,
                        Is.EqualTo("Ready")))

    [<Test>]
    member _.DisabledNativeIngressDoesNotReadOrWakeAfterCommit() =
        withBoundClients (fun a b rtA _ challenge ->
            let mutable callbacks = 0
            let mutable bindingReads = 0
            let mutable hostReads = 0
            let original = Guid.NewGuid()
            let cmd = resolveCommand "a" original challenge
            let ports : SchedulerHostPorts =
                { ReadVerifiedHeads = fun () -> hostReads <- hostReads + 1; failwith "unapproved head read"
                  Wake = fun _ -> failwith "unapproved scheduler wake"
                  Report = fun _ -> failwith "unapproved report"
                  Delay = fun _ _ -> failwith "unapproved timer" }
            let registeredHost, registeredCommit =
                registerHost false ports
                    (fun _ -> bindingReads <- bindingReads + 1; failwith "unapproved binding load")
                    (fun _ -> failwith "unapproved report")
            use host = registeredHost
            Assert.That(host.Start(), Is.False)
            Assert.That(host.IsRunning, Is.False)
            // The real Program callback is inert while default OFF, even when
            // a successful canonical write reaches the SignalR callback slot.
            let callback : Client.SignalR.Client.ChallengeWakePostCommit =
                fun actualCmd receipt ->
                    callbacks <- callbacks + 1
                    registeredCommit actualCmd receipt
            let result = execute rtA "Lead" cmd callback
            Assert.That(result.Error.IsNone, Is.True)
            Assert.That(callbacks, Is.EqualTo(1), "Trusted postcommit hook reached")
            Assert.That(bindingReads, Is.Zero)
            Assert.That(hostReads, Is.Zero)
            Assert.That(host.Notify WorkWakeTrigger.DependencyCommitted, Is.False)
            Assert.That(host.IsRunning, Is.False)
        )
