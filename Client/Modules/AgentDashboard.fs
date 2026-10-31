module Client.AgentDashboard

open System
open System.IO
open System.Text.Json
open Client.LedgerStore
open Client.ReviewWorkflow

let private options =
    JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)

let private deserialize<'t> (text: string) =
    try
        let value = JsonSerializer.Deserialize<'t>(text, options)
        if isNull (box value) then Error "Null canonical ledger entity."
        else Ok value
    with ex -> Error ("Invalid canonical ledger entity: " + ex.Message)

let private read<'t> binding snapshot path =
    match LedgerStore.readAt binding snapshot path with
    | Error error -> Error (string error)
    | Ok (None, _) -> Error ("Missing canonical ledger entity: " + path)
    | Ok (Some content, _) -> deserialize<'t> content

let private stateTime (agent: ReviewAgent) =
    match DateTimeOffset.TryParse(agent.StateEnteredAtUtc) with
    | true, value -> Some value
    | _ -> None

let private stringOption value =
    if String.IsNullOrWhiteSpace value then None else Some value

let private projectSnapshot project rt =
    match ProjectPaths.resolveProjectRoot project rt with
    | Error error -> Error (string error)
    | Ok (FolderPath root) ->
        // Unbound projects have no orchestration state. Never consult V1 files.
        if not (File.Exists(V2ReviewApi.ReviewV2Binding.pathForRoot root)) then Ok None
        else
            match V2ReviewApi.ReviewV2Binding.load project rt with
            | Error error -> Error (string error)
            | Ok binding ->
                match LedgerStore.fetch binding with
                | Error error -> Error (string error)
                | Ok snapshot ->
                    match LedgerStore.listAt binding snapshot "agents",
                          LedgerStore.listAt binding snapshot "tasks" with
                    | Error error, _ | _, Error error -> Error (string error)
                    | Ok agentPaths, Ok taskPaths ->
                        let taskRecords = taskPaths |> List.filter (fun path -> path.EndsWith("/task.json", StringComparison.Ordinal))
                        let reviewRecords = taskPaths |> List.filter (fun path -> path.EndsWith("/review.json", StringComparison.Ordinal))
                        let tasks = taskRecords |> List.map (read<ReviewTask> binding snapshot)
                        let agents = agentPaths |> List.map (read<ReviewAgent> binding snapshot)
                        let reviews = reviewRecords |> List.map (read<BranchReview> binding snapshot)
                        let firstError values = values |> List.tryPick (function Error message -> Some message | Ok _ -> None)
                        match firstError tasks, firstError agents, firstError reviews with
                        | Some error, _, _ | _, Some error, _ | _, _, Some error -> Error error
                        | None, None, None ->
                            let tasks = tasks |> List.choose (function Ok item -> Some item | _ -> None)
                            let agents = agents |> List.choose (function Ok item -> Some item | _ -> None)
                            let reviews = reviews |> List.choose (function Ok item -> Some item | _ -> None)
                            // A focus is an attempt ID, not a source worktree or historical task owner.
                            let attemptForTask (task: ReviewTask) =
                                if String.IsNullOrWhiteSpace task.AttemptId then Ok None
                                else
                                    let path = sprintf "tasks/%s/attempts/%s.json" task.TaskId (task.AttemptId.Replace("-", "").ToLowerInvariant())
                                    read<ReviewAttempt> binding snapshot path |> Result.map Some
                            let focused (agent: ReviewAgent) =
                                if String.IsNullOrWhiteSpace agent.FocusAttemptId then Ok None
                                else
                                    match tasks |> List.tryFind (fun task -> task.AttemptId = agent.FocusAttemptId) with
                                    | None -> Error ("Missing canonical focused task for agent " + agent.AgentId)
                                    | Some task ->
                                        match attemptForTask task with
                                        | Error error -> Error error
                                        | Ok None -> Error ("Missing canonical attempt for task " + task.TaskId)
                                        | Ok (Some attempt) when attempt.OwnerAgentId <> agent.AgentId ->
                                            Error ("Canonical agent/attempt owner mismatch for " + agent.AgentId)
                                        | Ok (Some attempt) ->
                                            Ok (Some ({ TaskId = task.TaskId; Title = task.Title; Lifecycle = task.Lifecycle
                                                        Phase = attempt.Phase; SourceRef = stringOption attempt.SourceRef
                                                        BaseCommit = stringOption attempt.BaseCommit
                                                        AssignmentGeneration = attempt.AssignmentGeneration } : AgentView.TaskFocus))
                            let rows : Result<AgentView.AgentRow, string> list =
                                agents
                                |> List.map (fun agent ->
                                    focused agent |> Result.map (fun focus ->
                                        ({ Key = project + "::" + agent.AgentId; Agent = agent.AgentId
                                           Project = project; State = agent.State
                                           StateEnteredAt = stateTime agent; StateReason = stringOption agent.StateReason
                                           Focus = focus } : AgentView.AgentRow)))
                            match firstError rows with
                            | Some error -> Error error
                            | None ->
                                let count =
                                    reviews |> List.filter (fun r -> r.Status = "Unclaimed" || r.Status = "Claimed") |> List.length
                                Ok (Some (rows |> List.choose (function Ok row -> Some row | _ -> None), count))

let load rt =
    match ProjectBrowser.listGitProjects rt with
    | Error error -> Error (string error)
    | Ok projects ->
        let results =
            projects
            |> Seq.map (fun (ProjectName project) -> project, projectSnapshot project rt)
            |> Seq.toList
        let rows =
            results |> List.collect (function _, Ok (Some (agents, _)) -> agents | _ -> [])
        let summaries : AgentView.ProjectSummary list =
            results |> List.choose (function
                | project, Ok (Some (_, count)) ->
                    Some ({ Project = project; PendingReviews = count; Error = None } : AgentView.ProjectSummary)
                | project, Error error ->
                    Some ({ Project = project; PendingReviews = 0; Error = Some error } : AgentView.ProjectSummary)
                | _, Ok None -> None)
        Ok ({ Agents = rows; Projects = summaries; Issues = []
              RefreshedAt = DateTimeOffset.UtcNow } : AgentView.Snapshot)
