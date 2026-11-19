module BrowserSchedulesTests

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Client
open Common
open NUnit.Framework

let private temp () =
    let dir = Path.Combine(Path.GetTempPath(), "jarvis-schedules-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    dir, Path.Combine(dir, "schedules.json")

let private add (service: BrowserSchedules.Service) id seconds =
    service.Schedule(id, seconds, "https://example.test/chat", "Scripts", "start.js",
        "agent-next", """{"token":"private-args"}""", false, windowRegistration="agents")

let private status (service: BrowserSchedules.Service) id =
    use doc = JsonDocument.Parse(service.List())
    doc.RootElement.EnumerateArray()
    |> Seq.find (fun x -> x.GetProperty("id").GetString() = id)
    |> fun x -> x.GetProperty("status").GetString()

let private awaitStatus test = task {
    let mutable found = false
    for _ in 1..120 do
        if test() then found <- true
        elif not found then do! Task.Delay(25)
    Assert.That(found, Is.True, "Expected schedule state was not reached")
}

[<Test>]
let durableOneShot () = task {
    let dir,path = temp()
    let mutable clock = DateTimeOffset.UtcNow
    let mutable called = 0
    let action (a:BrowserSchedules.ScheduledAction) = task {
        called <- called + 1
        Assert.That(a.ProjectName, Is.EqualTo("Scripts"))
        return Ok "new-tab-registration"
    }
    try
        do
            use first = new BrowserSchedules.Service(path, action, now=(fun () -> clock), pollMs=25)
            Assert.That((add first "orchestrator" 600).IsOk, Is.True)
            Assert.That(status first "orchestrator", Is.EqualTo("pending"))
            if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                let mode = File.GetUnixFileMode(path)
                Assert.That(mode.HasFlag(UnixFileMode.GroupRead), Is.False)
                Assert.That(mode.HasFlag(UnixFileMode.OtherRead), Is.False)
        clock <- clock.AddSeconds(601.)
        use restored = new BrowserSchedules.Service(path, action, now=(fun () -> clock), pollMs=25)
        restored.Start()
        do! awaitStatus (fun () -> status restored "orchestrator" = "completed")
        do! Task.Delay(100)
        Assert.That(called, Is.EqualTo(1))
        Assert.That(restored.List(), Does.Contain("new-tab-registration"))
        Assert.That(restored.List(), Does.Not.Contain("private-args"))
    finally
        Directory.Delete(dir, true)
}

type private NonPumpingContext() =
    inherit SynchronizationContext()
    override _.Post(_, _) = ()

[<Test>]
let schedulerPollingDoesNotCaptureCallerSynchronizationContext () = task {
    let dir, path = temp()
    let mutable now = DateTimeOffset.UtcNow
    let mutable calls = 0
    let execute (_: BrowserSchedules.ScheduledAction) = task {
        Interlocked.Increment(&calls) |> ignore
        return Ok "completed"
    }
    try
        use scheduler = new BrowserSchedules.Service(path, execute, now=(fun () -> now), pollMs=25)
        let previous = SynchronizationContext.Current
        try
            SynchronizationContext.SetSynchronizationContext(NonPumpingContext())
            Assert.That((add scheduler "isolated-loop" 5).IsOk, Is.True)
            scheduler.Start()
        finally
            SynchronizationContext.SetSynchronizationContext(previous)
        now <- now.AddSeconds(6.)
        do! awaitStatus (fun () -> status scheduler "isolated-loop" = "completed")
        Assert.That(calls, Is.EqualTo(1))
    finally
        Directory.Delete(dir, true)
}

[<Test>]
let schedulerTransientFaultIsReportedAndPollingRecovers () = task {
    let dir, path = temp()
    let mutable failClock = false
    let mutable diagnostic = ""
    let mutable now = DateTimeOffset.UtcNow
    let mutable calls = 0
    let clock () =
        if failClock then
            failClock <- false
            failwith "test-clock-failure"
        now
    let execute (_: BrowserSchedules.ScheduledAction) = task {
        calls <- calls + 1
        return Ok "completed"
    }
    try
        use scheduler = new BrowserSchedules.Service(path, execute,
            now=clock, pollMs=25, onFault=(fun reason -> diagnostic <- reason))
        Assert.That((add scheduler "faulted-loop" 5).IsOk, Is.True)
        now <- now.AddSeconds(6.)
        failClock <- true
        scheduler.Start()
        do! awaitStatus (fun () -> status scheduler "faulted-loop" = "completed")
        Assert.That(diagnostic, Does.Contain("Persistent scheduler polling fault (retrying)"))
        Assert.That(diagnostic, Does.Contain("test-clock-failure"))
        Assert.That(calls, Is.EqualTo(1), "Recovery must not duplicate a launch")
    finally
        Directory.Delete(dir, true)
}

[<Test>]
let restoredProductionScheduleStatePollsWithoutFaults () = task {
    let source = Environment.GetEnvironmentVariable("JARVIS_SCHEDULE_FIXTURE_PATH")
    if String.IsNullOrWhiteSpace(source) || not (File.Exists(source)) then
        Assert.Ignore("Explicit production schedule fixture not supplied")
    else
        let dir, path = temp()
        let mutable failure = ""
        let execute (_: BrowserSchedules.ScheduledAction) = task { return Ok "test-only-stub" }
        try
            File.Copy(source, path)
            use scheduler = new BrowserSchedules.Service(path, execute, pollMs=25,
                onFault=(fun details -> failure <- details))
            scheduler.Start()
            do! Task.Delay(150)
            Assert.That(failure, Is.Empty, failure)
        finally
            Directory.Delete(dir, true)
}

[<Test>]
let legacyMissingCommandArgsAreMigratedBeforeEquality () = task {
    let dir, path = temp()
    let execute (_: BrowserSchedules.ScheduledAction) = task { return Ok "test-only-stub" }
    try
        do
            use initial = new BrowserSchedules.Service(path, execute, pollMs=25)
            Assert.That((add initial "old-schema" 600).IsOk, Is.True)
        let state = JsonNode.Parse(File.ReadAllText(path))
        (state["actions"][0]).AsObject().Remove("CommandArgs") |> ignore
        File.WriteAllText(path, state.ToJsonString())
        use restored = new BrowserSchedules.Service(path, execute, pollMs=25)
        restored.Start()
        do! Task.Delay(100)
        Assert.That(status restored "old-schema", Is.EqualTo("pending"))
        let migrated = JsonNode.Parse(File.ReadAllText(path))
        Assert.That((migrated["actions"][0]["CommandArgs"]).ToJsonString(), Is.EqualTo("[]"))
    finally
        Directory.Delete(dir, true)
}

[<Test>]
let replaceAndCancel () = task {
    let dir,path = temp()
    let mutable clock = DateTimeOffset.UtcNow
    let mutable called = 0
    let action (_:BrowserSchedules.ScheduledAction) = task { called<-called+1; return Ok "id" }
    try
        use store = new BrowserSchedules.Service(path, action, now=(fun () -> clock), pollMs=25)
        Assert.That((add store "orchestrator" 600).IsOk, Is.True)
        Assert.That((add store "orchestrator" 1200).IsOk, Is.True)
        use listed = JsonDocument.Parse(store.List())
        Assert.That(listed.RootElement.GetArrayLength(), Is.EqualTo(1))
        Assert.That(store.Cancel("orchestrator").IsOk, Is.True)
        store.Start()
        clock <- clock.AddDays(1.)
        do! Task.Delay(100)
        Assert.That(called, Is.Zero)
    finally
        Directory.Delete(dir, true)
}

[<Test>]
let restartDoesNotReplayClaimedAction () = task {
    let dir,path = temp()
    let mutable called = 0
    let action (_:BrowserSchedules.ScheduledAction) = task { called<-called+1; return Ok "id" }
    try
        do
            use first = new BrowserSchedules.Service(path, action)
            Assert.That((add first "orchestrator" 600).IsOk, Is.True)
        let node = JsonNode.Parse(File.ReadAllText(path))
        node["actions"][0]["Status"] <- JsonValue.Create("running")
        File.WriteAllText(path, node.ToJsonString())
        use restored = new BrowserSchedules.Service(path, action, pollMs=25)
        Assert.That(status restored "orchestrator", Is.EqualTo("outcomeUnknown"))
        restored.Start()
        do! Task.Delay(100)
        Assert.That(called, Is.Zero)
    finally
        Directory.Delete(dir, true)
}

[<Test>]
let inputValidation () =
    let dir,path = temp()
    let action (_:BrowserSchedules.ScheduledAction) = task { return Ok "id" }
    try
        use store = new BrowserSchedules.Service(path, action)
        let invalid delay url args =
            store.Schedule("orchestrator", delay, url, "Scripts", "start.js", "", args, false, windowRegistration="agents").IsError
        Assert.That(invalid 0 "https://example.test/" "{}", Is.True)
        Assert.That(invalid 600 "file:///etc/passwd" "{}", Is.True)
        Assert.That(invalid 600 "https://user:pass@example.test/" "{}", Is.True)
        Assert.That(invalid 600 "https://example.test/" "bad-json", Is.True)
        Assert.That(store.Schedule("../escape",600,"https://example.test/","Scripts","start.js","","{}",false, windowRegistration="agents").IsError, Is.True)
    finally
        Directory.Delete(dir, true)


[<Test>]
let recurrenceSkipsMissedIntervalsOnRestart () = task {
    let dir,path = temp()
    let mutable clock = DateTimeOffset.UtcNow
    let mutable called = 0
    let action (_:BrowserSchedules.ScheduledAction) = task {
        called <- called + 1
        return Ok """{"registrationId":"agent-tab-01","status":"submitted","scriptResult":{"conversationUrl":"https://chatgpt.com/c/123"},"cleanup":"closed"}"""
    }
    try
        let first = new BrowserSchedules.Service(path, action, now=(fun () -> clock), pollMs=25)
        Assert.That(first.Schedule("hourly",600,"https://chatgpt.com/","Scripts","launch.js","",
            "{}" ,false,windowRegistration="agents",closeAfter=true,repeatEverySeconds=3600).IsOk, Is.True)
        first.Start()
        clock <- clock.AddSeconds(601.)
        do! awaitStatus (fun () -> called = 1 && status first "hourly" = "pending")
        Assert.That(first.List(), Does.Contain("https://chatgpt.com/c/123"))
        Assert.That(first.List(), Does.Contain("closed"))
        Assert.That(first.List(), Does.Contain("attemptId"))
        (first :> IDisposable).Dispose()
        clock <- clock.AddHours(5.)
        use restored = new BrowserSchedules.Service(path, action, now=(fun () -> clock), pollMs=25)
        restored.Start()
        do! Task.Delay(150)
        Assert.That(called, Is.EqualTo(1), "Missed occurrences must not pile up after restart")
        Assert.That(status restored "hourly", Is.EqualTo("pending"))
        use listed = JsonDocument.Parse(restored.List())
        Assert.That(listed.RootElement[0].GetProperty("dueAtUtc").GetDateTimeOffset(), Is.GreaterThan(clock))
        Assert.That(restored.Cancel("hourly").IsOk, Is.True)
    finally
        Directory.Delete(dir, true)
}

[<Test>]
let recurringUiIncompatibilitySuspendsUntilExplicitReconfiguration () = task {
    let dir,path = temp()
    let mutable clock = DateTimeOffset.UtcNow
    let mutable calls = 0
    let action (_:BrowserSchedules.ScheduledAction) = task {
        calls <- calls + 1
        return Error("created-tab-xyz", "UiIncompatible:modeSelection:ModeToggleUnavailable")
    }
    try
        use store = new BrowserSchedules.Service(path, action, now=(fun () -> clock), pollMs=25)
        Assert.That(store.Schedule("recurring", 600, "https://chatgpt.com/", "Scripts",
            "launch.js", "", "{}", false, windowRegistration="agents",
            repeatEverySeconds=3600).IsOk, Is.True)
        store.Start()
        clock <- clock.AddSeconds(601.)
        do! awaitStatus (fun () -> status store "recurring" = "suspended")
        Assert.That(store.List(), Does.Contain("UiIncompatible:modeSelection"))
        Assert.That(store.List(), Does.Contain("created-tab-xyz"))
        clock <- clock.AddDays(3.)
        do! Task.Delay(120)
        Assert.That(calls, Is.EqualTo(1), "A broken UI should never trigger repeated launches")
        Assert.That(store.Schedule("recurring", 600, "https://chatgpt.com/", "Scripts",
            "launch.js", "", "{}", false, windowRegistration="agents",
            repeatEverySeconds=3600).IsOk, Is.True, "Explicit reconfiguration re-enables schedule")
        Assert.That(status store "recurring", Is.EqualTo("pending"))
    finally
        Directory.Delete(dir, true)
}

let private commandSchedule id trigger : ScheduleExecutionCommand =
    { ScheduleId = id; ProjectName = "Scripts"; SourceProjectName = None
      TriggerKind = trigger
      RunAfterSeconds = Some 600; RunAtUtc = None
      RepeatEverySeconds = None; IdleAgentId = None; IdleSeconds = None
      ActionKind = "RunCommand"; Executable = Some "dotnet"
      Args = [ "--version"; "secret-argument" ]
      WorkingDirectory = None; TimeoutSeconds = Some 30; MaxOutputBytes = Some 256
      WindowRegistration = None; Url = None; ScriptPath = None
      ArgsJson = None; Alias = None; Active = None
      CloseTabAfterExecution = None }

let private valueOrFail = function
    | Ok value -> value
    | Error reason -> failwith reason

let private getDue (service: BrowserSchedules.Service) id =
    use json = JsonDocument.Parse(valueOrFail (service.Get(id)))
    json.RootElement.GetProperty("dueAtUtc").GetDateTimeOffset()

[<Test>]
let generalCliSchedulesAreDurableAndRedactArguments () = task {
    let dir,path = temp()
    let mutable clock = DateTimeOffset.UtcNow
    let seen = ResizeArray<string>()
    let executor (a: BrowserSchedules.ScheduledAction) = task {
        lock seen (fun () -> seen.Add(a.ActionKind))
        return Ok """{"exitCode":0,"timedOut":false}"""
    }
    try
        do
            use store = new BrowserSchedules.Service(path, executor, now=(fun () -> clock), pollMs=25)
            Assert.That(store.ScheduleExecution(commandSchedule "cli" "Once").IsOk, Is.True)
            Assert.That(store.List(), Does.Contain("RunCommand"))
            Assert.That(store.List(), Does.Not.Contain("secret-argument"))
            Assert.That(File.ReadAllText(path), Does.Contain("secret-argument"))
        clock <- clock.AddSeconds(601.)
        use restored = new BrowserSchedules.Service(path, executor, now=(fun () -> clock), pollMs=25)
        restored.Start()
        do! awaitStatus (fun () -> status restored "cli" = "completed")
        Assert.That(seen.Count, Is.EqualTo(1))
        Assert.That(restored.List(), Does.Contain("exitCode"))
        Assert.That(restored.List(), Does.Not.Contain("secret-argument"))
    finally
        Directory.Delete(dir, true)
}

[<Test>]
let idleManagerResetsFromCrossClientActivityAndWaitsForFreshActivityToRearm () = task {
    let dir,path = temp()
    let mutable clock = DateTimeOffset.UtcNow
    let mutable lastSeen: DateTimeOffset option = None
    let mutable calls = 0
    let observed project agent =
        if project = "Scripts" && agent = "Manager" then lastSeen else None
    let executor (_: BrowserSchedules.ScheduledAction) = task {
        System.Threading.Interlocked.Increment(&calls) |> ignore
        return Ok "finished"
    }
    try
        use store = new BrowserSchedules.Service(path, executor, now=(fun () -> clock), pollMs=25,
                                                  agentLastSeen=observed,
                                                  verifyIdleActivity=(fun p a -> Task.FromResult(Ok(observed p a))))
        let config =
            { commandSchedule "manager" "AfterAgentIdle" with
                RunAfterSeconds = None; IdleAgentId = Some "Manager"; IdleSeconds = Some 900 }
        Assert.That(store.ScheduleExecution(config).IsOk, Is.True)
        store.Start()
        clock <- clock.AddSeconds(850.)
        lastSeen <- Some clock // Activity from another connected Jarvis device.
        do! awaitStatus (fun () -> getDue store "manager" > clock.AddSeconds(899.))
        clock <- clock.AddSeconds(70.)
        do! Task.Delay(100)
        Assert.That(calls, Is.Zero, "Remote activity resets the idle deadline")
        clock <- clock.AddSeconds(831.)
        do! awaitStatus (fun () -> status store "manager" = "waitingActivity")
        Assert.That(calls, Is.EqualTo(1))
        clock <- clock.AddMinutes(20.)
        do! Task.Delay(100)
        Assert.That(calls, Is.EqualTo(1),
                    "A finished Manager does not self-recurse before the 30-minute silent cutoff.")
        lastSeen <- Some clock
        do! awaitStatus (fun () -> status store "manager" = "pending")
        Assert.That(getDue store "manager", Is.GreaterThan(clock))
        clock <- clock.AddSeconds(901.)
        do! awaitStatus (fun () -> calls = 2 && status store "manager" = "waitingActivity")
    finally
        Directory.Delete(dir, true)
}

[<Test>]
let recurringCliSkipsMissedOccurrencesOnRestart () = task {
    let dir,path = temp()
    let mutable clock = DateTimeOffset.UtcNow
    let mutable calls = 0
    let executor (_: BrowserSchedules.ScheduledAction) = task {
        System.Threading.Interlocked.Increment(&calls) |> ignore
        return Ok "done"
    }
    try
        let config =
            { commandSchedule "hourly-cli" "Every" with
                RunAfterSeconds = Some 600; RepeatEverySeconds = Some 3600 }
        do
            use first = new BrowserSchedules.Service(path, executor, now=(fun () -> clock), pollMs=25)
            Assert.That(first.ScheduleExecution(config).IsOk, Is.True)
        clock <- clock.AddDays(5.)
        use restarted = new BrowserSchedules.Service(path, executor, now=(fun () -> clock), pollMs=25)
        restarted.Start()
        do! Task.Delay(150)
        Assert.That(calls, Is.Zero, "Missed recurring executions must not replay")
        Assert.That(getDue restarted "hourly-cli", Is.GreaterThan(clock))
        clock <- (getDue restarted "hourly-cli").AddSeconds(1.)
        do! awaitStatus (fun () -> calls = 1 && status restarted "hourly-cli" = "pending")
        Assert.That(getDue restarted "hourly-cli", Is.GreaterThan(clock))
    finally
        Directory.Delete(dir, true)
}

[<Test>]
let newSchedulerValidationAndReadOnlyInspection () =
    let dir,path = temp()
    let executor (_: BrowserSchedules.ScheduledAction) = task { return Ok "" }
    try
        use store = new BrowserSchedules.Service(path, executor)
        let basic = commandSchedule "manager" "Once"
        Assert.That(store.ScheduleExecution({ basic with TriggerKind = "Every"; RepeatEverySeconds = Some 30 }).IsError, Is.True)
        Assert.That(store.ScheduleExecution({ basic with TriggerKind = "AfterAgentIdle"; RunAfterSeconds = None; IdleSeconds = Some 900 }).IsError, Is.True)
        Assert.That(store.ScheduleExecution({ basic with TriggerKind = "Unknown" }).IsError, Is.True)
        Assert.That(store.ScheduleExecution({ basic with ActionKind = "BrowserScript" }).IsError, Is.True)
        Assert.That(store.ScheduleExecution({ basic with RunAtUtc = Some DateTimeOffset.UtcNow }).IsError, Is.True)
        Assert.That(store.ScheduleExecution({ basic with ScheduleId = "../invalid" }).IsError, Is.True)
        Assert.That(store.Get("missing").IsError, Is.True)
        Assert.That(store.ScheduleExecution(basic).IsOk, Is.True)
        Assert.That((valueOrFail (store.Get("manager"))), Does.Contain("RunCommand"))
        Assert.That((valueOrFail (store.Get("manager"))), Does.Not.Contain("secret-argument"))
        Assert.That(store.Cancel("manager").IsOk, Is.True)
        Assert.That(status store "manager", Is.EqualTo("canceled"))
    finally
        Directory.Delete(dir, true)

[<Test>]
let activityPredicateDoesNotCountReadOnlyAgentObservationOrJobPolling () =
    Assert.That(AgentCommandInfo.isAgentActivity (RunCommandCommand
        { ProjectName = "Scripts"; Executable = "dotnet"; Args = []
          Reason = None; WorkingDirectory = None; TimeoutSeconds = None
          MaxOutputBytes = None }), Is.True)
    Assert.That(AgentCommandInfo.isAgentActivity (ObserveAgentCommand
        { ProjectName = "Scripts"; ObserverAgentId = "Manager"; TargetAgentId = "Worker 1" }), Is.False)
    Assert.That(AgentCommandInfo.isAgentActivity (GetJobResultCommand
        { ProjectName = "Scripts"; JobId = "id"; AfterSequence = None }), Is.False)

[<Test>]
let explicitUtcAndBrowserActionsUseSameScheduleStore () =
    let dir,path = temp()
    let executor (_: BrowserSchedules.ScheduledAction) = task { return Ok "done" }
    try
        use store = new BrowserSchedules.Service(path, executor)
        let at = DateTimeOffset.UtcNow.AddHours(1.)
        let cli = { commandSchedule "at-utc" "Once" with
                        RunAfterSeconds = None; RunAtUtc = Some at }
        Assert.That(store.ScheduleExecution(cli).IsOk, Is.True)
        Assert.That(getDue store "at-utc", Is.EqualTo(at))
        let browser =
            { commandSchedule "browser-1" "Once" with
                ActionKind = "BrowserScript"; Executable = None; Args = []
                WindowRegistration = Some "agents"; Url = Some "https://example.test"
                ScriptPath = Some "launch.js"; ArgsJson = Some "{}" }
        Assert.That(store.ScheduleExecution(browser).IsOk, Is.True)
        Assert.That(store.List(), Does.Contain("BrowserScript"))
        Assert.That(store.List(), Does.Contain("RunCommand"))
        Assert.That(store.Get("browser-1").IsOk, Is.True)
    finally
        Directory.Delete(dir, true)

[<Test>]
let staleManagerAttemptRequiresThirtyMinutesAndRejectsLateResult () = task {
    let dir,path = temp()
    let mutable clock = DateTimeOffset.UtcNow
    let verifiedLastSeen = clock
    let firstGate = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let mutable calls = 0
    let executor (_: BrowserSchedules.ScheduledAction) = task {
        let index = System.Threading.Interlocked.Increment(&calls)
        if index = 1 then do! firstGate.Task
        return Ok("attempt " + string index)
    }
    try
        use store = new BrowserSchedules.Service(path, executor, now=(fun () -> clock), pollMs=25,
                                                 verifyIdleActivity=(fun _ _ -> Task.FromResult(Ok(Some verifiedLastSeen))))
        let config =
            { commandSchedule "manager-stale" "AfterAgentIdle" with
                RunAfterSeconds = None; IdleAgentId = Some "Manager"; IdleSeconds = Some 60 }
        Assert.That(store.ScheduleExecution(config).IsOk, Is.True)
        store.Start()
        clock <- clock.AddSeconds(61.)
        do! awaitStatus (fun () -> calls = 1 && status store "manager-stale" = "running")
        clock <- clock.AddMinutes(29.)
        do! Task.Delay(100)
        Assert.That(calls, Is.EqualTo(1), "One running Manager before the stale cutoff")
        clock <- clock.AddMinutes(2.)
        do! awaitStatus (fun () -> calls = 2 && status store "manager-stale" = "waitingActivity")
        firstGate.TrySetResult(()) |> ignore
        do! Task.Delay(100)
        Assert.That(calls, Is.EqualTo(2))
        Assert.That(status store "manager-stale", Is.EqualTo("waitingActivity"),
                    "A late first-attempt result cannot overwrite the replacement")
    finally
        firstGate.TrySetResult(()) |> ignore
        Directory.Delete(dir, true)
}

[<Test>]
let independentClientsCannotBothOwnOrDispatchSameOccurrence () = task {
    let dir,path = temp()
    let mutable clock = DateTimeOffset.UtcNow
    let mutable executions = 0
    let executor (_: BrowserSchedules.ScheduledAction) = task {
        System.Threading.Interlocked.Increment(&executions) |> ignore
        return Ok "success"
    }
    try
        do! task {
            use owner = new BrowserSchedules.Service(path, executor, now=(fun () -> clock), pollMs=25)
            Assert.That(owner.ScheduleExecution(commandSchedule "once" "Once").IsOk, Is.True)
            Assert.Throws<IOException>(fun () ->
                use competitor = new BrowserSchedules.Service(path, executor, now=(fun () -> clock), pollMs=25)
                competitor.Start()) |> ignore
            // The second client cannot read/cas/overwrite a pending occurrence.
            owner.Start()
            clock <- clock.AddSeconds(601.)
            do! awaitStatus (fun () -> status owner "once" = "completed")
            Assert.That(executions, Is.EqualTo(1))
        }
        use afterOwnerStops = new BrowserSchedules.Service(path, executor, now=(fun () -> clock), pollMs=25)
        Assert.That(status afterOwnerStops "once", Is.EqualTo("completed"))
        afterOwnerStops.Start()
        do! Task.Delay(100)
        Assert.That(executions, Is.EqualTo(1), "No duplicate side effect on clean handoff.")
    finally
        Directory.Delete(dir, true)
}

[<Test>]
let idleLaunchFailsClosedWhenDisconnectedAndReconcilesMissedOtherDeviceActivity () = task {
    let dir,path = temp()
    let mutable clock = DateTimeOffset.UtcNow
    let createdAt = clock
    let mutable remoteActivity: DateTimeOffset option = None
    let mutable connected = true
    let mutable launches = 0
    let verify (project: string) (agent: string) = task {
        Assert.That(project, Is.EqualTo("Scripts"))
        Assert.That(agent, Is.EqualTo("Manager"))
        if connected then return Ok remoteActivity
        else return Error "Offline: cannot prove cross-device idle"
    }
    let execute (_: BrowserSchedules.ScheduledAction) = task {
        System.Threading.Interlocked.Increment(&launches) |> ignore
        return Ok "Manager queued"
    }
    try
        use store = new BrowserSchedules.Service(path, execute, now=(fun () -> clock), pollMs=25,
                                                 agentLastSeen=(fun _ _ -> None),
                                                 verifyIdleActivity=verify)
        let config =
            { commandSchedule "manager-remote" "AfterAgentIdle" with
                RunAfterSeconds = None; IdleAgentId = Some "Manager"; IdleSeconds = Some 900 }
        Assert.That(store.ScheduleExecution(config).IsOk, Is.True)
        store.Start()
        // The remote Manager is active. The local TUI never receives any hint.
        remoteActivity <- Some (createdAt.AddSeconds(800.))
        clock <- createdAt.AddSeconds(910.)
        do! awaitStatus (fun () -> getDue store "manager-remote" > createdAt.AddSeconds(1690.))
        Assert.That(launches, Is.Zero)
        // Another missed hint arrives while offline, when the old deadline expires.
        connected <- false
        remoteActivity <- Some (createdAt.AddSeconds(1600.))
        clock <- createdAt.AddSeconds(1710.)
        do! Task.Delay(150)
        Assert.That(launches, Is.Zero, "Disconnected Manager clock must not launch on stale data.")
        Assert.That(status store "manager-remote", Is.EqualTo("pending"))
        // Reconnection pulls a fresh server watermark and postpones launch again.
        connected <- true
        do! awaitStatus (fun () -> getDue store "manager-remote" > createdAt.AddSeconds(2490.))
        Assert.That(launches, Is.Zero)
        clock <- createdAt.AddSeconds(2510.)
        do! awaitStatus (fun () -> launches = 1 && status store "manager-remote" = "waitingActivity")
        // No local hint; server-only attributed activity re-arms the next episode.
        remoteActivity <- Some (createdAt.AddSeconds(2520.))
        do! awaitStatus (fun () -> status store "manager-remote" = "pending")
        Assert.That(getDue store "manager-remote", Is.GreaterThan(clock))
    finally
        Directory.Delete(dir, true)
}

[<Test>]
let idleScheduleWithoutVerifiedProviderNeverDispatchesManager () = task {
    let dir,path = temp()
    let mutable clock = DateTimeOffset.UtcNow
    let mutable calls = 0
    let executor (_: BrowserSchedules.ScheduledAction) = task {
        System.Threading.Interlocked.Increment(&calls) |> ignore
        return Ok "unexpected"
    }
    try
        use store = new BrowserSchedules.Service(path, executor, now=(fun () -> clock), pollMs=25)
        let cmd =
            { commandSchedule "unverified-manager" "AfterAgentIdle" with
                RunAfterSeconds = None; IdleAgentId = Some "Manager"; IdleSeconds = Some 60 }
        Assert.That(store.ScheduleExecution(cmd).IsOk, Is.True)
        store.Start()
        clock <- clock.AddSeconds(61.)
        do! Task.Delay(200)
        Assert.That(calls, Is.Zero)
        Assert.That(store.List(), Does.Contain("ActivityVerificationUnavailable"))
    finally
        Directory.Delete(dir, true)
}

[<Test>]
let replacingPendingIdleScheduleCannotReuseOlderAgentsVerifiedSnapshot () = task {
    let dir,path = temp()
    let mutable clock = DateTimeOffset.UtcNow
    let verifierStarted = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let releaseOld = TaskCompletionSource<Result<DateTimeOffset option,string>>(TaskCreationOptions.RunContinuationsAsynchronously)
    let mutable launches = 0
    let mutable newVerifications = 0
    let verify _ agent = task {
        if agent = "Manager A" then
            verifierStarted.TrySetResult(()) |> ignore
            return! releaseOld.Task
        else
            System.Threading.Interlocked.Increment(&newVerifications) |> ignore
            return Ok (Some clock)
    }
    let execute (_:BrowserSchedules.ScheduledAction) = task {
        System.Threading.Interlocked.Increment(&launches) |> ignore
        return Ok "submitted"
    }
    let config agent =
        { commandSchedule "slot" "AfterAgentIdle" with
            RunAfterSeconds = None; IdleAgentId = Some agent; IdleSeconds = Some 60 }
    try
        use store = new BrowserSchedules.Service(path, execute, now=(fun () -> clock),
                                                 pollMs=25, verifyIdleActivity=verify)
        Assert.That(store.ScheduleExecution(config "Manager A").IsOk, Is.True)
        store.Start()
        clock <- clock.AddSeconds(61.)
        do! verifierStarted.Task.WaitAsync(TimeSpan.FromSeconds(5.))
        Assert.That(store.ScheduleExecution(config "Manager B").IsOk, Is.True)
        clock <- clock.AddSeconds(61.)
        releaseOld.TrySetResult(Ok None) |> ignore
        do! awaitStatus (fun () -> newVerifications > 0)
        do! Task.Delay(100)
        Assert.That(launches, Is.Zero,
                    "A's verified snapshot must never authorize a replacement B launch.")
        Assert.That(getDue store "slot", Is.GreaterThan(clock),
                    "B must be verified independently and postponed by its current remote activity.")
    finally
        releaseOld.TrySetResult(Ok None) |> ignore
        Directory.Delete(dir, true)
}

[<Test>]
let replacingWaitingIdleScheduleCannotReuseOlderAgentsVerifiedSnapshot () = task {
    let dir,path = temp()
    let mutable clock = DateTimeOffset.UtcNow
    let verifiedLastSeen = clock
    let waitingVerificationStarted = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let releaseOld = TaskCompletionSource<Result<DateTimeOffset option,string>>(TaskCreationOptions.RunContinuationsAsynchronously)
    let mutable oldVerifications = 0
    let mutable newVerifications = 0
    let mutable launches = 0
    let verify _ agent = task {
        if agent = "Manager A" then
            let count = System.Threading.Interlocked.Increment(&oldVerifications)
            if count <= 2 then return Ok (Some verifiedLastSeen)
            else
                waitingVerificationStarted.TrySetResult(()) |> ignore
                return! releaseOld.Task
        else
            System.Threading.Interlocked.Increment(&newVerifications) |> ignore
            return Ok (Some clock)
    }
    let execute (_:BrowserSchedules.ScheduledAction) = task {
        System.Threading.Interlocked.Increment(&launches) |> ignore
        return Ok "submitted"
    }
    let config agent =
        { commandSchedule "slot" "AfterAgentIdle" with
            RunAfterSeconds = None; IdleAgentId = Some agent; IdleSeconds = Some 60 }
    try
        use store = new BrowserSchedules.Service(path, execute, now=(fun () -> clock),
                                                 pollMs=25, verifyIdleActivity=verify)
        Assert.That(store.ScheduleExecution(config "Manager A").IsOk, Is.True)
        store.Start()
        clock <- clock.AddSeconds(61.)
        do! awaitStatus (fun () -> launches = 1 && status store "slot" = "waitingActivity")
        do! waitingVerificationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5.))
        Assert.That(store.ScheduleExecution(config "Manager B").IsOk, Is.True)
        clock <- clock.AddSeconds(61.)
        releaseOld.TrySetResult(Ok None) |> ignore
        do! awaitStatus (fun () -> newVerifications > 0)
        do! Task.Delay(100)
        Assert.That(launches, Is.EqualTo(1),
                    "Replacing a waiting schedule must not inherit its previous Manager's verification.")
        Assert.That(getDue store "slot", Is.GreaterThan(clock))
    finally
        releaseOld.TrySetResult(Ok None) |> ignore
        Directory.Delete(dir, true)
}

[<Test>]
let submittedManagerWithoutFirstToolCallRecoversAfterThirtyMinutesAcrossRestart () = task {
    let dir,path = temp()
    let mutable clock = DateTimeOffset.UtcNow
    let verifiedLastSeen = clock
    let mutable launches = 0
    let execute (_:BrowserSchedules.ScheduledAction) = task {
        System.Threading.Interlocked.Increment(&launches) |> ignore
        return Ok "browser launch submitted, no activity observed"
    }
    try
        let firstRun () = task {
            use first = new BrowserSchedules.Service(path, execute, now=(fun () -> clock),
                                                     pollMs=25, verifyIdleActivity=(fun _ _ -> Task.FromResult(Ok(Some verifiedLastSeen))))
            let cmd =
                { commandSchedule "manager-silent" "AfterAgentIdle" with
                    RunAfterSeconds = None; IdleAgentId = Some "Manager"; IdleSeconds = Some 60 }
            Assert.That(first.ScheduleExecution(cmd).IsOk, Is.True)
            first.Start()
            clock <- clock.AddSeconds(61.)
            do! awaitStatus (fun () -> launches = 1 && status first "manager-silent" = "waitingActivity")
        }
        do! firstRun()
        clock <- clock.AddMinutes(29.)
        use restored = new BrowserSchedules.Service(path, execute, now=(fun () -> clock),
                                                     pollMs=25, verifyIdleActivity=(fun _ _ -> Task.FromResult(Ok(Some verifiedLastSeen))))
        restored.Start()
        do! Task.Delay(150)
        Assert.That(launches, Is.EqualTo(1), "No replacement before the silent-launch stale cutoff.")
        clock <- clock.AddMinutes(2.)
        do! awaitStatus (fun () -> launches = 2 && status restored "manager-silent" = "waitingActivity")
        clock <- clock.AddMinutes(1.)
        do! Task.Delay(150)
        Assert.That(launches, Is.EqualTo(2), "Recovery is bounded by the next 30-minute stale cutoff.")
    finally
        Directory.Delete(dir, true)
}

[<Test>]
let missingCrossDeviceActivityProofBlocksBothClientsAndRestartedIdleSchedules () = task {
    let dir,path = temp()
    let otherPath = Path.Combine(dir, "other-client-schedules.json")
    let mutable clock = DateTimeOffset.UtcNow
    let original = clock
    let mutable launches = 0
    let mutable verified: DateTimeOffset option = Some original
    let execute (_: BrowserSchedules.ScheduledAction) = task {
        System.Threading.Interlocked.Increment(&launches) |> ignore
        return Ok "UNSAFE idle launch"
    }
    let verify _ _ = Task.FromResult(Ok verified)
    let create path =
        new BrowserSchedules.Service(path, execute, now=(fun () -> clock), pollMs=25,
            agentLastSeen=(fun _ _ -> Some original), verifyIdleActivity=verify)
    let cmd =
        { commandSchedule "manager-no-proof" "AfterAgentIdle" with
            RunAfterSeconds = None; IdleAgentId = Some "Manager"; IdleSeconds = Some 60 }
    try
        let firstSession () = task {
            use clientA = create path
            use clientB = create otherPath
            Assert.That(clientA.ScheduleExecution(cmd).IsOk, Is.True)
            Assert.That(clientB.ScheduleExecution(cmd).IsOk, Is.True)
            clientA.Start()
            clientB.Start()

            // The server loses its DB or misses the last activity sample.
            // Local hints and an elapsed due timer are not enough to launch.
            verified <- None
            clock <- original.AddSeconds(120.)
            do! Task.Delay(200)
            Assert.That(launches, Is.Zero)
            Assert.That(clientA.List(), Does.Contain("ActivityProofUnavailable"))
            Assert.That(clientB.List(), Does.Contain("ActivityProofUnavailable"))
        }
        do! firstSession()

        // Both independent client schedule files outlive the server's state.
        // Restart with an empty activity source; a stale due date still cannot
        // become an authorization to execute.
        use restartedA = create path
        use restartedB = create otherPath
        restartedA.Start()
        restartedB.Start()
        clock <- clock.AddHours(2.)
        do! Task.Delay(200)
        Assert.That(launches, Is.Zero)
        Assert.That(restartedA.List(), Does.Contain("ActivityProofUnavailable"))
        Assert.That(restartedB.List(), Does.Contain("ActivityProofUnavailable"))
    finally
        Directory.Delete(dir, true)
}

[<Test>]
let distinctScheduleOwnerAndCanonicalScriptSourceAcrossBothApis () =
    let dir, path = temp()
    let execute (_: BrowserSchedules.ScheduledAction) = task { return Ok "unused" }
    try
        use store = new BrowserSchedules.Service(path, execute)
        let script = "organon/browser-scripts/chatgpt/launchAgent.js"
        let direct =
            store.Schedule("favn-manager", 600, "https://chatgpt.com/", "Favn",
                script, "", """{"privatePrompt":"never-list"}""", false,
                windowRegistration="agents", repeatEverySeconds=600,
                sourceProjectName="Servitor")
        Assert.That(direct.IsOk, Is.True)
        let general =
            { commandSchedule "jarvis-health" "Every" with
                ProjectName = "Jarvis"; SourceProjectName = Some "Servitor"
                ActionKind = "BrowserScript"; Executable = None; Args = []
                RunAfterSeconds = Some 1800; RepeatEverySeconds = Some 1800
                WindowRegistration = Some "agents"; Url = Some "https://chatgpt.com/"
                ScriptPath = Some script; ArgsJson = Some """{"privatePrompt":"also-private"}""" }
        Assert.That(store.ScheduleExecution(general).IsOk, Is.True)
        Assert.That(store.ScheduleExecution(
            { commandSchedule "illegal-cli" "Once" with SourceProjectName = Some "Servitor" }).IsError,
            Is.True, "RunCommand remains strictly owner-scoped")
        use listed = JsonDocument.Parse(store.List())
        let find id =
            listed.RootElement.EnumerateArray()
            |> Seq.find (fun a -> a.GetProperty("id").GetString() = id)
        let favn = find "favn-manager"
        let jarvis = find "jarvis-health"
        Assert.That(favn.GetProperty("projectName").GetString(), Is.EqualTo("Favn"))
        Assert.That(jarvis.GetProperty("projectName").GetString(), Is.EqualTo("Jarvis"))
        Assert.That(favn.GetProperty("sourceProjectName").GetString(), Is.EqualTo("Servitor"))
        Assert.That(jarvis.GetProperty("sourceProjectName").GetString(), Is.EqualTo("Servitor"))
        Assert.That(favn.GetProperty("scriptPath").GetString(), Is.EqualTo(script))
        Assert.That(store.List(), Does.Not.Contain("privatePrompt"))
        Assert.That(File.ReadAllText(path), Does.Contain("privatePrompt"),
            "Private arguments remain in the protected local schedule state only")
    finally
        Directory.Delete(dir, true)

[<Test>]
let legacySameProjectSourceMigratesWithoutGuessingOwner () =
    let dir, path = temp()
    let execute (_: BrowserSchedules.ScheduledAction) = task { return Ok "unused" }
    try
        do
            use first = new BrowserSchedules.Service(path, execute)
            Assert.That((add first "old-identity" 600).IsOk, Is.True)
        let legacy = JsonNode.Parse(File.ReadAllText(path))
        legacy["schemaVersion"] <- JsonValue.Create(3)
        (legacy["actions"][0]).AsObject().Remove("SourceProjectName") |> ignore
        File.WriteAllText(path, legacy.ToJsonString())
        use restored = new BrowserSchedules.Service(path, execute)
        use listed = JsonDocument.Parse(restored.List())
        let entry = listed.RootElement.EnumerateArray() |> Seq.head
        Assert.That(entry.GetProperty("projectName").GetString(), Is.EqualTo("Scripts"))
        Assert.That(entry.GetProperty("sourceProjectName").GetString(), Is.EqualTo("Scripts"))
        Assert.That(entry.GetProperty("status").GetString(), Is.EqualTo("pending"))
        let persisted = JsonNode.Parse(File.ReadAllText(path))
        Assert.That(persisted["schemaVersion"].GetValue<int>(), Is.EqualTo(4))
        Assert.That(((persisted["actions"][0])["SourceProjectName"]).GetValue<string>(),
                    Is.EqualTo("Scripts"))
    finally
        Directory.Delete(dir, true)

[<Test>]
let ambiguousV4SourceIsSuspendedAndNeverLaunched () = task {
    let dir, path = temp()
    let mutable called = 0
    let mutable clock = DateTimeOffset.UtcNow
    let execute (_: BrowserSchedules.ScheduledAction) = task {
        called <- called + 1
        return Ok "unexpected"
    }
    try
        do
            use first = new BrowserSchedules.Service(path, execute, now=(fun () -> clock))
            Assert.That((add first "missing-source" 600).IsOk, Is.True)
        let state = JsonNode.Parse(File.ReadAllText(path))
        (state["actions"][0]).AsObject().Remove("SourceProjectName") |> ignore
        File.WriteAllText(path, state.ToJsonString())
        use restored = new BrowserSchedules.Service(path, execute, now=(fun () -> clock), pollMs=25)
        Assert.That(status restored "missing-source", Is.EqualTo("suspended"))
        clock <- clock.AddDays(1.)
        restored.Start()
        do! Task.Delay(100)
        Assert.That(called, Is.Zero)
        Assert.That(restored.List(), Does.Contain("InvalidScheduleProjectIdentity"))
    finally
        Directory.Delete(dir, true)
}

[<Test>]
let idempotentGuardedOwnerRebindPreservesPrivateStateAndClaimIdentity () =
    let dir, path = temp()
    let execute (_: BrowserSchedules.ScheduledAction) = task { return Ok "unused" }
    try
        use store = new BrowserSchedules.Service(path, execute)
        Assert.That(store.Schedule("favn-manager", 600, "https://chatgpt.com/",
            "Servitor", "organon/browser-scripts/chatgpt/launchAgent.js", "",
            """{"prompt":"private"}""", false, windowRegistration="agents",
            repeatEverySeconds=600).IsOk, Is.True)
        let before = ((JsonNode.Parse(File.ReadAllText(path)))["actions"])[0]
        let revision = (before["RevisionId"]).GetValue<string>()
        Assert.That(store.RebindProject("favn-manager","Servitor","wrong","Favn",revision).IsError, Is.True)
        Assert.That(store.RebindProject("favn-manager","Servitor","Servitor","Favn","stale").IsError, Is.True)
        Assert.That(store.RebindProject("favn-manager","Servitor","Servitor","Favn",revision).IsOk, Is.True)
        let firstRebind = File.ReadAllText(path)
        Assert.That(store.RebindProject("favn-manager","Servitor","Servitor","Favn",revision).IsOk, Is.True)
        Assert.That(File.ReadAllText(path), Is.EqualTo(firstRebind),
            "An idempotent retry must not mutate state or reschedule an occurrence")
        let after = ((JsonNode.Parse(firstRebind))["actions"])[0]
        Assert.That((after["ProjectName"]).GetValue<string>(), Is.EqualTo("Favn"))
        Assert.That((after["SourceProjectName"]).GetValue<string>(), Is.EqualTo("Servitor"))
        for field in ["RevisionId";"AttemptId";"ArgsJson";"WindowRegistration";
                      "DueAtUtc";"Status";"UpdatedAtUtc";"RepeatEverySeconds";
                      "RegistrationId";"LastResult";"Cleanup";"Error"] do
            Assert.That((after[field]).ToJsonString(), Is.EqualTo((before[field]).ToJsonString()), field)
    finally
        Directory.Delete(dir, true)

[<Test>]
let scheduleSourcePermissionRequiresTwoIndependentProjectApprovals () = task {
    let command =
        ScheduleExecutionCommand
            { commandSchedule "guarded" "Every" with
                ProjectName = "Favn"; SourceProjectName = Some "Servitor"
                ActionKind = "BrowserScript"; Executable = None; Args = []
                RepeatEverySeconds = Some 600
                WindowRegistration = Some "agents"; Url = Some "https://chatgpt.com/"
                ScriptPath = Some "organon/browser-scripts/chatgpt/launchAgent.js"
                ArgsJson = Some "{}" }
    let source = PermissionPolicy.scheduleSourceAuthorizationCommand command
    Assert.That(source.IsSome, Is.True)
    match source.Value with
    | BrowserExecuteFileCommand file ->
        Assert.That(file.ProjectName, Is.EqualTo("Servitor"))
    | _ -> Assert.Fail("Cross-project source must be separately authorized")
    let promptOnly allowed (_: AgentCommand) (request: ConfirmationRequest) =
        task {
            return if request.ProjectName = Some allowed then
                       Client.PermissionApproval.AllowOnce
                   else Client.PermissionApproval.Deny
        }
    let! ownerAllowed = PermissionPolicy.authorizeWithTrust NoTrust (promptOnly "Favn") command
    let! sourceDenied = PermissionPolicy.authorizeWithTrust NoTrust (promptOnly "Favn") source.Value
    Assert.That(ownerAllowed.IsOk, Is.True)
    Assert.That(sourceDenied.IsError, Is.True, "Owner authority cannot grant source authority")
    let! ownerDenied = PermissionPolicy.authorizeWithTrust NoTrust (promptOnly "Servitor") command
    let! sourceAllowed = PermissionPolicy.authorizeWithTrust NoTrust (promptOnly "Servitor") source.Value
    Assert.That(ownerDenied.IsError, Is.True, "Source authority cannot grant owner authority")
    Assert.That(sourceAllowed.IsOk, Is.True)
    let sameProject =
        PermissionPolicy.scheduleSourceAuthorizationCommand
            (ScheduleExecutionCommand
                { commandSchedule "same-project" "Once" with SourceProjectName = None })
    Assert.That(sameProject.IsNone, Is.True)
}
