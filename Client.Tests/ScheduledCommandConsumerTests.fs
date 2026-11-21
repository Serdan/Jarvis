module ScheduledCommandConsumerTests

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Client
open Client.IO
open Client.JobManager
open Common
open NUnit.Framework

let private createTempProject () =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-scheduled-command-consumer-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(Path.Combine(root, "Project1")) |> ignore
    root

type private TestContext(root: string) =
    interface ProjectIO with
        member _.Project =
            { Root = ProjectDirectory root
              SpecialFiles = []
              FolderFilters = [] }
    interface FileIO with
        member _.File = FileOperations.impl
        member _.EditText(path, options, edit) =
            AtomicTextFiles.edit path options edit

let private scheduledCommand executable args timeout : RunCommandCommand =
    { ProjectName = "Project1"
      Executable = executable
      Args = args
      Reason = Some "Scheduled command consumer test"
      WorkingDirectory = None
      TimeoutSeconds = Some timeout
      MaxOutputBytes = Some 4096 }

let private runningResult () : RunCommandResult =
    { ExitCode = -1
      TimedOut = false
      StdOut = ""
      StdErr = ""
      Truncated = false
      JobId = Some "job-1"
      ProcessStatus = "Running" }

let private execute receive awaitTerminal command =
    JobManager.executeScheduledRunCommand receive awaitTerminal command

[<Test; NonParallelizable>]
let scheduledRunCommandConsumerWaitsForRealExitWithoutReplay () = task {
    let root = createTempProject ()
    let context = TestContext root
    let project = Path.Combine(root, "Project1")
    let marker = Path.Combine(project, "scheduled-once-count.txt")
    let script = """System.IO.File.AppendAllText("scheduled-once-count.txt", "X")
System.Threading.Thread.Sleep(2700)
"""
    File.WriteAllText(Path.Combine(project, "scheduled-once.fsx"), script)

    let mutable starts = 0
    let mutable initialStatus: string option = None
    let mutable jobId: string option = None

    let receive (command: RunCommandCommand) : Task<Client.Result<string>> =
        starts <- starts + 1
        match JobManager.runManagedCommand command context with
        | Error error -> Task.FromResult(Error error)
        | Ok initial ->
            initialStatus <- Some initial.ProcessStatus
            jobId <- initial.JobId
            Task.FromResult(Ok(JsonSerializer.Serialize initial))

    let awaitTerminal projectName initial timeoutSeconds =
        JobManager.awaitScheduledCommandTerminal projectName initial timeoutSeconds context

    try
        let! result =
            execute receive awaitTerminal
                (scheduledCommand "dotnet" [ "fsi"; "--exec"; "scheduled-once.fsx" ] 20)

        Assert.That(initialStatus, Is.EqualTo(Some "Running"),
                    "The short command response window must not be mistaken for process completion.")
        Assert.That(jobId.IsSome, Is.True)
        Assert.That(starts, Is.EqualTo(1), "The scheduled consumer must start exactly one OS process.")

        match result with
        | Ok summary ->
            use document = JsonDocument.Parse(summary)
            Assert.That(document.RootElement.GetProperty("processStatus").GetString(), Is.EqualTo("Completed"))
            Assert.That(document.RootElement.GetProperty("exitCode").GetInt32(), Is.EqualTo(0))
        | Error (_, reason) ->
            Assert.Fail("A successful long-running command must not become CommandFailed: " + reason)

        Assert.That(File.ReadAllText(marker), Is.EqualTo("X"),
                    "The external marker proves the command ran once and was not replayed.")
    finally
        jobId |> Option.iter (fun id ->
            JobManager.cancelJob { ProjectName = "Project1"; JobId = id } context |> ignore)
        Directory.Delete(root, true)
}

[<Test>]
let scheduledConsumerPreservesTypedPostStartUnknownWithoutReplay () = task {
    let mutable starts = 0
    let mutable polls = 0

    let receive (_: RunCommandCommand) : Task<Client.Result<string>> =
        starts <- starts + 1
        Task.FromResult(Error(Client.ProcessOutcomeUnknown "Started process job unavailable after admission."))

    let awaitTerminal (_: string) (_: RunCommandResult) (_: int) : Task<Result<JobStatus * bool, Client.EffectError>> =
        polls <- polls + 1
        Task.FromResult(Ok(JobStatus.Completed 0, false))

    let! result = execute receive awaitTerminal (scheduledCommand "dotnet" [] 5)

    match result with
    | Error ("", reason) when reason.StartsWith("OutcomeUnknown:", StringComparison.Ordinal) -> ()
    | other -> Assert.Fail(sprintf "Typed post-start uncertainty must reach the schedule as OutcomeUnknown, got %A" other)

    Assert.That(starts, Is.EqualTo(1))
    Assert.That(polls, Is.EqualTo(0), "A missing job handle must not trigger another command or a guessed terminal result.")
}

[<Test>]
let scheduledConsumerMapsTerminalProcessOutcomesExhaustively () = task {
    let initial = runningResult ()
    let receive (_: RunCommandCommand) : Task<Client.Result<string>> =
        Task.FromResult(Ok(JsonSerializer.Serialize initial))
    let action = scheduledCommand "dotnet" [] 5

    let run status =
        let awaitTerminal (_: string) (_: RunCommandResult) (_: int) =
            Task.FromResult(Ok(status, false))
        execute receive awaitTerminal action

    let! success = run (JobStatus.Completed 0)
    match success with
    | Ok summary ->
        use document = JsonDocument.Parse(summary)
        Assert.That(document.RootElement.GetProperty("exitCode").GetInt32(), Is.EqualTo(0))
    | other -> Assert.Fail(sprintf "Zero exit must be success, got %A" other)

    let! nonzero = run (JobStatus.Completed 7)
    match nonzero with
    | Error (_, "CommandFailed") -> ()
    | other -> Assert.Fail(sprintf "Nonzero exit must remain a definite failure, got %A" other)

    let! timedOut = run JobStatus.TimedOut
    match timedOut with
    | Error (_, "CommandTimedOut") -> ()
    | other -> Assert.Fail(sprintf "An explicit deadline must remain a timeout, got %A" other)

    let! canceled = run JobStatus.Canceled
    match canceled with
    | Error (_, "CommandCanceled") -> ()
    | other -> Assert.Fail(sprintf "Cancellation must remain distinct, got %A" other)

    let! spawnFailed = run (JobStatus.FailedToStart "executable unavailable")
    match spawnFailed with
    | Error (_, reason) when reason = "CommandSpawnFailed: executable unavailable" -> ()
    | other -> Assert.Fail(sprintf "Spawn failure must remain definite, got %A" other)

    let! unexpected = run JobStatus.Running
    match unexpected with
    | Error (_, reason) when reason.StartsWith("OutcomeUnknown:", StringComparison.Ordinal) -> ()
    | other -> Assert.Fail(sprintf "A nonterminal poll result must fail closed as unknown, got %A" other)
}

[<Test>]
let scheduledConsumerSeparatesDefiniteCommandErrorsFromUnknownEffects () = task {
    let action = scheduledCommand "dotnet" [] 5
    let definiteErrors : (Client.EffectError * string) list =
        [ Client.PermissionDenied "permission denied", "PermissionDenied"
          Client.ValidationError "invalid command", "ValidationError"
          Client.ProcessSpawnFailed "verified OS start failed", "CommandSpawnFailed" ]

    for error, expectedKind in definiteErrors do
        let receive (_: RunCommandCommand) : Task<Client.Result<string>> =
            Task.FromResult(Error error)
        let awaitTerminal (_: string) (_: RunCommandResult) (_: int) =
            Task.FromResult(Ok(JobStatus.Completed 0, false))
        let! result = execute receive awaitTerminal action
        match result with
        | Error (_, reason) ->
            Assert.That(reason.Contains("OutcomeUnknown", StringComparison.Ordinal), Is.False,
                        "Proven pre-start denial, validation, and typed spawn errors must remain definite.")
            Assert.That(reason.Contains(expectedKind, StringComparison.Ordinal), Is.True,
                        "The consumer must preserve the concrete definite failure class.")
        | other -> Assert.Fail(sprintf "A definite command error must remain an error, got %A" other)
}

[<Test>]
let scheduledConsumerTreatsMalformedAndUncertainResponsesAsUnknown () = task {
    let action = scheduledCommand "dotnet" [] 5
    let mutable polls = 0

    let malformedReceive (_: RunCommandCommand) : Task<Client.Result<string>> =
        Task.FromResult(Ok("{ malformed"))
    let noPoll (_: string) (_: RunCommandResult) (_: int) =
        polls <- polls + 1
        Task.FromResult(Ok(JobStatus.Completed 0, false))

    let! malformed = execute malformedReceive noPoll action
    match malformed with
    | Error ("", reason) when reason.StartsWith("OutcomeUnknown:", StringComparison.Ordinal) -> ()
    | other -> Assert.Fail(sprintf "Malformed response must be unknown, got %A" other)
    Assert.That(polls, Is.EqualTo(0))

    let receiveRunning (_: RunCommandCommand) : Task<Client.Result<string>> =
        Task.FromResult(Ok(JsonSerializer.Serialize(runningResult ())))
    let uncertainPoll (_: string) (_: RunCommandResult) (_: int) : Task<Result<JobStatus * bool, Client.EffectError>> =
        Task.FromResult(Error(Client.ProcessOutcomeUnknown "The started process job is no longer readable."))

    let! uncertain = execute receiveRunning uncertainPoll action
    match uncertain with
    | Error (summary, reason) when reason.StartsWith("OutcomeUnknown:", StringComparison.Ordinal) ->
        use document = JsonDocument.Parse(summary)
        Assert.That(document.RootElement.GetProperty("processStatus").GetString(), Is.EqualTo("Unknown"))
        Assert.That(document.RootElement.GetProperty("exitCode").GetInt32(), Is.EqualTo(-1))
    | other -> Assert.Fail(sprintf "An uncertain terminal read must stay unknown, got %A" other)
}


[<Test; NonParallelizable>]
let scheduledRunCommandConsumerPersistsTerminalSuccessThroughScheduler () = task {
    // Exercise the exact command-consumer function invoked by Program.fs as
    // the action callback of a disposable scheduler. This is not a live schedule.
    let root = createTempProject ()
    let project = Path.Combine(root, "Project1")
    let marker = Path.Combine(project, "scheduled-persisted-once.txt")
    let scheduleFile = Path.Combine(root, "scheduled-test.json")
    let script = """System.IO.File.AppendAllText("scheduled-persisted-once.txt", "X")
System.Threading.Thread.Sleep(2700)
"""
    File.WriteAllText(Path.Combine(project, "scheduled-persisted-once.fsx"), script)
    let context = TestContext root
    let mutable clock = DateTimeOffset.UtcNow
    let mutable starts = 0
    let mutable initialProcessStatus = ""
    let id = "scheduled-real-consumer"

    let status (service: Client.BrowserSchedules.Service) =
        use document = JsonDocument.Parse(service.List())
        document.RootElement.EnumerateArray()
        |> Seq.find (fun item -> item.GetProperty("id").GetString() = id)
        |> fun item -> item.GetProperty("status").GetString()

    let scheduledAction (action: Client.BrowserSchedules.ScheduledAction) =
        let command: RunCommandCommand =
            { ProjectName = action.ProjectName
              Executable = action.Executable
              Args = action.CommandArgs
              Reason = Some("Scheduled execution " + action.Id)
              WorkingDirectory = None
              TimeoutSeconds = Some action.TimeoutSeconds
              MaxOutputBytes = Some action.MaxOutputBytes }
        let receive (cmd: RunCommandCommand) : Task<Client.Result<string>> =
            System.Threading.Interlocked.Increment(&starts) |> ignore
            match JobManager.runManagedCommand cmd context with
            | Error error -> Task.FromResult(Error error)
            | Ok initial ->
                initialProcessStatus <- initial.ProcessStatus
                Task.FromResult(Ok(JsonSerializer.Serialize initial))
        let awaitTerminal projectName initial timeoutSeconds =
            JobManager.awaitScheduledCommandTerminal projectName initial timeoutSeconds context
        JobManager.executeScheduledRunCommand receive awaitTerminal command

    let config: ScheduleExecutionCommand =
        { ScheduleId = id; ProjectName = "Project1"; TriggerKind = "Once"
          RunAfterSeconds = Some 600; RunAtUtc = None; RepeatEverySeconds = None
          IdleAgentId = None; IdleSeconds = None
          ActionKind = "RunCommand"; Executable = Some "dotnet"
          Args = [ "fsi"; "--exec"; "scheduled-persisted-once.fsx" ]
          WorkingDirectory = None; TimeoutSeconds = Some 20; MaxOutputBytes = Some 4096
          WindowRegistration = None; Url = None; ScriptPath = None
          ArgsJson = None; Alias = None; Active = None
          CloseTabAfterExecution = None }

    try
        do! task {
            use service = new Client.BrowserSchedules.Service(
                scheduleFile, scheduledAction, now=(fun () -> clock), pollMs=25)
            Assert.That(service.ScheduleExecution(config).IsOk, Is.True)
            service.Start()
            clock <- clock.AddSeconds(601.)
            let mutable observedStatus = ""
            let mutable attempts = 0
            while observedStatus <> "completed" && attempts < 400 do
                do! Task.Delay(50)
                observedStatus <- status service
                attempts <- attempts + 1
            Assert.That(observedStatus, Is.EqualTo("completed"),
                "The real scheduled consumer must persist terminal success rather than early Running failure.")
            Assert.That(initialProcessStatus, Is.EqualTo("Running"))
            Assert.That(starts, Is.EqualTo(1), "Exactly one command must have been admitted.")
            Assert.That(File.ReadAllText(marker), Is.EqualTo("X"), "No external effect replay is permitted.")
            Assert.That(service.List(), Does.Contain("Completed"))
        }
        // Restart the disposable scheduler and verify that its terminal result survived.
        use recovered = new Client.BrowserSchedules.Service(scheduleFile, scheduledAction, pollMs=25)
        Assert.That(status recovered, Is.EqualTo("completed"))
        Assert.That(starts, Is.EqualTo(1))
    finally
        Directory.Delete(root, true)
}


[<Test>]
let scheduledRunCommandTypedUnknownPersistsWithoutReplayingOneShot () = task {
    // Worker 18 owns recurring re-arm behavior. This one-shot fixture proves
    // the command consumer emits the stable unknown outcome the scheduler stores.
    let root = createTempProject ()
    let scheduleFile = Path.Combine(root, "scheduled-unknown.json")
    let mutable clock = DateTimeOffset.UtcNow
    let mutable starts = 0
    let mutable terminalPolls = 0
    let id = "scheduled-unknown-consumer"
    let status (service: Client.BrowserSchedules.Service) =
        use document = JsonDocument.Parse(service.List())
        document.RootElement.EnumerateArray()
        |> Seq.find (fun item -> item.GetProperty("id").GetString() = id)
        |> fun item -> item.GetProperty("status").GetString()

    let consume (_: Client.BrowserSchedules.ScheduledAction) =
        let receive (_: RunCommandCommand) : Task<Client.Result<string>> =
            System.Threading.Interlocked.Increment(&starts) |> ignore
            Task.FromResult(Error(Client.ProcessOutcomeUnknown "Started job handle disappeared after admission."))
        let shouldNotPoll (_: string) (_: RunCommandResult) (_: int) =
            System.Threading.Interlocked.Increment(&terminalPolls) |> ignore
            Task.FromResult(Ok(JobStatus.Completed 0, false))
        JobManager.executeScheduledRunCommand receive shouldNotPoll (scheduledCommand "dotnet" [] 5)

    let config: ScheduleExecutionCommand =
        { ScheduleId = id; ProjectName = "Project1"; TriggerKind = "Once"
          RunAfterSeconds = Some 600; RunAtUtc = None; RepeatEverySeconds = None
          IdleAgentId = None; IdleSeconds = None
          ActionKind = "RunCommand"; Executable = Some "dotnet"; Args = []
          WorkingDirectory = None; TimeoutSeconds = Some 5; MaxOutputBytes = Some 4096
          WindowRegistration = None; Url = None; ScriptPath = None
          ArgsJson = None; Alias = None; Active = None
          CloseTabAfterExecution = None }

    try
        do! task {
            use service = new Client.BrowserSchedules.Service(
                scheduleFile, consume, now=(fun () -> clock), pollMs=25)
            Assert.That(service.ScheduleExecution(config).IsOk, Is.True)
            service.Start()
            clock <- clock.AddSeconds(601.)
            let mutable observedStatus = ""
            let mutable attempts = 0
            while observedStatus <> "outcomeUnknown" && attempts < 120 do
                do! Task.Delay(25)
                observedStatus <- status service
                attempts <- attempts + 1
            Assert.That(observedStatus, Is.EqualTo("outcomeUnknown"))
            Assert.That(starts, Is.EqualTo(1), "Unknown external effects must not be replayed.")
            Assert.That(terminalPolls, Is.Zero, "The missing job handle cannot yield a known terminal result.")
        }
        use recovered = new Client.BrowserSchedules.Service(scheduleFile, consume, pollMs=25)
        Assert.That(status recovered, Is.EqualTo("outcomeUnknown"),
            "The unknown outcome must survive reopening the isolated scheduler store.")
        Assert.That(starts, Is.EqualTo(1))
    finally
        Directory.Delete(root, true)
}


[<Test>]
let verifiedPreStartOsSpawnDenialRemainsDefiniteThroughScheduledConsumer () = task {
    let root = createTempProject ()
    let context = TestContext root
    let executable = Path.Combine(root, "never-created-" + Guid.NewGuid().ToString("N"))
    let command = scheduledCommand executable [] 5
    let mutable admissions = 0
    let mutable polls = 0

    let receive (cmd: RunCommandCommand) : Task<Client.Result<string>> =
        admissions <- admissions + 1
        Task.FromResult(
            JobManager.runManagedCommand cmd context
            |> Result.map JsonSerializer.Serialize)
    let awaitTerminal (_: string) (_: RunCommandResult) (_: int) =
        polls <- polls + 1
        Task.FromResult(Ok(JobStatus.Completed 0, false))

    try
        match JobManager.runManagedCommand command context with
        | Error(Client.ProcessSpawnFailed _) -> ()
        | other -> Assert.Fail(sprintf "The actual OS pre-start exception must be typed ProcessSpawnFailed, got %A" other)

        let! result = execute receive awaitTerminal command
        match result with
        | Error ("", reason) when reason.StartsWith("CommandSpawnFailed:", StringComparison.Ordinal) -> ()
        | other -> Assert.Fail(sprintf "A proven pre-start OS failure must not become uncertain, got %A" other)
        Assert.That(admissions, Is.EqualTo(1))
        Assert.That(polls, Is.EqualTo(0))
    finally
        Directory.Delete(root, true)
}

[<TestCase("post-start-serialization"); TestCase("unavailable-exit-code"); NonParallelizable>]
let admittedProcessAdverseObservationsPersistUnknownWithoutReplay (fault: string) = task {
    // Invoke the production scheduled RunCommand consumer from the actual
    // scheduler action callback. The command is a real, single OS process;
    // only the post-admission observation is fault-injected.
    let root = createTempProject ()
    let project = Path.Combine(root, "Project1")
    let marker = Path.Combine(project, "admitted-effect.txt")
    let scheduleFile = Path.Combine(root, "adverse-schedule.json")
    let script = """System.IO.File.AppendAllText("admitted-effect.txt", "X")
System.Threading.Thread.Sleep(600)
"""
    File.WriteAllText(Path.Combine(project, "admitted-effect.fsx"), script)
    let context = TestContext root
    let id = "adverse-run-command-" + fault
    let mutable clock = DateTimeOffset.UtcNow
    let mutable starts = 0
    let mutable polls = 0
    let mutable admittedJobId: string option = None

    let status (service: Client.BrowserSchedules.Service) =
        use document = JsonDocument.Parse(service.List())
        document.RootElement.EnumerateArray()
        |> Seq.find (fun item -> item.GetProperty("id").GetString() = id)
        |> fun item -> item.GetProperty("status").GetString()

    let action (scheduled: Client.BrowserSchedules.ScheduledAction) =
        let command = scheduledCommand scheduled.Executable scheduled.CommandArgs scheduled.TimeoutSeconds
        let receive (cmd: RunCommandCommand) : Task<Client.Result<string>> =
            System.Threading.Interlocked.Increment(&starts) |> ignore
            match JobManager.runManagedCommand cmd context with
            | Error error -> Task.FromResult(Error error)
            | Ok started ->
                admittedJobId <- started.JobId
                if fault = "post-start-serialization" then
                    // Simulate the transport serializer failing AFTER OS admission.
                    Task.FromResult(Error(Client.ExceptionError(
                        JsonException("Injected response-serialization error after process admission"))))
                else
                    Task.FromResult(Ok(JsonSerializer.Serialize started))

        let awaitTerminal (_: string) (_: RunCommandResult) (_: int) =
            System.Threading.Interlocked.Increment(&polls) |> ignore
            // The exact classifier used by Process.Exited must not claim a
            // definite start failure when ExitCode is inaccessible.
            let unverified = JobManager.statusAfterAdmittedProcessExit (fun () ->
                raise (InvalidOperationException("Injected ExitCode read failure")))
            match unverified with
            | JobStatus.ToolFailed reason when reason.Contains("OutcomeUnknown", StringComparison.Ordinal) -> ()
            | other -> Assert.Fail(sprintf "A started process with unverified ExitCode must be unknown, got %A" other)
            Task.FromResult(Ok(unverified, false))

        execute receive awaitTerminal command

    let config: ScheduleExecutionCommand =
        { ScheduleId = id; ProjectName = "Project1"; TriggerKind = "Once"
          RunAfterSeconds = Some 600; RunAtUtc = None; RepeatEverySeconds = None
          IdleAgentId = None; IdleSeconds = None
          ActionKind = "RunCommand"; Executable = Some "dotnet"
          Args = [ "fsi"; "--exec"; "admitted-effect.fsx" ]
          WorkingDirectory = None; TimeoutSeconds = Some 20; MaxOutputBytes = Some 4096
          WindowRegistration = None; Url = None; ScriptPath = None
          ArgsJson = None; Alias = None; Active = None
          CloseTabAfterExecution = None }

    try
        do! task {
            use service = new Client.BrowserSchedules.Service(
                scheduleFile, action, now=(fun () -> clock), pollMs=25)
            Assert.That(service.ScheduleExecution(config).IsOk, Is.True)
            service.Start()
            clock <- clock.AddSeconds(601.)
            let mutable observed = ""
            let mutable attempts = 0
            while observed <> "outcomeUnknown" && attempts < 400 do
                do! Task.Delay(50)
                observed <- status service
                attempts <- attempts + 1
            Assert.That(observed, Is.EqualTo("outcomeUnknown"),
                "Neither post-admission fault may be classified as a definite failed spawn.")
            Assert.That(starts, Is.EqualTo(1), "The scheduler must admit exactly one process.")
            Assert.That(admittedJobId.IsSome, Is.True, "The actual OS process was admitted.")
            Assert.That(polls, Is.EqualTo(if fault = "unavailable-exit-code" then 1 else 0))
        }

        let mutable markerAttempts = 0
        while not (File.Exists(marker)) && markerAttempts < 600 do
            do! Task.Delay(50)
            markerAttempts <- markerAttempts + 1
        Assert.That(File.Exists(marker), Is.True, "The admitted OS process must execute its external effect.")
        Assert.That(File.ReadAllText(marker), Is.EqualTo("X"), "The external effect must occur once.")

        use recovered = new Client.BrowserSchedules.Service(
            scheduleFile, action, now=(fun () -> clock), pollMs=25)
        Assert.That(status recovered, Is.EqualTo("outcomeUnknown"),
            "The uncertain result must survive a fresh scheduler instance.")
        recovered.Start()
        clock <- clock.AddDays(2.)
        do! Task.Delay(250)
        Assert.That(status recovered, Is.EqualTo("outcomeUnknown"))
        Assert.That(starts, Is.EqualTo(1), "An unknown one-shot must never be relaunched after recovery.")
        Assert.That(File.ReadAllText(marker), Is.EqualTo("X"))
    finally
        admittedJobId |> Option.iter (fun jobId ->
            JobManager.cancelJob { ProjectName = "Project1"; JobId = jobId } context |> ignore)
        Directory.Delete(root, true)
}
