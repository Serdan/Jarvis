module Client.Tests.WorkingTurnGuidanceTests

open System
open Client.WorkingTurnGuidance
open Client.ReviewWorkflow
open NUnit.Framework

let private started = DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero)

let private agent state since =
    let assignment : AgentAssignment = { TaskId = "task"; AttemptId = "attempt" }
    { AgentId = "Agent 1"; Version = 3
      CurrentAssignment = if List.contains state [ "Parked"; "Completed"; "NeedsWork" ] then
                              Unchecked.defaultof<AgentAssignment> else assignment
      Execution = if state = "Working" then "Working" else "Stopped"
      UnassignedDisposition = if state = "Parked" then "Parked" elif state = "Completed" || state = "NeedsWork" then "NeedsWork" else null
      StateReason = ""; StateSequence = 2L
      StateEnteredAtUtc = since; WorkingTurnStartedAtUtc = since
      ActiveSessionId = null; AssociatedSessionIds = [] }

[<Test>]
let workingResponseGuidanceStartsAt25AndEscalatesAt30 () =
    let worker = agent "Working" (started.ToString("O"))
    Assert.That(instructionAt (started.AddMinutes(24.99)) worker, Is.EqualTo(None))
    Assert.That(instructionAt (started.AddMinutes(25.0)) worker |> Option.get,
                Does.Contain("SOFT STOP"))
    Assert.That(instructionAt (started.AddMinutes(29.99)) worker |> Option.get,
                Does.Contain("SOFT STOP"))
    let hard = instructionAt (started.AddMinutes(30.0)) worker |> Option.get
    Assert.That(hard, Does.Contain("HARD STOP"))
    Assert.That(hard, Does.Contain("persist"))
    Assert.That(hard, Does.Contain("yield"))

[<Test>]
let nonworkingStatesDoNotProduceTimingInstructions () =
    for state in [ "Assigned"; "Ready"; "Completed"; "Parked" ] do
        Assert.That(instructionAt (started.AddHours(3)) (agent state (started.ToString("O"))),
                    Is.EqualTo(None), state)
    Assert.That(instructionAt (started.AddHours(3)) (agent "Working" null), Is.EqualTo(None))
    Assert.That(instructionAt (started.AddMinutes(-1)) (agent "Working" (started.ToString("O"))),
                Is.EqualTo(None))


[<Test>]
let currentAndSupersededSessionsReceiveDifferentInstructions () =
    let worker =
        { agent "Working" (started.ToString("O")) with
            ActiveSessionId = "new-session"
            AssociatedSessionIds = [ "old-session"; "new-session" ] }
    let now = started.AddMinutes(26)
    let current = forSession now "new-session" worker |> Option.get
    Assert.That(fst current, Is.EqualTo("working-turn"))
    Assert.That(snd current, Does.Contain("SOFT STOP"))
    let older = forSession now "old-session" worker |> Option.get
    Assert.That(fst older, Is.EqualTo("session-status"))
    Assert.That(snd older, Does.Contain("new-session"))
    Assert.That(snd older, Does.Contain("reread"))
    Assert.That(forSession now "unrelated-session" worker, Is.EqualTo(None))
    Assert.That(forSession now "new-session" { worker with Execution = "Stopped" }, Is.EqualTo(None))

[<Test>]
let recoveredSessionUsesNewCutoffRatherThanStateEntry () =
    let worker =
        { agent "Working" (started.ToString("O")) with
            ActiveSessionId = "recovered-session"
            AssociatedSessionIds = [ "previous-session"; "recovered-session" ]
            WorkingTurnStartedAtUtc = started.AddMinutes(29).ToString("O") }
    Assert.That(forSession (started.AddMinutes(30)) "recovered-session" worker, Is.EqualTo(None))
    let guidance = forSession (started.AddMinutes(54)) "recovered-session" worker |> Option.get
    Assert.That(snd guidance, Does.Contain("SOFT STOP"))


[<Test>]
let agentStateCacheAvoidsRepeatedFetchAndInvalidatesOnTaskTransition () =
    let cache = AgentCache(TimeSpan.FromMinutes 2.)
    let now = DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero)
    let project =
        Client.ProjectPaths.ProjectTarget.parse "Wayfold"
        |> function Ok target -> Client.ProjectPaths.ProjectTarget.projectId target | Error error -> failwithf "%A" error
    let mutable loads = 0
    let load () =
        loads <- loads + 1
        None
    cache.Get(now, project, "Agent 3", load) |> ignore
    cache.Get(now.AddMinutes(1.), project, "Agent 3", load) |> ignore
    Assert.That(loads, Is.EqualTo(1))
    cache.Get(now.AddMinutes(2.1), project, "Agent 3", load) |> ignore
    Assert.That(loads, Is.EqualTo(2))
    cache.Get(now.AddMinutes(2.1), project, "Agent 4", load) |> ignore
    Assert.That(loads, Is.EqualTo(3))
    cache.Invalidate(project, "Agent 3")
    cache.Get(now.AddMinutes(2.2), project, "Agent 3", load) |> ignore
    Assert.That(loads, Is.EqualTo(4))


[<Test>]
let project_invalidation_expires_every_agent_of_one_project_only () =
    let cache = AgentCache(TimeSpan.FromMinutes 2.)
    let now = started
    let project name =
        Client.ProjectPaths.ProjectTarget.parse name
        |> function Ok target -> Client.ProjectPaths.ProjectTarget.projectId target | Error error -> failwithf "%A" error
    let first = project "Wayfold"
    let second = project "Jarvis"
    let mutable loads = 0
    let load () =
        loads <- loads + 1
        None
    cache.Get(now, first, "One", load) |> ignore
    cache.Get(now, first, "Two", load) |> ignore
    cache.Get(now, second, "One", load) |> ignore
    Assert.That(loads, Is.EqualTo(3))
    cache.InvalidateProject first
    cache.Get(now.AddSeconds(1), first, "One", load) |> ignore
    cache.Get(now.AddSeconds(1), first, "Two", load) |> ignore
    cache.Get(now.AddSeconds(1), second, "One", load) |> ignore
    Assert.That(loads, Is.EqualTo(5), "Unrelated projects retain their cached state")
    cache.InvalidateAll()
    cache.Get(now.AddSeconds(2), second, "One", load) |> ignore
    Assert.That(loads, Is.EqualTo(6))
