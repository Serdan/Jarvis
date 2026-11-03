module Client.AgentView

open System

type TaskFocus =
    { TaskId: string
      Title: string
      Lifecycle: string
      Phase: string
      SourceRef: string option
      BaseCommit: string option
      AssignmentGeneration: int }

type AgentRow =
    { Key: string
      Agent: string
      Project: string
      State: string
      StateEnteredAt: DateTimeOffset option
      StateReason: string option
      Focus: TaskFocus option
      PendingNotifications: int
      LatestNotification: string option }

type ProjectSummary =
    { Project: string
      PendingReviews: int
      Error: string option }

type Snapshot =
    { Agents: AgentRow list
      Projects: ProjectSummary list
      Issues: string list
      RefreshedAt: DateTimeOffset }

let attentionRank state =
    match state with
    | "Ready" -> 0
    | "NeedsWork" -> 1
    | "Working" -> 2
    | "Parked" -> 3
    | _ -> 4

let rows (snapshot: Snapshot) =
    snapshot.Agents
    |> List.sortBy (fun item ->
        (if item.PendingNotifications > 0 then -1 else attentionRank item.State), item.Project.ToLowerInvariant(), item.Agent.ToLowerInvariant())

let stateAge (now: DateTimeOffset) (agent: AgentRow) =
    agent.StateEnteredAt
    |> Option.map (fun entered ->
        let elapsed = max TimeSpan.Zero (now - entered)
        if elapsed.TotalDays >= 1.0 then sprintf "%.0fd" (floor elapsed.TotalDays)
        elif elapsed.TotalHours >= 1.0 then sprintf "%.0fh" (floor elapsed.TotalHours)
        elif elapsed.TotalMinutes >= 1.0 then sprintf "%.0fm" (floor elapsed.TotalMinutes)
        else sprintf "%.0fs" (floor elapsed.TotalSeconds))
    |> Option.defaultValue "—"

let matches (filter: string) (row: AgentRow) =
    let terms = ActivityFilter.parseTerms filter
    let searchable =
        [ row.Agent; row.State; row.Project
          row.StateReason |> Option.defaultValue ""
          match row.Focus with
          | Some task -> yield! [ task.TaskId; task.Title; task.Lifecycle; task.Phase; task.SourceRef |> Option.defaultValue "" ]
          | None -> () ]
        |> String.concat " "
    ActivityFilter.matchesTerms terms (Some row.Project) searchable

let visibleRows filter snapshot =
    rows snapshot |> List.filter (matches filter)

let displayState state = if state = "NeedsWork" then "Needs work" else state

let agentSummary (row: AgentRow) =
    match row.Focus with
    | Some task -> sprintf "#%s - %s" task.TaskId (displayState row.State)
    | None -> displayState row.State

let changedAgents (before: Snapshot) (after: Snapshot) =
    let previous =
        rows before |> List.map (fun row -> row.Key, row) |> Map.ofList
    rows after
    |> List.choose (fun row ->
        match Map.tryFind row.Key previous with
        | Some previousRow when
            previousRow.State = row.State
            && previousRow.StateEnteredAt = row.StateEnteredAt
            && previousRow.StateReason = row.StateReason
            && previousRow.Focus = row.Focus
            && previousRow.PendingNotifications = row.PendingNotifications
            && previousRow.LatestNotification = row.LatestNotification -> None
        | _ -> Some row.Key)
    |> Set.ofList
