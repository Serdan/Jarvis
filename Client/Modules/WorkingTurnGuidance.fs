module Client.WorkingTurnGuidance

open System
open Client.ReviewWorkflow

[<Literal>]
let SoftMinutes = 25
[<Literal>]
let HardMinutes = 30

// Checked at response delivery, not by a background scheduler.
let instructionAt (now: DateTimeOffset) (agent: ReviewAgent) =
    if agent.State <> "Working" then None
    else
        let mutable started = Unchecked.defaultof<DateTimeOffset>
        if not (DateTimeOffset.TryParse(agent.WorkingTurnStartedAtUtc, &started))
           || now < started then None
        else
            let elapsed = now - started
            if elapsed >= TimeSpan.FromMinutes(float HardMinutes) then
                Some ("HARD STOP (30 minutes in Working): Do not start new work. " +
                      "Immediately persist/commit what is safe, yield to Ready with the exact next action or complete the assignment, " +
                      "and finish this response. " +
                      "Only use tools needed to save your work and report the result.")
            elif elapsed >= TimeSpan.FromMinutes(float SoftMinutes) then
                Some ("SOFT STOP (25 minutes in Working): Finish the current small unit, " +
                      "do not begin another, persist/commit your changes, yield to Ready with the next action or complete the assignment, " +
                      "and write your response.")
            else None
