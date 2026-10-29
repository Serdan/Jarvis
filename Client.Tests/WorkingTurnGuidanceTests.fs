module Client.Tests.WorkingTurnGuidanceTests

open System
open Client.WorkingTurnGuidance
open Client.ReviewWorkflow
open NUnit.Framework

let private started = DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero)

let private agent state since =
    { AgentId = "Agent 1"; Version = 3; State = state
      FocusAttemptId = "attempt"; ActiveAttemptId = "attempt"
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
