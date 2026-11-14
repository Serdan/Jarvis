module BrowserSchedulesTests

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
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
    { ScheduleId = id; ProjectName = "Scripts"; TriggerKind = trigger
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
                                                  agentLastSeen=observed)
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
        clock <- clock.AddHours(3.)
        do! Task.Delay(100)
        Assert.That(calls, Is.EqualTo(1), "A finished Manager never self-recurses")
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
    let firstGate = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let mutable calls = 0
    let executor (_: BrowserSchedules.ScheduledAction) = task {
        let index = System.Threading.Interlocked.Increment(&calls)
        if index = 1 then do! firstGate.Task
        return Ok("attempt " + string index)
    }
    try
        use store = new BrowserSchedules.Service(path, executor, now=(fun () -> clock), pollMs=25)
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
