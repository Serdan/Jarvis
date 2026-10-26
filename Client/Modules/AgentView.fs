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
      Error: string option }

type Snapshot =
    { Assignments: Assignment list
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
      Conflict: bool }

let private active (item: Assignment) = item.Status <> "completed"

let private dateOrder (item: Assignment) =
    item.UpdatedAt |> Option.defaultValue DateTimeOffset.MinValue

let rows (snapshot: Snapshot) =
    snapshot.Assignments
    |> List.groupBy (fun item -> item.Project, item.Agent)
    |> List.map (fun ((project, agent), entries) ->
        let activeAssignments = entries |> List.filter active
        let recentCompleted =
            entries
            |> List.filter (active >> not)
            |> List.sortByDescending dateOrder
            |> List.truncate 3
        let assignments = (activeAssignments |> List.sortByDescending dateOrder) @ recentCompleted
        let conflict =
            activeAssignments.Length > 1
            || (snapshot.Conflicts |> List.exists (fun message ->
                message.Contains(project, StringComparison.OrdinalIgnoreCase)
                && (message.Contains(agent, StringComparison.OrdinalIgnoreCase)
                    || (activeAssignments |> List.exists (fun assignment ->
                        message.Contains("Task " + assignment.TaskId + " ", StringComparison.OrdinalIgnoreCase))))))
        { Key = project + "::" + agent
          Agent = agent
          Project = project
          Projects = [project]
          Assignments = assignments
          Conflict = conflict })
    |> List.sortBy (fun item ->
        (if item.Conflict then 0 else 1), item.Project.ToLowerInvariant(), item.Agent.ToLowerInvariant())

let matches (filter: string) (row: AgentRow) =
    if String.IsNullOrWhiteSpace filter then true
    else
        let query = filter.Trim()
        let fields =
            row.Agent :: (row.Projects @
                (row.Assignments |> List.collect (fun task ->
                    [ task.TaskId
                      task.Title
                      task.Selector
                      task.Worktree
                      task.Branch |> Option.defaultValue ""
                      task.Phase ])))
        fields |> List.exists (fun value -> value.Contains(query, StringComparison.OrdinalIgnoreCase))

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
    if row.Conflict then $"{row.Assignments.Length} assignments - CONFLICT"
    else
        row.Assignments
        |> List.tryHead
        |> Option.map (fun item ->
            let additional = row.Assignments |> List.filter (active >> not) |> List.length
            let suffix = if additional > 0 && active item then $" (+{additional} completed)" else ""
            $"{item.Project}/{item.TaskId} - {phaseLabel item.Phase}{suffix}")
        |> Option.defaultValue "No recent tasks"

let private identity (task: Assignment) =
    task.Project, task.Agent, task.Worktree, task.TaskId

let changedAgents (before: Snapshot) (after: Snapshot) =
    let previous =
        before.Assignments
        |> List.map (fun item -> identity item, item)
        |> Map.ofList
    after.Assignments
    |> List.choose (fun item ->
        match previous |> Map.tryFind (identity item) with
        | Some prior
            when prior.Status = item.Status
                 && prior.Phase = item.Phase
                 && prior.UpdatedAt = item.UpdatedAt
                 && prior.CheckpointSummary = item.CheckpointSummary
                 && prior.CompletionFinalCommit = item.CompletionFinalCommit -> None
        | _ -> Some(item.Project + "::" + item.Agent))
    |> Set.ofList
