module Client.AgentView

open System

type Assignment =
    { Agent: string
      Project: string
      Selector: string
      TaskId: string
      Title: string
      Status: string
      Phase: string
      NextActor: string option
      Worktree: string
      Branch: string option
      Head: string
      Clean: bool option
      AssignmentRevision: string option
      AcknowledgedRevision: string option
      Instructions: string option
      BaseCommit: string option
      UpdatedAt: DateTimeOffset option
      LastUpdatedBy: string option
      CheckpointSummary: string option
      CheckpointNextAction: string option
      CheckpointLastCommit: string option
      CheckpointBlocker: string option
      CompletionFinalCommit: string option }

type ProjectSummary =
    { Project: string
      Worktrees: int
      PendingReviews: int
      Unacknowledged: int
      RetirementCandidates: int
      RetirementInspected: bool
      Error: string option }

type Snapshot =
    { Assignments: Assignment list
      KnownAgents: (string * string) list
      Projects: ProjectSummary list
      Conflicts: string list
      Issues: string list
      RefreshedAt: DateTimeOffset }

type AgentRow =
    { Key: string
      Agent: string
      Project: string
      Projects: string list
      Assignments: Assignment list
      State: string
      Conflict: bool }

let private actionable (item: Assignment) =
    (item.Status = "active" || item.Status = "open")
    && (item.NextActor = Some "agent" || item.NextActor = Some "reviewer")

let private suspended (item: Assignment) =
    item.Status = "parked" || item.Status = "blocked"

let private dateOrder (item: Assignment) =
    item.UpdatedAt |> Option.defaultValue DateTimeOffset.MinValue

let rows (snapshot: Snapshot) =
    let keys =
        (snapshot.Assignments |> List.map (fun item -> item.Project, item.Agent))
        @ snapshot.KnownAgents
        |> List.distinct
    keys
    |> List.map (fun (project, agent) ->
        let entries =
            snapshot.Assignments
            |> List.filter (fun item -> item.Project = project && item.Agent = agent)
        let current =
            entries |> List.filter actionable |> List.sortByDescending dateOrder
        let paused =
            entries |> List.filter suspended |> List.sortByDescending dateOrder
        let completed =
            entries
            |> List.filter (fun item -> item.Status = "completed")
            |> List.sortByDescending dateOrder
            |> List.truncate 3
        let assignments = current @ paused @ completed
        let state =
            if not current.IsEmpty then current.Head.Phase
            elif not paused.IsEmpty then paused.Head.Phase
            else "Free"
        let conflictFromSnapshot =
            snapshot.Conflicts
            |> List.exists (fun message ->
                message.Contains(project, StringComparison.OrdinalIgnoreCase)
                && (message.Contains(agent, StringComparison.OrdinalIgnoreCase)
                    || current |> List.exists (fun assignment ->
                        message.Contains("Task " + assignment.TaskId + " ", StringComparison.OrdinalIgnoreCase))))
        let conflict = current.Length > 1 || conflictFromSnapshot
        { Key = project + "::" + agent
          Agent = agent
          Project = project
          Projects = [project]
          Assignments = assignments
          State = state
          Conflict = conflict })
    |> List.sortBy (fun item ->
        (if item.Conflict then 0 else 1), item.Project.ToLowerInvariant(), item.Agent.ToLowerInvariant())

let matches (filter: string) (row: AgentRow) =
    let terms = ActivityFilter.parseTerms filter
    (row.Assignments.IsEmpty
     && ActivityFilter.matchesTerms terms (Some row.Project) (row.Agent + " " + row.State))
    || (row.Assignments
    |> List.exists (fun task ->
        let searchable =
            [ row.Agent; row.State; task.Project; task.TaskId; task.Title; task.Selector
              task.Worktree; task.Branch |> Option.defaultValue ""; task.Phase
              task.CheckpointSummary |> Option.defaultValue "" ]
            |> String.concat " "
        ActivityFilter.matchesTerms terms (Some task.Selector) searchable))

let visibleRows filter snapshot =
    rows snapshot |> List.filter (matches filter)

let phaseLabel phase =
    match phase with
    | "ReadyForReview" -> "Review"
    | "Acknowledged" -> "Acknowledged"
    | "Working" -> "Working"
    | "Assigned" -> "Assigned"
    | "Claimed" -> "Claimed"
    | other -> other

let agentSummary (row: AgentRow) =
    if row.Conflict then "Multiple actionable assignments - CONFLICT"
    elif row.State = "Free" then
        match row.Assignments |> List.tryHead with
        | Some last -> $"Free (last #{last.TaskId} {phaseLabel last.Phase})"
        | None -> "Free"
    else
        match row.Assignments |> List.tryHead with
        | None -> row.State
        | Some item ->
            let completedCount =
                row.Assignments |> List.filter (fun entry -> entry.Status = "completed") |> List.length
            let pausedCount =
                row.Assignments |> List.filter suspended |> List.length
            let suffix =
                [ if completedCount > 0 then $"{completedCount} completed"
                  if pausedCount > 0 && actionable item then $"{pausedCount} parked/blocked" ]
                |> function
                   | [] -> ""
                   | values -> " (+" + String.concat ", " values + ")"
            $"#{item.TaskId} - {phaseLabel item.Phase}{suffix}"

let changedAgents (before: Snapshot) (after: Snapshot) =
    let previous =
        rows before |> List.map (fun row -> row.Key, row) |> Map.ofList
    rows after
    |> List.choose (fun row ->
        match previous |> Map.tryFind row.Key with
        | Some prior
            when prior.State = row.State
                 && prior.Conflict = row.Conflict
                 && (prior.Assignments |> List.map (fun task ->
                     task.TaskId, task.Status, task.Phase, task.UpdatedAt,
                     task.AssignmentRevision, task.CheckpointSummary,
                     task.CheckpointNextAction, task.CheckpointBlocker,
                     task.CompletionFinalCommit))
                    = (row.Assignments |> List.map (fun task ->
                        task.TaskId, task.Status, task.Phase, task.UpdatedAt,
                        task.AssignmentRevision, task.CheckpointSummary,
                        task.CheckpointNextAction, task.CheckpointBlocker,
                        task.CompletionFinalCommit)) -> None
        | _ -> Some row.Key)
    |> Set.ofList
