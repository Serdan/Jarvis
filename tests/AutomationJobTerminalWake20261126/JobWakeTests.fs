module AutomationJobTerminalWake20261126.JobWakeTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open NUnit.Framework
open Common
open Client
open Client.ConsoleTui
open Client.OrchestrationApi
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.ChallengeWorkflow
open Client.AutomationJobTerminalWake
open Client.WorkQueueReactiveWake

module Durable = Client.BoundedProcess.DurableJobs

[<TestFixture; NonParallelizable>]
type JobWakeTests() =
    let ok = function
        | Ok value -> value
        | Error failure -> failwithf "Disposable ledger operation failed: %A" failure

    let git cwd args =
        use p = new Process()
        p.StartInfo.FileName <- "git"
        p.StartInfo.WorkingDirectory <- cwd
        p.StartInfo.RedirectStandardOutput <- true
        p.StartInfo.RedirectStandardError <- true
        for arg in args do p.StartInfo.ArgumentList.Add arg
        if not (p.Start()) then failwith "Git unavailable"
        let output = p.StandardOutput.ReadToEnd()
        let error = p.StandardError.ReadToEnd()
        p.WaitForExit()
        if p.ExitCode <> 0 then failwithf "Disposable git failed: %s" error
        output.Trim()

    let withFixture action =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-job-wake-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let priorXdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(root, "config"))
        try
            let bare, repo, jobs = Path.Combine(root, "remote.git"), Path.Combine(root, "repo"), Path.Combine(root, "jobs")
            git root ["init"; "--bare"; bare] |> ignore
            git root ["clone"; bare; repo] |> ignore
            git repo ["config"; "user.name"; "Job Wake Fixture"] |> ignore
            git repo ["config"; "user.email"; "wake@example.test"] |> ignore
            File.WriteAllText(Path.Combine(repo, "source.txt"), "disposable")
            git repo ["add"; "source.txt"] |> ignore
            git repo ["commit"; "-m"; "disposable baseline"] |> ignore
            git repo ["push"; "-u"; "origin"; "HEAD"] |> ignore
            Directory.CreateDirectory jobs |> ignore
            let binding: LedgerBinding =
                { RepositoryPath = repo; RemoteName = "origin"; ApprovedFetchUrl = bare
                  ApprovedPushUrl = bare; ProjectId = Guid.NewGuid(); LedgerId = Guid.NewGuid(); Epoch = 1 }
            LedgerStore.initialize binding true |> ok |> ignore
            for agent in ["Worker A"; "Worker B"] do
                ReviewWorkflow.registerAgent binding (Guid.NewGuid()) agent |> ok |> ignore
            action jobs binding
        finally
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", priorXdg)
            Directory.Delete(root, true)

    let createJob jobs project actor =
        let request: Durable.Request =
            { Schema = 1; JobId = Guid.NewGuid().ToString("N"); Project = project; Actor = actor
              Executable = "fixture-only"; Args = []; Reason = None; WorkingDirectory = jobs
              StartedAt = DateTimeOffset.UtcNow; MaxOutputBytes = 1024 }
        let directory = Durable.create jobs request
        directory, request

    // A disposable persisted receipt fixture, not a spawned OS job. The real
    // accepted observer reads it, checks the original request/job identity,
    // and distinguishes terminal receipts from unknown/dead live supervisors.
    let publish directory (request: Durable.Request) status terminal =
        let receipt: Durable.Receipt =
            { Schema = 1; JobId = request.JobId
              Supervisor = { Pid = Int32.MaxValue; Birth = "fixture-dead-supervisor" }
              Child = None; Started = true; Status = status
              CompletedAt = if terminal then Some(request.StartedAt.AddSeconds 1.) else None
              Events = []; NextSequence = 0L; Truncated = false; CancelError = None }
        let stage = Path.Combine(directory, "fixture.tmp")
        File.WriteAllBytes(stage, JsonSerializer.SerializeToUtf8Bytes receipt)
        File.Move(stage, Path.Combine(directory, "receipt.json"), true)

    let startTask binding =
        let task = "job-wake-" + Guid.NewGuid().ToString("N")
        ReviewWorkflow.createTask binding (Guid.NewGuid()) task task |> ok |> ignore
        let attempt = Guid.NewGuid()
        ReviewWorkflow.assignTask binding (Guid.NewGuid()) task attempt "Worker A" |> ok |> ignore
        ReviewWorkflow.beginAttempt binding (Guid.NewGuid()) task "Worker A" 1 1 |> ok |> ignore
        task, attempt.ToString("N")

    let register binding task (jobId: string) =
        ChallengeWorkflow.create binding (Guid.NewGuid()) "Worker A" "Await original job terminal receipt" task
            [{ OwnerKind = "Agent"; OwnerAgentId = "Worker A"; SuggestedAgentId = ""
               Description = JobTerminalCondition.describe (Guid.Parse jobId) }] |> ok

    let block binding task attempt (challenge: ChallengeCreated) =
        let _, taskRecord, maybeAttempt = ReviewWorkflow.getTask binding task |> ok
        let agent = (ReviewWorkflow.getAgentInbox binding "Worker A" |> ok).Agent
        ChallengeWorkflow.blockAttempt binding (Guid.NewGuid()) "Worker A" task attempt
            taskRecord.Version maybeAttempt.Value.Version agent.Version 1 "Waiting on original job"
            [challenge.ChallengeId]

    let run jobs binding callback =
        reconcile true jobs (fun project ->
            if project = "Fixture" then Ok binding
            else Error(Client.ValidationError "Foreign project fixture")) callback

    let noCallback _ _ = failwith "Unexpected wake"

    [<Test>]
    member _.NonzeroTerminalRequeuesExactlyOnceAndPreservesOutcome() =
        withFixture (fun jobs binding ->
            let directory, request = createJob jobs "Fixture" (Some "Worker A")
            let task, attempt = startTask binding
            let challenge = register binding task request.JobId
            block binding task attempt challenge |> ok |> ignore
            // No receipt cannot satisfy the wait; the original owner cannot
            // manufacture terminal evidence via the generic remote Resolve.
            Assert.That((run jobs binding noCallback).Committed, Is.Zero)
            match ChallengeWorkflow.resolve binding (Guid.NewGuid()) "Worker A"
                    challenge.ChallengeId challenge.ConditionIds.Head 1 "made-up exit" "" with
            | Error(ReviewAccessDenied _) -> ()
            | other -> Assert.Fail(sprintf "Forged generic resolution: %A" other)
            publish directory request (JobStatus.Completed 19) true
            let mutable hints = 0
            let callback cmd receipt =
                hints <- hints + 1
                match Client.AutomationChallengeWakeAdapter.inspectCommitted true binding cmd receipt with
                | Client.AutomationChallengeWakeAdapter.CommittedWakeDecision.Ready [candidate] ->
                    Assert.That(candidate.TaskId, Is.EqualTo(task))
                    Assert.That(candidate.BlockedGeneration, Is.EqualTo(1))
                | other -> Assert.Fail(sprintf "Existing wake adapter held genuine terminal requeue: %A" other)
            let report = run jobs binding callback
            Assert.That(report.Failures, Is.Empty)
            Assert.That(report.Committed, Is.EqualTo(1))
            Assert.That(hints, Is.EqualTo(1))
            let current = (ReviewWorkflow.getAgentInbox binding "Worker A" |> ok)
            Assert.That(current.Agent.State, Is.EqualTo("Ready"))
            Assert.That(current.RunnableQueue.Head.Attempt.AssignmentGeneration, Is.EqualTo(2))
            let detail = ChallengeWorkflow.get binding "Worker A" challenge.ChallengeId |> ok
            Assert.That(detail.Challenge.Conditions.Head.Evidence, Does.Contain("Completed(19)"))
            let head = (LedgerStore.fetch binding |> ok).Commit
            Assert.That((run jobs binding callback).Committed, Is.Zero)
            Assert.That(hints, Is.EqualTo(1))
            Assert.That((LedgerStore.fetch binding |> ok).Commit, Is.EqualTo(head)))

    [<Test>]
    member _.CompletionBeforeRegistrationAndCancellationNeverStrandWait() =
        withFixture (fun jobs binding ->
            let directory, request = createJob jobs "Fixture" (Some "Worker A")
            publish directory request JobStatus.Canceled true
            Assert.That((run jobs binding noCallback).Committed, Is.Zero)
            let task, attempt = startTask binding
            let challenge = register binding task request.JobId
            // Registration reconciliation also covers a client restart with
            // no in-memory event or registry. It resolves the condition even
            // before BlockAttempt, whose canonical guard rejects a stale wait.
            let first = run jobs binding (fun _ _ -> ())
            Assert.That(first.Failures, Is.Empty)
            Assert.That(first.Committed, Is.EqualTo(1))
            let detail = ChallengeWorkflow.get binding "Worker A" challenge.ChallengeId |> ok
            Assert.That(detail.Challenge.Conditions.Head.Evidence, Does.Contain("Canceled"))
            match block binding task attempt challenge with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Already-completed dependency stranded owner: %A" other)
            Assert.That((ReviewWorkflow.getAgentInbox binding "Worker A" |> ok).Agent.State, Is.EqualTo("Working")))

    [<Test>]
    member _.UnknownWrongActorForeignProjectAndOffAreNotTerminal() =
        withFixture (fun jobs binding ->
            let unknownDir, unknown = createJob jobs "Fixture" (Some "Worker A")
            publish unknownDir unknown JobStatus.Running false
            let foreignDir, foreign = createJob jobs "Other" (Some "Worker A")
            publish foreignDir foreign (JobStatus.Completed 0) true
            let otherDir, other = createJob jobs "Fixture" (Some "Worker B")
            publish otherDir other (JobStatus.Completed 0) true
            let task, _ = startTask binding
            let conditions =
                [unknown.JobId; foreign.JobId; other.JobId; Guid.NewGuid().ToString("N")]
                |> List.map (fun id ->
                    { OwnerKind = "Agent"; OwnerAgentId = "Worker A"; SuggestedAgentId = ""
                      Description = JobTerminalCondition.describe(Guid.Parse id) })
            let challenge = ChallengeWorkflow.create binding (Guid.NewGuid()) "Worker A" "Negative job evidence" task conditions |> ok
            let head = (LedgerStore.fetch binding |> ok).Commit
            let off = reconcile false "/missing" (fun _ -> failwith "off read binding") noCallback
            Assert.That(off.Failures, Is.Empty)
            Assert.That(off.Committed, Is.Zero)
            let report = run jobs binding noCallback
            Assert.That(report.Committed, Is.Zero)
            Assert.That((LedgerStore.fetch binding |> ok).Commit, Is.EqualTo(head))
            Assert.That((ChallengeWorkflow.get binding "Worker A" challenge.ChallengeId |> ok).Challenge.Status,
                        Is.EqualTo("Pending")))

    [<Test>]
    member _.SiblingConditionRetainsItsOwnMeaningAndFailureToStartIsTerminal() =
        withFixture (fun jobs binding ->
            let directory, request = createJob jobs "Fixture" (Some "Worker A")
            let task, attempt = startTask binding
            let challenge =
                ChallengeWorkflow.create binding (Guid.NewGuid()) "Worker A" "Two conditions" task
                    [{ OwnerKind = "Agent"; OwnerAgentId = "Worker A"; SuggestedAgentId = ""
                       Description = JobTerminalCondition.describe(Guid.Parse request.JobId) }
                     { OwnerKind = "Agent"; OwnerAgentId = "Worker B"; SuggestedAgentId = ""
                       Description = "Independent evidence" }] |> ok
            block binding task attempt challenge |> ok |> ignore
            publish directory request (JobStatus.FailedToStart "fixture failure") true
            let report = run jobs binding (fun _ _ -> ())
            Assert.That(report.Failures, Is.Empty)
            Assert.That(report.Committed, Is.EqualTo(1))
            let detail = ChallengeWorkflow.get binding "Worker A" challenge.ChallengeId |> ok
            Assert.That(detail.Challenge.Status, Is.EqualTo("Pending"))
            Assert.That(detail.Challenge.Conditions.Head.Evidence, Does.Contain("FailedToStart"))
            Assert.That((ReviewWorkflow.getAgentInbox binding "Worker A" |> ok).Agent.State, Is.EqualTo("NeedsWork"))
            ChallengeWorkflow.resolve binding (Guid.NewGuid()) "Worker B" challenge.ChallengeId
                challenge.ConditionIds.Tail.Head detail.Challenge.Version "independent" "" |> ok |> ignore
            Assert.That((ReviewWorkflow.getAgentInbox binding "Worker A" |> ok).Agent.State, Is.EqualTo("Ready")))

    [<Test>]
    member _.RegisteredHostRecoversTerminalAfterDisconnectedStartup() =
        withFixture (fun jobs binding ->
            let directory, request = createJob jobs "Fixture" (Some "Worker A")
            let task, attempt = startTask binding
            let challenge = register binding task request.JobId
            block binding task attempt challenge |> ok |> ignore
            publish directory request (JobStatus.Completed 7) true
            use connected = new System.Threading.ManualResetEventSlim(false)
            use observedFailure = new System.Threading.ManualResetEventSlim(false)
            use delivered = new System.Threading.ManualResetEventSlim(false)
            let load project =
                if project = "Fixture" && connected.IsSet then Ok binding
                else
                    observedFailure.Set()
                    Error(Client.ContextError "Disposable disconnected binding")
            let mutable callbackCount = 0
            use host = new Host(true, jobs, load,
                                (fun _ _ ->
                                    System.Threading.Interlocked.Increment(&callbackCount) |> ignore
                                    delivered.Set()),
                                ignore)
            Assert.That(observedFailure.Wait(TimeSpan.FromSeconds 15.), Is.True)
            Assert.That((ReviewWorkflow.getAgentInbox binding "Worker A" |> ok).Agent.State, Is.EqualTo("NeedsWork"))
            connected.Set()
            Assert.That(host.Notify(), Is.True)
            Assert.That(delivered.Wait(TimeSpan.FromSeconds 15.), Is.True)
            Assert.That((ReviewWorkflow.getAgentInbox binding "Worker A" |> ok).Agent.State, Is.EqualTo("Ready"))
            Assert.That(callbackCount, Is.EqualTo(1)))

    [<Test>]
    member _.RegisteredHostObservesReceiptPublishedAfterRegistration() =
        withFixture (fun jobs binding ->
            let task, attempt = startTask binding
            use delivered = new System.Threading.ManualResetEventSlim(false)
            use host = new Host(true, jobs, (fun project ->
                                    if project = "Fixture" then Ok binding
                                    else Error(Client.ValidationError "Unknown fixture project")),
                                (fun _ _ -> delivered.Set()), ignore)
            let directory, request = createJob jobs "Fixture" (Some "Worker A")
            let challenge = register binding task request.JobId
            block binding task attempt challenge |> ok |> ignore
            // A post-commit registration hint races with the atomic receipt
            // publication; either the fresh scan or its preinstalled watcher
            // must observe the exact original terminal state.
            Assert.That(host.Notify(), Is.True)
            publish directory request (JobStatus.Completed 0) true
            Assert.That(delivered.Wait(TimeSpan.FromSeconds 15.), Is.True)
            Assert.That((ReviewWorkflow.getAgentInbox binding "Worker A" |> ok).Agent.State, Is.EqualTo("Ready")))

    [<Test>]
    member _.DefaultOffHostDoesNotOpenRegistryOrBinding() =
        use host = new Host(false, "/path/does/not/exist", (fun _ -> failwith "off binding read"),
                            noCallback, (fun _ -> failwith "off host report"))
        Assert.That(host.Notify(), Is.False)

    [<Test>]
    member _.ReservedConditionsRejectMalformedOrReassignedOwnership() =
        withFixture (fun jobs binding ->
            let _, request = createJob jobs "Fixture" (Some "Worker A")
            let task, _ = startTask binding
            let definition owner description: ChallengeConditionSpec =
                { OwnerKind = "Agent"; OwnerAgentId = owner; SuggestedAgentId = ""; Description = description }
            for invalid in [ definition "Worker A" "jarvis:job-terminal:v1:not-a-uuid"
                             definition "Worker B" (JobTerminalCondition.describe(Guid.Parse request.JobId)) ] do
                match ChallengeWorkflow.create binding (Guid.NewGuid()) "Worker A" "Invalid" task [invalid] with
                | Error(InvalidBinding _) -> ()
                | other -> Assert.Fail(sprintf "Invalid job descriptor accepted: %A" other)
            let challenge = register binding task request.JobId
            match ChallengeWorkflow.assignCondition binding (Guid.NewGuid()) "Worker A"
                    challenge.ChallengeId challenge.ConditionIds.Head 1 "Worker B" with
            | Error(ReviewAccessDenied _) -> ()
            | other -> Assert.Fail(sprintf "Original job owner reassigned: %A" other)
            match ChallengeWorkflow.splitCondition binding (Guid.NewGuid()) "Worker A"
                    challenge.ChallengeId challenge.ConditionIds.Head 1
                    [definition "Worker A" "A"; definition "Worker A" "B"] with
            | Error(ReviewAccessDenied _) -> ()
            | other -> Assert.Fail(sprintf "Original job condition split: %A" other))

    [<TestCase(false)>]
    [<TestCase(true)>]
    member _.RegisteredRuntimeUsesAuthenticatedCommitAndRecoversLostWake(restart: bool) =
        withFixture (fun jobs binding ->
            let runtime = Runtime(Path.GetDirectoryName jobs, ConsoleTui(), FullTrust)
            let head = (LedgerStore.fetch binding |> ok).Commit
            LedgerBindingConfig.bindExisting "repo" runtime binding.ProjectId binding.LedgerId binding.Epoch
                "origin" binding.ApprovedFetchUrl binding.ApprovedPushUrl head true |> ok |> ignore
            let load name = LedgerBindingConfig.load name runtime
            Client.RoleInstructions.RoleInstructions.setLaunchTemplate binding "fixture" (Guid.NewGuid()) 0
                "Agent {{agentId}}, project {{projectName}}. Call inbox." |> ok |> ignore
            let directory, request = createJob jobs "repo" (Some "Worker A")
            let task, attempt = startTask binding
            let create: ChallengeWriteCommand =
                { ProjectName = "repo"; ActorId = "Worker A"; OperationId = Guid.NewGuid().ToString("D")
                  Action = "Create"; ChallengeId = ""; ConditionId = ""; ExpectedVersion = 0
                  Title = "Original job"; TaskId = task; AttemptId = ""; DependencyChallengeIds = []
                  ExpectedTaskVersion = 0; ExpectedAttemptVersion = 0; ExpectedAgentVersion = 0
                  AssignmentGeneration = 0
                  Conditions = [{ OwnerKind = "Agent"; OwnerAgentId = "Worker A"; SuggestedAgentId = ""
                                  Description = JobTerminalCondition.describe(Guid.Parse request.JobId) }]
                  Evidence = ""; Reason = ""; Outcome = ""; NewOwnerAgentId = ""; Subscribe = false }
            let execute actor cmd callback =
                let wire = JsonSerializer.Serialize<AgentCommand>(ChallengeWriteCommand cmd)
                Client.SignalR.Client.executeAuthenticatedCommandWithChallengeWake runtime wire actor None false callback
                |> fun pending -> pending.GetAwaiter().GetResult()
            let mutable callbacks = 0
            let mutable sends = 0
            use sent = new System.Threading.ManualResetEventSlim(false)
            use invalidated = new System.Threading.ManualResetEventSlim(false)
            let sendPorts: Client.ScheduledReadyResume.ExecutionPorts =
                { ReadLimits = fun _ _ -> Ok Client.ScheduledReadyResume.disabled
                  CanSend = fun _ -> System.Threading.Tasks.Task.FromResult(Ok true)
                  Send = fun prepared ->
                    Assert.That(prepared.Ready.Generation, Is.EqualTo(2))
                    System.Threading.Interlocked.Increment(&sends) |> ignore
                    sent.Set()
                    System.Threading.Tasks.Task.FromResult(Ok "fixture") }
            let ports: Client.WorkQueueSchedulerHostLoop.SchedulerHostPorts =
                { ReadVerifiedHeads = fun () -> Ok { AccountHead = "fixture-only"; ProjectHead = (LedgerStore.fetch binding |> ok).Commit }
                  Wake = fun _ ->
                    Client.ScheduledReadyResume.executeOnce sendPorts binding "fixture" "repo" "launchAgent.js" "script-sha" "route-sha"
                    |> fun t -> t.GetAwaiter().GetResult() |> ignore
                    WorkWakeResult.Disabled
                  Report = ignore
                  Delay = fun _ ct -> System.Threading.Tasks.Task.Delay(System.Threading.Timeout.Infinite, ct) }
            use scheduler = new Client.WorkQueueSchedulerHostLoop.WorkQueueSchedulerHostLoop.Host(ports, enabled = true)
            let invalidate _ ids =
                if Array.contains "Worker A" ids then invalidated.Set()
            let runRegistration () =
                new RuntimeRegistration(true, (fun () -> jobs), load, scheduler,
                    (fun _ _ -> failwith "fixture lost fast-path wake"), invalidate, ignore)
            let registerAndBlock (registration: RuntimeRegistration) =
                let callback cmd receipt =
                    callbacks <- callbacks + 1
                    registration.OnChallengeCommit(cmd, receipt)
                let created = execute "Worker A" create callback
                Assert.That(created.Error.IsNone, Is.True, sprintf "%A" created.Error)
                Assert.That(callbacks, Is.EqualTo(1))
                let id = Guid.Parse(create.OperationId).ToString("N")
                let replay = execute "Worker A" create callback
                Assert.That(replay.Error.IsNone, Is.True, sprintf "%A" replay.Error)
                Assert.That(callbacks, Is.EqualTo(1))
                let _, taskRecord, maybeAttempt = ReviewWorkflow.getTask binding task |> ok
                let agent = (ReviewWorkflow.getAgentInbox binding "Worker A" |> ok).Agent
                let blocked = { create with Action = "BlockAttempt"; OperationId = Guid.NewGuid().ToString("D")
                                            AttemptId = attempt; Conditions = []; DependencyChallengeIds = [id]
                                            ExpectedTaskVersion = taskRecord.Version; ExpectedAttemptVersion = maybeAttempt.Value.Version
                                            ExpectedAgentVersion = agent.Version; AssignmentGeneration = 1; Reason = "Waiting" }
                let response = execute "Worker A" blocked callback
                Assert.That(response.Error.IsNone, Is.True, sprintf "%A" response.Error)
                Assert.That(callbacks, Is.EqualTo(2))
                Assert.That((execute "Worker B" { create with OperationId = Guid.NewGuid().ToString("D") } callback).Error.IsSome, Is.True)
                Assert.That(callbacks, Is.EqualTo(2))
                Assert.That((ReviewWorkflow.getAgentInbox binding "Worker A" |> ok).Agent.State, Is.EqualTo("NeedsWork"))
            if restart then
                do
                    use registration = runRegistration()
                    registerAndBlock registration
                publish directory request JobStatus.Canceled true
                let result = reconcile true jobs load (fun _ _ -> failwith "lost after canonical commit")
                Assert.That(result.Committed, Is.EqualTo(1))
                use registration = runRegistration()
                registration.OnConnected()
                Assert.That(sent.Wait(TimeSpan.FromSeconds 20.), Is.True)
                registration.OnInvalidated()
            else
                use registration = runRegistration()
                registerAndBlock registration
                registration.OnConnected()
                publish directory request (JobStatus.FailedToStart "fixture") true
                Assert.That(invalidated.Wait(TimeSpan.FromSeconds 20.), Is.True)
                Assert.That(sent.Wait(TimeSpan.FromSeconds 20.), Is.True)
                registration.OnConnected()
            scheduler.StopAsync().GetAwaiter().GetResult()
            Assert.That(sends, Is.EqualTo(1))
            Assert.That((ReviewWorkflow.getAgentInbox binding "Worker A" |> ok).RunnableQueue.Head.Attempt.AssignmentGeneration,
                        Is.EqualTo(2)))

    [<Test>]
    member _.EvictedTerminalReceiptLeavesCanonicalWaitHeld() =
        withFixture (fun jobs binding ->
            let directory, request = createJob jobs "Fixture" (Some "Worker A")
            let task, attempt = startTask binding
            let challenge = register binding task request.JobId
            block binding task attempt challenge |> ok |> ignore
            publish directory request (JobStatus.Completed 0) true
            Assert.That(Durable.tryPrune directory, Is.True)
            let head = (LedgerStore.fetch binding |> ok).Commit
            Assert.That((run jobs binding noCallback).Committed, Is.Zero)
            Assert.That((LedgerStore.fetch binding |> ok).Commit, Is.EqualTo(head))
            Assert.That((ReviewWorkflow.getAgentInbox binding "Worker A" |> ok).Agent.State, Is.EqualTo("NeedsWork")))

    [<Test>]
    member _.RuntimeRegistrationOffNeverOpensRegistryOrStartsScheduler() =
        let ports: Client.WorkQueueSchedulerHostLoop.SchedulerHostPorts =
            { ReadVerifiedHeads = fun () -> failwith "off head read"
              Wake = fun _ -> failwith "off wake"
              Report = ignore
              Delay = fun _ _ -> failwith "off timer" }
        use scheduler = new Client.WorkQueueSchedulerHostLoop.WorkQueueSchedulerHostLoop.Host(ports)
        use registration = new RuntimeRegistration(false, (fun () -> failwith "off root"),
                                    (fun _ -> failwith "off binding"), scheduler, noCallback,
                                    (fun _ _ -> failwith "off invalidation"), ignore)
        registration.OnConnected()
        registration.OnInvalidated()
        Assert.That(scheduler.IsRunning, Is.False)

    [<Test>]
    member _.ProgramRegistersTheSameDefaultOffRuntime() =
        let source = File.ReadAllText(Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../../Client/Program.fs")))
        Assert.That(source, Does.Contain("let challengeWakeEnabled = false"))
        Assert.That(source, Does.Contain("BoundedProcess.DurableJobs.root workspace"))
        Assert.That(source, Does.Contain("new AutomationJobTerminalWake.RuntimeRegistration("))
        Assert.That(source, Does.Contain("jobWake.OnChallengeCommit(cmd, receipt)"))
        Assert.That(source, Does.Contain("jobWake.OnConnected()"))
        Assert.That(source, Does.Contain("jobWake.OnInvalidated()"))
        Assert.That(source.IndexOf("(jobWake :> IDisposable).Dispose()", StringComparison.Ordinal),
                    Is.LessThan(source.IndexOf("do! challengeHost.StopAsync()", StringComparison.Ordinal)))
