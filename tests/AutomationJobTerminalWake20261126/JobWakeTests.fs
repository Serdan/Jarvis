module AutomationJobTerminalWake20261126.JobWakeTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open NUnit.Framework
open Common
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.ChallengeWorkflow
open Client.AutomationJobTerminalWake

module Durable = Client.BoundedProcess.DurableJobs

[<TestFixture>]
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
