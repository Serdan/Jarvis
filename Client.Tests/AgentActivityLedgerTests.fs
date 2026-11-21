module Client.Tests.AgentActivityLedgerTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
open Client
open Client.AgentActivityLedger
open Client.LedgerStore
open Common
open Common.SignalR
open NUnit.Framework

[<TestFixture>]
type AgentActivityLedgerTests() =
    let git cwd args =
        use proc = new Process()
        proc.StartInfo.FileName <- "git"
        proc.StartInfo.WorkingDirectory <- cwd
        proc.StartInfo.RedirectStandardOutput <- true
        proc.StartInfo.RedirectStandardError <- true
        args |> List.iter proc.StartInfo.ArgumentList.Add
        Assert.That(proc.Start(), Is.True)
        let output = proc.StandardOutput.ReadToEnd()
        let error = proc.StandardError.ReadToEnd()
        proc.WaitForExit()
        Assert.That(proc.ExitCode, Is.Zero, error)
        output.Trim()

    let succeeded = function
        | Ok result -> result
        | Error error -> failwithf "Unexpected ledger error: %A" error

    let account =
        AgentActivityProtocol.accountScopeFromSubject "fixture-operator"
        |> Option.get

    let agentCommand agent project : AgentCommand =
        TaskReadCommand {
            ProjectName = project
            AgentId = agent
            Action = "GetTask"
            TaskId = "fixture-task"
            AssignmentGeneration = 0
            CheckpointSequence = 0L
            Cursor = ""
            PageSize = 0
            BeforeOid = null
            AfterOid = null
            RelativePath = null
        }

    let context device generation : AgentActivitySessionContext =
        { AccountScope = account
          DeviceId = device
          ConnectionId = "connection-" + device
          ConnectionGeneration = generation }

    let dispatch project agent device generation (atUtc: DateTimeOffset) : AgentActivityDispatch =
        { SchemaVersion = AgentActivityProtocol.schemaVersion
          ProjectName = project
          AgentId = agent
          AccountScope = account
          DeviceId = device
          ConnectionId = "connection-" + device
          ConnectionGeneration = generation
          CorrelationId = Guid.NewGuid().ToString("N")
          EventId = Guid.NewGuid().ToString("D")
          CommandName = "TaskRead"
          ObservedAtUtc = atUtc }

    let withClones f =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-agent-activity-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try
            let remote = Path.Combine(root, "remote.git")
            let a = Path.Combine(root, "device-a")
            let b = Path.Combine(root, "device-b")
            git root [ "init"; "--bare"; remote ] |> ignore
            git root [ "clone"; remote; a ] |> ignore
            git a [ "config"; "user.name"; "Activity Fixture" ] |> ignore
            git a [ "config"; "user.email"; "activity@example.test" ] |> ignore
            File.WriteAllText(Path.Combine(a, "source.txt"), "source stays unchanged")
            git a [ "add"; "source.txt" ] |> ignore
            git a [ "commit"; "-m"; "source" ] |> ignore
            git a [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
            git root [ "clone"; remote; b ] |> ignore
            git b [ "config"; "user.name"; "Activity Fixture" ] |> ignore
            git b [ "config"; "user.email"; "activity@example.test" ] |> ignore
            let projectId, ledgerId = Guid.NewGuid(), Guid.NewGuid()
            let binding path =
                { RepositoryPath = path
                  RemoteName = "origin"
                  ApprovedFetchUrl = remote
                  ApprovedPushUrl = remote
                  ProjectId = projectId
                  LedgerId = ledgerId
                  Epoch = 1 }
            let first, second = binding a, binding b
            LedgerStore.initialize first true |> succeeded |> ignore
            Client.ReviewWorkflow.ReviewWorkflow.registerAgent first (Guid.NewGuid()) "Worker 8" |> succeeded |> ignore
            Client.ReviewWorkflow.ReviewWorkflow.registerAgent first (Guid.NewGuid()) "Worker 9" |> succeeded |> ignore
            LedgerStore.fetch second |> succeeded |> ignore
            let sourceHead = git a [ "rev-parse"; "HEAD" ]
            f first second
            Assert.That(git a [ "rev-parse"; "HEAD" ], Is.EqualTo(sourceHead))
            Assert.That(git b [ "rev-parse"; "HEAD" ], Is.EqualTo(sourceHead))
            Assert.That(git a [ "status"; "--porcelain" ], Is.Empty)
            Assert.That(git b [ "status"; "--porcelain" ], Is.Empty)
        finally
            Directory.Delete(root, true)

    let record (binding: LedgerBinding) (agent: string) device generation (event: AgentActivityDispatch) : Result<LedgerWrite,string> =
        match LedgerStore.fetch binding with
        | Error failure -> Error("ActivityProofUnavailable: " + sprintf "%A" failure)
        | Ok snapshot ->
            AgentActivityLedger.record binding snapshot (context device generation)
                agent event (agentCommand agent event.ProjectName)

    let proof (binding: LedgerBinding) (agent:string) (nowUtc: DateTimeOffset) : Result<AgentActivityProof,string> =
        let snapshot = LedgerStore.fetch binding |> succeeded
        AgentActivityLedger.readAt binding snapshot account agent nowUtc

    let assertUnavailable (result: Result<'a, string>) =
        match result with
        | Error reason -> Assert.That(reason, Does.StartWith("ActivityProofUnavailable:"))
        | Ok _ -> Assert.Fail "A missing, stale, or malformed watermark cannot establish activity proof."

    [<Test>]
    member _.FirstActivityIsDurableAndConsumedWithSameAgentSnapshot() =
        withClones (fun first second ->
            let at = DateTimeOffset.UtcNow.AddMinutes(-2.)
            assertUnavailable (proof second "Worker 8" (at.AddMinutes 3.))
            let event = dispatch "Fixture" "Worker 8" "device-a" 1L at
            let committed = record first "Worker 8" "device-a" 1L event |> succeeded
            Assert.That(committed.AlreadyApplied, Is.False)
            match proof second "Worker 8" (at.AddMinutes 3.) with
            | Error reason -> Assert.Fail reason
            | Ok actual ->
                Assert.That(actual.SnapshotCommit, Is.Not.Empty)
                Assert.That(actual.Agent.AgentId, Is.EqualTo("Worker 8"))
                Assert.That(actual.ProjectId, Is.Not.Empty)
                Assert.That(actual.LedgerId, Is.Not.Empty)
                Assert.That(actual.Epoch, Is.EqualTo(1))
                Assert.That(actual.Activity.LastActivityEventId, Is.EqualTo(event.EventId))
                Assert.That(actual.Activity.LastActivityCommandName, Is.EqualTo("TaskRead"))
                Assert.That(actual.Age, Is.EqualTo(TimeSpan.FromMinutes 3.))
        )

    [<Test>]
    member _.TwoClonesReplayAndOutOfOrderKeepMonotonicActivityWithAuditableReceipts() =
        withClones (fun first second ->
            let early = DateTimeOffset.UtcNow.AddMinutes(-5.)
            let firstEvent = dispatch "Fixture" "Worker 8" "device-a" 1L early
            let firstWrite = record first "Worker 8" "device-a" 1L firstEvent |> succeeded
            let nextEvent = dispatch "Fixture" "Worker 8" "device-b" 2L (early.AddMinutes 2.)
            let nextWrite = record second "Worker 8" "device-b" 2L nextEvent |> succeeded
            Assert.That(nextWrite.Operation.Sequence, Is.GreaterThan(firstWrite.Operation.Sequence))
            let replay = record first "Worker 8" "device-a" 1L firstEvent |> succeeded
            Assert.That(replay.AlreadyApplied, Is.True)
            Assert.That(replay.Operation.Sequence, Is.EqualTo(firstWrite.Operation.Sequence))
            let lateArrival = dispatch "Fixture" "Worker 8" "device-a" 1L (early.AddMinutes 1.)
            let lateWrite = record first "Worker 8" "device-a" 1L lateArrival |> succeeded
            Assert.That(lateWrite.Operation.Sequence, Is.GreaterThan(nextWrite.Operation.Sequence))
            let current = proof second "Worker 8" (early.AddMinutes 4.) |> succeeded
            Assert.That(current.Activity.LastActivityAtUtc, Is.EqualTo(nextEvent.ObservedAtUtc.ToString("O")))
            Assert.That(current.Activity.LastActivityEventId, Is.EqualTo(nextEvent.EventId))
            Assert.That(current.Activity.LastRecordedEventId, Is.EqualTo(lateArrival.EventId))
            Assert.That(current.Activity.LastRecordedSequence, Is.EqualTo(lateWrite.Operation.Sequence))
            Assert.That(current.Activity.LastActivitySequence, Is.EqualTo(nextWrite.Operation.Sequence))
            Assert.That(current.Age, Is.EqualTo(TimeSpan.FromMinutes 2.))
        )

    [<Test>]
    member _.CompetingClonesConvergeWithoutDroppingAnActivityCommit() =
        withClones (fun first second ->
            let baseTime = DateTimeOffset.UtcNow.AddMinutes(-10.)
            let a = dispatch "Fixture" "Worker 8" "device-a" 1L baseTime
            let b = dispatch "Fixture" "Worker 8" "device-b" 3L (baseTime.AddMinutes 3.)
            let writes =
                [| Task.Run(fun () -> record first "Worker 8" "device-a" 1L a)
                   Task.Run(fun () -> record second "Worker 8" "device-b" 3L b) |]
            Task.WaitAll(writes |> Array.map (fun task -> task :> Task))
            for written in writes do
                let result = written.Result |> succeeded
                Assert.That(result.Operation.Sequence, Is.GreaterThan(0L))
            let actual = proof second "Worker 8" (baseTime.AddMinutes 5.) |> succeeded
            Assert.That(actual.Activity.LastActivityEventId, Is.EqualTo(b.EventId))
            Assert.That(actual.Activity.LastActivitySequence, Is.GreaterThan(0L))
        )

    [<Test>]
    member _.ExactActorAccountProjectAndSessionBindingAreMandatory() =
        withClones (fun first second ->
            let now = DateTimeOffset.UtcNow
            let event = dispatch "Fixture" "Worker 8" "device-a" 1L now
            AgentActivityLedger.validateDispatch (context "device-a" 1L) "Worker 9"
                (agentCommand "Worker 9" "Fixture") event
            |> assertUnavailable
            AgentActivityLedger.validateDispatch (context "device-b" 1L) "Worker 8"
                (agentCommand "Worker 8" "Fixture") event
            |> assertUnavailable
            AgentActivityLedger.validateDispatch (context "device-a" 2L) "Worker 8"
                (agentCommand "Worker 8" "Fixture") event
            |> assertUnavailable
            AgentActivityLedger.validateDispatch (context "device-a" 1L) "Worker 8"
                (agentCommand "Worker 8" "OtherProject") event
            |> assertUnavailable
            record first "Worker 8" "device-a" 1L event |> succeeded |> ignore
            assertUnavailable (proof second "Worker 9" (now.AddMinutes 1.))
            let otherScope = AgentActivityProtocol.accountScopeFromSubject "different-operator" |> Option.get
            let snapshot = LedgerStore.fetch second |> succeeded
            AgentActivityLedger.readAt second snapshot otherScope "Worker 8" (now.AddMinutes 1.)
            |> assertUnavailable
            AgentActivityLedger.readAt second snapshot account "Worker 8" (now.AddMinutes(-1.))
            |> assertUnavailable
            let unrelatedBinding = { second with LedgerId = Guid.NewGuid() }
            AgentActivityLedger.readAt unrelatedBinding snapshot account "Worker 8" (now.AddMinutes 1.)
            |> assertUnavailable
        )

    [<Test>]
    member _.UnknownPublicationAndReplayedEventNeverReachToolEffect() =
        withClones (fun first second ->
            let mutable executed = 0
            let execute () = task { executed <- executed + 1; return "effect" }
            let denied =
                AgentActivityLedger.executeAfterCommit
                    (fun () -> Error "ActivityProofUnavailable: remote unknown")
                    execute
                |> fun task -> task.GetAwaiter().GetResult()
            match denied with
            | Error(ActivityProofUnavailable reason) ->
                Assert.That(reason, Does.Contain("remote unknown"))
            | _ -> Assert.Fail "Unknown publication must suppress the entire tool effect."
            Assert.That(executed, Is.Zero)

            let now = DateTimeOffset.UtcNow.AddMinutes(-1.)
            let event = dispatch "Fixture" "Worker 8" "device-a" 1L now
            let firstWrite = record first "Worker 8" "device-a" 1L event |> succeeded
            let duplicate =
                AgentActivityLedger.executeAfterCommit
                    (fun () -> record second "Worker 8" "device-a" 1L event)
                    execute
                |> fun task -> task.GetAwaiter().GetResult()
            match duplicate with
            | Error DuplicateActivityEvent -> ()
            | _ -> Assert.Fail "A committed event replay must not replay an uncertain tool effect."
            Assert.That(executed, Is.Zero)
            Assert.That(firstWrite.AlreadyApplied, Is.False)
            Assert.That((proof second "Worker 8" now).IsOk, Is.True)
        )

    [<Test>]
    member _.RejectedObserverActivityAndInvalidDispatchNeverPublish() =
        withClones (fun first second ->
            let now = DateTimeOffset.UtcNow
            let ignored : AgentCommand =
                LedgerReadCommand { ProjectName = "Fixture"; Action = "Health"; OperationId = ""
                                    Cursor = ""; PageSize = 0 }
            Assert.That(AgentActivityLedger.requiresProof "Worker 8" ignored, Is.False)
            let bad = { dispatch "Fixture" "Worker 8" "device-a" 1L now with
                            ObservedAtUtc = now.ToOffset(TimeSpan.FromHours 2.) }
            record first "Worker 8" "device-a" 1L bad |> assertUnavailable
            let invalidId = { dispatch "Fixture" "Worker 8" "device-a" 1L now with EventId = "" }
            record first "Worker 8" "device-a" 1L invalidId |> assertUnavailable
            assertUnavailable (proof second "Worker 8" now)
        )

    [<Test>]
    member _.IngressRequiresAuthenticatedEnvelopeAndRejectsReplayBeforeExecution() =
        withClones (fun first second ->
            let at = DateTimeOffset.UtcNow.AddMinutes(-1.)
            let event = dispatch "Fixture" "Worker 8" "device-a" 1L at
            let command = agentCommand "Worker 8" "Fixture"
            let plain = System.Text.Json.JsonSerializer.Serialize<AgentCommand>(command)
            let wrapped =
                System.Text.Json.JsonSerializer.Serialize<AgentActivityCommandEnvelope>
                    { SchemaVersion = AgentActivityProtocol.schemaVersion
                      CommandJson = plain; Activity = event }
            let load project =
                if project = "Fixture" then
                    Ok(first, LedgerStore.fetch first |> succeeded)
                else Error "untrusted project"
            let session = Some(context "device-a" 1L)
            AgentActivityIngress.admitWith load session event.CorrelationId "Worker 8" plain
            |> assertUnavailable
            Assert.That((proof second "Worker 8" at).IsError, Is.True)
            let accepted =
                AgentActivityIngress.admitWith load session event.CorrelationId "Worker 8" wrapped
            match accepted with
            | Ok json -> Assert.That(json, Is.EqualTo(plain))
            | Error reason -> Assert.Fail(reason)
            Assert.That((proof second "Worker 8" (at.AddMinutes 1.)).IsOk, Is.True)
            let replay =
                AgentActivityIngress.admitWith load session event.CorrelationId "Worker 8" wrapped
            assertUnavailable replay
            let wrongCorrelation =
                AgentActivityIngress.admitWith load session "not-the-same-correlation" "Worker 8" wrapped
            assertUnavailable wrongCorrelation
            let wrongSession =
                AgentActivityIngress.admitWith load (Some(context "device-a" 2L)) event.CorrelationId "Worker 8" wrapped
            assertUnavailable wrongSession
        )

    [<Test>]
    member _.ProofPublicationFailureOnUnreachableBareRemoteNeverExecutes() =
        withClones (fun first second ->
            let remote = first.ApprovedFetchUrl
            Directory.Move(remote, remote + ".offline")
            let event = dispatch "Fixture" "Worker 8" "device-a" 1L DateTimeOffset.UtcNow
            let mutable executed = 0
            let result =
                AgentActivityLedger.executeAfterCommit
                    (fun () -> record first "Worker 8" "device-a" 1L event)
                    (fun () -> task { executed <- executed + 1; return "effect" })
                |> fun task -> task.GetAwaiter().GetResult()
            match result with
            | Error(ActivityProofUnavailable reason) ->
                Assert.That(reason, Does.StartWith("ActivityProofUnavailable:"))
            | _ -> Assert.Fail "Unreachable canonical remote must reject pre-execution."
            Assert.That(executed, Is.Zero)
            Directory.Move(remote + ".offline", remote)
            assertUnavailable (proof second "Worker 8" (event.ObservedAtUtc.AddMinutes 1.))
        )

    [<Test>]
    member _.ToolFailureAfterPublicationDoesNotEraseTheActivityEvent() =
        withClones (fun first second ->
            let at = DateTimeOffset.UtcNow.AddMinutes(-1.)
            let event = dispatch "Fixture" "Worker 8" "device-a" 1L at
            let mutable invoked = 0
            let failed =
                try
                    AgentActivityLedger.executeAfterCommit
                        (fun () -> record first "Worker 8" "device-a" 1L event)
                        (fun () ->
                            invoked <- invoked + 1
                            Task.FromException<string>(InvalidOperationException("Injected post-proof tool failure")))
                    |> fun pending -> pending.GetAwaiter().GetResult()
                    |> ignore
                    false
                with :? InvalidOperationException -> true
            Assert.That(failed, Is.True)
            Assert.That(invoked, Is.EqualTo(1))
            let canonical = proof second "Worker 8" (at.AddMinutes 2.) |> succeeded
            Assert.That(canonical.Activity.LastActivityEventId, Is.EqualTo(event.EventId))
        )
