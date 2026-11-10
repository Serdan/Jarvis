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

type internal DashboardRead =
    { Identity: string
      Head: string
      Agents: AgentView.AgentRow list
      PendingReviews: int }

type internal ProjectResult =
    { Project: string
      Aliases: string list
      Result: Result<DashboardRead, string> }

let private bindingIdentity (binding: LedgerBinding) =
    binding.ProjectId.ToString("N") + ":" + binding.LedgerId.ToString("N") + ":" + string binding.Epoch

let private projectSnapshot project identity binding snapshot =
    // Derived rows are immutable at the verified Git head and can be reused
    // across project aliases without retaining the originating checkout.
    let cacheKey = identity
    match cachedProjects.TryGetValue(cacheKey) with
    | true, (head, (agents, reviews)) when head = snapshot.Commit ->
        Ok { Identity = identity; Head = snapshot.Commit; Agents = agents; PendingReviews = reviews }
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
                            ({ Key = identity + "::" + agent.AgentId; Agent = agent.AgentId
                               Project = project; ProjectLabel = project
                               ProjectAliases = [project]; State = agent.State
                               StateEnteredAt = stateTime agent; StateReason = stringOption agent.StateReason
                               Focus = focus
                               PendingNotifications = notification.Pending
                               LatestNotification = notification.Latest
                               ActivityKeys = [] } : AgentView.AgentRow)))
                match firstError rows with
                | Some error -> Error error
                | None ->
                    let count =
                        reviews |> List.filter (fun r -> r.Status = "Unclaimed" || r.Status = "Claimed") |> List.length
                    let data = (rows |> List.choose (function Ok row -> Some row | _ -> None), count)
                    cachedProjects[cacheKey] <- (snapshot.Commit, data)
                    Ok { Identity = identity; Head = snapshot.Commit; Agents = fst data; PendingReviews = snd data }
/// Build one view from verified per-project records. Multiple configured
/// project names may point at one ledger; only one set of counts is exposed.
/// Mismatched heads for a shared identity are an inconsistency, not a reason
/// to silently select whichever alias happened to be enumerated first.
let internal snapshotFromResults (results: ProjectResult list) =
    let allNames (result: ProjectResult) =
        result.Project :: result.Aliases
        |> List.filter (not << String.IsNullOrWhiteSpace)
        |> List.distinctBy (fun name -> name.ToUpperInvariant())

    let bound =
        results
        |> List.choose (fun result ->
            match result.Result with
            | Ok read -> Some (result, read)
            | Error _ -> None)
        |> List.groupBy (fun (_, read) -> read.Identity)
        |> List.map (fun (identity, sources) ->
            let project = (fst sources.Head).Project
            let aliases =
                sources |> List.collect (fun (source, _) -> allNames source)
                |> List.distinctBy (fun name -> name.ToUpperInvariant())
            let reads = sources |> List.map snd
            let first = reads.Head
            // Row presentation varies by alias; compare the canonical fields
            // only. Equal Git OIDs must yield equal state and unread metadata.
            let canonicalContents (read: DashboardRead) =
                let agents =
                    read.Agents
                    |> List.map (fun row ->
                        row.Agent, row.State, row.StateEnteredAt, row.StateReason,
                        row.Focus, row.PendingNotifications, row.LatestNotification)
                    |> List.sortBy (fun (agent, _, _, _, _, _, _) -> agent)
                read.PendingReviews, agents
            let error =
                if reads |> List.exists (fun read -> read.Head <> first.Head) then
                    Some ("Inconsistent canonical ledger heads for aliases of " + project
                          + "; counts are unavailable until a consistent refresh.")
                elif reads |> List.exists (fun read ->
                    canonicalContents read <> canonicalContents first) then
                    Some ("Inconsistent canonical ledger contents for aliases of " + project)
                else None
            // Preserve the unknown-state distinction even when other aliases
            // returned counts: the same identity cannot report two valid heads.
            let data = if error.IsSome then None else Some (first.Agents, first.PendingReviews)
            project, aliases, identity, data, error)
    let unavailable =
        results
        |> List.choose (fun result ->
            match result.Result with
            | Ok _ -> None
            | Error reason ->
                Some (result.Project, allNames result,
                      "unavailable:" + result.Project, None, Some reason))
    let unique = bound @ unavailable
    let identityByAlias =
        unique
        |> List.collect (fun (_, aliases, identity, _, _) ->
            aliases |> List.map (fun alias -> alias.ToUpperInvariant(), identity))
        |> List.groupBy fst
        |> List.map (fun (alias, entries) ->
            alias, entries |> List.map snd |> Set.ofList)
        |> Map.ofList
    let activityAliases identity (aliases: string list) =
        aliases
        |> List.filter (fun name ->
            let identities = Map.find (name.ToUpperInvariant()) identityByAlias
            identities.Count = 1 && identities.Contains identity)
    let summaries : AgentView.ProjectSummary list =
        unique
        |> List.map (fun (project, aliases, identity, data, error) ->
            { Project = project
              Aliases = aliases
              Identity = identity
              Counts = data |> Option.map (fun (agents, reviews) -> AgentView.projectCounts agents reviews)
              Error = error })
    let labelsSnapshot : AgentView.Snapshot =
        { Agents = []; Projects = summaries; Issues = []; RefreshedAt = DateTimeOffset.UtcNow }
    let projectLabels =
        summaries
        |> List.map (fun project -> project.Identity, AgentView.projectDisplayName labelsSnapshot project)
        |> Map.ofList
    let rows =
        unique
        |> List.collect (fun (project, aliases, identity, data, _) ->
            match data with
            | None -> []
            | Some (agents, _) ->
                let names = activityAliases identity aliases
                let label = Map.find identity projectLabels
                agents |> List.map (fun row ->
                    { row with Key = identity + "::" + row.Agent
                               Project = project
                               ProjectLabel = label
                               ProjectAliases = aliases
                               ActivityKeys = names |> List.map (fun name -> name + "::" + row.Agent) }))
    ({ Agents = rows; Projects = summaries; Issues = []
       RefreshedAt = DateTimeOffset.UtcNow } : AgentView.Snapshot)

/// Reading a local approved binding cannot invent a ledger or cause a remote
/// refresh. Unbound ordinary Git projects remain absent from the Agents view.
let private inspectProject project rt =
    match ProjectPaths.resolveProjectRoot project rt with
    | Error error -> Error (string error)
    | Ok (FolderPath root) ->
        if not (File.Exists(OrchestrationApi.LedgerBindingConfig.pathForRoot root)) then Ok None
        else
            OrchestrationApi.LedgerBindingConfig.inspectLocal project rt
            |> Result.map Some
            |> Result.mapError string

let private refreshGroup identity (members: (string * LedgerBinding * string) array) rt =
    let project, firstBinding, _ = members[0]
    let aliases = members |> Array.map (fun (name, _, _) -> name) |> Array.toList
    let matchingTransport (binding: LedgerBinding) =
        binding.ApprovedFetchUrl = firstBinding.ApprovedFetchUrl
        && binding.ApprovedPushUrl = firstBinding.ApprovedPushUrl
    let result =
        if members |> Array.exists (fun (_, binding, _) -> not (matchingTransport binding)) then
            Error ("Aliases of " + project + " have conflicting approved ledger remotes; counts unavailable.")
        else
            match OrchestrationApi.LedgerBindingConfig.loadWithSnapshot project rt with
            | Error error -> Error (string error)
            | Ok (verifiedBinding, snapshot) ->
                if bindingIdentity verifiedBinding <> identity || not (matchingTransport verifiedBinding) then
                    Error ("Canonical ledger binding changed during refresh of " + project)
                else
                    // Re-read each anchor after the network fetch. One alias
                    // could have a newer trusted head than the representative;
                    // showing its older state as freshly verified would be wrong.
                    let inconsistent =
                        members
                        |> Array.tryPick (fun (alias, _, _) ->
                            match OrchestrationApi.LedgerBindingConfig.inspectLocal alias rt with
                            | Error error -> Some (alias + ": " + string error)
                            | Ok (currentBinding, trustedHead) ->
                                if bindingIdentity currentBinding <> identity
                                   || not (matchingTransport currentBinding) then
                                    Some (alias + ": local ledger binding changed during refresh.")
                                else
                                    match LedgerStore.verifyTrustedHead verifiedBinding trustedHead snapshot.Commit with
                                    | Ok () -> None
                                    | Error error -> Some (alias + ": " + string error))
                    match inconsistent with
                    | Some reason -> Error ("Cannot verify shared ledger aliases: " + reason)
                    | None -> projectSnapshot project identity verifiedBinding snapshot
    { Project = project; Aliases = aliases; Result = result }

let load rt =
    match ProjectBrowser.listGitProjects rt with
    | Error error -> Error (string error)
    | Ok projects ->
        // Resolve and validate local identities first. Parallel fetches happen
        // only *after* grouping aliases, exactly once per canonical ledger.
        let inspected =
            projects
            |> Seq.toArray
            |> Array.Parallel.map (fun (ProjectName project) -> project, inspectProject project rt)
        let bound =
            inspected
            |> Array.choose (function
                | project, Ok (Some (binding, trustedHead)) -> Some (project, binding, trustedHead)
                | _ -> None)
            |> Array.groupBy (fun (_, binding, _) -> bindingIdentity binding)
        let fetched =
            bound |> Array.Parallel.map (fun (identity, members) -> refreshGroup identity members rt)
        let failures =
            inspected
            |> Array.choose (function
                | project, Error reason ->
                    Some { Project = project; Aliases = [project]; Result = Error reason }
                | _ -> None)
        Ok (snapshotFromResults (Array.append fetched failures |> Array.toList))
