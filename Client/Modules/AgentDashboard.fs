module Client.AgentDashboard

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO
open System.Text.Json
open Common.SignalR
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.RoleInstructions

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

let private agentConfigPath (agentId: string) =
    let key =
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(agentId))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_')
    $"records/agent-instructions/{key}.json"

// Snapshot contents are immutable at a canonical Git commit. Reuse derived
// rows when the remote head has not changed, including notification summaries.
let private cachedProjects =
    ConcurrentDictionary<string, string * (AgentView.AgentRow list * int * int)>(StringComparer.Ordinal)

type internal DashboardRead =
    { Identity: string
      Head: string
      Agents: AgentView.AgentRow list
      UnclaimedReviews: int
      PendingReviews: int }

type internal DashboardFailure =
    | TemporaryBindingContention of string
    | OtherDashboardFailure of string

let private classifyFailure (error: Client.EffectError) =
    match error with
    | Client.LedgerFailureError issue when issue.Code = "TrustBindingContended" ->
        TemporaryBindingContention issue.Message
    | _ -> OtherDashboardFailure (string error)

let private failureMessage = function
    | TemporaryBindingContention reason | OtherDashboardFailure reason -> reason

type internal ProjectResult =
    { Project: string
      Aliases: string list
      Result: Result<DashboardRead, DashboardFailure> }

let private bindingIdentity (binding: LedgerBinding) =
    binding.ProjectId.ToString("N") + ":" + binding.LedgerId.ToString("N") + ":" + string binding.Epoch

let private projectSnapshot project identity binding snapshot =
    // Derived rows are immutable at the verified Git head and can be reused
    // across project aliases without retaining the originating checkout.
    let cacheKey = identity
    match cachedProjects.TryGetValue(cacheKey) with
    | true, (head, (agents, unclaimed, total)) when head = snapshot.Commit ->
        Ok { Identity = identity; Head = snapshot.Commit; Agents = agents
             UnclaimedReviews = unclaimed; PendingReviews = total }
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
            let configs =
                paths
                |> List.filter (fun path -> path.StartsWith("records/agent-instructions/", StringComparison.Ordinal))
                |> List.map (fun path ->
                    read<AgentInstructionConfig> records path
                    |> Result.bind (fun config ->
                        if path = agentConfigPath config.AgentId
                           && not (isNull (box config.RoleIds)) && config.RoleIds.Length <= 16 && config.Version > 0 then
                            Ok config
                        else Error ("Invalid canonical role attachment: " + path)))
            let reviews = reviewRecords |> List.map (read<BranchReview> records)
            let notifications = notificationSummaries records
            let firstError values = values |> List.tryPick (function Error message -> Some message | Ok _ -> None)
            match firstError tasks, firstError agents, firstError reviews, firstError configs, notifications with
            | Some error, _, _, _, _ | _, Some error, _, _, _
            | _, _, Some error, _, _ | _, _, _, Some error, _ -> Error error
            | _, _, _, _, Error error -> Error error
            | None, None, None, None, Ok notificationCounts ->
                let tasks = tasks |> List.choose (function Ok item -> Some item | _ -> None)
                let agents = agents |> List.choose (function Ok item -> Some item | _ -> None)
                let reviews = reviews |> List.choose (function Ok item -> Some item | _ -> None)
                let roleConfigs =
                    configs |> List.choose (function Ok item -> Some (item.AgentId, item.RoleIds) | _ -> None)
                    |> Map.ofList
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
                               RoleIds = roleConfigs |> Map.tryFind agent.AgentId |> Option.defaultValue []
                               Activity =
                                   if agent.State = "Working" && isNull (box agent.CurrentAssignment) then
                                       stringOption agent.StateReason
                                   else None
                               ReviewContext =
                                   reviews |> List.tryFind (fun review ->
                                       review.Status = "Claimed" && review.ReviewerId = agent.AgentId)
                                   |> Option.map (fun review -> $"#{review.ReviewId} {review.Kind}")
                               PendingNotifications = notification.Pending
                               LatestNotification = notification.Latest
                               ActivityKeys = []
                               LastSeenAt = None
                               LastSeenDevice = None
                               Machines = [] } : AgentView.AgentRow)))
                match firstError rows with
                | Some error -> Error error
                | None ->
                    // Each canonical review.json is one current request, including
                    // taskless requests in tasks/_reviews. Documents, snapshots,
                    // and historical task events are not review records.
                    let unclaimed, total =
                        reviews |> List.fold (fun (u, t) review ->
                            match review.Status with
                            | "Unclaimed" -> u + 1, t + 1
                            | "Claimed" -> u, t + 1
                            | _ -> u, t) (0, 0)
                    let agentRows = rows |> List.choose (function Ok row -> Some row | _ -> None)
                    cachedProjects[cacheKey] <- (snapshot.Commit, (agentRows, unclaimed, total))
                    Ok { Identity = identity; Head = snapshot.Commit; Agents = agentRows
                         UnclaimedReviews = unclaimed; PendingReviews = total }
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
                read.UnclaimedReviews, read.PendingReviews, agents
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
            let data = if error.IsSome then None else Some (first.Agents, first.UnclaimedReviews, first.PendingReviews)
            project, aliases, identity, data, error, (if error.IsSome then AgentView.Unavailable else AgentView.Fresh))
    let unavailable =
        results
        |> List.choose (fun result ->
            match result.Result with
            | Ok _ -> None
            | Error reason ->
                let status =
                    match reason with
                    | TemporaryBindingContention _ -> AgentView.TemporarilyUnavailable
                    | OtherDashboardFailure _ -> AgentView.Unavailable
                Some (result.Project, allNames result, "unavailable:" + result.Project,
                      None, Some (failureMessage reason), status))
    let unique = bound @ unavailable
    let identityByAlias =
        unique
        |> List.collect (fun (_, aliases, identity, _, _, _) ->
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
        |> List.map (fun (project, aliases, identity, data, error, status) ->
            { Project = project
              Aliases = aliases
              Identity = identity
              Counts = data |> Option.map (fun (agents, unclaimed, total) ->
                  AgentView.projectCountsWithReviews agents unclaimed total)
              Status = status
              Error = error })
    let labelsSnapshot : AgentView.Snapshot =
        { Agents = []; Projects = summaries; Issues = []; RefreshedAt = DateTimeOffset.UtcNow }
    let projectLabels =
        summaries
        |> List.map (fun project -> project.Identity, AgentView.projectDisplayName labelsSnapshot project)
        |> Map.ofList
    let rows =
        unique
        |> List.collect (fun (project, aliases, identity, data, _, _) ->
            match data with
            | None -> []
            | Some (agents, _, _) ->
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
    | Error error -> Error (classifyFailure error)
    | Ok (FolderPath root) ->
        if not (File.Exists(OrchestrationApi.LedgerBindingConfig.pathForRoot root)) then Ok None
        else
            OrchestrationApi.LedgerBindingConfig.inspectLocal project rt
            |> Result.map Some
            |> Result.mapError classifyFailure

let private refreshGroup identity (members: (string * LedgerBinding * string) array) rt =
    let project, firstBinding, _ = members[0]
    let aliases = members |> Array.map (fun (name, _, _) -> name) |> Array.toList
    let matchingTransport (binding: LedgerBinding) =
        binding.ApprovedFetchUrl = firstBinding.ApprovedFetchUrl
        && binding.ApprovedPushUrl = firstBinding.ApprovedPushUrl
    let result =
        if members |> Array.exists (fun (_, binding, _) -> not (matchingTransport binding)) then
            Error (OtherDashboardFailure ("Aliases of " + project + " have conflicting approved ledger remotes; counts unavailable."))
        else
            match OrchestrationApi.LedgerBindingConfig.loadWithSnapshot project rt with
            | Error error -> Error (classifyFailure error)
            | Ok (verifiedBinding, snapshot) ->
                if bindingIdentity verifiedBinding <> identity || not (matchingTransport verifiedBinding) then
                    Error (OtherDashboardFailure ("Canonical ledger binding changed during refresh of " + project))
                else
                    // Every alias must still trust this exact snapshot.
                    let inconsistent =
                        members
                        |> Array.tryPick (fun (alias, _, _) ->
                            match OrchestrationApi.LedgerBindingConfig.inspectLocal alias rt with
                            | Error error -> Some (classifyFailure error)
                            | Ok (currentBinding, trustedHead) ->
                                if bindingIdentity currentBinding <> identity
                                   || not (matchingTransport currentBinding) then
                                    Some (OtherDashboardFailure (alias + ": local ledger binding changed during refresh."))
                                else
                                    match LedgerStore.verifyTrustedHead verifiedBinding trustedHead snapshot.Commit with
                                    | Ok () -> None
                                    | Error error -> Some (OtherDashboardFailure (alias + ": " + string error)))
                    match inconsistent with
                    | Some (TemporaryBindingContention reason) ->
                        Error (TemporaryBindingContention reason)
                    | Some reason ->
                        Error (OtherDashboardFailure ("Cannot verify shared ledger aliases: " + failureMessage reason))
                    | None ->
                        projectSnapshot project identity verifiedBinding snapshot
                        |> Result.mapError OtherDashboardFailure
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


/// Discover trusted local ledger identities without fetching Git. The server
/// deduplicates these claims across devices and chooses one refresh provider.
let discoverClaims rt : Result<AgentRosterClaim array, string> =
    match ProjectBrowser.listGitProjects rt with
    | Error error -> Error (string error)
    | Ok projects ->
        let members =
            projects
            |> Seq.choose (fun (ProjectName project) ->
                match inspectProject project rt with
                | Ok (Some (binding, _)) -> Some (bindingIdentity binding, project)
                | _ -> None)
            |> Seq.groupBy fst
            |> Seq.map (fun (identity, values) ->
                let aliases =
                    values |> Seq.map snd |> Seq.distinct |> Seq.sort |> Seq.toArray
                { Identity = identity
                  ProjectName = aliases[0]
                  Aliases = aliases })
            |> Seq.toArray
        Ok members

/// One verified Git fetch per requested ledger. The prior server-observed head
/// is checked for ancestry by the owning client, so a lagging provider cannot
/// replace a newer snapshot with an older commit.
let readForRoster (request: AgentRosterRefreshRequest) rt : AgentRosterRefreshResponse =
    let failed error =
        { RequestId = request.RequestId; Identity = request.Identity
          Data = Unchecked.defaultof<_>; Error = error }
    try
        match discoverClaims rt with
        | Error error -> failed error
        | Ok claims ->
            match claims |> Array.tryFind (fun c ->
                c.Identity = request.Identity && c.ProjectName = request.ProjectName) with
            | None -> failed "Ledger advertisement no longer matches a trusted local binding."
            | Some claim ->
                match inspectProject claim.ProjectName rt with
                | Error error -> failed (failureMessage error)
                | Ok None -> failed "Local ledger binding disappeared."
                | Ok (Some (binding, head)) ->
                    let read = refreshGroup request.Identity
                                   [| claim.ProjectName, binding, head |] rt
                    match read.Result with
                    | Error error -> failed (failureMessage error)
                    | Ok data ->
                        let monotonic =
                            if String.IsNullOrWhiteSpace request.PreviousHead then Ok ()
                            else
                                LedgerStore.verifyTrustedHead binding request.PreviousHead data.Head
                                |> Result.mapError string
                        match monotonic with
                        | Error error -> failed ("Snapshot ancestry could not be proven: " + error)
                        | Ok () ->
                            let wireFocus (focus: AgentView.TaskFocus option) =
                                match focus with
                                | None -> Unchecked.defaultof<AgentRosterFocus>
                                | Some focus ->
                                    { TaskId = focus.TaskId
                                      Title = focus.Title
                                      Lifecycle = focus.Lifecycle
                                      Phase = focus.Phase
                                      SourceRef = focus.SourceRef |> Option.defaultValue ""
                                      BaseCommit = focus.BaseCommit |> Option.defaultValue ""
                                      AssignmentGeneration = focus.AssignmentGeneration }
                            let agents =
                                data.Agents
                                |> List.map (fun row ->
                                    { AgentId = row.Agent
                                      State = row.State
                                      StateEnteredAtUtc = row.StateEnteredAt |> Option.toNullable
                                      StateReason = row.StateReason |> Option.defaultValue ""
                                      Focus = wireFocus row.Focus
                                      RoleIds = row.RoleIds |> List.toArray
                                      Activity = row.Activity |> Option.defaultValue ""
                                      ReviewContext = row.ReviewContext |> Option.defaultValue ""
                                      PendingNotifications = row.PendingNotifications
                                      LatestNotification = row.LatestNotification |> Option.defaultValue "" })
                                |> List.toArray
                            { RequestId = request.RequestId; Identity = request.Identity
                              Error = ""
                              Data =
                                { Identity = request.Identity; Head = data.Head
                                  ProjectName = claim.ProjectName; Aliases = claim.Aliases
                                  Agents = agents; UnclaimedReviews = data.UnclaimedReviews
                                  PendingReviews = data.PendingReviews } }
    with ex ->
        failed ("Client roster inspection failed: " + ex.Message)

/// Rehydrate a display-only snapshot; never use these data to authorize local
/// operations. Missing remote last-seen data stays unknown, even if a local
/// activity file contains timestamps from a previous server lifetime.
let fromGlobalRoster (view: AgentRosterView) : AgentView.Snapshot =
    let status (entry: AgentRosterLedgerView) =
        match entry.Status with
        | "Fresh" -> AgentView.Fresh
        | "Refreshing" -> AgentView.TemporarilyUnavailable
        | "Stale" -> AgentView.Stale
        | _ -> AgentView.Unavailable
    let counts (entry: AgentRosterLedgerView) =
        if isNull (box entry.Data) then None
        else
            let agents = entry.Data.Agents
            let count state = agents |> Array.sumBy (fun a -> if a.State = state then 1 else 0)
            Some ({ Tracked = agents.Length
                    UnclaimedReviews = entry.Data.UnclaimedReviews
                    PendingReviews = entry.Data.PendingReviews
                    Unread = agents |> Array.sumBy _.PendingNotifications
                    Working = count "Working"
                    Ready = count "Ready"
                    Waiting = count "NeedsWork"
                    Parked = count "Parked" } : AgentView.ProjectCounts)
    let summaries =
        view.Ledgers
        |> Array.map (fun entry ->
            ({ Project = entry.ProjectName
               Aliases = entry.Aliases |> Array.toList
               Identity = entry.Identity
               Counts = counts entry
               Status = status entry
               Error = if String.IsNullOrWhiteSpace entry.Error then None else Some entry.Error }
                : AgentView.ProjectSummary))
        |> Array.toList
    let skeleton : AgentView.Snapshot =
        { Agents = []; Projects = summaries; Issues = []; RefreshedAt = view.ServerNowUtc }
    let devices =
        view.Activity
        |> Array.map (fun item -> (item.Identity, item.AgentId), item)
        |> Map.ofArray
    let rows =
        view.Ledgers
        |> Array.collect (fun ledger ->
            if isNull (box ledger.Data) then [||]
            else
                let summary = summaries |> List.find (fun s -> s.Identity = ledger.Identity)
                let projectLabel =
                    let label = AgentView.projectDisplayName skeleton summary
                    if summary.Status = AgentView.Fresh then label
                    else label + " [STALE]"
                ledger.Data.Agents
                |> Array.map (fun agent ->
                    let activity = devices |> Map.tryFind (ledger.Identity, agent.AgentId)
                    let focus =
                        if isNull (box agent.Focus) then None
                        else
                            Some ({ TaskId = agent.Focus.TaskId
                                    Title = agent.Focus.Title
                                    Lifecycle = agent.Focus.Lifecycle
                                    Phase = agent.Focus.Phase
                                    SourceRef = Option.ofObj agent.Focus.SourceRef
                                                |> Option.filter (String.IsNullOrWhiteSpace >> not)
                                    BaseCommit = Option.ofObj agent.Focus.BaseCommit
                                                 |> Option.filter (String.IsNullOrWhiteSpace >> not)
                                    AssignmentGeneration = agent.Focus.AssignmentGeneration }
                                   : AgentView.TaskFocus)
                    ({ Key = ledger.Identity + "::" + agent.AgentId
                       Agent = agent.AgentId
                       Project = ledger.ProjectName
                       ProjectLabel = projectLabel
                       ProjectAliases = ledger.Aliases |> Array.toList
                       State = agent.State
                       StateEnteredAt = if agent.StateEnteredAtUtc.HasValue then
                                            Some agent.StateEnteredAtUtc.Value else None
                       StateReason = Option.ofObj agent.StateReason
                                     |> Option.filter (String.IsNullOrWhiteSpace >> not)
                       Focus = focus
                       RoleIds = agent.RoleIds |> Array.toList
                       Activity = Option.ofObj agent.Activity
                                  |> Option.filter (String.IsNullOrWhiteSpace >> not)
                       ReviewContext = Option.ofObj agent.ReviewContext
                                       |> Option.filter (String.IsNullOrWhiteSpace >> not)
                       PendingNotifications = agent.PendingNotifications
                       LatestNotification = Option.ofObj agent.LatestNotification
                                            |> Option.filter (String.IsNullOrWhiteSpace >> not)
                       ActivityKeys = []
                       LastSeenAt = activity |> Option.map _.LastSeenAtUtc
                       LastSeenDevice = activity |> Option.map _.DeviceName
                       Machines = ledger.ProviderMachines |> Array.toList } : AgentView.AgentRow)))
        |> Array.toList
    { skeleton with Agents = rows }