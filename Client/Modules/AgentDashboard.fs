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
                let project = snapshot.ProjectName
                let selector =
                    if group.Worktree.IsMain then project
                    else project + "::" + System.IO.Path.GetFileName(group.Worktree.Path)
                let phase =
                    if Set.contains (group.Worktree.Path, task.Id) integrated then "Integrated"
                    else "Completed"
                let completedEntry : AgentView.Assignment =
                    { Agent = agent
                      Project = project
                      Selector = selector
                      TaskId = task.Id
                      Title = task.Title
                      Status = task.Status
                      Phase = phase
                      NextActor = None
                      Worktree = group.Worktree.Path
                      Branch = group.Worktree.Branch
                      Head = group.Worktree.Head
                      Clean = group.Worktree.IsClean
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
                Some completedEntry
            | _ -> None))

let fromSnapshots now (results: (string * Result<TaskStore.OrchestrationSnapshot, string>) list) : AgentView.Snapshot =
    let assignments =
        results |> List.collect (function
            | _, Ok snapshot ->
                (snapshot.Assignments |> List.map assignment) @ completed snapshot
            | _ -> [])
    let projects : AgentView.ProjectSummary list =
        results |> List.map (function
            | project, Error error ->
                { Project = project
                  Worktrees = 0; PendingReviews = 0; Unacknowledged = 0
                  RetirementCandidates = 0; Error = Some error }
            | project, Ok snapshot ->
                { Project = project
                  Worktrees = snapshot.Worktrees.Length
                  PendingReviews = snapshot.ReviewerTaskCount
                  Unacknowledged = snapshot.UnacknowledgedTaskCount
                  RetirementCandidates = snapshot.RetirementCandidates.Length
                  Error = None })
    { Assignments = assignments
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
    match ProjectBrowser.listProjects rt with
    | Error error -> Error(string error)
    | Ok projects ->
        projects
        |> Seq.map (fun (ProjectName project) ->
            let response = TaskStore.getOrchestrationSnapshot { ProjectName = project } rt
            project, response |> Result.mapError string)
        |> Seq.toList
        |> fromSnapshots DateTimeOffset.UtcNow
        |> Ok
