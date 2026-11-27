// Frozen serial oracle: 175ad976a6226f1cb07901b4b43f3d6b0ecf3de1.
// Keep the original scheduler read path independent of the batched reader.
module SerialRosterOracle

open System
open System.Text
open System.Text.Json
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.RoleInstructions
open Client.ScheduledReadyResume

let private bad message = Error(InvalidBinding("Scheduled ready resume: " + message))

let private key (id: string) =
    Convert.ToBase64String(Encoding.UTF8.GetBytes id).TrimEnd('=').Replace('+', '-').Replace('/', '_')

let private readAgentId binding snapshot path =
    LedgerStore.readAt binding snapshot path
    |> Result.bind (function
        | Some raw, Some _ ->
            try
                use json = JsonDocument.Parse raw
                let id = json.RootElement.GetProperty("agentId").GetString()
                if String.IsNullOrWhiteSpace id || path <> "agents/" + key id + ".json" then
                    bad "Agent identity/path mismatch."
                else Ok id
            with _ -> bad "Malformed registered agent record."
        | _ -> bad "Missing registered agent record.")

// Ready is read without invoking agent_inbox (which would activate the agent).
// The canonical inbox projection also validates blocked attempt/wait indexes.
let private readReady binding snapshot (agent: ReviewAgent) =
    if agent.State <> "Ready" || agent.Execution <> "Stopped" then Ok None
    else
        ReviewWorkflow.getAgentInboxAt binding snapshot agent.AgentId
        |> Result.bind (fun inbox ->
            match inbox.Focused, inbox.RunnableQueue with
            | Some focused, [runnable]
                when focused = runnable
                     && focused.Task.Lifecycle = "InProgress"
                     && focused.Task.AttemptId = focused.Attempt.AttemptId
                     && focused.Attempt.OwnerAgentId = agent.AgentId
                     && focused.Attempt.AssignmentGeneration > 0
                     && agent.StateSequence > 0L
                     && not (inbox.Blocked |> List.exists (fun item ->
                         item.Attempt.AttemptId = focused.Attempt.AttemptId)) ->
                let candidate =
                    { AgentId = agent.AgentId
                      StateSequence = agent.StateSequence
                      TaskId = focused.Task.TaskId
                      AttemptId = focused.Attempt.AttemptId
                      Generation = focused.Attempt.AssignmentGeneration
                      Priority = focused.Attempt.Priority
                      IssuedSequence = focused.Attempt.IssuedSequence }
                ReviewWorkflow.getResponseAlertsAt binding snapshot agent.AgentId
                |> Result.map (fun queue ->
                    if queue.Pending |> List.exists (fun alert ->
                        alert.Message.TrimStart().StartsWith("STOP", StringComparison.OrdinalIgnoreCase)) then None
                    else Some candidate)
            | _ -> Ok None)

/// The roster and optional role attachments come from a single verified head.
/// With no role caps, role-resolution failures of unrelated Working agents
/// must not become a capacity prerequisite.
let readRosterAt (binding: LedgerBinding) (snapshot: LedgerSnapshot) (limits: Limits) =
    LedgerStore.listAt binding snapshot "agents"
    |> Result.bind (fun paths ->
        let paths = paths |> List.filter (fun path ->
            path.StartsWith("agents/", StringComparison.Ordinal)
            && path.EndsWith(".json", StringComparison.Ordinal)
            && not (path.EndsWith(".challenge-subscription.json", StringComparison.Ordinal)))
        let read path =
            readAgentId binding snapshot path
            |> Result.bind (fun id -> ReviewWorkflow.getAgentWorkingTurnAt binding snapshot id)
            |> Result.bind (fun agent ->
                readReady binding snapshot agent
                |> Result.bind (fun ready ->
                    let working = agent.Execution = "Working"
                    let roles =
                        if Map.isEmpty limits.Roles || (not working && ready.IsNone) then Ok Set.empty
                        else
                            RoleInstructions.resolveAtKnownAgent binding snapshot agent.AgentId
                            |> Result.map (fun resolved -> Set.ofList resolved.RoleIds)
                    roles |> Result.map (fun ids ->
                        { AgentId = agent.AgentId; Working = working; Roles = ids; Ready = ready })))
        paths |> List.fold (fun state path ->
            state |> Result.bind (fun entries -> read path |> Result.map (fun entry -> entry :: entries))) (Ok []))
