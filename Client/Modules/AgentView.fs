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
      /// Disambiguated human-readable project label for display only.
      ProjectLabel: string
      /// Every configured project name that resolves to this canonical ledger.
      ProjectAliases: string list
      State: string
      StateEnteredAt: DateTimeOffset option
      StateReason: string option
      Focus: TaskFocus option
      RoleIds: string list
      // Activity and review context are independent of implementation focus.
      Activity: string option
      ReviewContext: string option
      PendingNotifications: int
      LatestNotification: string option
      /// Project aliases that can unambiguously identify local tool-call activity.
      /// The canonical Key is for selection and must not be reused for presence.
      ActivityKeys: string list }

type ProjectCounts =
    { Tracked: int
      PendingReviews: int
      Unread: int
      Working: int
      Ready: int
      Waiting: int
      Parked: int }

type ProjectStatus =
    | Fresh
    | TemporarilyUnavailable
    | Stale
    | Unavailable

type ProjectSummary =
    { Project: string
      Aliases: string list
      Identity: string
      Counts: ProjectCounts option
      Status: ProjectStatus
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

/// Each @selector can match any alias of the same canonical ledger. A project
/// is never matched through the alias of a different ledger with that name.
let private matchesProjectAliases (terms: ActivityFilter.Terms) aliases =
    terms.Projects
    |> List.forall (fun projectTerm ->
        aliases
        |> List.exists (fun alias ->
            ActivityFilter.matchesTerms
                { Text = []; Projects = [projectTerm] } (Some alias) ""))

/// Text search narrows agent rows, not per-project totals. Only @project
/// selectors narrow the project summary.
let visibleProjects filter (snapshot: Snapshot) =
    let terms = ActivityFilter.parseTerms filter
    snapshot.Projects
    |> List.filter (fun project -> matchesProjectAliases terms project.Aliases)
    |> List.sortBy (fun project -> project.Project.ToLowerInvariant(), project.Identity)

let projectDisplayName (snapshot: Snapshot) (project: ProjectSummary) =
    let duplicates =
        snapshot.Projects
        |> List.filter (fun item ->
            String.Equals(item.Project, project.Project, StringComparison.OrdinalIgnoreCase))
    if duplicates.Length <= 1 then project.Project
    else
        // The short ledger prefix is a display hint, not an identity. Extend
        // it until it uniquely names this project among same-named ledgers.
        let identifier (identity: string) =
            match identity.Split(':') with
            | [| projectId; ledgerId; epoch |] -> ledgerId + "-" + projectId + "-" + epoch
            | _ -> identity
        let key = identifier project.Identity
        let others =
            duplicates
            |> List.filter (fun item -> item.Identity <> project.Identity)
            |> List.map (fun item -> identifier item.Identity)
        let prefix length (value: string) = value.Substring(0, min length value.Length)
        let length =
            [8 .. key.Length]
            |> List.tryFind (fun length ->
                others |> List.forall (fun other -> prefix length key <> prefix length other))
            |> Option.defaultValue key.Length
        project.Project + " [" + prefix length key + "]"

/// Preserve the distinguishing ledger suffix when a long project name
/// exceeds the available terminal columns.
let private fitProjectLabel width displayName =
    let label = ActivityFilter.projectToken displayName
    if width <= 0 then ""
    elif label.Length <= width then label
    elif width = 1 then "…"
    else
        let marker = label.LastIndexOf(" [", StringComparison.Ordinal)
        if marker > 0 && width >= label.Length - marker + 2 then
            let suffix = label.Substring(marker)
            let prefix = label.Substring(0, min marker (width - suffix.Length - 1))
            prefix + "…" + suffix
        else label.Substring(0, width - 1) + "…"

let projectColumnLabel width (row: AgentRow) =
    fitProjectLabel width row.ProjectLabel

/// Full labels when possible, unambiguous abbreviations when space is tight.
let projectStatusLine width displayName (project: ProjectSummary) =
    let clip length (value: string) =
        if length <= 0 then ""
        elif value.Length <= length then value
        elif length = 1 then "…"
        else value.Substring(0, length - 1) + "…"
    let label = ActivityFilter.projectToken displayName
    let status = if project.Status = Stale then "STALE | " else ""
    let detail =
        match project.Counts with
        | None when project.Status = TemporarilyUnavailable -> "TEMPORARILY UNAVAILABLE (counts unknown)"
        | None -> "UNAVAILABLE (counts unknown)"
        | Some c ->
            sprintf "Tracked %d | Review %d | Unread %d | Working %d | Ready %d | Waiting %d | Parked %d"
                c.Tracked c.PendingReviews c.Unread c.Working c.Ready c.Waiting c.Parked
    let full = label + " | " + status + detail
    if full.Length <= width then full
    else
        let compact =
            match project.Counts with
            | None when project.Status = TemporarilyUnavailable -> "TEMP UNAVAILABLE (counts unknown)"
            | None -> "UNAVAILABLE (counts unknown)"
            | Some c ->
                (if project.Status = Stale then "STALE " else "")
                + sprintf "T%d Rev%d U%d W%d Rd%d Wait%d P%d"
                    c.Tracked c.PendingReviews c.Unread c.Working c.Ready c.Waiting c.Parked
        let budget = max 0 (width - compact.Length - 3)
        if budget > 0 then fitProjectLabel budget displayName + " | " + compact |> clip width
        else clip width compact

/// Small terminals use a dedicated project-label row and wrap the counters,
/// rather than clipping away the identity or hiding activity categories.
let projectStatusLines width displayName (project: ProjectSummary) =
    if width >= 55 then [projectStatusLine width displayName project]
    elif width <= 0 then []
    else
        let clip (value: string) =
            if value.Length <= width then value
            elif width = 1 then "…"
            else value.Substring(0, width - 1) + "…"
        let tokens =
            match project.Counts with
            | None when project.Status = TemporarilyUnavailable ->
                ["TEMPORARILY"; "UNAVAILABLE"; "counts unknown"]
            | None -> ["UNAVAILABLE"; "counts unknown"]
            | Some c ->
                [ if project.Status = Stale then "STALE"
                  sprintf "T%d" c.Tracked; sprintf "Rev%d" c.PendingReviews
                  sprintf "U%d" c.Unread; sprintf "W%d" c.Working
                  sprintf "Rd%d" c.Ready; sprintf "Wait%d" c.Waiting
                  sprintf "P%d" c.Parked ]
        let prefix, last =
            tokens
            |> List.fold (fun (completed, current) token ->
                let candidate = if current = "" then token else current + " | " + token
                if candidate.Length <= width then completed, candidate
                elif current = "" then completed, clip token
                else current :: completed, clip token) ([], "")
        let counters = List.rev (last :: prefix)
        fitProjectLabel width displayName :: counters

/// Content-sized roster columns, capped to preserve space for task details.
/// Use the full filtered list so scrolling never changes column positions.
let columnWidths width (agents: AgentRow array) =
    let maxLength minimum projection =
        agents
        |> Array.fold (fun length row -> max length (projection row |> String.length)) minimum
        |> min 24
    let project = maxLength 7 (fun row -> ActivityFilter.projectToken row.ProjectLabel)
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
    let mutable latest = None
    for key in agent.ActivityKeys do
        match seen.TryGetValue key with
        | true, timestamp ->
            match latest with
            | Some previous when previous >= timestamp -> ()
            | _ -> latest <- Some timestamp
        | _ -> ()
    latest

let matches (filter: string) (row: AgentRow) =
    let terms = ActivityFilter.parseTerms filter
    let searchable =
        [ row.Agent; row.State; row.Project; row.ProjectLabel; yield! row.ProjectAliases
          yield! row.RoleIds
          row.Activity |> Option.defaultValue ""; row.ReviewContext |> Option.defaultValue ""
          row.StateReason |> Option.defaultValue ""
          match row.Focus with
          | Some task -> yield! [ task.TaskId; task.Title; task.Lifecycle; task.Phase; task.SourceRef |> Option.defaultValue "" ]
          | None -> () ]
        |> String.concat " "
    matchesProjectAliases terms row.ProjectAliases
    && ActivityFilter.matchesTerms { terms with Projects = [] } None searchable

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

/// Shared task-or-activity display. No synthetic task identifiers are needed.
let agentContext (row: AgentRow) =
    [ match row.Focus with
      | Some focus -> $"#{focus.TaskId} {focus.Title}"
      | None -> ()
      if not row.RoleIds.IsEmpty then
          "Roles: " + String.concat ", " row.RoleIds
      match row.Activity with
      | Some text -> "Activity: " + text
      | None -> ()
      match row.ReviewContext with
      | Some text -> text
      | None -> () ]
    |> String.concat " | "

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
            && previousRow.RoleIds = row.RoleIds
            && previousRow.Activity = row.Activity
            && previousRow.ReviewContext = row.ReviewContext
            && previousRow.PendingNotifications = row.PendingNotifications
            && previousRow.LatestNotification = row.LatestNotification -> None
        | _ -> Some row.Key)
    |> Set.ofList


/// UI-only carry-forward of independently verified dashboard rows when a
/// transient binding lock collision prevents a fresh read. Never authorize
/// operations from these observations.
let preserveTransientProjects (previous: Snapshot option) (latest: Snapshot) =
    match previous with
    | None -> latest
    | Some before ->
        let matchesAlias name (project: ProjectSummary) =
            project.Aliases
            |> List.exists (fun alias -> String.Equals(alias, name, StringComparison.OrdinalIgnoreCase))
        let retained =
            latest.Projects
            |> List.choose (fun current ->
                if current.Status <> TemporarilyUnavailable then None
                else
                    match before.Projects |> List.filter (matchesAlias current.Project) with
                    | [previousProject] when previousProject.Counts.IsSome
                                         && (previousProject.Status = Fresh || previousProject.Status = Stale)
                                         && not (latest.Projects |> List.exists (fun p ->
                                             p.Status = Fresh
                                             && (p.Identity = previousProject.Identity
                                                 || matchesAlias current.Project p))) ->
                        Some (current, previousProject)
                    | _ -> None)
        if retained.IsEmpty then latest
        else
            let converted =
                latest.Projects
                |> List.map (fun project ->
                    match retained |> List.tryFind (fun (current, _) -> current = project) with
                    | Some (_, verified) ->
                        { verified with Project = project.Project
                                        Aliases = project.Aliases
                                        Status = Stale
                                        Error = Some "Temporary trust-binding contention; last verified counts shown as stale." }
                    | None -> project)
            // A verified ledger may have multiple configured aliases. When
            // all fail to acquire their respective local locks, each failure
            // initially has a synthetic unavailable identity. Carry forward
            // the one canonical verified identity, merging only current
            // aliases and counting the ledger exactly once.
            let projects =
                let _, reversed =
                    converted
                    |> List.fold (fun (seen, acc) project ->
                        if project.Status <> Stale then seen, project :: acc
                        elif Set.contains project.Identity seen then seen, acc
                        else
                            let aliases =
                                converted
                                |> List.filter (fun item ->
                                    item.Status = Stale && item.Identity = project.Identity)
                                |> List.collect _.Aliases
                                |> List.distinctBy (fun alias -> alias.ToUpperInvariant())
                            Set.add project.Identity seen,
                            { project with Aliases = aliases } :: acc) (Set.empty, [])
                List.rev reversed
            let identities = retained |> List.map (fun (_, prior) -> prior.Identity) |> Set.ofList
            let rows =
                before.Agents
                |> List.filter (fun row -> identities |> Set.exists (fun id -> row.Key.StartsWith(id + "::", StringComparison.Ordinal)))
                |> List.map (fun row ->
                    if row.ProjectLabel.EndsWith(" [STALE]", StringComparison.Ordinal) then row
                    else { row with ProjectLabel = row.ProjectLabel + " [STALE]" })
            { latest with Projects = projects; Agents = latest.Agents @ rows }
