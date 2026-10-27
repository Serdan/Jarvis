module Client.AgentDashboard

open System
open Common

let private assignment (value: TaskStore.AgentAssignment) : AgentView.Assignment =
    { Agent = value.Agent
      Project = value.ProjectName.Split("::", StringSplitOptions.None)[0]
      Selector = value.ProjectName
      TaskId = value.Task.Id
      Title = value.Task.Title
      Status = value.Task.Status
      Phase = value.Phase
      NextActor = value.Task.NextActor
      Worktree = value.Worktree.Path
      Branch = value.Worktree.Branch
      Head = value.Worktree.Head
      Clean = value.Worktree.IsClean
      AssignmentRevision = value.AssignmentRevision
      AcknowledgedRevision = value.AcknowledgedAssignmentRevision
      Instructions = value.AssignmentInstructions
      BaseCommit = value.AssignmentBaseCommit
      UpdatedAt = value.Task.UpdatedAt
      LastUpdatedBy = value.Task.LastUpdatedBy
      CheckpointSummary = value.Task.CheckpointSummary
      CheckpointNextAction = value.Task.CheckpointNextAction
      CheckpointLastCommit = value.Task.CheckpointLastCommit
      CheckpointBlocker = value.Task.CheckpointBlocker
      CompletionFinalCommit = value.Task.CompletionFinalCommit }

let private historicalAssignment project (worktree: Worktrees.WorktreeInfo) (task: TaskStore.TaskSummary) phase : AgentView.Assignment =
    let selector =
        if worktree.IsMain then project
        else project + "::" + System.IO.Path.GetFileName(worktree.Path)
    { Agent = task.Owner.Value
      Project = project
      Selector = selector
      TaskId = task.Id
      Title = task.Title
      Status = task.Status
      Phase = phase
      NextActor = task.NextActor
      Worktree = worktree.Path
      Branch = worktree.Branch
      Head = worktree.Head
      Clean = worktree.IsClean
      AssignmentRevision = task.AssignmentRevision
      AcknowledgedRevision = None
      Instructions = None
      BaseCommit = None
      UpdatedAt = task.UpdatedAt
      LastUpdatedBy = task.LastUpdatedBy
      CheckpointSummary = task.CheckpointSummary
      CheckpointNextAction = task.CheckpointNextAction
      CheckpointLastCommit = task.CheckpointLastCommit
      CheckpointBlocker = task.CheckpointBlocker
      CompletionFinalCommit = task.CompletionFinalCommit }

let private suspended (snapshot: TaskStore.OrchestrationSnapshot) =
    snapshot.SuspendedTasks
    |> List.map (fun entry ->
        { historicalAssignment snapshot.ProjectName entry.Worktree entry.Task entry.Phase
            with NextActor = None })

let private completed (snapshot: TaskStore.OrchestrationSnapshot) =
    let integrated =
        snapshot.IntegratedTasks
        |> List.map (fun item -> item.Worktree.Path, item.Task.Id)
        |> Set.ofList
    snapshot.Worktrees
    |> List.collect (fun group ->
        group.Tasks
        |> List.choose (fun task ->
            match task.Owner, task.Status with
            | Some agent, "completed" ->
                let phase =
                    if Set.contains (group.Worktree.Path, task.Id) integrated then "Integrated"
                    else "Completed"
                let completedEntry =
                    historicalAssignment snapshot.ProjectName group.Worktree task phase
                Some completedEntry
            | _ -> None))

let fromSnapshots now (results: (string * Result<TaskStore.OrchestrationSnapshot, string>) list) : AgentView.Snapshot =
    let assignments =
        results |> List.collect (function
            | _, Ok snapshot ->
                (snapshot.Assignments |> List.map assignment)
                @ suspended snapshot
                @ completed snapshot
            | _ -> [])
    let projects : AgentView.ProjectSummary list =
        results |> List.map (function
            | project, Error error ->
                { Project = project
                  Worktrees = 0; PendingReviews = 0; Unacknowledged = 0
                  RetirementCandidates = 0; RetirementInspected = false; Error = Some error }
            | project, Ok snapshot ->
                { Project = project
                  Worktrees = snapshot.Worktrees.Length
                  PendingReviews = snapshot.ReviewerTaskCount
                  Unacknowledged = snapshot.UnacknowledgedTaskCount
                  RetirementCandidates = snapshot.RetirementCandidates.Length
                  RetirementInspected = snapshot.RetirementInspected
                  Error = None })
    { Assignments = assignments
      KnownAgents =
          results |> List.collect (function
              | _, Ok snapshot ->
                  snapshot.KnownAgents |> List.map (fun agent -> snapshot.ProjectName, agent)
              | _ -> [])
      Projects = projects
      Conflicts = results |> List.collect (function
          | _, Ok snapshot -> snapshot.Conflicts |> List.map (fun issue -> snapshot.ProjectName + ": " + issue)
          | _ -> [])
      Issues = results |> List.collect (function
          | project, Error error -> [ project + ": " + error ]
          | _, Ok snapshot ->
              snapshot.InspectionErrors |> List.map (fun error -> snapshot.ProjectName + ": " + error))
      RefreshedAt = now }

let load rt =
    match ProjectBrowser.listGitProjects rt with
    | Error error -> Error(string error)
    | Ok projects ->
        projects
        |> Seq.map (fun (ProjectName project) ->
            let response =
                TaskStore.getOrchestrationSnapshot
                    { ProjectName = project; Status = None; Owner = None; NextActor = None
                      ReviewerOnly = false; Offset = 0; Limit = 60; IncludeRetirement = false } rt
            project, response |> Result.mapError string)
        |> Seq.toList
        |> fromSnapshots DateTimeOffset.UtcNow
        |> Ok
