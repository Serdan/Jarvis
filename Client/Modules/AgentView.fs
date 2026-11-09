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

type ProjectCounts =
    { Tracked: int
      PendingReviews: int
      Unread: int
      Working: int
      Ready: int
      Waiting: int
      Parked: int }

type ProjectSummary =
    { Project: string
      Identity: string
      Counts: ProjectCounts option
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

let projectCounts (agents: AgentRow list) pendingReviews =
    let count state = agents |> List.sumBy (fun row -> if row.State = state then 1 else 0)
    { Tracked = agents.Length
      PendingReviews = pendingReviews
      Unread = agents |> List.sumBy _.PendingNotifications
      Working = count "Working"
      Ready = count "Ready"
      Waiting = count "NeedsWork"
      Parked = count "Parked" }

let private zeroCounts =
    { Tracked = 0; PendingReviews = 0; Unread = 0
      Working = 0; Ready = 0; Waiting = 0; Parked = 0 }

let totalCounts (snapshot: Snapshot) =
    snapshot.Projects
    |> List.choose _.Counts
    |> List.fold (fun sum item ->
        { Tracked = sum.Tracked + item.Tracked
          PendingReviews = sum.PendingReviews + item.PendingReviews
          Unread = sum.Unread + item.Unread
          Working = sum.Working + item.Working
          Ready = sum.Ready + item.Ready
          Waiting = sum.Waiting + item.Waiting
          Parked = sum.Parked + item.Parked }) zeroCounts

/// Text search narrows agent rows, not per-project totals. Only @project
/// selectors narrow the project summary.
let visibleProjects filter (snapshot: Snapshot) =
    let terms = ActivityFilter.parseTerms filter
    snapshot.Projects
    |> List.filter (fun project ->
        ActivityFilter.matchesTerms { terms with Text = [] } (Some project.Project) "")
    |> List.sortBy (fun project -> project.Project.ToLowerInvariant(), project.Identity)

let projectDisplayName (snapshot: Snapshot) (project: ProjectSummary) =
    let duplicate =
        snapshot.Projects
        |> List.filter (fun item ->
            String.Equals(item.Project, project.Project, StringComparison.OrdinalIgnoreCase))
    if duplicate.Length <= 1 then project.Project
    else
        let parts = project.Identity.Split(':')
        let key = if parts.Length > 1 then parts[1] else project.Identity
        project.Project + " [" + key.Substring(0, min 8 key.Length) + "]"

/// Full labels when possible, unambiguous abbreviations when space is tight.
let projectStatusLine width displayName (project: ProjectSummary) =
    let clip length (value: string) =
        if length <= 0 then ""
        elif value.Length <= length then value
        elif length = 1 then "…"
        else value.Substring(0, length - 1) + "…"
    let label = ActivityFilter.projectToken displayName
    let detail =
        match project.Counts with
        | None -> "UNAVAILABLE (counts unknown)"
        | Some c ->
            sprintf "Tracked %d | Review %d | Unread %d | Working %d | Ready %d | Waiting %d | Parked %d"
                c.Tracked c.PendingReviews c.Unread c.Working c.Ready c.Waiting c.Parked
    let full = label + " | " + detail
    if full.Length <= width then full
    else
        let compact =
            match project.Counts with
            | None -> "UNAVAILABLE (counts unknown)"
            | Some c ->
                sprintf "T%d Rev%d U%d W%d Rd%d Wait%d P%d"
                    c.Tracked c.PendingReviews c.Unread c.Working c.Ready c.Waiting c.Parked
        let budget = max 0 (width - compact.Length - 3)
        if budget > 0 then clip budget label + " | " + compact |> clip width
        else clip width compact

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

/// A registered worktree uses Project::worktree for file routing, but agent
/// activity belongs to the parent project.
let activityProject (target: ProjectPaths.ProjectTarget) =
    ProjectPaths.ProjectTarget.projectName target

/// Seen timestamps come from explicit agent-attributed tool calls, not sessions.
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
