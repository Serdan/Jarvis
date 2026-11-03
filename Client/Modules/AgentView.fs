module Client.AgentView

open System
open System.Collections.Generic

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

let private agentSortKey (name: string) =
    let name = name.Trim().ToLowerInvariant()
    let digits = name |> Seq.rev |> Seq.takeWhile Char.IsDigit |> Seq.length
    if digits = 0 then name, Int64.MaxValue, name
    else
        let prefix = name.Substring(0, name.Length - digits).TrimEnd()
        let suffix = name.Substring(name.Length - digits)
        match Int64.TryParse suffix with
        | true, number -> prefix, number, name
        | _ -> name, Int64.MaxValue, name

let rows (snapshot: Snapshot) =
    snapshot.Agents
    |> List.sortBy (fun item ->
        item.Project.ToLowerInvariant(), agentSortKey item.Agent, item.Key)

/// Content-sized roster columns, capped to preserve space for task details.
/// Use the full filtered list so scrolling never changes column positions.
let columnWidths width (agents: AgentRow array) =
    let maxLength minimum projection =
        agents
        |> Array.fold (fun length row -> max length (projection row |> String.length)) minimum
        |> min 24
    let project = maxLength 7 (fun row -> ActivityFilter.projectToken row.Project)
    let agent = maxLength 5 (fun row -> row.Agent)
    let available = max 0 (width - 27 - 8 - 10)
    if project + agent <= available then project, agent
    else
        let projectWidth = min project (available / 2)
        let agentWidth = min agent (available - projectWidth)
        min project (available - agentWidth), agentWidth

let relativeAge (now: DateTimeOffset) (timestamp: DateTimeOffset option) =
    timestamp
    |> Option.map (fun entered ->
        let elapsed = max TimeSpan.Zero (now - entered)
        if elapsed.TotalDays >= 1.0 then sprintf "%.0fd" (floor elapsed.TotalDays)
        elif elapsed.TotalHours >= 1.0 then sprintf "%.0fh" (floor elapsed.TotalHours)
        elif elapsed.TotalMinutes >= 1.0 then sprintf "%.0fm" (floor elapsed.TotalMinutes)
        else sprintf "%.0fs" (floor elapsed.TotalSeconds))
    |> Option.defaultValue "—"

let stateAge (now: DateTimeOffset) (agent: AgentRow) =
    relativeAge now agent.StateEnteredAt

/// Seen timestamps come from session-attributed tool calls, not parsed messages.
let lastSeenAt (agent: AgentRow) (seen: IDictionary<string, DateTimeOffset>) =
    match seen.TryGetValue agent.Key with
    | true, timestamp -> Some timestamp
    | _ -> None

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
    rows snapshot
    |> List.filter (fun row -> row.State <> "Parked" && matches filter row)

let displayState state = if state = "NeedsWork" then "Waiting" else state

/// Consistent colors for the canonical ledger states. Labels remain visible
/// so the Agents screen is readable without color.
let stateColor = function
    | "Working" -> ConsoleColor.Green
    | "Ready" -> ConsoleColor.Yellow
    | "Assigned" -> ConsoleColor.Cyan
    | "NeedsWork" -> ConsoleColor.Magenta
    | "Completed" -> ConsoleColor.Blue
    | "Parked" -> ConsoleColor.DarkGray
    | _ -> ConsoleColor.Gray

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
