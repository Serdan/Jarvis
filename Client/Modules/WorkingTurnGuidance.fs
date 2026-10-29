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


// Return advisory content to be included with the calling session's tool response.
let forSession (now: DateTimeOffset) sessionId (agent: ReviewAgent) =
    if agent.State <> "Working" || String.IsNullOrWhiteSpace sessionId then None
    elif agent.ActiveSessionId = sessionId then
        instructionAt now agent |> Option.map (fun content -> "working-turn", content)
    elif not (String.IsNullOrWhiteSpace agent.ActiveSessionId)
         && not (isNull (box agent.AssociatedSessionIds))
         && List.contains sessionId agent.AssociatedSessionIds then
        Some ("session-status",
              $"A newer session ({agent.ActiveSessionId}) is associated with agent {agent.AgentId}. " +
              $"Current state: {agent.State}; focused attempt: {agent.FocusAttemptId}. " +
              "Stop outdated work and reread the Jarvis assignment before continuing.")
    else None
