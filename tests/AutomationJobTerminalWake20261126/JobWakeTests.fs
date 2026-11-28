module AutomationJobTerminalWake20261126.JobWakeTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Collections.Generic
open System.Collections.Concurrent
open System.IdentityModel.Tokens.Jwt
open System.Security.Claims
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Authentication.JwtBearer
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.SignalR
open Microsoft.AspNetCore.SignalR.Client
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.IdentityModel.Protocols
open Microsoft.IdentityModel.Protocols.OpenIdConnect
open Microsoft.IdentityModel.Tokens
open Common.SignalR
open NUnit.Framework
open Common
open global.Client
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
                                (fun _ _ _ ->
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
                                (fun _ _ _ -> delivered.Set()), ignore)
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
                            (fun _ -> noCallback), (fun _ -> failwith "off host report"))
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
                    (fun _ _ -> failwith "fixture lost fast-path wake"), invalidate, ignore, ignore)
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
                                    (fun _ _ -> failwith "off invalidation"), (fun _ -> failwith "off transport"), ignore)
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

    // Real loopback host, existing JWT-authenticated client ingress and two
    // separate checkouts of one canonical ledger. Only the launch effect is fake.
    [<TestCase(0)>] // delivered hint
    [<TestCase(1)>] // disconnected receiver, recover on reconnect
    [<TestCase(2)>] // dropped hint while connected, recover on full rescan
    member _.HostedAuthenticatedJobWake(mode: int) =
        withFixture (fun jobs binding ->
            let await (t: Task<'T>) = t.GetAwaiter().GetResult()
            let wait (t: Task) = t.GetAwaiter().GetResult()
            let root = Path.GetDirectoryName jobs
            let root2 = Path.Combine(root, "second-device")
            Directory.CreateDirectory(root2) |> ignore
            let repo2 = Path.Combine(root2, "repo")
            let jobs2 = Path.Combine(root2, "jobs")
            Directory.CreateDirectory(jobs2) |> ignore
            git root2 ["clone"; binding.ApprovedFetchUrl; repo2] |> ignore
            git repo2 ["config"; "user.name"; "Second Job Wake Fixture"] |> ignore
            git repo2 ["config"; "user.email"; "wake2@example.test"] |> ignore
            let binding2 = { binding with RepositoryPath = repo2 }
            let rt1 = Runtime(root, ConsoleTui(), FullTrust)
            let rt2 = Runtime(root2, ConsoleTui(), FullTrust)
            Client.RoleInstructions.RoleInstructions.setLaunchTemplate binding "fixture" (Guid.NewGuid()) 0
                "Agent {{agentId}}, project {{projectName}}. Call inbox." |> ok |> ignore
            let taskId, attemptId = startTask binding
            let directory, request = createJob jobs "repo" (Some "Worker A")
            let challenge = register binding taskId request.JobId
            block binding taskId attemptId challenge |> ok |> ignore
            let head = (LedgerStore.fetch binding |> ok).Commit
            for runtime in [rt1; rt2] do
                LedgerBindingConfig.bindExisting "repo" runtime binding.ProjectId binding.LedgerId binding.Epoch
                    "origin" binding.ApprovedFetchUrl binding.ApprovedPushUrl head true |> ok |> ignore

            let issuer = "urn:jarvis:offline:job-wake"
            let audience = "urn:jarvis:offline"
            let key = SymmetricSecurityKey(Encoding.UTF8.GetBytes("disposable-job-wake-test-signing-key-not-for-production-20261126"))
            let token user =
                JwtSecurityToken(issuer, audience,
                    [Claim("sub", user); Claim("scope", "client:connect")],
                    DateTime.UtcNow.AddMinutes(-1.), DateTime.UtcNow.AddMinutes(10.),
                    SigningCredentials(key, SecurityAlgorithms.HmacSha256))
                |> JwtSecurityTokenHandler().WriteToken
            let builder = WebApplication.CreateBuilder()
            builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
            builder.Configuration.AddInMemoryCollection(
                [KeyValuePair("Auth0Domain", issuer)
                 KeyValuePair("Audience", audience)
                 KeyValuePair("DatabasePath", Path.Combine(root, "telemetry.sqlite"))]) |> ignore
            Program.configureServices builder.Services builder.Configuration
            builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, fun options ->
                let metadata = OpenIdConnectConfiguration()
                metadata.Issuer <- issuer
                metadata.SigningKeys.Add(key)
                options.ConfigurationManager <- StaticConfigurationManager<OpenIdConnectConfiguration>(metadata)
                options.TokenValidationParameters.ValidIssuer <- issuer
                options.TokenValidationParameters.ValidateIssuer <- true
                options.TokenValidationParameters.ValidAudience <- audience
                options.TokenValidationParameters.ValidateAudience <- true
                options.TokenValidationParameters.IssuerSigningKey <- key
                options.TokenValidationParameters.ValidateIssuerSigningKey <- true
                options.TokenValidationParameters.ValidateLifetime <- true) |> ignore
            use app = builder.Build()
            Program.configureApp app |> ignore
            wait (app.StartAsync())
            let address = app.Urls |> Seq.exactlyOne
            let makeHub user =
                let jwt = token user
                HubConnectionBuilder()
                    .WithUrl(address + "/client", Action<Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions>(fun o ->
                        o.AccessTokenProvider <- Func<Task<string>>(fun () -> Task.FromResult jwt)))
                    .Build()
            let registerHub (hub: HubConnection) device =
                hub.InvokeAsync<ClientRegistrationResult>("Connect",
                    { DeviceId = device; DeviceName = device; ProtocolVersion = AgentProtocol.version
                      ClientVersion = "offline-job-wake-fixture"; SupportedCommands = [] }) |> await
            let one, two, foreign, unregistered = makeHub "alice", makeHub "alice", makeHub "bob", makeHub "alice"
            let stale, replacement = makeHub "alice", makeHub "alice"
            let hubs = [one; two; foreign; unregistered; stale; replacement]
            let mutable sends = 0
            let mutable wakes = 0
            let mutable accepted = 0
            let mutable foreignSeen = 0
            let mutable unregisteredSeen = 0
            let mutable rawReceived = 0
            let mutable allowReceiver = true
            let captured = ConcurrentQueue<JobWakeInvalidation>()
            use sent = new ManualResetEventSlim(false)
            use timerPulse = new SemaphoreSlim(0, 1)
            let until (label: string) condition =
                Assert.That(SpinWait.SpinUntil(Func<bool>(condition), TimeSpan.FromSeconds 30.), Is.True, label)
            let sendPorts: Client.ScheduledReadyResume.ExecutionPorts =
                { ReadLimits = fun _ _ -> Ok Client.ScheduledReadyResume.disabled
                  CanSend = fun _ -> Task.FromResult(Ok true)
                  Send = fun prepared ->
                    Assert.That(prepared.Ready.Generation, Is.EqualTo(2))
                    Interlocked.Increment(&sends) |> ignore
                    sent.Set()
                    Task.FromResult(Ok "fake-launch") }
            let receiverPorts: Client.WorkQueueSchedulerHostLoop.SchedulerHostPorts =
                { ReadVerifiedHeads = fun () -> Ok { AccountHead = "fixture-only"; ProjectHead = (LedgerStore.fetch binding2 |> ok).Commit }
                  Wake = fun _ ->
                    try
                        Client.ScheduledReadyResume.executeOnce sendPorts binding2 "fixture" "repo" "launchAgent.js" "script-sha" "route-sha"
                        |> await |> ignore
                        WorkWakeResult.Disabled
                    finally Interlocked.Increment(&wakes) |> ignore
                  Report = ignore
                  Delay = fun _ ct -> timerPulse.WaitAsync(ct) }
            let offPorts: Client.WorkQueueSchedulerHostLoop.SchedulerHostPorts =
                { ReadVerifiedHeads = fun () -> failwith "sender scheduler disabled"
                  Wake = fun _ -> failwith "sender scheduler disabled"
                  Report = ignore; Delay = fun _ _ -> failwith "sender scheduler disabled" }
            use scheduler1 = new Client.WorkQueueSchedulerHostLoop.WorkQueueSchedulerHostLoop.Host(offPorts)
            use scheduler2 = new Client.WorkQueueSchedulerHostLoop.WorkQueueSchedulerHostLoop.Host(receiverPorts, enabled = true)
            let reports = ConcurrentQueue<string>()
            use receiver = new RuntimeRegistration(true, (fun () -> jobs2), (fun name -> LedgerBindingConfig.load name rt2),
                                scheduler2, (fun _ _ -> ()), (fun _ _ -> ()), (fun _ -> ()), reports.Enqueue)
            use sender = new RuntimeRegistration(true, (fun () -> jobs), (fun name -> LedgerBindingConfig.load name rt1),
                                scheduler1, (fun _ _ -> failwith "local fast path lost"), (fun _ _ -> ()),
                                publishInvalidation one (fun () -> true), reports.Enqueue)
            use receiverSubscription =
                registerInvalidationReceiver two (fun () -> allowReceiver)
                    (fun name -> LedgerBindingConfig.inspectLocal name rt2 |> Result.map fst)
                    (fun project ids ->
                        Assert.That(project, Is.EqualTo("repo"))
                        Assert.That((ids = [|"Worker A"|]), Is.True)
                        Client.SignalR.Client.invalidateAgentStates rt2 project ids
                        Interlocked.Increment(&accepted) |> ignore)
                    receiver.OnInvalidated reports.Enqueue
            use rawSubscription = two.On<JobWakeInvalidation>("InvalidateJobWake", Action<JobWakeInvalidation>(fun hint ->
                captured.Enqueue hint
                Interlocked.Increment(&rawReceived) |> ignore))
            use foreignSubscription = foreign.On<JobWakeInvalidation>("InvalidateJobWake", Action<JobWakeInvalidation>(fun _ ->
                Interlocked.Increment(&foreignSeen) |> ignore))
            use unregisteredSubscription = unregistered.On<JobWakeInvalidation>("InvalidateJobWake", Action<JobWakeInvalidation>(fun _ ->
                Interlocked.Increment(&unregisteredSeen) |> ignore))
            let hint =
                { ProjectName = "repo"; ProjectId = binding.ProjectId; LedgerId = binding.LedgerId
                  Epoch = binding.Epoch; AgentIds = [|"Worker A"|] }
            let rejected hub invalidHint =
                let mutable failed = false
                try
                    (hub: HubConnection).InvokeCoreAsync<obj>("PublishJobWakeInvalidation", [|box invalidHint|]) |> await |> ignore
                with :? HubException -> failed <- true
                Assert.That(failed, Is.True, "unregistered, stale, or malformed publish must be rejected")
            try
                for hub in hubs do wait (hub.StartAsync())
                registerHub one "a" |> ignore
                registerHub two "b" |> ignore
                registerHub foreign "a" |> ignore
                registerHub stale "replaced" |> ignore
                registerHub replacement "replaced" |> ignore
                receiver.OnConnected()
                sender.OnConnected()
                until "startup verified scan" (fun () -> Volatile.Read(&wakes) >= 1)
                rejected unregistered hint
                rejected stale hint
                rejected one { hint with ProjectName = "../repo" }
                // Valid shape from another user never reaches Alice's devices.
                publishInvalidation foreign (fun () -> true) hint
                // Same user but foreign label, ledger, or epoch: reject before
                // invalidating a local cache or requesting a wake.
                publishInvalidation one (fun () -> true) { hint with ProjectName = "foreign" }
                publishInvalidation one (fun () -> true) { hint with ProjectId = Guid.NewGuid() }
                publishInvalidation one (fun () -> true) { hint with LedgerId = Guid.NewGuid() }
                publishInvalidation one (fun () -> true) { hint with Epoch = binding.Epoch + 1 }
                until "foreign project hints reached the transport" (fun () -> Volatile.Read(&rawReceived) >= 4)
                Assert.That(Volatile.Read(&accepted), Is.Zero)
                // Even a correctly scoped stale/forged hint is not terminal truth.
                let beforeWake = Volatile.Read(&wakes)
                publishInvalidation one (fun () -> true) hint
                until "valid hint accepted" (fun () -> Volatile.Read(&accepted) >= 1)
                until "valid hint requested verified scan" (fun () -> Volatile.Read(&wakes) > beforeWake)
                Assert.That(Volatile.Read(&sends), Is.Zero)
                Assert.That((ReviewWorkflow.getAgentInbox binding2 "Worker A" |> ok).Agent.State, Is.EqualTo("NeedsWork"))
                let beforeAccepted = Volatile.Read(&accepted)
                let beforeRaw = Volatile.Read(&rawReceived)
                if mode = 1 then wait (two.StopAsync())
                elif mode = 2 then allowReceiver <- false

                // Atomic durable receipt publication drives the real observer,
                // canonical resolve/requeue CAS, registered sender, and remote receiver.
                publish directory request (JobStatus.Completed 0) true
                until "canonical requeue committed" (fun () ->
                    (ReviewWorkflow.getAgentInbox binding "Worker A" |> ok).Agent.State = "Ready")
                if mode = 0 then
                    until "remote post-CAS hint accepted" (fun () -> Volatile.Read(&accepted) > beforeAccepted)
                elif mode = 1 then
                    Assert.That(Volatile.Read(&sends), Is.Zero)
                    wait (two.StartAsync())
                    registerHub two "b" |> ignore
                    Client.SignalR.Client.invalidateAllAgentStates rt2
                    receiver.OnConnected()
                else
                    until "lost hint reached transport" (fun () -> Volatile.Read(&rawReceived) > beforeRaw)
                    Assert.That(Volatile.Read(&accepted), Is.EqualTo(beforeAccepted))
                    Assert.That(Volatile.Read(&sends), Is.Zero)
                    allowReceiver <- true
                    timerPulse.Release() |> ignore
                Assert.That(sent.Wait(TimeSpan.FromSeconds 30.), Is.True, "canonical catchup must reach fake launch")
                Assert.That((ReviewWorkflow.getAgentInbox binding2 "Worker A" |> ok).RunnableQueue.Head.Attempt.AssignmentGeneration,
                            Is.EqualTo(2))
                // Duplicate and reordered stale hints cannot duplicate launch.
                let beforeDuplicate = Volatile.Read(&wakes)
                publishInvalidation one (fun () -> true) hint
                publishInvalidation one (fun () -> true) { hint with Epoch = binding.Epoch + 1 }
                publishInvalidation one (fun () -> true) hint
                until "duplicate hint was reconciled" (fun () -> Volatile.Read(&wakes) > beforeDuplicate)
                wait (scheduler2.StopAsync())
                Assert.That(Volatile.Read(&sends), Is.EqualTo(1))
                Assert.That(Volatile.Read(&foreignSeen), Is.Zero)
                Assert.That(Volatile.Read(&unregisteredSeen), Is.Zero)
                Assert.That(captured.ToArray() |> Array.exists (fun h -> h = hint), Is.True)
            finally
                for hub in hubs do wait (hub.DisposeAsync().AsTask())
                wait (app.StopAsync()))
