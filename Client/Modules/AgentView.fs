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
      Focus: TaskFocus option }

type ProjectSummary =
    { Project: string
      PendingReviews: int
      Error: string option }

type Snapshot =
    { Agents: AgentRow list
      Projects: ProjectSummary list
      Issues: string list
      RefreshedAt: DateTimeOffset }

let rows (snapshot: Snapshot) =
    snapshot.Agents
    |> List.sortBy (fun item -> item.Project.ToLowerInvariant(), item.Agent.ToLowerInvariant())

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

let agentSummary (row: AgentRow) =
    match row.Focus with
    | Some task -> sprintf "#%s - %s" task.TaskId row.State
    | None -> row.State

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
            && previousRow.Focus = row.Focus -> None
        | _ -> Some row.Key)
    |> Set.ofList
