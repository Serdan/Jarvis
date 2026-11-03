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
    | Ok (Some content, _) ->
        if typeof<'t>.Name = "ReviewAgent" || typeof<'t>.Name = "ReviewAttempt" then
            try
                use doc = System.Text.Json.JsonDocument.Parse(content)
                if doc.RootElement.GetProperty("schemaVersion").GetInt32() <> 3 then
                    Error "Legacy agent/attempt ledger data requires explicit MigrateOrthogonalAgentState before reading dashboard."
                else deserialize<'t> content
            with ex -> Error ("Invalid canonical agent/attempt JSON: " + ex.Message)
        else deserialize<'t> content

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
        if not (File.Exists(OrchestrationApi.LedgerBindingConfig.pathForRoot root)) then Ok None
        else
            match OrchestrationApi.LedgerBindingConfig.load project rt with
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
                        let notifications =
                            LedgerStore.listAt binding snapshot "notifications"
                            |> Result.mapError string
                            |> Result.bind (fun paths ->
                                let records = paths
                                              |> List.filter (fun path -> path.EndsWith(".json", StringComparison.Ordinal))
                                              |> List.map (read<AgentNotification> binding snapshot)
                                match records |> List.tryPick (function Error error -> Some error | Ok _ -> None) with
                                | Some error -> Error error
                                | None -> Ok (records |> List.choose (function Ok item -> Some item | _ -> None)))
                        let firstError values = values |> List.tryPick (function Error message -> Some message | Ok _ -> None)
                        match firstError tasks, firstError agents, firstError reviews, notifications with
                        | Some error, _, _, _ | _, Some error, _, _ | _, _, Some error, _ -> Error error
                        | _, _, _, Error error -> Error error
                        | None, None, None, Ok notifications ->
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
                                    match tasks |> List.tryFind (fun task ->
                                        task.AttemptId = agent.FocusAttemptId
                                        && task.TaskId = agent.CurrentAssignment.TaskId) with
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
                                           Focus = focus
                                           PendingNotifications =
                                             notifications |> List.filter (fun n ->
                                                 n.ToAgentId = agent.AgentId && String.IsNullOrWhiteSpace n.AcknowledgedAtUtc) |> List.length
                                           LatestNotification =
                                             notifications |> List.filter (fun n ->
                                                 n.ToAgentId = agent.AgentId && String.IsNullOrWhiteSpace n.AcknowledgedAtUtc)
                                               |> List.sortByDescending (fun n -> n.CreatedAtUtc, n.NotificationId)
                                               |> List.tryHead
                                               |> Option.map (fun n ->
                                                   n.FromAgentId + (if String.IsNullOrWhiteSpace n.TaskId then "" else " #" + n.TaskId)
                                                   + ": " + n.Message) } : AgentView.AgentRow)))
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
