module Client.AgentDashboard

open System
open System.Collections.Concurrent
open System.Collections.Generic
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

let private read<'t> (records: Map<string, struct (string * string)>) path =
    match Map.tryFind path records with
    | None -> Error ("Missing canonical ledger entity: " + path)
    | Some struct (_, content) ->
        if typeof<'t>.Name = "ReviewAgent" || typeof<'t>.Name = "ReviewAttempt" then
            try
                use doc = System.Text.Json.JsonDocument.Parse(content)
                if doc.RootElement.GetProperty("schemaVersion").GetInt32() <> 3 then
                    Error "Legacy agent/attempt ledger schema is unsupported; this client requires orthogonal agent state."
                else deserialize<'t> content
            with ex -> Error ("Invalid canonical agent/attempt JSON: " + ex.Message)
        else deserialize<'t> content

// Reuse parsed immutable OID content when a new head references the same
// notification. Unlike the old JSON-string-keyed dictionary, this has hard
// entry/byte bounds and does not retain duplicate full JSON as cache keys.
let private parsedNotifications =
    BoundedFifoCache<Result<AgentNotification, string>>(16384, 16L * 1024L * 1024L)

let internal parsedNotificationCacheStats () = parsedNotifications.Counts

let private parseNotification (oid: string) (content: string) =
    match parsedNotifications.TryGet oid with
    | ValueSome parsed -> parsed
    | ValueNone ->
        let parsed = deserialize<AgentNotification> content
        // A parsed record holds the string fields, not the input JSON.
        // Count UTF-16 content bytes as a conservative record estimate.
        parsedNotifications.Add(oid, parsed, 2L * int64 (oid.Length + content.Length) + 128L)
        parsed

type internal NotificationSummary =
    { Pending: int
      Latest: string option }

/// Parse notifications once per changed snapshot, reduce unread counts and
/// newest item in a single pass, then discard all intermediate records. This
/// avoids rescanning/sorting the entire historical list for each agent.
let internal notificationSummaries (records: Map<string, struct (string * string)>) =
    let summaries = Dictionary<string, struct (int * AgentNotification)>(StringComparer.Ordinal)
    let mutable firstError = None
    for KeyValue(path, struct (oid, content)) in records do
        if path.StartsWith("notifications/", StringComparison.Ordinal) then
            match parseNotification oid content with
            | Error message ->
                if firstError.IsNone then firstError <- Some message
            | Ok notification when
                not (String.IsNullOrWhiteSpace notification.ToAgentId)
                && String.IsNullOrWhiteSpace notification.AcknowledgedAtUtc ->
                match summaries.TryGetValue notification.ToAgentId with
                | true, struct (count, newest) ->
                    let latest =
                        if (notification.CreatedAtUtc, notification.NotificationId) >
                           (newest.CreatedAtUtc, newest.NotificationId)
                        then notification else newest
                    summaries[notification.ToAgentId] <- struct (count + 1, latest)
                | _ -> summaries.Add(notification.ToAgentId, struct (1, notification))
            | _ -> ()
    match firstError with
    | Some message -> Error message
    | None ->
        summaries
        |> Seq.map (fun (KeyValue(agentId, struct (count, newest))) ->
            agentId,
            { Pending = count
              Latest = Some (
                  newest.FromAgentId
                  + (if String.IsNullOrWhiteSpace newest.TaskId then "" else " #" + newest.TaskId)
                  + ": " + newest.Message) })
        |> Map.ofSeq
        |> Ok

let private stateTime (agent: ReviewAgent) =
    match DateTimeOffset.TryParse(agent.StateEnteredAtUtc) with
    | true, value -> Some value
    | _ -> None

let private stringOption value =
    if String.IsNullOrWhiteSpace value then None else Some value

// Snapshot contents are immutable at a canonical Git commit. Reuse derived
// rows when the remote head has not changed, including notification summaries.
let private cachedProjects =
    ConcurrentDictionary<string, string * (AgentView.AgentRow list * int)>(StringComparer.Ordinal)

let private projectSnapshot project rt =
    match ProjectPaths.resolveProjectRoot project rt with
    | Error error -> Error (string error)
    | Ok (FolderPath root) ->
        // Unbound projects have no orchestration state. Never consult V1 files.
        if not (File.Exists(OrchestrationApi.LedgerBindingConfig.pathForRoot root)) then Ok None
        else
            let cacheKey = root + "::" + project
            match OrchestrationApi.LedgerBindingConfig.load project rt with
            | Error error -> Error (string error)
            | Ok binding ->
                let identity =
                    binding.ProjectId.ToString("N") + ":" + binding.LedgerId.ToString("N") + ":" + string binding.Epoch
                match LedgerStore.fetch binding with
                | Error error -> Error (string error)
                | Ok snapshot ->
                    match cachedProjects.TryGetValue(cacheKey) with
                    | true, (head, (agents, reviews)) when head = snapshot.Commit ->
                        Ok (Some (identity, agents, reviews))
                    | _ ->
                        match LedgerStore.readDashboardRecordsWithOidsAt binding snapshot with
                        | Error error -> Error (string error)
                        | Ok records ->
                            let paths = records |> Map.toSeq |> Seq.map fst |> Seq.toList
                            let agentPaths = paths |> List.filter (fun path -> path.StartsWith("agents/", StringComparison.Ordinal))
                            let taskPaths = paths |> List.filter (fun path -> path.StartsWith("tasks/", StringComparison.Ordinal))
                            let taskRecords = taskPaths |> List.filter (fun path -> path.EndsWith("/task.json", StringComparison.Ordinal))
                            let reviewRecords = taskPaths |> List.filter (fun path -> path.EndsWith("/review.json", StringComparison.Ordinal))
                            let tasks = taskRecords |> List.map (read<ReviewTask> records)
                            let agents = agentPaths |> List.map (read<ReviewAgent> records)
                            let reviews = reviewRecords |> List.map (read<BranchReview> records)
                            let notifications = notificationSummaries records
                            let firstError values = values |> List.tryPick (function Error message -> Some message | Ok _ -> None)
                            match firstError tasks, firstError agents, firstError reviews, notifications with
                            | Some error, _, _, _ | _, Some error, _, _ | _, _, Some error, _ -> Error error
                            | _, _, _, Error error -> Error error
                            | None, None, None, Ok notificationCounts ->
                                let tasks = tasks |> List.choose (function Ok item -> Some item | _ -> None)
                                let agents = agents |> List.choose (function Ok item -> Some item | _ -> None)
                                let reviews = reviews |> List.choose (function Ok item -> Some item | _ -> None)
                                // A focus is an attempt ID, not a source worktree or historical task owner.
                                let attemptForTask (task: ReviewTask) =
                                    if String.IsNullOrWhiteSpace task.AttemptId then Ok None
                                    else
                                        let path = sprintf "tasks/%s/attempts/%s.json" task.TaskId (task.AttemptId.Replace("-", "").ToLowerInvariant())
                                        read<ReviewAttempt> records path |> Result.map Some
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
                                            let notification =
                                                notificationCounts
                                                |> Map.tryFind agent.AgentId
                                                |> Option.defaultValue { Pending = 0; Latest = None }
                                            ({ Key = project + "::" + agent.AgentId; Agent = agent.AgentId
                                               Project = project; State = agent.State
                                               StateEnteredAt = stateTime agent; StateReason = stringOption agent.StateReason
                                               Focus = focus
                                               PendingNotifications = notification.Pending
                                               LatestNotification = notification.Latest } : AgentView.AgentRow)))
                                match firstError rows with
                                | Some error -> Error error
                                | None ->
                                    let count =
                                        reviews |> List.filter (fun r -> r.Status = "Unclaimed" || r.Status = "Claimed") |> List.length
                                    let data = (rows |> List.choose (function Ok row -> Some row | _ -> None), count)
                                    cachedProjects[cacheKey] <- (snapshot.Commit, data)
                                    Ok (Some (identity, fst data, snd data))
/// Build one view from the same canonical per-project records used for the
/// agent roster. Identity deduplication matters when multiple local checkouts
/// or clients point at the same remote ledger.
let internal snapshotFromResults (results: (string * Result<(string * AgentView.AgentRow list * int) option, string>) list) =
    let seen = HashSet<string>(StringComparer.Ordinal)
    let unique =
        results
        |> List.choose (fun (project, result) ->
            match result with
            | Ok (Some (identity, agents, reviews)) when seen.Add identity ->
                Some (project, identity, Some (agents, reviews), None)
            | Ok (Some _) | Ok None -> None
            | Error error -> Some (project, "unavailable:" + project, None, Some error))
    let rows =
        unique
        |> List.collect (function _, _, Some (agents, _), _ -> agents | _ -> [])
    let summaries : AgentView.ProjectSummary list =
        unique
        |> List.map (fun (project, identity, data, error) ->
            { Project = project
              Identity = identity
              Counts = data |> Option.map (fun (agents, reviews) -> AgentView.projectCounts agents reviews)
              Error = error })
    ({ Agents = rows; Projects = summaries; Issues = []
       RefreshedAt = DateTimeOffset.UtcNow } : AgentView.Snapshot)

let load rt =
    match ProjectBrowser.listGitProjects rt with
    | Error error -> Error (string error)
    | Ok projects ->
        // Fetch each project on the existing refresh cycle, never from an
        // agent inbox or a separate per-project timer.
        let results =
            projects
            |> Seq.toArray
            |> Array.Parallel.map (fun (ProjectName project) -> project, projectSnapshot project rt)
            |> Array.toList
        Ok (snapshotFromResults results)
