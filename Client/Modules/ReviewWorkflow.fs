module Client.ReviewWorkflow

open System
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Security.Cryptography
open FsToolkit.ErrorHandling
open Client.LedgerStore

[<CLIMutable>]
type ReviewTask =
    { TaskId: string
      Title: string
      Version: int
      Lifecycle: string
      SpecVersion: int
      AttemptId: string }

[<CLIMutable>]
type TaskSpecImport =
    { TaskId: string
      Sequence: int64
      OperationId: string
      ActorId: string
      SourceRepositoryId: string
      SourceRef: string
      SourceCommitOid: string
      SourceRelativePath: string
      SourceBlobOid: string
      CanonicalBlobOid: string
      TaskVersionBefore: int
      TaskVersionAfter: int
      SpecVersionAfter: int }

[<CLIMutable>]
type TaskSpecPage =
    { TaskId: string
      SnapshotCommit: string
      SpecBlobOid: string
      Offset: int
      TotalBytes: int
      Content: string
      PageBytes: int
      HasMore: bool
      NextCursor: string
      ExpiresAtUtc: string }

[<CLIMutable>]
type TaskSpecCursor =
    { ProjectId: string
      LedgerId: string
      TaskId: string
      SnapshotCommit: string
      SpecBlobOid: string
      Offset: int
      ExpiresAtUtc: string }

[<CLIMutable>]
type TaskSpecDiff =
    { TaskId: string
      SnapshotCommit: string
      BeforeOid: string
      AfterOid: string
      Diff: string
      Truncated: bool }

[<CLIMutable>]
type TaskSpecExport =
    { TaskId: string
      SnapshotCommit: string
      SpecBlobOid: string
      RelativePath: string
      OperationId: string
      AlreadyApplied: bool
      Authority: string }

[<CLIMutable>]
type AgentAssignment =
    { TaskId: string
      AttemptId: string }

[<CLIMutable>]
type ReviewAgent =
    { AgentId: string
      Version: int
      CurrentAssignment: AgentAssignment
      Execution: string
      UnassignedDisposition: string
      StateReason: string
      StateSequence: int64
      StateEnteredAtUtc: string
      WorkingTurnStartedAtUtc: string
      // Canonical bounded evidence of admitted Working activity. Null on
      // legacy agents, before first ingress, and after leaving Working.
      ActivityLeaseUntilUtc: string
      ActiveSessionId: string
      AssociatedSessionIds: string list }
    with
        [<System.Text.Json.Serialization.JsonIgnore>]
        member this.State =
            if not (isNull (box this.CurrentAssignment)) then
                if this.Execution = "Working" then "Working" else "Ready"
            elif this.UnassignedDisposition = "Parked" then "Parked"
            else "NeedsWork"
        [<System.Text.Json.Serialization.JsonIgnore>]
        member this.FocusAttemptId =
            if isNull (box this.CurrentAssignment) then null else this.CurrentAssignment.AttemptId
        [<System.Text.Json.Serialization.JsonIgnore>]
        member this.ActiveAttemptId =
            if this.Execution = "Working" then this.FocusAttemptId else null

[<CLIMutable>]
type ReviewAttempt =
    { TaskId: string
      AttemptId: string
      Version: int
      OwnerAgentId: string
      Phase: string
      AssignmentGeneration: int
      Priority: int
      IssuedSequence: int64
      SpecBlobOid: string
      BaseCommit: string
      TargetRef: string
      SourceRepositoryId: string
      SourceRemote: string
      SourcePushRemote: string
      SourceRef: string }

[<CLIMutable>]
type AssignmentIssue =
    { TaskId: string
      AttemptId: string
      Generation: int
      OwnerAgentId: string
      IssuerId: string
      IssuedSequence: int64
      Instructions: string
      Reason: string
      SpecBlobOid: string
      SourceBaseCommit: string
      TargetRef: string
      SourceRepositoryId: string
      SourceRemote: string
      SourcePushRemote: string
      SourceRef: string
      PreviousGeneration: int }

[<CLIMutable>]
type AssignmentReceipt =
    { TaskId: string
      AttemptId: string
      Generation: int
      OwnerAgentId: string
      AcknowledgedSequence: int64
      SpecBlobOid: string
      IssueSequence: int64 }

[<CLIMutable>]
type BranchReview =
    { ReviewId: string
      TaskId: string
      AttemptId: string
      OwnerAgentId: string
      Kind: string
      TaskRef: string
      ReviewRef: string
      BaseCommit: string
      TargetRef: string
      Status: string
      ReviewerId: string
      Version: int
      CreatedSequence: int64
      DecisionSequence: int64
      Acknowledged: bool }

[<CLIMutable>]
type BranchReviewInspection =
    { SnapshotCommit: string
      Review: BranchReview
      HeadCommit: string }

[<CLIMutable>]
type ReviewDocumentInfo =
    { Path: string
      BlobOid: string
      SizeBytes: int }

[<CLIMutable>]
type ReviewDocument =
    { Path: string
      BlobOid: string
      Content: string }

[<CLIMutable>]
type TaskLifecycleEvent =
    { TaskId: string
      AttemptId: string
      OperationId: string
      Sequence: int64
      Action: string
      ActorId: string
      Reason: string
      PreviousLifecycle: string
      NewLifecycle: string
      PreviousPhase: string
      NewPhase: string
      PreviousOwnerId: string
      NewOwnerId: string
      PreviousGeneration: int
      NewGeneration: int
      ReviewRequestId: string
      ReviewDisposition: string
      Evidence: string
      // Optional for historical schema-v2 lifecycle events.
      RequestingAgentId: string
      TargetAgentId: string
      PreviousAgentState: string
      NewAgentState: string
      PreviousAgentVersion: int
      NewAgentVersion: int }

[<CLIMutable>]
type ReviewFeedbackNotice =
    { TaskId: string
      AttemptId: string
      RequestId: string
      OwnerAgentId: string
      DecisionSequence: int64
      Status: string
      Feedback: string
      Acknowledged: bool
      Version: int }

[<CLIMutable>]
type AgentNotification =
    { NotificationId: string
      FromAgentId: string
      FromProjectName: string
      ToAgentId: string
      TaskId: string
      Message: string
      CreatedAtUtc: string
      AcknowledgedAtUtc: string }

// These canonical records are independent of ReviewAgent.Version: a worker
// with an exact expectedAgentVersion must be able to admit its own operation
// without invalidating that assertion. A reservation has no expiration: a
// failed heartbeat cannot silently make an active mutating operation correctable.
[<CLIMutable>]
type InFlightAgentOperation =
    { Id: string
      TaskId: string
      AttemptId: string
      StartedAtUtc: string }

[<CLIMutable>]
type AgentInFlightReservations =
    { AgentId: string
      Operations: InFlightAgentOperation list
      // Completion remains recent even when no agent-version field changed.
      // This rejects stale corrections immediately following a real call.
      RecentlyCompletedUntilUtc: string }

[<CLIMutable>]
type IntegrationSubmission =
    { SubmissionId: string
      TaskId: string
      ActorId: string
      SourceRef: string
      SubmittedCommit: string
      ReviewId: string
      TargetRef: string
      Status: string
      CreatedSequence: int64
      IntegratedCommit: string
      CompletedAtUtc: string }

[<CLIMutable>]
type IntegrationSummary =
    { SnapshotCommit: string
      Latest: IntegrationSubmission option
      Submissions: IntegrationSubmission list }

[<CLIMutable>]
type AgentCheckpoint =
    { TaskId: string
      AttemptId: string
      OwnerAgentId: string
      AssignmentGeneration: int
      Sequence: int64
      Summary: string
      NextAction: string
      Kind: string
      SourceCommit: string
      Evidence: string
      EvidenceStatus: string }

[<CLIMutable>]
type InboxTask =
    { Task: ReviewTask
      Attempt: ReviewAttempt }

[<CLIMutable>]
type AgentInbox =
    { SnapshotCommit: string
      Agent: ReviewAgent
      Focused: InboxTask option
      OperatorActionRequired: bool
      NeedsOrchestratorAssignment: bool
      Working: InboxTask option
      RunnableQueue: InboxTask list
      Parked: InboxTask list
      Blocked: InboxTask list
      PendingReviews: BranchReview list
      AvailableReviews: BranchReview list
      UnacknowledgedFeedback: ReviewFeedbackNotice list
      PendingNotifications: AgentNotification list
      IntegrationSubmissions: IntegrationSubmission list
      SlotAvailable: bool
      Unassigned: bool }

type ReviewVerdict =
    | Accept
    | RequestChanges

module ReviewWorkflow =
    let private options =
        JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)
    let private json value = JsonSerializer.Serialize(value, options)
    let private enterState state (agent: ReviewAgent) =
        let execution = if state = "Working" then "Working" else "Stopped"
        let assignment = if List.contains state [ "Completed"; "Parked"; "NeedsWork" ] then Unchecked.defaultof<AgentAssignment> else agent.CurrentAssignment
        let disposition =
            if state = "Parked" then "Parked"
            elif isNull (box assignment) then "NeedsWork" else null
        let sameAssignment =
            if isNull (box agent.CurrentAssignment) then isNull (box assignment)
            elif isNull (box assignment) then false
            else agent.CurrentAssignment = assignment
        let changed = agent.Execution <> execution
                      || not sameAssignment
                      || agent.UnassignedDisposition <> disposition
        if not changed then agent
        else
            let now = DateTimeOffset.UtcNow.ToString("O")
            { agent with Execution = execution; CurrentAssignment = assignment
                         UnassignedDisposition = disposition; StateEnteredAtUtc = now
                         WorkingTurnStartedAtUtc = if execution = "Working" then now else null
                         ActivityLeaseUntilUtc = null
                         ActiveSessionId = if execution = "Working" then agent.ActiveSessionId else null }

    let private assigned taskId attemptId (agent: ReviewAgent) =
        let current : AgentAssignment = { TaskId = taskId; AttemptId = attemptId }
        { agent with CurrentAssignment = current; UnassignedDisposition = null
                     Execution = "Stopped"; StateEnteredAtUtc = DateTimeOffset.UtcNow.ToString("O") }

    let private matchesAssignment taskId (attempt: ReviewAttempt) (agent: ReviewAgent) =
        not (isNull (box agent.CurrentAssignment))
        && agent.CurrentAssignment.TaskId = taskId
        && agent.CurrentAssignment.AttemptId = attempt.AttemptId

    let private associateSession (sessionId: string) (agent: ReviewAgent) =
        if String.IsNullOrWhiteSpace sessionId then agent
        else
            let ids = if isNull (box agent.AssociatedSessionIds) then [] else agent.AssociatedSessionIds
            { agent with ActiveSessionId = sessionId
                         AssociatedSessionIds = if List.contains sessionId ids then ids else ids @ [ sessionId ] }

    let private resetSessions (agent: ReviewAgent) =
        { agent with ActiveSessionId = null
                     AssociatedSessionIds = []
                     WorkingTurnStartedAtUtc = null
                     ActivityLeaseUntilUtc = null }

    // An entity's schema is explicit on disk without adding boilerplate to
    // each in-memory record. Changing it requires an intentional upgrade path.
    let private entityJson<'t> (value: 't) =
        let content = json value
        if not (content.StartsWith("{", StringComparison.Ordinal)) then
            invalidArg "value" "A ledger entity must be a JSON object."
        let version = if List.contains typeof<'t>.Name [ "BranchReview"; "ReviewAgent"; "ReviewAttempt" ] then 3 else 2
        $"{{\"schemaVersion\":{version}," + content.Substring(1)

    let private validEntity<'t> (content: string) =
        try
            use doc = JsonDocument.Parse(content)
            let root = doc.RootElement
            if root.ValueKind <> JsonValueKind.Object then
                Error "Entity must be an object."
            else
                let properties = root.EnumerateObject() |> Seq.toArray
                let names = System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
                let unique = properties |> Array.forall (fun property -> names.Add property.Name)
                let mutable schema = Unchecked.defaultof<JsonElement>
                let schemaValid =
                    root.TryGetProperty("schemaVersion", &schema)
                    && schema.ValueKind = JsonValueKind.Number
                    && schema.GetInt32() = (if List.contains typeof<'t>.Name [ "BranchReview"; "ReviewAgent"; "ReviewAttempt" ] then 3 else 2)
                let required =
                    typeof<'t>.GetProperties()
                    |> Array.filter (fun property ->
                        not (Attribute.IsDefined(property, typeof<System.Text.Json.Serialization.JsonIgnoreAttribute>)))
                    |> Array.map (fun property -> options.PropertyNamingPolicy.ConvertName(property.Name))
                let complete =
                    required |> Array.forall (fun property ->
                        let mutable memberValue = Unchecked.defaultof<JsonElement>
                        root.TryGetProperty(property, &memberValue)
                        // FromProjectName was added after schema v2 notifications
                        // were already stored. Missing means same-project; all
                        // other notification members remain mandatory.
                        || (typeof<'t> = typeof<AgentNotification>
                            && property = "fromProjectName")
                        || (typeof<'t> = typeof<ReviewAgent>
                            && property = "activityLeaseUntilUtc")
                        || (typeof<'t> = typeof<TaskLifecycleEvent>
                            && List.contains property
                                [ "requestingAgentId"; "targetAgentId"
                                  "previousAgentState"; "newAgentState"
                                  "previousAgentVersion"; "newAgentVersion" ]))
                let status (name: string) (allowed: string list) =
                    let mutable field = Unchecked.defaultof<JsonElement>
                    root.TryGetProperty(name, &field)
                    && field.ValueKind = JsonValueKind.String
                    && List.contains (field.GetString()) allowed
                let stateValid =
                    match typeof<'t>.Name with
                    | "ReviewTask" -> status "lifecycle" [ "Open"; "InProgress"; "Implemented"; "Integrated"; "Closed"; "Cancelled" ]
                    | "ReviewAgent" ->
                        status "execution" [ "Stopped"; "Working" ]
                        && (let mutable current = Unchecked.defaultof<JsonElement>
                            let mutable disposition = Unchecked.defaultof<JsonElement>
                            root.TryGetProperty("currentAssignment", &current)
                            && root.TryGetProperty("unassignedDisposition", &disposition)
                            && (if current.ValueKind = JsonValueKind.Null then
                                    status "unassignedDisposition" [ "NeedsWork"; "Parked" ]
                                    && status "execution" [ "Stopped" ]
                                else current.ValueKind = JsonValueKind.Object
                                     && disposition.ValueKind = JsonValueKind.Null))
                    | "ReviewAttempt" -> status "phase" [ "Queued"; "Started"; "Finished"; "Deferred"; "Blocked";
                          "Superseded"; "Abandoned" ]
                    | "BranchReview" ->
                        status "status" [ "Unclaimed"; "Claimed"; "Accepted"; "ChangesRequested"; "Withdrawn" ]
                        && (let mutable kind = Unchecked.defaultof<JsonElement>
                            root.TryGetProperty("kind", &kind) && kind.ValueKind = JsonValueKind.String)
                    | "IntegrationSubmission" -> status "status" [ "Pending"; "Integrated" ]
                    | "ReviewFeedbackNotice" -> status "status" [ "Accepted"; "ChangesRequested" ]
                    | "AgentCheckpoint" -> status "kind" [ "Yield"; "Completion"; "Recovery"; "Progress"; "Submission"; "Revision" ]
                    | "TaskLifecycleEvent" ->
                        status "action" [ "ParkAttempt"; "BlockAttempt"; "ResumeAttempt"; "CancelTask";
                                          "AbandonAttempt"; "TransferOwner"; "CloseTask"; "ReopenTask";
                                          "CorrectAgentWorkingState" ]
                    | "AssignmentIssue" ->
                        status "reason" [ "Assignment"; "ReviewChanges"; "CompletionRequeue"; "Recovery"; "ExplicitReissue"; "AdministrativeResume"; "OwnershipTransfer"; "SpecAdoption" ]
                    | "AssignmentReceipt" -> true
                    | _ -> true
                if not unique then Error "Duplicate entity JSON member."
                elif not schemaValid then Error "Unsupported or missing entity schemaVersion."
                elif not complete then Error "Missing required entity JSON member."
                elif not stateValid then Error "Invalid entity lifecycle, state, status or evidence kind."
                else Ok ()
        with ex -> Error ex.Message
    let private idPattern = Regex("^[A-Za-z0-9_-]{1,80}$", RegexOptions.Compiled)
    let private refPattern = Regex("^refs/heads/[A-Za-z0-9][A-Za-z0-9_./-]*$", RegexOptions.Compiled)

    let private taskPath taskId = $"tasks/{taskId}/task.json"
    let private specPath taskId = $"tasks/{taskId}/spec.html"
    let private importPath taskId sequence = $"tasks/{taskId}/imports/{sequence}.json"
    let private attemptPath taskId attemptId = $"tasks/{taskId}/attempts/{attemptId}.json"
    let private checkpointPath taskId attemptId sequence =
        $"tasks/{taskId}/attempts/{attemptId}/checkpoints/{sequence}.json"
    let private issuePath taskId attemptId generation =
        $"tasks/{taskId}/attempts/{attemptId}/assignments/{generation}.json"
    let private receiptPath taskId attemptId generation =
        $"tasks/{taskId}/attempts/{attemptId}/assignments/{generation}.ack.json"
    let private branchReviewPath taskId reviewId = $"tasks/{taskId}/reviews/{reviewId}/review.json"
    let private reviewDocumentPath taskId requestId documentPath =
        $"tasks/{taskId}/reviews/{requestId}/{documentPath}"
    let private validReviewDocumentPath (documentPath: string) =
        documentPath = "summary.md"
        || (not (isNull documentPath)
            && Regex.IsMatch(documentPath, "^findings/[a-z0-9][a-z0-9_-]{0,79}[.]md$",
                             RegexOptions.CultureInvariant))
    let private lifecycleEventPath taskId sequence =
        $"tasks/{taskId}/lifecycle/{sequence}.json"

    let private integrationPath taskId (submissionId: Guid) =
        $"tasks/{taskId}/integrations/" + submissionId.ToString("N") + ".json"

    let private notificationPath (notificationId: Guid) =
        "notifications/" + notificationId.ToString("N") + ".json"

    let private reviewRef taskId requestId number = $"refs/heads/jarvis-review/{taskId}/{requestId}/r{number}"
    let private agentPath (agentId: string) =
        let safe =
            Convert.ToBase64String(Encoding.UTF8.GetBytes agentId)
                .TrimEnd('=').Replace('+', '-').Replace('/', '_')
        $"agents/{safe}.json"

    // The separate record is checked in the *same* ledger transaction as an
    // operator correction; reservation acquisition and correction serialize.
    let private inFlightPath agentId =
        "records/working-activity/" + (agentPath agentId).Substring("agents/".Length)

    let private validTaskId (taskId: string) = not (isNull taskId) && idPattern.IsMatch taskId
    let private validAgent (agent: string) =
        not (String.IsNullOrWhiteSpace agent) && Encoding.UTF8.GetByteCount(agent) <= 120
    let private validText limit (value: string) =
        not (String.IsNullOrWhiteSpace value) && Encoding.UTF8.GetByteCount(value) <= limit
    // Canonical task specifications may contain markup, but must not embed
    // active browser content. Display surfaces must still treat HTML as untrusted.
    let private validTaskHtml (value: string) =
        validText (512 * 1024) value
        && not (Regex.IsMatch(value,
            "<\\s*(script|iframe|object|embed|base|form|svg|math)\\b|\\bon[a-z]+\\s*=|javascript\\s*:",
            RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant))
    let private validBranch (value: string) =
        not (isNull value) && refPattern.IsMatch value
        && not (value.Contains("..")) && not (value.EndsWith("/"))

    // Content hashes, not large HTML, belong in immutable operation request
    // records. The canonical spec still lives as the full Git blob.
    let private specDigest (content: string) =
        SHA256.HashData(Encoding.UTF8.GetBytes(if isNull content then "" else content))
        |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

    // Old direct-library entrypoints retain the historic intent actor for
    // immutable ledger hash compatibility. All MCP administrative writes call
    // the explicit *As functions with resolved caller attribution instead.
    let private legacyAdministrativeActor = "Orchestrator"

    let private intent operationId actor kind payload =
        { OperationId = operationId
          Actor = actor
          Kind = kind
          RequestJson = json payload }

    // Immutable events receive one authoritative occurrence timestamp on creation.
    // Normal entity updates retain their existing fields; analytics can read the
    // event JSON directly from Git without a separate event store or counters.
    let private change path (blob: string option) value =
        let content = entityJson value
        let segments = (path: string).Split('/')
        let isEvent =
            blob.IsNone
            && (segments |> Array.exists (fun part ->
                List.contains part
                    [ "checkpoints"; "assignments"; "receipts"; "revisions"
                      "decisions"; "events"; "lifecycle"; "approvals"
                      "imports"; "orphan-cleanups" ]))
        let content =
            if isEvent then
                content.Substring(0, content.Length - 1)
                + ",\"occurredAtUtc\":" + json (DateTimeOffset.UtcNow.ToString("O")) + "}"
            else content
        { Path = path; ExpectedBlobOid = blob; Content = content }

    // Shared canonical JSON validation for individual entity reads and the
    // batched inbox. Never bypass schema/lifecycle/identity validation.
    let private decodeEntity<'t> path (content: string) : Result<'t, LedgerFailure> =
        try
            match validEntity<'t> content with
            | Error message ->
                Error(LedgerIntegrityError $"Invalid ledger entity {path}: {message}")
            | Ok () ->
                let parsed = JsonSerializer.Deserialize<'t>(content, options)
                let parsed =
                    if typeof<'t> = typeof<AgentNotification> && not (isNull (box parsed)) then
                        let notification = unbox<AgentNotification>(box parsed)
                        if isNull notification.FromProjectName then
                            unbox<'t>(box { notification with FromProjectName = "" })
                        else parsed
                    else parsed
                if isNull (box parsed) then
                    Error(LedgerIntegrityError $"Null ledger entity: {path}")
                else Ok parsed
        with ex ->
            Error(LedgerIntegrityError $"Invalid ledger entity {path}: {ex.Message}")

    let private read<'t> binding snapshot path : Result<('t * string) option, LedgerFailure> =
        result {
            let! content, blobOid = LedgerStore.readAt binding snapshot path
            match content, blobOid with
            | None, None -> return None
            | Some content, Some blobOid ->
                let! parsed = decodeEntity<'t> path content
                return Some(parsed, blobOid)
            | _ -> return! Error(LedgerIntegrityError $"Inconsistent ledger entity: {path}")
        }

    let private required<'t> binding snapshot path : Result<'t * string, LedgerFailure> =
        result {
            let! value = read<'t> binding snapshot path
            match value with
            | Some record -> return record
            | None -> return! Error(StaleEntityVersion $"Missing ledger entity: {path}")
        }

    let private requiredFromRecords<'t> (records: Map<string, string>) path =
        match Map.tryFind path records with
        | None -> Error(StaleEntityVersion $"Missing ledger entity: {path}")
        | Some content -> decodeEntity<'t> path content

    let private getAttempt binding snapshot taskId =
        result {
            let! task, taskOid = required<ReviewTask> binding snapshot (taskPath taskId)
            if String.IsNullOrWhiteSpace task.AttemptId then
                return! Error(StaleEntityVersion "Task has no active attempt.")
            let! attempt, attemptOid =
                required<ReviewAttempt> binding snapshot (attemptPath taskId task.AttemptId)
            return task, taskOid, attempt, attemptOid
        }

    let private issue taskId (attempt: ReviewAttempt) generation issuer reason
                      instructions sequence =
        { TaskId = taskId
          AttemptId = attempt.AttemptId
          Generation = generation
          OwnerAgentId = attempt.OwnerAgentId
          IssuerId = issuer
          IssuedSequence = sequence
          Instructions = instructions
          Reason = reason
          SpecBlobOid = attempt.SpecBlobOid
          SourceBaseCommit = attempt.BaseCommit
          TargetRef = attempt.TargetRef
          SourceRepositoryId = attempt.SourceRepositoryId
          SourceRemote = attempt.SourceRemote
          SourcePushRemote = attempt.SourcePushRemote
          SourceRef = attempt.SourceRef
          PreviousGeneration = if generation = 1 then 0 else generation - 1 }

    let private requiredIssue binding snapshot taskId (attempt: ReviewAttempt) =
        result {
            let! issuance, _ =
                required<AssignmentIssue> binding snapshot
                    (issuePath taskId attempt.AttemptId attempt.AssignmentGeneration)
            if issuance.Generation <> attempt.AssignmentGeneration
               || issuance.AttemptId <> attempt.AttemptId
               || issuance.OwnerAgentId <> attempt.OwnerAgentId
               || issuance.SpecBlobOid <> attempt.SpecBlobOid
               || issuance.IssuedSequence <> attempt.IssuedSequence then
                return! Error(LedgerIntegrityError "Current assignment generation record is inconsistent.")
            return issuance
        }

    let createTaskWithSpecAs binding actor operationId taskId title html =
        if not (validTaskId taskId) || not (validText 512 title)
           || not (validTaskHtml html) then
            Error(InvalidBinding "Invalid task ID, title or canonical HTML specification.")
        else
            LedgerStore.transact binding
                (intent operationId actor "CreateTask"
                    {| taskId = taskId; title = title; specSha256 = specDigest html |})
                (fun snapshot ->
                    result {
                        let! existing = read<ReviewTask> binding snapshot (taskPath taskId)
                        if existing.IsSome then return! Error(StaleEntityVersion "Task already exists.")
                        let! oldSpec, _ = LedgerStore.readAt binding snapshot (specPath taskId)
                        if oldSpec.IsSome then return! Error(LedgerIntegrityError "Orphan task spec already exists.")
                        let task =
                            { TaskId = taskId; Title = title; Version = 1
                              Lifecycle = "Open"; SpecVersion = 1; AttemptId = null }
                        return
                            [ change (taskPath taskId) None task
                              { Path = specPath taskId; ExpectedBlobOid = None; Content = html } ]
                    })

    // Core fixture compatibility: new MCP clients must supply the canonical HTML.

    let createTaskWithSpec binding operationId taskId title html =
        createTaskWithSpecAs binding legacyAdministrativeActor operationId taskId title html

    let createTask binding operationId taskId title =
        createTaskWithSpec binding operationId taskId title
            ($"<h1>{System.Net.WebUtility.HtmlEncode(title)}</h1>")

    let reviseTaskSpecPinnedAs binding actor operationId taskId expectedTaskVersion expectedSpecBlobOid title html =
        if not (validTaskId taskId) || expectedTaskVersion < 1
           || not (validText 512 title) || not (validTaskHtml html) then
            Error(InvalidBinding "Invalid task spec revision.")
        else
            LedgerStore.transact binding
                (intent operationId actor
                    (if String.IsNullOrWhiteSpace expectedSpecBlobOid then "ReviseTaskSpec" else "ReviseTaskSpecPinned")
                    {| taskId = taskId; expectedTaskVersion = expectedTaskVersion
                       title = title; specSha256 = specDigest html
                       expectedSpecBlobOid = expectedSpecBlobOid |})
                (fun snapshot ->
                    result {
                        let! task, taskOid = required<ReviewTask> binding snapshot (taskPath taskId)
                        let! _, specOid = LedgerStore.readAt binding snapshot (specPath taskId)
                        if not (List.contains task.Lifecycle [ "Open"; "InProgress" ])
                           || task.Version <> expectedTaskVersion || specOid.IsNone
                           || (not (String.IsNullOrWhiteSpace expectedSpecBlobOid)
                               && specOid <> Some expectedSpecBlobOid)
                           || (task.Lifecycle = "InProgress"
                               && String.IsNullOrWhiteSpace expectedSpecBlobOid) then
                            return! Error(StaleEntityVersion "Task revision requires exact task/spec identity; assigned tasks require pinned spec OID.")
                        return
                            [ change (taskPath taskId) (Some taskOid)
                                { task with Version = task.Version + 1
                                            SpecVersion = task.SpecVersion + 1; Title = title }
                              { Path = specPath taskId; ExpectedBlobOid = specOid; Content = html } ]
                    })

    // Legacy Open-only revision remains compatible; in-progress revisions
    // must supply explicit expected canonical blob via pinned tool.

    let reviseTaskSpecPinned binding operationId taskId expectedTaskVersion expectedSpecBlobOid title html =
        reviseTaskSpecPinnedAs binding legacyAdministrativeActor operationId taskId expectedTaskVersion expectedSpecBlobOid title html

    let reviseTaskSpec binding operationId taskId expectedTaskVersion title html =
        reviseTaskSpecPinned binding operationId taskId expectedTaskVersion null title html

    // Split preflight from publication to exercise the real race boundary
    // deterministically: every CAS retry rechecks the source snapshot.
    let importTaskSpecPreparedAs binding actor operationId taskId expectedTaskVersion
                               expectedSpecBlobOid expectedSourceBlobOid title relativePath
                               (original: SourceSpecFile) =
        if not (validTaskId taskId) || expectedTaskVersion < 1
           || not (validText 512 title) || String.IsNullOrWhiteSpace expectedSpecBlobOid
           || String.IsNullOrWhiteSpace relativePath || not (validTaskHtml original.Html)
           || (not (String.IsNullOrWhiteSpace expectedSourceBlobOid)
               && original.BlobOid <> expectedSourceBlobOid) then
            Error(InvalidBinding "Import requires safe pinned HTML, task/spec versions and source identity.")
        else
                LedgerStore.transact binding
                    (intent operationId actor "ImportTaskSpec"
                        {| taskId = taskId; expectedTaskVersion = expectedTaskVersion
                           expectedSpecBlobOid = expectedSpecBlobOid; expectedSourceBlobOid = original.BlobOid
                           sourceRepositoryId = original.RepositoryId; sourceRef = original.SourceRef
                           sourceCommitOid = original.CommitOid; sourceRelativePath = relativePath
                           expectedSourceBlobOidInput = expectedSourceBlobOid
                           title = title |})
                    (fun snapshot -> result {
                        let! current = LedgerStore.readRegisteredSourceSpec binding relativePath
                        if current.RepositoryId <> original.RepositoryId
                           || current.SourceRef <> original.SourceRef
                           || current.CommitOid <> original.CommitOid
                           || current.BlobOid <> original.BlobOid
                           || current.Html <> original.Html then
                            return! Error(SourceUnavailable "Source HTML changed between import preflight and ledger publication.")
                        let! task, taskOid = required<ReviewTask> binding snapshot (taskPath taskId)
                        let! _, specOid = LedgerStore.readAt binding snapshot (specPath taskId)
                        if task.Version <> expectedTaskVersion
                           || not (List.contains task.Lifecycle [ "Open"; "InProgress" ])
                           || specOid <> Some expectedSpecBlobOid then
                            return! Error(StaleEntityVersion "Import targets stale task/spec or a terminal lifecycle.")
                        let provenance =
                            { TaskId = taskId; Sequence = snapshot.Header.NextSequence
                              OperationId = operationId.ToString("D"); ActorId = actor
                              SourceRepositoryId = original.RepositoryId
                              SourceRef = original.SourceRef; SourceCommitOid = original.CommitOid
                              SourceRelativePath = relativePath; SourceBlobOid = original.BlobOid
                              CanonicalBlobOid = original.BlobOid
                              TaskVersionBefore = task.Version; TaskVersionAfter = task.Version + 1
                              SpecVersionAfter = task.SpecVersion + 1 }
                        return [
                            change (taskPath taskId) (Some taskOid)
                                { task with Title = title; Version = task.Version + 1
                                            SpecVersion = task.SpecVersion + 1 }
                            { Path = specPath taskId; ExpectedBlobOid = specOid
                              Content = original.Html }
                            change (importPath taskId snapshot.Header.NextSequence) None provenance ]
                    })

    let importTaskSpecPrepared binding operationId taskId expectedTaskVersion expectedSpecBlobOid expectedSourceBlobOid title relativePath (original: SourceSpecFile) =
        importTaskSpecPreparedAs binding legacyAdministrativeActor operationId taskId expectedTaskVersion expectedSpecBlobOid expectedSourceBlobOid title relativePath original

    let importTaskSpecAs binding actor operationId taskId expectedTaskVersion
                       expectedSpecBlobOid expectedSourceBlobOid title relativePath =
        result {
            let! original = LedgerStore.readRegisteredSourceSpec binding relativePath
            return! importTaskSpecPreparedAs binding actor operationId taskId expectedTaskVersion
                        expectedSpecBlobOid expectedSourceBlobOid title relativePath original
        }

    // In-progress spec updates never rewrite issued instructions: only an
    // explicit generation reissue can adopt the new canonical version.

    let importTaskSpec binding operationId taskId expectedTaskVersion expectedSpecBlobOid expectedSourceBlobOid title relativePath =
        importTaskSpecAs binding legacyAdministrativeActor operationId taskId expectedTaskVersion expectedSpecBlobOid expectedSourceBlobOid title relativePath

    let adoptTaskSpecAs binding actor operationId taskId ownerId expectedTaskVersion
                      expectedAttemptVersion expectedAgentVersion generation
                      expectedCurrentSpecOid instructions =
        if not (validTaskId taskId) || not (validAgent ownerId)
           || expectedTaskVersion < 1 || expectedAttemptVersion < 1
           || expectedAgentVersion < 1 || generation < 1
           || not (validText 8192 instructions)
           || String.IsNullOrWhiteSpace expectedCurrentSpecOid then
            Error(InvalidBinding "Spec adoption requires pinned canonical blob, exact versions and instructions.")
        else
            LedgerStore.transact binding
                (intent operationId actor "AdoptTaskSpec"
                    {| taskId = taskId; ownerId = ownerId
                       expectedTaskVersion = expectedTaskVersion
                       expectedAttemptVersion = expectedAttemptVersion
                       expectedAgentVersion = expectedAgentVersion; generation = generation
                       expectedCurrentSpecOid = expectedCurrentSpecOid; instructions = instructions |})
                (fun snapshot -> result {
                    let! task, _, attempt, attemptOid = getAttempt binding snapshot taskId
                    let! agent, agentOid = required<ReviewAgent> binding snapshot (agentPath ownerId)
                    let! _, specOid = LedgerStore.readAt binding snapshot (specPath taskId)
                    if task.Lifecycle <> "InProgress" || task.Version <> expectedTaskVersion
                       || attempt.Version <> expectedAttemptVersion
                       || attempt.OwnerAgentId <> ownerId
                       || attempt.AssignmentGeneration <> generation
                       || specOid <> Some expectedCurrentSpecOid
                       || attempt.SpecBlobOid = expectedCurrentSpecOid
                       || not (List.contains attempt.Phase [ "Queued" ])
                       || agent.State <> "Ready" || agent.Version <> expectedAgentVersion
                       || agent.FocusAttemptId <> attempt.AttemptId
                       || not (String.IsNullOrWhiteSpace agent.ActiveAttemptId)
                       then
                        return! Error(StaleEntityVersion "Spec adoption requires an unstarted, unchanged Assigned attempt and exact new canonical blob.")
                    let! _ = requiredIssue binding snapshot taskId attempt
                    let next =
                        { attempt with Version = attempt.Version + 1
                                       Phase = "Queued"
                                       AssignmentGeneration = generation + 1
                                       IssuedSequence = snapshot.Header.NextSequence
                                       SpecBlobOid = expectedCurrentSpecOid }
                    return [
                        change (attemptPath taskId attempt.AttemptId) (Some attemptOid) next
                        change (issuePath taskId attempt.AttemptId (generation + 1)) None
                            (issue taskId next (generation + 1) actor "SpecAdoption"
                                instructions snapshot.Header.NextSequence)
                        change (agentPath ownerId) (Some agentOid)
                            { agent with Version = agent.Version + 1; StateReason = instructions
                                         StateSequence = snapshot.Header.NextSequence } ]
                })

    let adoptTaskSpec binding operationId taskId ownerId expectedTaskVersion expectedAttemptVersion expectedAgentVersion generation expectedCurrentSpecOid instructions =
        adoptTaskSpecAs binding legacyAdministrativeActor operationId taskId ownerId expectedTaskVersion expectedAttemptVersion expectedAgentVersion generation expectedCurrentSpecOid instructions

    let getTaskSpecPage binding taskId cursor pageBytes =
        if not (validTaskId taskId) || pageBytes < 1 || pageBytes > 32768
           || (not (String.IsNullOrEmpty cursor) && cursor.Length > 2048) then
            Error(InvalidBinding "Spec page requires task ID and 1..32768 UTF-8 byte page size.")
        else result {
            let now = DateTimeOffset.UtcNow
            let! snapshot, blobOid, offset, expiry =
                if String.IsNullOrEmpty cursor then
                    result {
                        let! current = LedgerStore.fetch binding
                        let! _ = required<ReviewTask> binding current (taskPath taskId)
                        let! _, oid = LedgerStore.readAt binding current (specPath taskId)
                        match oid with
                        | Some value -> return current, value, 0, now.AddMinutes(30.0)
                        | None -> return! Error(LedgerIntegrityError "Canonical task specification is missing.")
                    }
                else
                    try
                        let normalized = cursor.Replace('-', '+').Replace('_', '/')
                        let data = Convert.FromBase64String(normalized.PadRight((normalized.Length + 3) / 4 * 4, '='))
                        let token = JsonSerializer.Deserialize<TaskSpecCursor>(data, options)
                        let mutable expires = Unchecked.defaultof<DateTimeOffset>
                        if isNull (box token)
                           || token.ProjectId <> binding.ProjectId.ToString("D")
                           || token.LedgerId <> binding.LedgerId.ToString("D")
                           || token.TaskId <> taskId || token.Offset < 0
                           || not (DateTimeOffset.TryParse(token.ExpiresAtUtc, &expires))
                           || expires <= now || expires > now.AddMinutes(30.0) then
                            Error(InvalidBinding "Spec cursor expired, exceeds 30-minute lifetime or has mismatched project/task identity.")
                        else
                            LedgerStore.verifiedHistoricalSnapshot binding token.SnapshotCommit
                            |> Result.map (fun pinned -> pinned, token.SpecBlobOid, token.Offset, expires)
                    with _ -> Error(InvalidBinding "Malformed spec continuation cursor.")
            let! content = LedgerStore.readHistoricalTaskSpec binding snapshot taskId blobOid
            let data = Encoding.UTF8.GetBytes(content)
            if offset > data.Length || (offset < data.Length && offset > 0
                 && (data[offset] &&& 0xC0uy) = 0x80uy) then
                return! Error(InvalidBinding "Spec cursor splits a UTF-8 code point.")
            let mutable finish = min data.Length (offset + pageBytes)
            while finish < data.Length && (data[finish] &&& 0xC0uy) = 0x80uy do
                finish <- finish - 1
            if finish = offset && offset < data.Length then
                return! Error(InvalidBinding "Page size cannot hold the next UTF-8 character.")
            let hasMore = finish < data.Length
            let next =
                if not hasMore then null
                else
                    let token =
                        { ProjectId = binding.ProjectId.ToString("D")
                          LedgerId = binding.LedgerId.ToString("D"); TaskId = taskId
                          SnapshotCommit = snapshot.Commit; SpecBlobOid = blobOid
                          Offset = finish; ExpiresAtUtc = expiry.ToString("O") }
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(json token))
                        .TrimEnd('=').Replace('+', '-').Replace('/', '_')
            return
                { TaskId = taskId; SnapshotCommit = snapshot.Commit; SpecBlobOid = blobOid
                  Offset = offset; TotalBytes = data.Length
                  Content = Encoding.UTF8.GetString(data, offset, finish - offset)
                  PageBytes = finish - offset; HasMore = hasMore
                  NextCursor = next; ExpiresAtUtc = expiry.ToString("O") }
        }

    let diffTaskSpec binding taskId beforeOid afterOid =
        if not (validTaskId taskId) then Error(InvalidBinding "Invalid diff task ID.")
        else result {
            let! snapshot = LedgerStore.fetch binding
            let! _ = required<ReviewTask> binding snapshot (taskPath taskId)
            let! diff, truncated =
                LedgerStore.diffHistoricalTaskSpecs binding snapshot taskId beforeOid afterOid
            return { TaskId = taskId; SnapshotCommit = snapshot.Commit
                     BeforeOid = beforeOid; AfterOid = afterOid
                     Diff = diff; Truncated = truncated }
        }

    let exportTaskSpecWithId binding (operationId: Guid) taskId relativePath expectedSpecOid =
        if not (validTaskId taskId)
           || String.IsNullOrWhiteSpace expectedSpecOid then
            Error(InvalidBinding "Export requires task and exact expected canonical spec blob.")
        else result {
            let! snapshot = LedgerStore.fetch binding
            let! _ = required<ReviewTask> binding snapshot (taskPath taskId)
            let! html, oid = LedgerStore.readAt binding snapshot (specPath taskId)
            if oid <> Some expectedSpecOid || html.IsNone then
                return! Error(StaleEntityVersion "Canonical spec changed before export.")
            let! alreadyApplied =
                LedgerStore.exportRegisteredSpec binding relativePath taskId snapshot.Commit
                    expectedSpecOid operationId html.Value
            return { TaskId = taskId; SnapshotCommit = snapshot.Commit
                     SpecBlobOid = expectedSpecOid; RelativePath = relativePath
                     OperationId = operationId.ToString("D"); AlreadyApplied = alreadyApplied
                     Authority = "NonAuthoritativeSourceCopy" }
        }

    let exportTaskSpec binding taskId relativePath expectedSpecOid =
        exportTaskSpecWithId binding (Guid.NewGuid()) taskId relativePath expectedSpecOid

    let private integrationSubmissionsAt binding snapshot taskId = result {
        let! paths = LedgerStore.listAt binding snapshot "tasks"
        let prefix = $"tasks/{taskId}/integrations/"
        let mutable submissions = []
        for path in paths do
            if path.StartsWith(prefix, StringComparison.Ordinal)
               && path.EndsWith(".json", StringComparison.Ordinal) then
                let! submission, _ = required<IntegrationSubmission> binding snapshot path
                match Guid.TryParseExact(submission.SubmissionId, "N") with
                | true, id when submission.TaskId = taskId
                                && path = integrationPath taskId id ->
                    submissions <- submission :: submissions
                | _ -> return! Error(LedgerIntegrityError "Invalid integration submission identity.")
        return submissions |> List.sortBy (fun s -> s.CreatedSequence, s.SubmissionId)
    }

    let getIntegrationSubmissions binding taskId =
        if not (validTaskId taskId) then Error(InvalidBinding "Invalid task ID.")
        else result {
            let! snapshot = LedgerStore.fetch binding
            let! _ = required<ReviewTask> binding snapshot (taskPath taskId)
            let! submissions = integrationSubmissionsAt binding snapshot taskId
            return { SnapshotCommit = snapshot.Commit
                     Latest = submissions |> List.tryLast
                     Submissions = submissions }
        }

    let submitIntegration binding operationId taskId actorId submissionId
                          sourceRef submittedCommit reviewId targetRef =
        if not (validTaskId taskId) || not (validAgent actorId)
           || submissionId = Guid.Empty || not (validBranch sourceRef)
           || not (validBranch targetRef)
           || (not (String.IsNullOrWhiteSpace reviewId)
               && not (match Guid.TryParse reviewId with true, id when id <> Guid.Empty -> true | _ -> false)) then
            Error(InvalidBinding "Invalid integration submission.")
        else
            // API callers may supply the ordinary hyphenated UUID; ledger paths use N.
            // Normalize before journaling so D and N spellings are idempotent.
            let reviewId =
                if String.IsNullOrWhiteSpace reviewId then ""
                else (Guid.Parse reviewId).ToString("N")
            let action = intent operationId actorId "SubmitIntegration"
                            {| taskId = taskId; submissionId = submissionId
                               sourceRef = sourceRef; submittedCommit = submittedCommit
                               reviewId = reviewId; targetRef = targetRef |}
            match LedgerStore.findApplied binding action with
            | Error failure -> Error failure
            | Ok(Some applied) -> Ok applied
            | Ok None -> result {
                do! LedgerStore.verifySourceCommit binding sourceRef submittedCommit
                let! snapshot = LedgerStore.fetch binding
                let! _ = required<ReviewTask> binding snapshot (taskPath taskId)
                if reviewId <> "" then
                    let! review, _ = required<BranchReview> binding snapshot
                                        (branchReviewPath taskId reviewId)
                    if review.TaskRef <> sourceRef then
                        return! Error(InvalidBinding "Review belongs to another source branch.")
                    let! reviewed = LedgerStore.inspectRemoteReviewRef binding review.ReviewRef
                    if reviewed <> Some submittedCommit then
                        return! Error(InvalidBinding "Submitted commit differs from the frozen review snapshot.")
                return! LedgerStore.transact binding action (fun current -> result {
                    let! _ = required<ReviewTask> binding current (taskPath taskId)
                    let! _ = required<ReviewAgent> binding current (agentPath actorId)
                    let path = integrationPath taskId submissionId
                    let! previous = read<IntegrationSubmission> binding current path
                    if previous.IsSome then
                        return! Error(StaleEntityVersion "Integration submission ID already exists.")
                    let! submissions = integrationSubmissionsAt binding current taskId
                    let branchSubmissions =
                        submissions |> List.filter (fun item -> item.SourceRef = sourceRef)
                    // A newer submission must preserve every older unintegrated slice;
                    // the branch cannot silently rewrite a pending integration boundary.
                    for prior in branchSubmissions do
                        if prior.Status = "Pending" then
                            do! LedgerStore.verifySourceAncestry binding prior.SubmittedCommit submittedCommit
                    let record =
                        { SubmissionId = submissionId.ToString("N")
                          TaskId = taskId; ActorId = actorId
                          SourceRef = sourceRef; SubmittedCommit = submittedCommit
                          ReviewId = reviewId; TargetRef = targetRef
                          Status = "Pending"; CreatedSequence = current.Header.NextSequence
                          IntegratedCommit = null; CompletedAtUtc = null }
                    return [ change path None record ]
                })
            }

    let completeIntegration binding operationId taskId actorId submissionId integratedCommit =
        if not (validTaskId taskId) || not (validAgent actorId)
           || submissionId = Guid.Empty then
            Error(InvalidBinding "Invalid integration completion.")
        else
            let action = intent operationId actorId "CompleteIntegration"
                           {| taskId = taskId; submissionId = submissionId
                              integratedCommit = integratedCommit |}
            match LedgerStore.findApplied binding action with
            | Error failure -> Error failure
            | Ok(Some applied) -> Ok applied
            | Ok None -> result {
                let! snapshot = LedgerStore.fetch binding
                let path = integrationPath taskId submissionId
                let! currentRecord, _ = required<IntegrationSubmission> binding snapshot path
                if currentRecord.Status <> "Pending" then
                    return! Error(StaleEntityVersion "Integration submission is already completed.")
                do! LedgerStore.verifyIntegrationOnRemote binding currentRecord.TargetRef
                        currentRecord.SubmittedCommit integratedCommit
                return! LedgerStore.transact binding action (fun current -> result {
                    let! _ = required<ReviewAgent> binding current (agentPath actorId)
                    let! existing, oid = required<IntegrationSubmission> binding current path
                    if existing.Status <> "Pending" then
                        return! Error(StaleEntityVersion "Integration submission is already completed.")
                    return [ change path (Some oid)
                                { existing with Status = "Integrated"
                                                IntegratedCommit = integratedCommit
                                                CompletedAtUtc = DateTimeOffset.UtcNow.ToString("O") } ]
                })
            }

    let notifyAgent binding operationId fromAgentId fromProjectName toAgentId taskId message =
        let crossProject = not (String.IsNullOrWhiteSpace fromProjectName)
        if not (validAgent fromAgentId) || not (validAgent toAgentId)
           || (crossProject && (fromProjectName.Length > 256 || fromProjectName.Contains('/')))
           || not (validText 4096 message)
           || (not (String.IsNullOrWhiteSpace taskId) && not (validTaskId taskId)) then
            Error(InvalidBinding "Invalid notification sender, recipient, task or message.")
        else
            // Operation ID is also the stable notification ID: retries cannot duplicate messages.
            LedgerStore.transact binding
                (intent operationId fromAgentId "NotifyAgent"
                    {| toAgentId = toAgentId; fromProjectName = fromProjectName; taskId = taskId; message = message |})
                (fun snapshot -> result {
                    // A cross-project sender was verified against its source ledger
                    // by the server before routing this destination-ledger write.
                    if not crossProject then
                        let! _ = required<ReviewAgent> binding snapshot (agentPath fromAgentId)
                        ()
                    let! _ = required<ReviewAgent> binding snapshot (agentPath toAgentId)
                    if not crossProject && not (String.IsNullOrWhiteSpace taskId) then
                        let! _ = required<ReviewTask> binding snapshot (taskPath taskId)
                        ()
                    let notification =
                        { NotificationId = operationId.ToString("N")
                          FromAgentId = fromAgentId; FromProjectName = if crossProject then fromProjectName else ""
                          ToAgentId = toAgentId
                          TaskId = if isNull taskId then "" else taskId
                          Message = message
                          CreatedAtUtc = DateTimeOffset.UtcNow.ToString("O")
                          AcknowledgedAtUtc = null }
                    return [ change (notificationPath operationId) None notification ]
                })

    let acknowledgeNotification binding operationId agentId notificationId =
        if not (validAgent agentId) || notificationId = Guid.Empty then
            Error(InvalidBinding "Invalid notification acknowledgement.")
        else
            LedgerStore.transact binding
                (intent operationId agentId "AcknowledgeNotification"
                    {| notificationId = notificationId; agentId = agentId |})
                (fun snapshot -> result {
                    let path = notificationPath notificationId
                    let! notification, oid = required<AgentNotification> binding snapshot path
                    if notification.NotificationId <> notificationId.ToString("N") then
                        return! Error(LedgerIntegrityError "Notification identity mismatch.")
                    if notification.ToAgentId <> agentId then
                        return! Error(ReviewAccessDenied "Only the recipient may acknowledge a notification.")
                    if not (String.IsNullOrWhiteSpace notification.AcknowledgedAtUtc) then
                        return! Error(StaleEntityVersion "Notification has already been acknowledged.")
                    return [ change path (Some oid)
                                { notification with AcknowledgedAtUtc = DateTimeOffset.UtcNow.ToString("O") } ]
                })

    let pendingNotifications binding snapshot agentId = result {
        let! paths = LedgerStore.listAt binding snapshot "notifications"
        let mutable pending = []
        for path in paths do
            if path.EndsWith(".json", StringComparison.Ordinal) then
                let! notification, _ = required<AgentNotification> binding snapshot path
                match Guid.TryParseExact(notification.NotificationId, "N") with
                | true, parsed when path = notificationPath parsed
                                    && validAgent notification.FromAgentId
                                    && validAgent notification.ToAgentId ->
                    if notification.ToAgentId = agentId
                       && String.IsNullOrWhiteSpace notification.AcknowledgedAtUtc then
                        pending <- notification :: pending
                | _ -> return! Error(LedgerIntegrityError "Invalid notification identity.")
        return pending |> List.sortBy (fun n -> n.CreatedAtUtc, n.NotificationId)
    }

    let registerAgentAs binding actor operationId agentId =
        if not (validAgent agentId) then Error(InvalidBinding "Invalid agent ID.")
        else
            LedgerStore.transact binding
                (intent operationId actor "RegisterAgent" {| agentId = agentId |})
                (fun snapshot ->
                    result {
                        let! existing = read<ReviewAgent> binding snapshot (agentPath agentId)
                        if existing.IsSome then return! Error(StaleEntityVersion "Agent already registered.")
                        let agent =
                            { AgentId = agentId; Version = 1; CurrentAssignment = Unchecked.defaultof<AgentAssignment>
                              Execution = "Stopped"; UnassignedDisposition = "Parked"
                              StateReason = "Registered without an assignment"
                              StateSequence = snapshot.Header.NextSequence
                              StateEnteredAtUtc = DateTimeOffset.UtcNow.ToString("O")
                              WorkingTurnStartedAtUtc = null; ActivityLeaseUntilUtc = null
                              ActiveSessionId = null; AssociatedSessionIds = [] }
                        return [ change (agentPath agentId) None agent ]
                    })

    let registerAgent binding operationId agentId =
        registerAgentAs binding legacyAdministrativeActor operationId agentId

    let assignTaskAs binding actor operationId taskId attemptId ownerId =
        if not (validTaskId taskId) || attemptId = Guid.Empty
           || not (validAgent ownerId) then
            Error(InvalidBinding "Invalid task assignment.")
        else
            let attemptIdText = attemptId.ToString("N")
            LedgerStore.transact binding
                (intent operationId actor "AssignTask"
                    {| taskId = taskId; attemptId = attemptId; ownerId = ownerId |})
                (fun snapshot ->
                    result {
                        let! task, taskOid = required<ReviewTask> binding snapshot (taskPath taskId)
                        let! agent, agentOid = required<ReviewAgent> binding snapshot (agentPath ownerId)
                        let! specContent, specOid = LedgerStore.readAt binding snapshot (specPath taskId)
                        if specContent.IsNone || specOid.IsNone then
                            return! Error(LedgerIntegrityError "Canonical task spec is missing.")
                        if task.Lifecycle <> "Open" || not (String.IsNullOrWhiteSpace task.AttemptId) then
                            return! Error(StaleEntityVersion "Task already has an implementation owner.")
                        if not (List.contains agent.State [ "Parked"; "NeedsWork" ])
                           || not (String.IsNullOrWhiteSpace agent.ActiveAttemptId) then
                            return! Error(AgentBusy "Agent must be unassigned (Needs work or Parked) before receiving an assignment.")
                        let attempt =
                            { TaskId = taskId; AttemptId = attemptIdText; Version = 1
                              OwnerAgentId = ownerId; Phase = "Queued"
                              AssignmentGeneration = 1; Priority = 0
                              IssuedSequence = snapshot.Header.NextSequence
                              SpecBlobOid = specOid.Value
                              BaseCommit = null; TargetRef = null
                              SourceRepositoryId = null; SourceRemote = null
                              SourcePushRemote = null; SourceRef = null
                              }
                        return
                            [ change (taskPath taskId) (Some taskOid)
                                { task with Version = task.Version + 1; Lifecycle = "InProgress"; AttemptId = attemptIdText }
                              change (attemptPath taskId attemptIdText) None attempt
                              change (issuePath taskId attemptIdText 1) None
                                (issue taskId attempt 1 actor "Assignment"
                                    $"Implement task {taskId} against the pinned task specification."
                                    snapshot.Header.NextSequence)
                              change (agentPath ownerId) (Some agentOid)
                                { (assigned taskId attemptIdText (resetSessions agent)) with
                                    Version = agent.Version + 1
                                    StateReason = $"Assigned task {taskId}; awaiting operator start"
                                    StateSequence = snapshot.Header.NextSequence } ]
                    })

    let assignTask binding operationId taskId attemptId ownerId =
        assignTaskAs binding legacyAdministrativeActor operationId taskId attemptId ownerId

    let acknowledgeAssignment binding operationId taskId ownerId expectedAttemptVersion generation =
        if not (validTaskId taskId) || not (validAgent ownerId)
           || expectedAttemptVersion < 1 || generation < 1 then
            Error(InvalidBinding "Invalid assignment acknowledgment.")
        else
            LedgerStore.transact binding
                (intent operationId ownerId "AcknowledgeAssignment"
                    {| taskId = taskId; ownerId = ownerId
                       expectedVersion = expectedAttemptVersion; generation = generation |})
                (fun snapshot ->
                    result {
                        let! task, _, attempt, attemptOid = getAttempt binding snapshot taskId
                        let! agent, _ = required<ReviewAgent> binding snapshot (agentPath ownerId)
                        if task.Lifecycle <> "InProgress"
                           || attempt.OwnerAgentId <> ownerId
                           || attempt.Version <> expectedAttemptVersion
                           || attempt.AssignmentGeneration <> generation
                           || attempt.Phase <> "Queued"
                           || agent.State <> "Ready"
                           || agent.FocusAttemptId <> attempt.AttemptId
                           || not (String.IsNullOrWhiteSpace agent.ActiveAttemptId) then
                            return! Error(StaleEntityVersion "Only the current queued generation can be acknowledged.")
                        let! issuance = requiredIssue binding snapshot taskId attempt
                        let! existing =
                            read<AssignmentReceipt> binding snapshot
                                (receiptPath taskId attempt.AttemptId generation)
                        if existing.IsSome then
                            return! Error(LedgerIntegrityError "Assignment generation already has an acknowledgment.")
                        let receipt =
                            { TaskId = taskId; AttemptId = attempt.AttemptId
                              Generation = generation; OwnerAgentId = ownerId
                              AcknowledgedSequence = snapshot.Header.NextSequence
                              SpecBlobOid = issuance.SpecBlobOid
                              IssueSequence = issuance.IssuedSequence }
                        return
                            [ change (receiptPath taskId attempt.AttemptId generation) None receipt
                              change (attemptPath taskId attempt.AttemptId) (Some attemptOid)
                                { attempt with Version = attempt.Version + 1; Phase = "Queued" } ]
                    })

    let getAssignment binding taskId generation =
        if not (validTaskId taskId) || generation < 1 then
            Error(InvalidBinding "Invalid assignment generation query.")
        else
            result {
                let! snapshot = LedgerStore.fetch binding
                let! task, _, attempt, _ = getAttempt binding snapshot taskId
                if generation > attempt.AssignmentGeneration then
                    return! Error(StaleEntityVersion "Assignment generation has not been issued.")
                let! issuance, _ =
                    required<AssignmentIssue> binding snapshot
                        (issuePath taskId attempt.AttemptId generation)
                let! receipt =
                    read<AssignmentReceipt> binding snapshot
                        (receiptPath taskId attempt.AttemptId generation)
                if issuance.TaskId <> taskId || issuance.Generation <> generation
                   || issuance.AttemptId <> attempt.AttemptId
                   || issuance.OwnerAgentId <> attempt.OwnerAgentId then
                    return! Error(LedgerIntegrityError "Assignment generation does not match its task/attempt.")
                match receipt with
                | Some (ack, _) when ack.Generation <> generation
                                    || ack.SpecBlobOid <> issuance.SpecBlobOid
                                    || ack.IssueSequence <> issuance.IssuedSequence
                                    || ack.OwnerAgentId <> issuance.OwnerAgentId ->
                    return! Error(LedgerIntegrityError "Acknowledgment does not match immutable generation.")
                | _ ->
                    return snapshot, task, attempt, issuance, receipt |> Option.map fst
            }

    let beginAttemptWithSession binding operationId taskId ownerId expectedVersion generation sessionId =
        if not (validTaskId taskId) || not (validAgent ownerId) || expectedVersion < 1 || generation < 1 then
            Error(InvalidBinding "Invalid BeginAttempt request.")
        else
            LedgerStore.transact binding
                (intent operationId ownerId "BeginAttempt"
                    {| taskId = taskId; ownerId = ownerId; expectedVersion = expectedVersion; generation = generation |})
                (fun snapshot ->
                    result {
                        let! task, _, attempt, attemptOid = getAttempt binding snapshot taskId
                        let! agent, agentOid = required<ReviewAgent> binding snapshot (agentPath ownerId)
                        if task.Lifecycle <> "InProgress" || attempt.OwnerAgentId <> ownerId
                           || attempt.Version <> expectedVersion || attempt.AssignmentGeneration <> generation
                           || not (List.contains attempt.Phase [ "Queued" ]) then
                            return! Error(StaleEntityVersion "Attempt is not available for Begin.")
                        let! issuance = requiredIssue binding snapshot taskId attempt
                        let! existingAcknowledgment =
                            read<AssignmentReceipt> binding snapshot
                                (receiptPath taskId attempt.AttemptId generation)
                        match existingAcknowledgment with
                        | Some (ack, _) when
                            ack.Generation <> generation
                            || ack.SpecBlobOid <> issuance.SpecBlobOid
                            || ack.IssueSequence <> issuance.IssuedSequence
                            || ack.OwnerAgentId <> ownerId ->
                            return! Error(LedgerIntegrityError "Acknowledgment is not for the current assignment.")
                        | _ -> ()
                        if agent.State <> "Ready" || agent.FocusAttemptId <> attempt.AttemptId then
                            return! Error(StaleEntityVersion "Agent has not been assigned this attempt for startup.")
                        if not (String.IsNullOrWhiteSpace agent.ActiveAttemptId) then
                            return! Error(AgentBusy "Agent is already Working.")
                        return
                            [ change (attemptPath taskId attempt.AttemptId) (Some attemptOid)
                                { attempt with Version = attempt.Version + 1; Phase = "Started" }
                              change (agentPath ownerId) (Some agentOid)
                                (enterState "Working" agent |> associateSession sessionId |> fun updated -> { updated with Version = agent.Version + 1; StateReason = $"Started task {taskId}"; StateSequence = snapshot.Header.NextSequence }) ]
                            @ (if existingAcknowledgment.IsNone then
                                   [ change (receiptPath taskId attempt.AttemptId generation) None
                                       { TaskId = taskId; AttemptId = attempt.AttemptId
                                         Generation = generation; OwnerAgentId = ownerId
                                         AcknowledgedSequence = snapshot.Header.NextSequence
                                         SpecBlobOid = issuance.SpecBlobOid
                                         IssueSequence = issuance.IssuedSequence } ]
                               else [])
                    })


    let beginAttempt binding operationId taskId ownerId expectedVersion generation =
        beginAttemptWithSession binding operationId taskId ownerId expectedVersion generation ""

    // Agent identity, assignment ownership, versions, and canonical ledger state
    // determine validity. Provider session identifiers are not execution fences.
    let yieldAgentWithSession binding operationId taskId ownerId expectedAttemptVersion expectedAgentVersion
                   generation summary nextAction sessionId =
        if not (validTaskId taskId) || not (validAgent ownerId)
           || expectedAttemptVersion < 0 || expectedAgentVersion < 0 || generation < 0
           || not (validText 8192 summary) || not (validText 8192 nextAction) then
            Error(InvalidBinding "Yield requires current versions and a substantive checkpoint/next action.")
        else
            LedgerStore.transact binding
                (intent operationId ownerId "YieldAgent"
                    {| taskId = taskId; ownerId = ownerId
                       expectedAttemptVersion = expectedAttemptVersion
                       expectedAgentVersion = expectedAgentVersion; generation = generation
                       summary = summary; nextAction = nextAction |})
                (fun snapshot ->
                    result {
                        let! task, _, attempt, attemptOid = getAttempt binding snapshot taskId
                        let! agent, agentOid = required<ReviewAgent> binding snapshot (agentPath ownerId)
                        let generation = if generation = 0 then attempt.AssignmentGeneration else generation
                        if task.Lifecycle <> "InProgress" || attempt.OwnerAgentId <> ownerId
                           || (expectedAttemptVersion <> 0 && attempt.Version <> expectedAttemptVersion)
                           || attempt.AssignmentGeneration <> generation
                           || attempt.Phase <> "Started" || agent.State <> "Working"
                           || (expectedAgentVersion <> 0 && agent.Version <> expectedAgentVersion)
                           || agent.ActiveAttemptId <> attempt.AttemptId
                           || agent.FocusAttemptId <> attempt.AttemptId then
                            return! Error(StaleEntityVersion "Yield requires the current Working assignment and slot.")
                        let checkpoint =
                            { TaskId = taskId; AttemptId = attempt.AttemptId
                              OwnerAgentId = ownerId; AssignmentGeneration = generation
                              Sequence = snapshot.Header.NextSequence
                              Summary = summary; NextAction = nextAction; Kind = "Yield"
                              SourceCommit = null; Evidence = null; EvidenceStatus = "NotProvided" }
                        return
                            [ change (checkpointPath taskId attempt.AttemptId snapshot.Header.NextSequence) None checkpoint
                              change (attemptPath taskId attempt.AttemptId) (Some attemptOid)
                                { attempt with Version = attempt.Version + 1; Phase = "Started" }
                              change (agentPath ownerId) (Some agentOid)
                                (enterState "Ready" agent |> fun updated -> { updated with Version = agent.Version + 1; StateReason = nextAction; StateSequence = snapshot.Header.NextSequence }) ]
                    })

    let yieldAgent binding operationId taskId ownerId expectedAttemptVersion expectedAgentVersion generation summary nextAction =
        yieldAgentWithSession binding operationId taskId ownerId expectedAttemptVersion expectedAgentVersion
            generation summary nextAction ""

    // Existing sessions asking to resume an already-Working attempt only need
    // the current association context. No ledger mutation or timer reset is needed.
    // This is advisory, not a general-purpose session write fence.
    let inspectWorkingResumeSession binding taskId ownerId generation sessionId =
        if String.IsNullOrWhiteSpace sessionId then Ok None
        else result {
            let! snapshot = LedgerStore.fetch binding
            let! task, _, attempt, _ = getAttempt binding snapshot taskId
            let! agent, _ = required<ReviewAgent> binding snapshot (agentPath ownerId)
            if task.Lifecycle = "InProgress"
               && attempt.OwnerAgentId = ownerId
               && (generation = 0 || attempt.AssignmentGeneration = generation)
               && agent.State = "Working"
               && attempt.Phase = "Started"
               && agent.FocusAttemptId = attempt.AttemptId
               && agent.ActiveAttemptId = attempt.AttemptId
               && (agent.ActiveSessionId = sessionId
                   || (not (isNull (box agent.AssociatedSessionIds))
                       && List.contains sessionId agent.AssociatedSessionIds)) then
                let isCurrent = agent.ActiveSessionId = sessionId
                return Some
                    {| status = if isCurrent then "AlreadyActive" else "SupersededSession"
                       message =
                           if isCurrent then "This session is already active. No recovery or timer reset was recorded."
                           else "A newer session is active. Reread the agent assignment before attempting recovery."
                       activeSessionId = agent.ActiveSessionId
                       associatedSessionIds = agent.AssociatedSessionIds
                       agentState = agent.State
                       taskId = taskId
                       attemptId = attempt.AttemptId
                       assignmentGeneration = attempt.AssignmentGeneration
                       head = snapshot.Commit |}
            else return None
        }

    let private resumeAgentCore binding operationId taskId ownerId expectedAttemptVersion expectedAgentVersion generation sessionId allowWorkingContinuation =
        if not (validTaskId taskId) || not (validAgent ownerId)
           || expectedAttemptVersion < 0 || expectedAgentVersion < 0 || generation < 0 then
            Error(InvalidBinding "Invalid ResumeAgent version or generation.")
        else
            LedgerStore.transact binding
                (intent operationId ownerId "ResumeAgent"
                    {| taskId = taskId; ownerId = ownerId
                       expectedAttemptVersion = expectedAttemptVersion
                       expectedAgentVersion = expectedAgentVersion; generation = generation
                       sessionId = sessionId |})
                (fun snapshot ->
                    result {
                        let! task, _, attempt, attemptOid = getAttempt binding snapshot taskId
                        let! agent, agentOid = required<ReviewAgent> binding snapshot (agentPath ownerId)
                        let generation = if generation = 0 then attempt.AssignmentGeneration else generation
                        if task.Lifecycle <> "InProgress" || attempt.OwnerAgentId <> ownerId
                           || (expectedAttemptVersion <> 0 && attempt.Version <> expectedAttemptVersion)
                           || attempt.AssignmentGeneration <> generation
                           || (expectedAgentVersion <> 0 && agent.Version <> expectedAgentVersion)
                           || agent.FocusAttemptId <> attempt.AttemptId then
                            return! Error(StaleEntityVersion "Current task/attempt/owner changed; reread assignment.")
                        let! issuance = requiredIssue binding snapshot taskId attempt
                        if agent.State = "Ready" && attempt.Phase = "Queued"
                           && String.IsNullOrWhiteSpace agent.ActiveAttemptId then
                            // Current assignment generation and ledger ownership
                            // govern start eligibility, not a provider session.
                            // Starting a newly assigned attempt is now an ordinary
                            // ResumeAgent transition. Preserve the original BeginAttempt
                            // behavior: validate and atomically record the assignment receipt.
                            let! existingAcknowledgment =
                                read<AssignmentReceipt> binding snapshot
                                    (receiptPath taskId attempt.AttemptId generation)
                            match existingAcknowledgment with
                            | Some (ack, _) when
                                ack.Generation <> generation
                                || ack.SpecBlobOid <> issuance.SpecBlobOid
                                || ack.IssueSequence <> issuance.IssuedSequence
                                || ack.OwnerAgentId <> ownerId ->
                                return! Error(LedgerIntegrityError "Acknowledgment is not for the current assignment.")
                            | _ -> ()
                            return
                                [ change (attemptPath taskId attempt.AttemptId) (Some attemptOid)
                                    { attempt with Version = attempt.Version + 1; Phase = "Started" }
                                  change (agentPath ownerId) (Some agentOid)
                                    (enterState "Working" agent |> associateSession sessionId
                                     |> fun updated ->
                                         { updated with Version = agent.Version + 1
                                                        StateReason = $"Started task {taskId}"
                                                        StateSequence = snapshot.Header.NextSequence }) ]
                                @ (if existingAcknowledgment.IsNone then
                                       [ change (receiptPath taskId attempt.AttemptId generation) None
                                           { TaskId = taskId; AttemptId = attempt.AttemptId
                                             Generation = generation; OwnerAgentId = ownerId
                                             AcknowledgedSequence = snapshot.Header.NextSequence
                                             SpecBlobOid = issuance.SpecBlobOid
                                             IssueSequence = issuance.IssuedSequence } ]
                                   else [])
                        elif agent.State = "Ready" && attempt.Phase = "Started"
                           && String.IsNullOrWhiteSpace agent.ActiveAttemptId then
                            return
                                [ change (attemptPath taskId attempt.AttemptId) (Some attemptOid)
                                    { attempt with Version = attempt.Version + 1; Phase = "Started" }
                                  change (agentPath ownerId) (Some agentOid)
                                    (enterState "Working" agent |> associateSession sessionId
                                     |> fun updated ->
                                        { updated with
                                            Version = agent.Version + 1
                                            StateReason = $"Resumed task {taskId}"
                                            StateSequence = snapshot.Header.NextSequence }) ]
                        elif allowWorkingContinuation
                             && agent.State = "Working" && attempt.Phase = "Started"
                             && agent.ActiveAttemptId = attempt.AttemptId then
                            // An explicit resume continues the current agent slot,
                            // regardless of the model provider or session metadata.
                            let updated =
                                { (associateSession sessionId agent) with
                                    Version = agent.Version + 1
                                    WorkingTurnStartedAtUtc = DateTimeOffset.UtcNow.ToString("O")
                                    ActivityLeaseUntilUtc = null
                                    StateReason = $"Resumed task {taskId}"
                                    StateSequence = snapshot.Header.NextSequence }
                            return
                                [ change (agentPath ownerId) (Some agentOid) updated
                                  change ($"tasks/{taskId}/attempts/{attempt.AttemptId}/events/{snapshot.Header.NextSequence}.json") None
                                    {| taskId = taskId; attemptId = attempt.AttemptId
                                       ownerAgentId = ownerId; assignmentGeneration = generation
                                       kind = "AgentTurnResumed"
                                       occurredAtUtc = DateTimeOffset.UtcNow.ToString("O")
                                       reason = "Explicit resume of Working agent"
                                       sequence = snapshot.Header.NextSequence |} ]
                        else
                            return! Error(StaleEntityVersion "Resume requires a Ready queued/started attempt or a Working attempt on the same assignment.")
                    })

    let resumeAgentWithSession binding operationId taskId ownerId expectedAttemptVersion expectedAgentVersion generation sessionId =
        resumeAgentCore binding operationId taskId ownerId expectedAttemptVersion expectedAgentVersion generation sessionId true

    // Automatic activation is only a Ready -> Working transition. Repeated or
    // concurrent calls must never restart a Working agent's clock.
    let activateReadyAgent binding operationId taskId agentId =
        resumeAgentCore binding operationId taskId agentId 0 0 0 "" false

    // Every admitted Working tool call is protected by a bounded canonical
    // lease. The client renews at ingress only when the lease approaches expiry
    // and periodically during long-running commands. An operator correction
    // must reject live leases, even when its entity versions are otherwise
    // current. The lease does not restart the turn or create worker evidence.
    let private activityLeaseDuration = TimeSpan.FromMinutes(5.)
    let private activityLeaseRenewalMargin = TimeSpan.FromMinutes(2.)

    let private activityLeaseRemaining now (agent: ReviewAgent) =
        let mutable expires = Unchecked.defaultof<DateTimeOffset>
        if String.IsNullOrWhiteSpace agent.ActivityLeaseUntilUtc then TimeSpan.Zero
        elif DateTimeOffset.TryParse(agent.ActivityLeaseUntilUtc, &expires) then expires - now
        else TimeSpan.MaxValue // A malformed canonical lease is not proof of inactivity.

    let workingActivityLeaseSufficient now (agent: ReviewAgent) =
        let mutable expires = Unchecked.defaultof<DateTimeOffset>
        agent.State = "Working"
        && not (String.IsNullOrWhiteSpace agent.ActivityLeaseUntilUtc)
        && DateTimeOffset.TryParse(agent.ActivityLeaseUntilUtc, &expires)
        && expires - now > activityLeaseRenewalMargin

    let internal recordWorkingActivityAt binding operationId agentId taskId attemptId (now: DateTimeOffset) =
        if not (validAgent agentId) || not (validTaskId taskId)
           || String.IsNullOrWhiteSpace attemptId then
            Error(InvalidBinding "Working activity requires an exact agent assignment.")
        else
            LedgerStore.transact binding
                (intent operationId agentId "RenewWorkingActivityLease"
                    {| agentId = agentId; taskId = taskId; attemptId = attemptId |})
                (fun snapshot -> result {
                    let! task, _, attempt, _ = getAttempt binding snapshot taskId
                    let! agent, agentOid = required<ReviewAgent> binding snapshot (agentPath agentId)
                    // A task may be closed independently while its owner is
                    // still Working and finishing the attempt. Correction
                    // remains InProgress-only, but ordinary tool ingress must
                    // not break after administrative closure.
                    if not (List.contains task.Lifecycle [ "InProgress"; "Closed" ])
                       || attempt.AttemptId <> attemptId
                       || attempt.OwnerAgentId <> agentId
                       || attempt.Phase <> "Started"
                       || agent.State <> "Working"
                       || not (matchesAssignment taskId attempt agent)
                       || agent.ActiveAttemptId <> attemptId then
                        return! Error(StaleEntityVersion "Working assignment changed before lease renewal.")
                    // The version fences any stale operator snapshot, and the
                    // deadline blocks corrections during the whole lease.
                    return [ change (agentPath agentId) (Some agentOid)
                                 { agent with Version = agent.Version + 1
                                              ActivityLeaseUntilUtc = (now + activityLeaseDuration).ToString("O") } ]
                })

    let recordWorkingActivity binding operationId agentId taskId attemptId =
        recordWorkingActivityAt binding operationId agentId taskId attemptId DateTimeOffset.UtcNow

    // Durable in-flight reservations protect potentially long-running or
    // mutating commands independently of the expiring cheap-read lease.
    // They do NOT change ReviewAgent.Version, so owner commands with exact
    // expected versions remain valid. No TTL: a failed release must block
    // administrative correction, not silently declare running work stale.
    let beginInFlightAgentOperation binding operationId agentId taskId attemptId (reservationId: Guid) =
        if not (validAgent agentId) || not (validTaskId taskId)
           || String.IsNullOrWhiteSpace attemptId || reservationId = Guid.Empty then
            Error(InvalidBinding "In-flight activity requires exact agent, task, attempt and reservation ID.")
        else
            LedgerStore.transact binding
                (intent operationId agentId "BeginInFlightAgentOperation"
                    {| agentId = agentId; taskId = taskId; attemptId = attemptId
                       reservationId = reservationId.ToString("N") |})
                (fun snapshot -> result {
                    let! task, _, attempt, _ = getAttempt binding snapshot taskId
                    let! agent, _ = required<ReviewAgent> binding snapshot (agentPath agentId)
                    if not (List.contains task.Lifecycle [ "InProgress"; "Closed" ])
                       || attempt.AttemptId <> attemptId
                       || attempt.OwnerAgentId <> agentId
                       || attempt.Phase <> "Started"
                       || agent.State <> "Working"
                       || not (matchesAssignment taskId attempt agent) then
                        return! Error(StaleEntityVersion "Assignment changed before in-flight activity admission.")
                    let path = inFlightPath agentId
                    let! existing = read<AgentInFlightReservations> binding snapshot path
                    let entries, oid =
                        match existing with
                        | None -> [], None
                        | Some (record, oid) ->
                            (if record.AgentId = agentId && not (isNull (box record.Operations))
                             then record.Operations else []), Some oid
                    match existing with
                    | Some (record, _) when record.AgentId <> agentId
                                           || isNull (box record.Operations) ->
                        return! Error(LedgerIntegrityError "Invalid in-flight reservation record.")
                    | _ -> ()
                    if entries.Length >= 128 || entries |> List.exists (fun entry ->
                           entry.Id = reservationId.ToString("N")) then
                        return! Error(StaleEntityVersion "In-flight reservation already exists or limit reached.")
                    let entry =
                        { Id = reservationId.ToString("N"); TaskId = taskId; AttemptId = attemptId
                          StartedAtUtc = DateTimeOffset.UtcNow.ToString("O") }
                    let previousUntil =
                        existing |> Option.map (fun (value, _) -> value.RecentlyCompletedUntilUtc)
                                 |> Option.defaultValue null
                    return [ change path oid
                                 { AgentId = agentId; Operations = entries @ [ entry ]
                                   RecentlyCompletedUntilUtc = previousUntil } ]
                })

    let internal endInFlightAgentOperationAt binding operationId agentId (reservationId: Guid) (now: DateTimeOffset) =
        if not (validAgent agentId) || reservationId = Guid.Empty then
            Error(InvalidBinding "Releasing activity requires exact agent and reservation.")
        else
            LedgerStore.transact binding
                (intent operationId agentId "EndInFlightAgentOperation"
                    {| agentId = agentId; reservationId = reservationId.ToString("N") |})
                (fun snapshot -> result {
                    let path = inFlightPath agentId
                    let! record, oid =
                        required<AgentInFlightReservations> binding snapshot path
                    if record.AgentId <> agentId
                       || isNull (box record.Operations) then
                        return! Error(LedgerIntegrityError "Invalid in-flight reservation record.")
                    if not (record.Operations |> List.exists
                               (fun entry -> entry.Id = reservationId.ToString("N"))) then
                        return! Error(StaleEntityVersion "In-flight reservation already released.")
                    let remaining =
                        record.Operations
                        |> List.filter (fun entry -> entry.Id <> reservationId.ToString("N"))
                    return [ change path (Some oid)
                                 { record with Operations = remaining
                                               RecentlyCompletedUntilUtc =
                                                   (now.AddMinutes(5.)).ToString("O") } ]
                })

    let endInFlightAgentOperation binding operationId agentId reservationId =
        endInFlightAgentOperationAt binding operationId agentId reservationId DateTimeOffset.UtcNow

    let resumeAgent binding operationId taskId ownerId expectedAttemptVersion expectedAgentVersion generation =
        resumeAgentWithSession binding operationId taskId ownerId expectedAttemptVersion expectedAgentVersion generation ""

    let completeAgentWorkWithSession binding operationId taskId ownerId expectedAttemptVersion expectedAgentVersion
                          generation summary sessionId =
        if not (validTaskId taskId) || not (validAgent ownerId)
           || expectedAttemptVersion < 0 || expectedAgentVersion < 0 || generation < 0
           || not (validText 8192 summary) then
            Error(InvalidBinding "Completion requires a summary and exact assignment versions.")
        else
            LedgerStore.transact binding
                (intent operationId ownerId "CompleteAgentWork"
                    {| taskId = taskId; ownerId = ownerId
                       expectedAttemptVersion = expectedAttemptVersion
                       expectedAgentVersion = expectedAgentVersion; generation = generation; summary = summary |})
                (fun snapshot ->
                    result {
                        let! task, _, attempt, attemptOid = getAttempt binding snapshot taskId
                        let! agent, agentOid = required<ReviewAgent> binding snapshot (agentPath ownerId)
                        let generation = if generation = 0 then attempt.AssignmentGeneration else generation
                        let validPhase =
                            (agent.State = "Working" && attempt.Phase = "Started"
                             && agent.ActiveAttemptId = attempt.AttemptId)
                            || (agent.State = "Ready" && attempt.Phase = "Started"
                                && String.IsNullOrWhiteSpace agent.ActiveAttemptId)
                        if not (List.contains task.Lifecycle [ "InProgress"; "Closed" ])
                           || attempt.OwnerAgentId <> ownerId
                           || (expectedAttemptVersion <> 0 && attempt.Version <> expectedAttemptVersion)
                           || attempt.AssignmentGeneration <> generation
                           || (expectedAgentVersion <> 0 && agent.Version <> expectedAgentVersion)
                           || agent.FocusAttemptId <> attempt.AttemptId || not validPhase then
                            return! Error(StaleEntityVersion "Only the current Working or Ready agent can complete this assignment.")
                        let checkpoint =
                            { TaskId = taskId; AttemptId = attempt.AttemptId
                              OwnerAgentId = ownerId; AssignmentGeneration = generation
                              Sequence = snapshot.Header.NextSequence
                              Summary = summary
                              NextAction = "Assignment completed"
                              Kind = "Completion"
                              SourceCommit = null; Evidence = null; EvidenceStatus = "NotProvided" }
                        return
                            [ change (checkpointPath taskId attempt.AttemptId snapshot.Header.NextSequence) None checkpoint
                              change (attemptPath taskId attempt.AttemptId) (Some attemptOid)
                                { attempt with Version = attempt.Version + 1; Phase = "Finished" }
                              change (agentPath ownerId) (Some agentOid)
                                (enterState "Completed" agent |> fun updated ->
                                    { updated with Version = agent.Version + 1
                                                   StateReason = summary; StateSequence = snapshot.Header.NextSequence }) ]

                    })

    let completeAgentWork binding operationId taskId ownerId expectedAttemptVersion expectedAgentVersion generation summary =
        completeAgentWorkWithSession binding operationId taskId ownerId expectedAttemptVersion expectedAgentVersion
            generation summary ""

    // A progress checkpoint never yields the Working execution slot. Its
    // source/test evidence is owner-reported, not independently verified.
    // Diagnose invalid fields independently so an unbound audit agent does
    // not mistake oversized evidence for a missing source commit.
    let private checkpointInputError taskId ownerId expectedAttemptVersion expectedAgentVersion
                                     generation summary nextAction (sourceCommit: string) (evidence: string) =
        let textError name (value: string) =
            if String.IsNullOrWhiteSpace value then
                Some(InvalidBinding $"CheckpointAttempt {name} must contain non-whitespace text.")
            elif Encoding.UTF8.GetByteCount value > 8192 then
                Some(InvalidBinding $"CheckpointAttempt {name} exceeds 8192 UTF-8 bytes.")
            else None
        if not (validTaskId taskId) then Some(InvalidBinding "CheckpointAttempt taskId is invalid.")
        elif not (validAgent ownerId) then Some(InvalidBinding "CheckpointAttempt agentId is invalid.")
        elif expectedAttemptVersion < 0 then Some(InvalidBinding "CheckpointAttempt expectedAttemptVersion cannot be negative.")
        elif expectedAgentVersion < 0 then Some(InvalidBinding "CheckpointAttempt expectedAgentVersion cannot be negative.")
        elif generation < 0 then Some(InvalidBinding "CheckpointAttempt assignmentGeneration cannot be negative.")
        elif (textError "summary" summary).IsSome then textError "summary" summary
        elif (textError "nextAction" nextAction).IsSome then textError "nextAction" nextAction
        elif not (isNull evidence) && Encoding.UTF8.GetByteCount evidence > 8192 then
            Some(InvalidBinding "CheckpointAttempt evidence exceeds 8192 UTF-8 bytes; store the full report separately and pass a bounded reference.")
        elif not (String.IsNullOrEmpty sourceCommit) && not (LedgerStore.validSourceCommitOid sourceCommit) then
            Some(InvalidBinding "CheckpointAttempt sourceCommit must be empty or a 40/64-character lowercase Git commit OID.")
        else None

    let checkpointAttemptWithSession binding operationId taskId ownerId expectedAttemptVersion
                          expectedAgentVersion generation summary nextAction sourceCommit evidence sessionId =
        match checkpointInputError taskId ownerId expectedAttemptVersion expectedAgentVersion
                                   generation summary nextAction sourceCommit evidence with
        | Some error -> Error error
        | None ->
            LedgerStore.transact binding
                (intent operationId ownerId "CheckpointAttempt"
                    {| taskId = taskId; ownerId = ownerId; expectedAttemptVersion = expectedAttemptVersion
                       expectedAgentVersion = expectedAgentVersion; generation = generation
                       summary = summary; nextAction = nextAction
                       sourceCommit = sourceCommit; evidence = evidence |})
                (fun snapshot ->
                    result {
                        let! task, _, attempt, attemptOid = getAttempt binding snapshot taskId
                        let generation = if generation = 0 then attempt.AssignmentGeneration else generation
                        if task.Lifecycle <> "InProgress" then
                            return! Error(StaleEntityVersion "CheckpointAttempt task.lifecycle must be InProgress.")
                        if attempt.OwnerAgentId <> ownerId then
                            return! Error(StaleEntityVersion "CheckpointAttempt agentId is not the current attempt owner.")
                        if attempt.Phase <> "Started" then
                            return! Error(StaleEntityVersion "CheckpointAttempt attempt.phase must be Started.")
                        if expectedAttemptVersion <> 0 && attempt.Version <> expectedAttemptVersion then
                            return! Error(StaleEntityVersion "CheckpointAttempt expectedAttemptVersion differs from the current attempt version.")
                        if attempt.AssignmentGeneration <> generation then
                            return! Error(StaleEntityVersion "CheckpointAttempt assignmentGeneration differs from the current assignment generation.")
                        let! agent, _ = required<ReviewAgent> binding snapshot (agentPath ownerId)
                        if expectedAgentVersion <> 0 && agent.Version <> expectedAgentVersion then
                            return! Error(StaleEntityVersion "CheckpointAttempt expectedAgentVersion differs from the current agent version.")
                        if agent.State <> "Working" then
                            return! Error(StaleEntityVersion "CheckpointAttempt agent.state must be Working.")
                        if agent.ActiveAttemptId <> attempt.AttemptId then
                            return! Error(StaleEntityVersion "CheckpointAttempt agent.activeAttemptId differs from the task attempt.")
                        let! _ = requiredIssue binding snapshot taskId attempt
                        if not (String.IsNullOrWhiteSpace sourceCommit) then
                            do! LedgerStore.verifySourceObject binding sourceCommit
                        let checkpoint =
                            { TaskId = taskId; AttemptId = attempt.AttemptId
                              OwnerAgentId = ownerId; AssignmentGeneration = generation
                              Sequence = snapshot.Header.NextSequence; Summary = summary
                              NextAction = nextAction; Kind = "Progress"
                              SourceCommit = sourceCommit; Evidence = evidence
                              EvidenceStatus = if String.IsNullOrWhiteSpace evidence then "NotProvided" else "OwnerReported" }
                        return
                            [ change (checkpointPath taskId attempt.AttemptId snapshot.Header.NextSequence) None checkpoint
                              change (attemptPath taskId attempt.AttemptId) (Some attemptOid)
                                { attempt with Version = attempt.Version + 1 } ]
                    })

    let checkpointAttempt binding operationId taskId ownerId expectedAttemptVersion expectedAgentVersion generation summary nextAction sourceCommit evidence =
        checkpointAttemptWithSession binding operationId taskId ownerId expectedAttemptVersion expectedAgentVersion
            generation summary nextAction sourceCommit evidence ""

    let getCheckpoint binding taskId sequence =
        if not (validTaskId taskId) || sequence < 1L then
            Error(InvalidBinding "Invalid checkpoint sequence.")
        else result {
            let! snapshot = LedgerStore.fetch binding
            let! _, _, attempt, _ = getAttempt binding snapshot taskId
            let! checkpoint, _ =
                required<AgentCheckpoint> binding snapshot
                    (checkpointPath taskId attempt.AttemptId sequence)
            return snapshot.Commit, checkpoint
        }

    let parkAgentAs binding actor operationId agentId expectedAgentVersion reason =
        if not (validAgent agentId) || expectedAgentVersion < 1 || not (validText 2048 reason) then
            Error(InvalidBinding "Parking requires an agent version and reason.")
        else
            LedgerStore.transact binding
                (intent operationId actor "ParkAgent"
                    {| agentId = agentId; expectedAgentVersion = expectedAgentVersion
                       reason = reason |})
                (fun snapshot ->
                    result {
                        let! agent, agentOid = required<ReviewAgent> binding snapshot (agentPath agentId)
                        if agent.Version <> expectedAgentVersion
                           || not (List.contains agent.State [ "NeedsWork"; "Parked" ])
                           || not (isNull (box agent.CurrentAssignment)) then
                            return! Error(StaleEntityVersion "Park may not interrupt an assigned or Working agent.")
                        return
                            [ change (agentPath agentId) (Some agentOid)
                                { (enterState "Parked" agent) with
                                    Version = agent.Version + 1
                                    StateReason = reason
                                    StateSequence = snapshot.Header.NextSequence
                                } ]
                    })

    // Administrative state changes apply only to unassigned agents.
    // Ready is reserved for assigned attempts and Working is entered by
    // activity, so neither is a directly writable administrative state.
    let setAgentAdministrativeStateAs binding actor operationId agentId targetState expectedAgentVersion reason =
        if targetState = "Parked" then
            parkAgentAs binding actor operationId agentId expectedAgentVersion reason
        elif targetState <> "NeedsWork" || not (validAgent agentId)
             || expectedAgentVersion < 1 || not (validText 2048 reason) then
            Error(InvalidBinding "Administrative state must be NeedsWork or Parked with an exact agent version and reason.")
        else
            LedgerStore.transact binding
                (intent operationId actor "SetAgentState"
                    {| agentId = agentId; targetState = targetState
                       expectedAgentVersion = expectedAgentVersion; reason = reason |})
                (fun snapshot ->
                    result {
                        let! agent, agentOid = required<ReviewAgent> binding snapshot (agentPath agentId)
                        if agent.Version <> expectedAgentVersion
                           || not (List.contains agent.State [ "NeedsWork"; "Parked" ])
                           || not (isNull (box agent.CurrentAssignment)) then
                            return! Error(StaleEntityVersion "Administrative state change cannot interrupt an assignment.")
                        return
                            [ change (agentPath agentId) (Some agentOid)
                                { (enterState "NeedsWork" agent) with
                                    Version = agent.Version + 1
                                    StateReason = reason
                                    StateSequence = snapshot.Header.NextSequence } ]
                    })

    // Source repository identity is an endpoint fingerprint independent from
    // the ledger UUID. An agent's checkout path never enters the owner key.

    let parkAgent binding operationId agentId expectedAgentVersion reason =
        parkAgentAs binding legacyAdministrativeActor operationId agentId expectedAgentVersion reason

    let private verifySourceRepository binding (attempt: ReviewAttempt) =
        result {
            let! source = LedgerStore.sourceRepositoryIdentity binding
            if String.IsNullOrWhiteSpace attempt.SourceRepositoryId
               || attempt.SourceRepositoryId <> source.RepositoryId
               || attempt.SourceRemote <> source.FetchUrl
               || attempt.SourcePushRemote <> source.PushUrl then
                return! Error(SourceUnavailable "Source repository differs from the attempt's approved identity.")
            return source
        }

    let bindAttemptSource binding operationId taskId ownerId expectedVersion generation
                          sourceRef baseOid targetRef allowDetached allowUncommitted =
        if not (validTaskId taskId) || not (validAgent ownerId)
           || not (validBranch sourceRef) || not (validBranch targetRef)
           || sourceRef = "refs/heads/jarvis-orchestration"
           || sourceRef.StartsWith("refs/heads/jarvis-review/", StringComparison.Ordinal) then
            Error(InvalidBinding "Invalid source repository/ref binding.")
        else
            LedgerStore.transact binding
                (intent operationId ownerId "BindAttemptSource"
                    {| taskId = taskId; ownerId = ownerId; expectedVersion = expectedVersion
                       generation = generation; sourceRef = sourceRef; baseOid = baseOid
                       targetRef = targetRef; allowDetached = allowDetached
                       allowUncommitted = allowUncommitted |})
                (fun snapshot ->
                    result {
                        let! _, _, attempt, attemptOid = getAttempt binding snapshot taskId
                        if attempt.OwnerAgentId <> ownerId || attempt.Phase <> "Started"
                           || attempt.Version <> expectedVersion || attempt.AssignmentGeneration <> generation
                           || not (String.IsNullOrEmpty attempt.BaseCommit)
                           || not (String.IsNullOrEmpty attempt.SourceRepositoryId) then
                            return! Error(StaleEntityVersion "Source already pinned or attempt not Working.")
                        let! identity = LedgerStore.sourceRepositoryIdentity binding
                        let! observation = LedgerStore.inspectSourceWorktree binding sourceRef
                        if observation.State = "SourceUnavailable" then
                            return! Error(SourceUnavailable "Working source branch does not exist.")
                        if observation.State = "StaleDetachedCheckout" then
                            return! Error(SourceUnavailable "Detached checkout HEAD differs from the pinned source branch head.")
                        if observation.SelectedWorktreeDetached && not allowDetached then
                            return! Error(SourceUnavailable "Detached checkout requires explicit allowDetached.")
                        if observation.SelectedWorktreeDirty && not allowUncommitted then
                            return! Error(SourceUnavailable "Uncommitted checkout requires explicit allowUncommitted.")
                        do! LedgerStore.verifySourceCommit binding sourceRef observation.SourceHead
                        do! LedgerStore.verifySourceObject binding baseOid
                        do! LedgerStore.verifySourceAncestry binding baseOid observation.SourceHead
                        let! remoteTargetHead = LedgerStore.readRemoteTarget binding targetRef
                        do! LedgerStore.verifySourceAncestry binding baseOid remoteTargetHead
                        return [ change (attemptPath taskId attempt.AttemptId) (Some attemptOid)
                                    { attempt with Version = attempt.Version + 1
                                                   BaseCommit = baseOid; TargetRef = targetRef
                                                   SourceRepositoryId = identity.RepositoryId
                                                   SourceRemote = identity.FetchUrl
                                                   SourcePushRemote = identity.PushUrl
                                                   SourceRef = sourceRef } ]
                    })

    // Source refs are editable work context, not ownership or authorization.
    // Claimed review refs preserve their own original commit independently.
    let rebindAttemptSource binding operationId taskId ownerId expectedVersion generation sourceRef =
        if not (validTaskId taskId) || not (validAgent ownerId)
           || not (validBranch sourceRef)
           || sourceRef = "refs/heads/jarvis-orchestration"
           || sourceRef.StartsWith("refs/heads/jarvis-review/", StringComparison.Ordinal) then
            Error(InvalidBinding "Invalid replacement source ref.")
        else
            LedgerStore.transact binding
                (intent operationId ownerId "RebindAttemptSource"
                    {| taskId = taskId; ownerId = ownerId; expectedVersion = expectedVersion
                       generation = generation; sourceRef = sourceRef |})
                (fun snapshot ->
                    result {
                        let! _, _, attempt, attemptOid = getAttempt binding snapshot taskId
                        let! agent, _ = required<ReviewAgent> binding snapshot (agentPath ownerId)
                        if attempt.OwnerAgentId <> ownerId
                           || attempt.Version <> expectedVersion
                           || attempt.AssignmentGeneration <> generation
                           || agent.FocusAttemptId <> attempt.AttemptId
                           || not ((attempt.Phase = "Started" && agent.State = "Working"
                                    && agent.ActiveAttemptId = attempt.AttemptId)
                                   || (attempt.Phase = "Started" && agent.State = "Ready"
                                       && String.IsNullOrWhiteSpace agent.ActiveAttemptId))
                           || String.IsNullOrWhiteSpace attempt.SourceRepositoryId
                           || String.IsNullOrWhiteSpace attempt.TargetRef then
                            return! Error(StaleEntityVersion "Source context can be updated by the owner or orchestrator while Working or Ready.")
                        let! _ = verifySourceRepository binding attempt
                        // Source context can change regardless of unrelated dirty
                        // or detached worktrees. The referenced branch must exist.
                        let! _ = LedgerStore.sourceBranchHead binding sourceRef
                        return [ change (attemptPath taskId attempt.AttemptId) (Some attemptOid)
                                    { attempt with Version = attempt.Version + 1; SourceRef = sourceRef } ]
                    })

    // Source context belongs to the attempt; independent reviews supply
    // their own optional comparison target.
    let bindReviewBase binding operationId taskId ownerId expectedVersion generation baseOid targetRef =
        if not (validTaskId taskId) || not (validAgent ownerId) || not (validBranch targetRef) then
            Error(InvalidBinding "Invalid source baseline binding.")
        else
            LedgerStore.transact binding
                (intent operationId ownerId "BindReviewBase"
                    {| taskId = taskId; ownerId = ownerId; expectedVersion = expectedVersion
                       generation = generation; baseOid = baseOid; targetRef = targetRef |})
                (fun snapshot ->
                    result {
                        let! _, _, attempt, attemptOid = getAttempt binding snapshot taskId
                        if attempt.OwnerAgentId <> ownerId || attempt.Phase <> "Started"
                           || attempt.Version <> expectedVersion || attempt.AssignmentGeneration <> generation
                           || not (String.IsNullOrEmpty attempt.BaseCommit) then
                            return! Error(StaleEntityVersion "Review baseline already pinned or attempt not Working.")
                        let! identity = LedgerStore.sourceRepositoryIdentity binding
                        do! LedgerStore.verifySourceObject binding baseOid
                        return [ change (attemptPath taskId attempt.AttemptId) (Some attemptOid)
                                    { attempt with Version = attempt.Version + 1; BaseCommit = baseOid
                                                   TargetRef = targetRef
                                                   SourceRepositoryId = identity.RepositoryId
                                                   SourceRemote = identity.FetchUrl
                                                   SourcePushRemote = identity.PushUrl } ]
                    })

    let inspectAttemptSource binding taskId =
        result {
            let! snapshot = LedgerStore.fetch binding
            let! _, _, attempt, _ = getAttempt binding snapshot taskId
            if String.IsNullOrWhiteSpace attempt.SourceRepositoryId then
                return snapshot.Commit, attempt, None
            else
                let! _ = verifySourceRepository binding attempt
                if String.IsNullOrWhiteSpace attempt.SourceRef then
                    return snapshot.Commit, attempt, None
                else
                    let! observation = LedgerStore.inspectSourceWorktree binding attempt.SourceRef
                    return snapshot.Commit, attempt, Some observation
        }

    let reissueQueuedAttemptAs binding actor operationId taskId ownerId expectedAttemptVersion
                             expectedAgentVersion generation reason =
        if not (validTaskId taskId) || not (validAgent ownerId)
           || expectedAttemptVersion < 1 || expectedAgentVersion < 1 || generation < 1
           || not (validText 2048 reason) then
            Error(InvalidBinding "Reissue requires versions, generation and a reason.")
        else
            LedgerStore.transact binding
                (intent operationId actor "ReissueQueuedAttempt"
                    {| taskId = taskId; ownerId = ownerId
                       expectedAttemptVersion = expectedAttemptVersion
                       expectedAgentVersion = expectedAgentVersion
                       generation = generation; reason = reason |})
                (fun snapshot ->
                    result {
                        let! task, _, attempt, attemptOid = getAttempt binding snapshot taskId
                        let! agent, agentOid = required<ReviewAgent> binding snapshot (agentPath ownerId)
                        if task.Lifecycle <> "InProgress" || attempt.OwnerAgentId <> ownerId
                           || attempt.Phase <> "Queued" || attempt.Version <> expectedAttemptVersion
                           || attempt.AssignmentGeneration <> generation
                           then
                            return! Error(StaleEntityVersion "Task is not a current queued reissue.")
                        if agent.Version <> expectedAgentVersion
                           || not (List.contains agent.State [ "Parked"; "NeedsWork" ])
                           || not (String.IsNullOrWhiteSpace agent.ActiveAttemptId) then
                            return! Error(AgentBusy "Cannot replace the current assignment of a Ready or Working agent.")
                        return
                            [ change (attemptPath taskId attempt.AttemptId) (Some attemptOid)
                                { attempt with Version = attempt.Version + 1 }
                              change (agentPath ownerId) (Some agentOid)
                                { (assigned taskId attempt.AttemptId (resetSessions agent)) with
                                    Version = agent.Version + 1
                                    StateReason = reason
                                    StateSequence = snapshot.Header.NextSequence
                                } ]
                    })

    // Explicitly issue new instructions, optionally adopting a new canonical
    // HTML spec, while the same owner is Assigned but has not started work.
    // Prior issuance and acknowledgments stay immutable in ledger history.

    let reissueQueuedAttempt binding operationId taskId ownerId expectedAttemptVersion expectedAgentVersion generation reason =
        reissueQueuedAttemptAs binding legacyAdministrativeActor operationId taskId ownerId expectedAttemptVersion expectedAgentVersion generation reason

    let reissueAssignmentAs binding actor operationId taskId ownerId expectedAttemptVersion
                          expectedAgentVersion generation instructions newSpecHtml =
        if not (validTaskId taskId) || not (validAgent ownerId)
           || expectedAttemptVersion < 1 || expectedAgentVersion < 1 || generation < 1
           || not (validText 8192 instructions)
           || (not (String.IsNullOrEmpty newSpecHtml) && not (validTaskHtml newSpecHtml)) then
            Error(InvalidBinding "Explicit reissue requires current versions, instructions and safe optional HTML.")
        else
            LedgerStore.transact binding
                (intent operationId actor "ReissueAssignment"
                    {| taskId = taskId; ownerId = ownerId
                       expectedAttemptVersion = expectedAttemptVersion
                       expectedAgentVersion = expectedAgentVersion
                       generation = generation; instructions = instructions
                       specSha256 = specDigest newSpecHtml |})
                (fun snapshot ->
                    result {
                        let! task, taskOid, attempt, attemptOid = getAttempt binding snapshot taskId
                        let! agent, agentOid = required<ReviewAgent> binding snapshot (agentPath ownerId)
                        if task.Lifecycle <> "InProgress" || attempt.OwnerAgentId <> ownerId
                           || attempt.Version <> expectedAttemptVersion
                           || attempt.AssignmentGeneration <> generation
                           || not (List.contains attempt.Phase [ "Queued" ])
                           || agent.State <> "Ready"
                           || agent.Version <> expectedAgentVersion
                           || agent.FocusAttemptId <> attempt.AttemptId
                           || not (String.IsNullOrWhiteSpace agent.ActiveAttemptId)
                           then
                            return! Error(StaleEntityVersion "Only a queued, unstarted Ready assignment can be reissued.")
                        let! _ = requiredIssue binding snapshot taskId attempt
                        let! originalSpec, specOid = LedgerStore.readAt binding snapshot (specPath taskId)
                        if originalSpec.IsNone || specOid <> Some attempt.SpecBlobOid then
                            return! Error(LedgerIntegrityError "Current assigned spec no longer matches the pinned blob.")
                        let changingSpec =
                            not (String.IsNullOrEmpty newSpecHtml)
                            && newSpecHtml <> originalSpec.Value
                        let! selectedOid =
                            if changingSpec then LedgerStore.computeBlobOid binding newSpecHtml
                            else Ok attempt.SpecBlobOid
                        let next =
                            { attempt with Version = attempt.Version + 1
                                           Phase = "Queued"
                                           AssignmentGeneration = generation + 1
                                           IssuedSequence = snapshot.Header.NextSequence
                                           SpecBlobOid = selectedOid }
                        return
                            [ change (attemptPath taskId attempt.AttemptId) (Some attemptOid) next
                              change (issuePath taskId attempt.AttemptId (generation + 1)) None
                                (issue taskId next (generation + 1) actor
                                    "ExplicitReissue" instructions snapshot.Header.NextSequence)
                              change (agentPath ownerId) (Some agentOid)
                                { agent with Version = agent.Version + 1
                                             StateReason = instructions
                                             StateSequence = snapshot.Header.NextSequence } ]
                            @ (if changingSpec then
                                   [ { Path = specPath taskId
                                       ExpectedBlobOid = specOid; Content = newSpecHtml }
                                     change (taskPath taskId) (Some taskOid)
                                         { task with Version = task.Version + 1
                                                     SpecVersion = task.SpecVersion + 1 } ]
                               else [])
                    })

    // Operator-authorized repair: a stalled or lost session never causes this
    // transition automatically. Rebind the SAME attempt, advance its generation,
    // retain its source/reviews and issue an explicit operator restart.

    let reissueAssignment binding operationId taskId ownerId expectedAttemptVersion expectedAgentVersion generation instructions newSpecHtml =
        reissueAssignmentAs binding legacyAdministrativeActor operationId taskId ownerId expectedAttemptVersion expectedAgentVersion generation instructions newSpecHtml

    let recoverAgentAssignmentAs binding actor operationId taskId ownerId expectedAttemptVersion
                               expectedAgentVersion generation reason =
        if not (validTaskId taskId) || not (validAgent ownerId)
           || expectedAttemptVersion < 1 || expectedAgentVersion < 1 || generation < 1
           || not (validText 2048 reason) then
            Error(InvalidBinding "Recovery requires exact versions and an explicit reason.")
        else
            LedgerStore.transact binding
                (intent operationId actor "RecoverAgentAssignment"
                    {| taskId = taskId; ownerId = ownerId
                       expectedAttemptVersion = expectedAttemptVersion
                       expectedAgentVersion = expectedAgentVersion
                       generation = generation; reason = reason |})
                (fun snapshot ->
                    result {
                        let! task, _, attempt, attemptOid = getAttempt binding snapshot taskId
                        let! agent, agentOid = required<ReviewAgent> binding snapshot (agentPath ownerId)
                        if task.Lifecycle <> "InProgress" || attempt.OwnerAgentId <> ownerId
                           || attempt.Version <> expectedAttemptVersion
                           || attempt.AssignmentGeneration <> generation
                           || agent.Version <> expectedAgentVersion
                           || agent.FocusAttemptId <> attempt.AttemptId
                           || not (List.contains agent.State [ "Ready"; "Working" ])
                           || not (List.contains attempt.Phase [ "Queued"; "Started" ])
                           || (agent.State = "Working" && agent.ActiveAttemptId <> attempt.AttemptId)
                           || (agent.State <> "Working"
                               && not (String.IsNullOrWhiteSpace agent.ActiveAttemptId)) then
                            return! Error(StaleEntityVersion "Cannot recover a stale or unrelated assignment.")
                        let checkpoint =
                            { TaskId = taskId; AttemptId = attempt.AttemptId
                              OwnerAgentId = ownerId; AssignmentGeneration = generation
                              Sequence = snapshot.Header.NextSequence
                              Summary = reason; NextAction = "Restart the same assignment"
                              Kind = "Recovery"
                              SourceCommit = null; Evidence = null; EvidenceStatus = "NotProvided" }
                        return
                            [ change (checkpointPath taskId attempt.AttemptId snapshot.Header.NextSequence)
                                None checkpoint
                              change (attemptPath taskId attempt.AttemptId) (Some attemptOid)
                                { attempt with Version = attempt.Version + 1
                                               AssignmentGeneration = attempt.AssignmentGeneration + 1
                                               Phase = "Queued"; IssuedSequence = snapshot.Header.NextSequence }
                              change (agentPath ownerId) (Some agentOid)
                                (enterState "Ready" { agent with ActiveSessionId = null; WorkingTurnStartedAtUtc = null } |> fun updated -> { updated with Version = agent.Version + 1; StateReason = reason; StateSequence = snapshot.Header.NextSequence })
                              change (issuePath taskId attempt.AttemptId
                                          (attempt.AssignmentGeneration + 1)) None
                                (issue taskId attempt (attempt.AssignmentGeneration + 1)
                                    actor "Recovery" reason snapshot.Header.NextSequence) ]
                    })

    let recoverAgentAssignment binding operationId taskId ownerId expectedAttemptVersion expectedAgentVersion generation reason =
        recoverAgentAssignmentAs binding legacyAdministrativeActor operationId taskId ownerId expectedAttemptVersion expectedAgentVersion generation reason

    let getTaskWithSpec binding taskId =
        if not (validTaskId taskId) then Error(InvalidBinding "Invalid task ID.")
        else
            result {
                let! snapshot = LedgerStore.fetch binding
                let! task, _ = required<ReviewTask> binding snapshot (taskPath taskId)
                let! html, specOid = LedgerStore.readAt binding snapshot (specPath taskId)
                match html, specOid with
                | None, _ | _, None ->
                    return! Error(LedgerIntegrityError "Task lacks a canonical spec blob.")
                | Some html, Some specOid ->
                    if String.IsNullOrWhiteSpace task.AttemptId then
                        return snapshot, task, None, html, specOid
                    else
                        let! attempt, _ =
                            required<ReviewAttempt> binding snapshot (attemptPath taskId task.AttemptId)
                        return snapshot, task, Some attempt, html, specOid
            }

    // Administrative task lifecycle is distinct from agent Parked and reviewer
    // state. No inactivity timer or stale frontend may implicitly invoke it.
    let private adminEvent actor (task: ReviewTask) (attempt: ReviewAttempt option)
                           (operationId: Guid) sequence action reason nextLifecycle nextPhase nextOwner reviewId reviewDisposition evidence =
        { TaskId = task.TaskId
          AttemptId = attempt |> Option.map (fun a -> a.AttemptId) |> Option.defaultValue null
          OperationId = operationId.ToString("D")
          Sequence = sequence; Action = action; ActorId = actor; Reason = reason
          PreviousLifecycle = task.Lifecycle; NewLifecycle = nextLifecycle
          PreviousPhase = attempt |> Option.map (fun a -> a.Phase) |> Option.defaultValue null
          NewPhase = nextPhase
          PreviousOwnerId = attempt |> Option.map (fun a -> a.OwnerAgentId) |> Option.defaultValue null
          NewOwnerId = nextOwner
          PreviousGeneration = attempt |> Option.map (fun a -> a.AssignmentGeneration) |> Option.defaultValue 0
          NewGeneration = attempt |> Option.map (fun a -> a.AssignmentGeneration) |> Option.defaultValue 0
          ReviewRequestId = reviewId; ReviewDisposition = reviewDisposition; Evidence = evidence
          RequestingAgentId = null; TargetAgentId = null
          PreviousAgentState = null; NewAgentState = null
          PreviousAgentVersion = 0; NewAgentVersion = 0 }

    // Operator-authorized correction of stale Working state. The requesting
    // agent identity is supplied provenance, while actor is the authenticated
    // operator identity. This does not create a checkpoint or reissue work.
    let correctAgentWorkingStateAs binding actor operationId requestingAgentId targetAgentId
                                   taskId attemptId expectedTaskVersion expectedAttemptVersion
                                   expectedAgentVersion generation reason =
        if not (validAgent actor) || not (validAgent requestingAgentId)
           || not (validAgent targetAgentId) || not (validTaskId taskId)
           || String.IsNullOrWhiteSpace attemptId
           || expectedTaskVersion < 1 || expectedAttemptVersion < 1
           || expectedAgentVersion < 1 || generation < 1
           || not (validText 2048 reason) then
            Error(InvalidBinding "Administrative Working-to-Ready correction requires exact identities, versions, generation and reason.")
        else
            LedgerStore.transact binding
                (intent operationId actor "CorrectAgentWorkingState"
                    {| requestingAgentId = requestingAgentId; targetAgentId = targetAgentId
                       taskId = taskId; attemptId = attemptId
                       expectedTaskVersion = expectedTaskVersion
                       expectedAttemptVersion = expectedAttemptVersion
                       expectedAgentVersion = expectedAgentVersion
                       generation = generation; reason = reason |})
                (fun snapshot -> result {
                    let! task, _, attempt, _ = getAttempt binding snapshot taskId
                    let! agent, agentOid =
                        required<ReviewAgent> binding snapshot (agentPath targetAgentId)
                    // A durable reservation fences an admitted operation even
                    // if the network is down and its time-bounded lease has
                    // expired. This read is in the same optimistic Git
                    // transaction as the correction's agent-state update.
                    let! reservations =
                        read<AgentInFlightReservations> binding snapshot (inFlightPath targetAgentId)
                    match reservations with
                    | Some (record, _) when record.AgentId <> targetAgentId
                                           || isNull (box record.Operations) ->
                        return! Error(LedgerIntegrityError "Corrupt target agent reservation record.")
                    | Some (record, _) when not (List.isEmpty record.Operations) ->
                        return! Error(StaleEntityVersion "Cannot correct agent with in-flight operations.")
                    | Some (record, _) when not (String.IsNullOrWhiteSpace record.RecentlyCompletedUntilUtc) ->
                        let mutable recentUntil = Unchecked.defaultof<DateTimeOffset>
                        if not (DateTimeOffset.TryParse(record.RecentlyCompletedUntilUtc, &recentUntil))
                           || recentUntil > DateTimeOffset.UtcNow then
                            return! Error(StaleEntityVersion "Agent recently completed a genuine operation.")
                    | _ -> ()
                    if task.Version <> expectedTaskVersion
                       || task.Lifecycle <> "InProgress"
                       || attempt.AttemptId <> attemptId
                       || attempt.OwnerAgentId <> targetAgentId
                       || attempt.Version <> expectedAttemptVersion
                       || attempt.AssignmentGeneration <> generation
                       || attempt.Phase <> "Started"
                       || agent.Version <> expectedAgentVersion
                       || agent.State <> "Working"
                       || activityLeaseRemaining DateTimeOffset.UtcNow agent > TimeSpan.Zero
                       || not (matchesAssignment taskId attempt agent)
                       || agent.ActiveAttemptId <> attempt.AttemptId then
                        return! Error(StaleEntityVersion "Administrative correction targets a stale or unrelated Working assignment.")
                    let correction =
                        enterState "Ready" agent
                        |> fun updated ->
                            { updated with Version = agent.Version + 1
                                           StateReason = reason
                                           StateSequence = snapshot.Header.NextSequence }
                    let audit =
                        { adminEvent actor task (Some attempt) operationId snapshot.Header.NextSequence
                              "CorrectAgentWorkingState" reason task.Lifecycle attempt.Phase
                              targetAgentId null "Unchanged" null with
                            RequestingAgentId = requestingAgentId
                            TargetAgentId = targetAgentId
                            PreviousAgentState = agent.State
                            NewAgentState = correction.State
                            PreviousAgentVersion = agent.Version
                            NewAgentVersion = correction.Version }
                    return
                        [ change (agentPath targetAgentId) (Some agentOid) correction
                          change (lifecycleEventPath taskId snapshot.Header.NextSequence) None audit ]
                })

    // Only clear a focus that still belongs to this attempt. Never interrupt
    // an owner's newer assignment, or turn agent Parked into an attempt phase.
    let private clearAdministrativeFocus binding snapshot (attempt: ReviewAttempt) expectedAgentVersion reason =
        result {
            let! agent, agentOid = required<ReviewAgent> binding snapshot (agentPath attempt.OwnerAgentId)
            if agent.Version <> expectedAgentVersion then
                return! Error(StaleEntityVersion "Agent version changed before lifecycle operation.")
            if agent.FocusAttemptId <> attempt.AttemptId then
                // This is historical/deferred work. The agent may be Working on
                // something else; explicit retirement must not touch that slot.
                if agent.ActiveAttemptId = attempt.AttemptId then
                    return! Error(LedgerIntegrityError "Agent has active slot without matching focus.")
                return []
            else
                if not (String.IsNullOrWhiteSpace agent.ActiveAttemptId)
                   && agent.ActiveAttemptId <> attempt.AttemptId then
                    return! Error(AgentBusy "Agent is Working on an unrelated attempt.")
                if agent.Execution = "Working"
                   && agent.ActiveAttemptId <> attempt.AttemptId then
                    return! Error(LedgerIntegrityError "Working attempt and agent slot disagree.")
                if agent.State = "Working"
                   && (agent.ActiveAttemptId <> attempt.AttemptId || attempt.Phase <> "Started") then
                    return! Error(LedgerIntegrityError "Agent Working slot disagrees with attempt phase/focus.")
                if agent.Execution <> "Working" && not (String.IsNullOrWhiteSpace agent.ActiveAttemptId) then
                    return! Error(LedgerIntegrityError "Nonworking attempt unexpectedly holds an active slot.")
                return [ change (agentPath attempt.OwnerAgentId) (Some agentOid)
                             (enterState "Completed" agent |> fun updated -> { updated with Version = agent.Version + 1; StateReason = reason; StateSequence = snapshot.Header.NextSequence }) ]
        }

    let changeAttemptAdministrationAs binding actor operationId taskId action expectedTaskVersion expectedAttemptVersion
                                    expectedAgentVersion generation reason =
        if not (validTaskId taskId) || expectedTaskVersion < 1 || expectedAttemptVersion < 1
           || expectedAgentVersion < 1 || generation < 1 || not (validText 2048 reason)
           || not (List.contains action [ "ParkAttempt"; "BlockAttempt"; "ResumeAttempt"; "AbandonAttempt"; "CancelTask" ]) then
            Error(InvalidBinding "Invalid administrative task/attempt transition.")
        else
            LedgerStore.transact binding
                (intent operationId actor action
                    {| taskId = taskId; expectedTaskVersion = expectedTaskVersion
                       expectedAttemptVersion = expectedAttemptVersion
                       expectedAgentVersion = expectedAgentVersion
                       generation = generation; reason = reason |})
                (fun snapshot -> result {
                    let! task, taskOid, attempt, attemptOid = getAttempt binding snapshot taskId
                    if task.Version <> expectedTaskVersion
                       || not (task.Lifecycle = "InProgress"
                               || (task.Lifecycle = "Closed" && action = "AbandonAttempt"))
                       || attempt.Version <> expectedAttemptVersion
                       || attempt.AssignmentGeneration <> generation then
                        return! Error(StaleEntityVersion "Administrative transition targets a stale task, attempt or generation.")
                    let allowed =
                        if action = "ResumeAttempt" then List.contains attempt.Phase [ "Deferred"; "Blocked" ]
                        else List.contains attempt.Phase [ "Queued"; "Started"; "Deferred"; "Blocked"; "Finished" ]
                    if not allowed || (action = "ParkAttempt" || action = "BlockAttempt")
                                      && not (List.contains attempt.Phase [ "Queued"; "Started" ]) then
                        return! Error(StaleEntityVersion "Attempt is not in an administratively actionable phase.")
                    let! agent, agentOid =
                        required<ReviewAgent> binding snapshot (agentPath attempt.OwnerAgentId)
                    if agent.Version <> expectedAgentVersion then
                        return! Error(StaleEntityVersion "Agent version differs from administrative precondition.")
                    let resume = action = "ResumeAttempt"
                    let terminal = action = "CancelTask" || action = "AbandonAttempt"
                    let! focusChanges =
                        if resume then
                            if not (isNull (box agent.CurrentAssignment)) then
                                Error(AgentBusy "Cannot resume deferred/blocked work while its owner has another assignment.")
                            else
                                Ok [ change (agentPath attempt.OwnerAgentId) (Some agentOid)
                                         { (assigned taskId attempt.AttemptId (resetSessions agent)) with
                                             Version = agent.Version + 1
                                             StateReason = reason
                                             StateSequence = snapshot.Header.NextSequence } ]
                        else clearAdministrativeFocus binding snapshot attempt expectedAgentVersion reason
                    let nextPhase =
                        match action with
                        | "ParkAttempt" -> "Deferred" | "BlockAttempt" -> "Blocked"
                        | "ResumeAttempt" -> "Queued" | _ -> "Abandoned"
                    let nextLifecycle =
                        match action with
                        | "CancelTask" -> "Cancelled"
                        | "AbandonAttempt" when task.Lifecycle = "Closed" -> "Closed"
                        | "AbandonAttempt" -> "Open"
                        | _ -> "InProgress"
                    let nextGen = if resume then generation + 1 else generation
                    let nextAttempt =
                        { attempt with Version = attempt.Version + 1; Phase = nextPhase
                                       AssignmentGeneration = nextGen
                                       IssuedSequence = if resume then snapshot.Header.NextSequence else attempt.IssuedSequence
                                       }
                    let audit =
                        { adminEvent actor task (Some attempt) operationId snapshot.Header.NextSequence
                              action reason nextLifecycle nextPhase attempt.OwnerAgentId null "Unchanged" null
                          with NewGeneration = nextGen }
                    let taskUpdates =
                        if terminal && task.Lifecycle <> "Closed" then
                            [ change (taskPath taskId) (Some taskOid)
                                { task with Version = task.Version + 1; Lifecycle = nextLifecycle
                                            AttemptId = if action = "AbandonAttempt" then null else task.AttemptId } ]
                        else []
                    return taskUpdates
                           @ [ change (attemptPath taskId attempt.AttemptId) (Some attemptOid) nextAttempt
                               change (lifecycleEventPath taskId snapshot.Header.NextSequence) None audit ]
                           @ focusChanges
                           @ (if resume then
                                  [ change (issuePath taskId attempt.AttemptId nextGen) None
                                      (issue taskId nextAttempt nextGen actor "AdministrativeResume"
                                          ("Resume administratively parked or blocked attempt: " + reason)
                                          snapshot.Header.NextSequence) ]
                              else [])
                })

    // Open tasks have no attempt/agent to revoke. Cancellation remains a
    // distinct, audited no-source outcome, not a fabricated integration.

    let changeAttemptAdministration binding operationId taskId action expectedTaskVersion expectedAttemptVersion expectedAgentVersion generation reason =
        changeAttemptAdministrationAs binding legacyAdministrativeActor operationId taskId action expectedTaskVersion expectedAttemptVersion expectedAgentVersion generation reason

    let cancelOpenTaskAs binding actor operationId taskId expectedTaskVersion reason =
        if not (validTaskId taskId) || expectedTaskVersion < 1 || not (validText 2048 reason) then
            Error(InvalidBinding "Open-task cancellation requires exact task version and reason.")
        else
            LedgerStore.transact binding
                (intent operationId actor "CancelTask"
                    {| taskId = taskId; expectedTaskVersion = expectedTaskVersion; reason = reason |})
                (fun snapshot -> result {
                    let! task, oid = required<ReviewTask> binding snapshot (taskPath taskId)
                    if task.Version <> expectedTaskVersion || task.Lifecycle <> "Open"
                       || not (String.IsNullOrWhiteSpace task.AttemptId) then
                        return! Error(StaleEntityVersion "Only an unassigned Open task can use open-task cancellation.")
                    return [ change (taskPath taskId) (Some oid)
                                 { task with Version = task.Version + 1; Lifecycle = "Cancelled" }
                             change (lifecycleEventPath taskId snapshot.Header.NextSequence) None
                                 (adminEvent actor task None operationId snapshot.Header.NextSequence
                                     "CancelTask" reason "Cancelled" null null null "None" null) ]
                })

    let cancelOpenTask binding operationId taskId expectedTaskVersion reason =
        cancelOpenTaskAs binding legacyAdministrativeActor operationId taskId expectedTaskVersion reason

    let transferAttemptOwnerAs binding actor operationId taskId newAttemptId newOwner
                             expectedTaskVersion expectedAttemptVersion expectedAgentVersion
                             generation reason =
        if not (validTaskId taskId) || newAttemptId = Guid.Empty
           || not (validAgent newOwner)
           || expectedTaskVersion < 1 || expectedAttemptVersion < 1 || expectedAgentVersion < 1
           || generation < 1 || not (validText 2048 reason) then
            Error(InvalidBinding "Owner transfer requires a registered new owner, new attempt and exact versions.")
        else
            let newId = newAttemptId.ToString("N")
            LedgerStore.transact binding
                (intent operationId actor "TransferOwner"
                    {| taskId = taskId; newAttemptId = newAttemptId; newOwner = newOwner
                       expectedTaskVersion = expectedTaskVersion
                       expectedAttemptVersion = expectedAttemptVersion; expectedAgentVersion = expectedAgentVersion
                       generation = generation; reason = reason |})
                (fun snapshot -> result {
                    let! task, taskOid, attempt, attemptOid = getAttempt binding snapshot taskId
                    if task.Lifecycle <> "InProgress" || task.Version <> expectedTaskVersion
                       || attempt.Version <> expectedAttemptVersion
                       || attempt.AssignmentGeneration <> generation
                       || (attempt.OwnerAgentId = newOwner && attempt.Phase <> "Finished")
                       || not (Set.contains attempt.Phase (Set.ofList
                              [ "Queued"; "Started"; "Deferred"; "Blocked"; "Finished" ])) then
                        return! Error(StaleEntityVersion "Owner transfer requires the current revocable attempt and versions.")
                    // Reviews stay with the task across an explicit handoff. A finished
                    // worker can also be assigned again after a rejected final review.
                    let! successor = read<ReviewAttempt> binding snapshot (attemptPath taskId newId)
                    if successor.IsSome then
                        return! Error(StaleEntityVersion "Transfer attempt ID is not unique.")
                    let! nextOwnerAgent, nextOwnerOid = required<ReviewAgent> binding snapshot (agentPath newOwner)
                    if not (List.contains nextOwnerAgent.State [ "NeedsWork"; "Parked" ])
                       || not (String.IsNullOrWhiteSpace nextOwnerAgent.ActiveAttemptId) then
                        return! Error(AgentBusy "New owner is not idle.")
                    let! previousAgentChanges =
                        clearAdministrativeFocus binding snapshot attempt expectedAgentVersion reason
                    let next =
                        { attempt with AttemptId = newId; Version = 1
                                       OwnerAgentId = newOwner; Phase = "Queued"
                                       AssignmentGeneration = 1; IssuedSequence = snapshot.Header.NextSequence
                                       BaseCommit = attempt.BaseCommit; TargetRef = attempt.TargetRef
                                       SourceRepositoryId = attempt.SourceRepositoryId
                                       SourceRemote = attempt.SourceRemote; SourcePushRemote = attempt.SourcePushRemote
                                       SourceRef = attempt.SourceRef
                                       }
                    let audit =
                        { adminEvent actor task (Some attempt) operationId snapshot.Header.NextSequence
                              "TransferOwner" reason "InProgress" "Queued" newOwner null "Unchanged" null
                          with NewGeneration = 1 }
                    return [ change (taskPath taskId) (Some taskOid)
                                 { task with Version = task.Version + 1; AttemptId = newId }
                             change (attemptPath taskId attempt.AttemptId) (Some attemptOid)
                                 { attempt with Version = attempt.Version + 1; Phase = "Superseded"
                                                }
                             change (attemptPath taskId newId) None next
                             change (issuePath taskId newId 1) None
                                 (issue taskId next 1 actor "OwnershipTransfer"
                                     ("Transfer from " + attempt.OwnerAgentId + ": " + reason)
                                     snapshot.Header.NextSequence)
                             change (agentPath newOwner) (Some nextOwnerOid)
                                 { (assigned taskId newId (resetSessions nextOwnerAgent)) with
                                     Version = nextOwnerAgent.Version + 1
                                     StateReason = reason
                                     StateSequence = snapshot.Header.NextSequence
                                 }
                             change (lifecycleEventPath taskId snapshot.Header.NextSequence) None audit ]
                           @ previousAgentChanges
                })

    // Reopening never resurrects an old generation. The old attempt and
    // review/integration evidence stay addressable by immutable paths.

    let transferAttemptOwner binding operationId taskId newAttemptId newOwner expectedTaskVersion expectedAttemptVersion expectedAgentVersion generation reason =
        transferAttemptOwnerAs binding legacyAdministrativeActor operationId taskId newAttemptId newOwner expectedTaskVersion expectedAttemptVersion expectedAgentVersion generation reason

    let reopenTaskAs binding actor operationId taskId expectedTaskVersion reason =
        if not (validTaskId taskId) || expectedTaskVersion < 1
           || not (validText 2048 reason) then
            Error(InvalidBinding "Reopen requires terminal task version and explanation.")
        else
            LedgerStore.transact binding
                (intent operationId actor "ReopenTask"
                    {| taskId = taskId; expectedTaskVersion = expectedTaskVersion; reason = reason |})
                (fun snapshot -> result {
                    let! task, oid = required<ReviewTask> binding snapshot (taskPath taskId)
                    if task.Version <> expectedTaskVersion
                       || not (List.contains task.Lifecycle [ "Closed"; "Cancelled" ]) then
                        return! Error(StaleEntityVersion "Only a Closed or Cancelled task can reopen.")
                    let! oldAttempt =
                        if String.IsNullOrWhiteSpace task.AttemptId then Ok None
                        else read<ReviewAttempt> binding snapshot (attemptPath taskId task.AttemptId)
                             |> Result.map (Option.map fst)
                    match oldAttempt with
                    | Some attempt when List.contains attempt.Phase [ "Queued"; "Started"; "Deferred"; "Blocked" ] ->
                        return! Error(LedgerIntegrityError "Cannot reopen with unresolved active work or review.")
                    | _ -> ()
                    return [ change (taskPath taskId) (Some oid)
                                 { task with Version = task.Version + 1; Lifecycle = "Open"; AttemptId = null }
                             change (lifecycleEventPath taskId snapshot.Header.NextSequence) None
                                 (adminEvent actor task oldAttempt operationId snapshot.Header.NextSequence
                                     "ReopenTask" reason "Open" null null null "None" null) ]
                })

    let reopenTask binding operationId taskId expectedTaskVersion reason =
        reopenTaskAs binding legacyAdministrativeActor operationId taskId expectedTaskVersion reason

    let getTaskLifecycleHistory binding taskId =
        if not (validTaskId taskId) then Error(InvalidBinding "Invalid lifecycle history task.")
        else result {
            let! snapshot = LedgerStore.fetch binding
            let! _ = required<ReviewTask> binding snapshot (taskPath taskId)
            let! paths = LedgerStore.listAt binding snapshot "tasks"
            let events = paths |> List.filter (fun path ->
                path.StartsWith($"tasks/{taskId}/lifecycle/", StringComparison.Ordinal)
                && path.EndsWith(".json", StringComparison.Ordinal))
            let mutable history = []
            for path in events do
                let! item, _ = required<TaskLifecycleEvent> binding snapshot path
                if item.TaskId <> taskId || path <> lifecycleEventPath taskId item.Sequence then
                    return! Error(LedgerIntegrityError "Invalid lifecycle event identity.")
                history <- item :: history
            return snapshot.Commit, history |> List.sortBy (fun item -> item.Sequence)
        }

    let getTaskAt binding snapshot taskId =
        if not (validTaskId taskId) then Error(InvalidBinding "Invalid task ID.")
        else
            result {
                let! task, _ = required<ReviewTask> binding snapshot (taskPath taskId)
                if String.IsNullOrWhiteSpace task.AttemptId then
                    return task, None
                else
                    let! attempt, _ =
                        required<ReviewAttempt> binding snapshot (attemptPath taskId task.AttemptId)
                    return task, Some attempt
            }

    let getTask binding taskId =
        result {
            let! snapshot = LedgerStore.fetch binding
            let! task, attempt = getTaskAt binding snapshot taskId
            return snapshot, task, attempt
            }

    // Markdown files are regular orphan-ledger Git blobs. The review itself
    // identifies the source snapshot, while these files contain arbitrarily
    // detailed reviewer findings and implementation responses.
    let private requireDocumentReview binding snapshot taskId id =
        result {
            let! review, _ = required<BranchReview> binding snapshot (branchReviewPath taskId id)
            return review.Status, review.OwnerAgentId, review.ReviewerId, review.ReviewerId
        }

    let listReviewDocuments binding taskId requestId =
        if not (validTaskId taskId) || requestId = Guid.Empty then
            Error(InvalidBinding "Invalid review document lookup.")
        else result {
            let! snapshot = LedgerStore.fetch binding
            let id = requestId.ToString("N")
            let! _ = requireDocumentReview binding snapshot taskId id
            let! paths = LedgerStore.listAt binding snapshot "tasks"
            let prefix = $"tasks/{taskId}/reviews/{id}/"
            let mutable documents = []
            for path in paths do
                if path.StartsWith(prefix, StringComparison.Ordinal) then
                    let relative = path.Substring(prefix.Length)
                    if validReviewDocumentPath relative then
                        let! content, oid = LedgerStore.readAt binding snapshot path
                        match content, oid with
                        | Some body, Some blob ->
                            documents <- { Path = relative; BlobOid = blob
                                           SizeBytes = Encoding.UTF8.GetByteCount body } :: documents
                        | _ -> return! Error(LedgerIntegrityError "Missing review document.")
            return snapshot.Commit, documents |> List.sortBy (fun document -> document.Path)
        }

    let getReviewDocument binding taskId requestId documentPath =
        if not (validTaskId taskId) || requestId = Guid.Empty
           || not (validReviewDocumentPath documentPath) then
            Error(InvalidBinding "Invalid review document path.")
        else result {
            let! snapshot = LedgerStore.fetch binding
            let id = requestId.ToString("N")
            let! _ = requireDocumentReview binding snapshot taskId id
            let! content, oid =
                LedgerStore.readAt binding snapshot (reviewDocumentPath taskId id documentPath)
            match content, oid with
            | Some body, Some blob ->
                return snapshot.Commit,
                    { Path = documentPath; BlobOid = blob; Content = body }
            | _ -> return! Error(StaleEntityVersion "Review document does not exist.")
        }

    let putReviewDocument binding operationId taskId requestId actorId documentPath
                          expectedBlobOid (content: string) =
        if not (validTaskId taskId) || requestId = Guid.Empty || not (validAgent actorId)
           || not (validReviewDocumentPath documentPath)
           || isNull content || Encoding.UTF8.GetByteCount content > 512 * 1024 then
            Error(InvalidBinding "Invalid review document path or content (maximum 512 KiB).")
        else
            let id = requestId.ToString("N")
            let expected = if String.IsNullOrWhiteSpace expectedBlobOid then None else Some expectedBlobOid
            LedgerStore.transact binding
                (intent operationId actorId "PutReviewDocument"
                    {| taskId = taskId; requestId = id; documentPath = documentPath
                       expectedBlobOid = expectedBlobOid; contentSha256 = specDigest content |})
                (fun snapshot -> result {
                    let! _ = required<BranchReview> binding snapshot (branchReviewPath taskId id)
                    return [ { Path = reviewDocumentPath taskId id documentPath
                               ExpectedBlobOid = expected; Content = content } ]
                })

    // Derive every queue and slot observation from a single verified ledger commit.
    // Queue membership is never duplicated in agent.json.
    let getAgentWorkingTurnAt binding snapshot agentId =
        if not (validAgent agentId) then Error(InvalidBinding "Invalid Working agent ID.")
        else required<ReviewAgent> binding snapshot (agentPath agentId) |> Result.map fst

    let getAgentWorkingTurn binding agentId =
        LedgerStore.fetch binding
        |> Result.bind (fun snapshot -> getAgentWorkingTurnAt binding snapshot agentId)

    // Resolve agent association from the canonical ledger even after a client
    // restart. This is identity for activity tracking, not evidence of Working.
    let private findAgentBySessionWhere accepts binding sessionId =
        if String.IsNullOrWhiteSpace sessionId then Ok None
        else result {
            let! snapshot = LedgerStore.fetch binding
            let! paths = LedgerStore.listAt binding snapshot "agents"
            let mutable matches = []
            for path in paths do
                let! agent, _ = required<ReviewAgent> binding snapshot path
                if accepts agent
                   && (agent.ActiveSessionId = sessionId
                       || (not (isNull (box agent.AssociatedSessionIds))
                           && List.contains sessionId agent.AssociatedSessionIds)) then
                    matches <- agent :: matches
            // Never guess which agent owns a session if multiple records match.
            return match matches with [ agent ] -> Some agent | _ -> None
        }

    let findAgentBySession binding sessionId =
        findAgentBySessionWhere (fun _ -> true) binding sessionId

    // Existing Working-only lookup remains available for time cutoff guidance.
    let findWorkingAgentBySession binding sessionId =
        findAgentBySessionWhere (fun agent -> agent.State = "Working") binding sessionId

    let getAgentInboxAt binding snapshot agentId =
        if not (validAgent agentId) then Error(InvalidBinding "Invalid inbox agent ID.")
        else
            result {
                let! records = LedgerStore.readAgentInboxRecordsAt binding snapshot
                let! agent = requiredFromRecords<ReviewAgent> records (agentPath agentId)
                let paths = records |> Map.toList |> List.map fst
                let mutable notifications = []
                for path in paths do
                    if path.StartsWith("notifications/", StringComparison.Ordinal)
                       && path.EndsWith(".json", StringComparison.Ordinal) then
                        let! notification = requiredFromRecords<AgentNotification> records path
                        match Guid.TryParseExact(notification.NotificationId, "N") with
                        | true, parsed when path = notificationPath parsed
                                            && validAgent notification.FromAgentId
                                            && validAgent notification.ToAgentId ->
                            if notification.ToAgentId = agentId
                               && String.IsNullOrWhiteSpace notification.AcknowledgedAtUtc then
                                notifications <- notification :: notifications
                        | _ -> return! Error(LedgerIntegrityError "Invalid notification identity.")
                let notifications = notifications |> List.sortBy (fun n -> n.CreatedAtUtc, n.NotificationId)
                let allIntegrationPaths = paths
                let mutable integrationRecords = []
                for path in allIntegrationPaths do
                    if path.Contains("/integrations/", StringComparison.Ordinal)
                       && path.EndsWith(".json", StringComparison.Ordinal) then
                        let! record = requiredFromRecords<IntegrationSubmission> records path
                        match Guid.TryParseExact(record.SubmissionId, "N") with
                        | true, id when path = integrationPath record.TaskId id ->
                            if record.ActorId = agentId
                               || (not (isNull (box agent.CurrentAssignment))
                                   && agent.CurrentAssignment.TaskId = record.TaskId) then
                                integrationRecords <- record :: integrationRecords
                        | _ -> return! Error(LedgerIntegrityError "Invalid integration submission identity.")

                let taskPaths = paths |> List.filter (fun path -> path.EndsWith("/task.json", StringComparison.Ordinal))
                let reviewPaths = paths |> List.filter (fun path -> path.EndsWith("/review.json", StringComparison.Ordinal))
                let mutable failure = None
                let mutable owned = []
                let mutable pending = []
                let mutable available = []
                let mutable feedback = []
                for path in taskPaths do
                    if failure.IsNone then
                        match requiredFromRecords<ReviewTask> records path with
                        | Error error -> failure <- Some error
                        | Ok task ->
                            if not (validTaskId task.TaskId) || path <> taskPath task.TaskId
                               || not (List.contains task.Lifecycle
                                   [ "Open"; "InProgress"; "Implemented"; "Integrated"; "Closed"; "Cancelled" ]) then
                                failure <- Some(LedgerIntegrityError "Invalid task identity or lifecycle in inbox.")
                            elif not (String.IsNullOrWhiteSpace task.AttemptId) then
                                match requiredFromRecords<ReviewAttempt> records (attemptPath task.TaskId task.AttemptId) with
                                | Error error -> failure <- Some error
                                | Ok attempt ->
                                    if attempt.TaskId <> task.TaskId || attempt.AttemptId <> task.AttemptId
                                       || not (List.contains attempt.Phase
                                           [ "Queued"; "Started"; "Finished"; "Deferred"; "Blocked"; "Superseded"; "Abandoned" ]) then
                                        failure <- Some(LedgerIntegrityError "Invalid task/attempt identity or phase in inbox.")
                                    elif attempt.OwnerAgentId = agentId then
                                        owned <- { Task = task; Attempt = attempt } :: owned
                for path in reviewPaths do
                    let! review = requiredFromRecords<BranchReview> records path
                    if not (validTaskId review.TaskId)
                       || path <> branchReviewPath review.TaskId review.ReviewId then
                        failure <- Some(LedgerIntegrityError "Invalid canonical review identity.")
                    else
                        if review.Status = "Unclaimed" then
                            if review.OwnerAgentId = agentId then pending <- review :: pending
                            else available <- review :: available
                        elif review.Status = "Claimed"
                             && (review.OwnerAgentId = agentId || review.ReviewerId = agentId) then
                            pending <- review :: pending
                        if review.OwnerAgentId = agentId && review.DecisionSequence > 0L
                           && not review.Acknowledged
                           && List.contains review.Status [ "Accepted"; "ChangesRequested" ] then
                            feedback <- { TaskId = review.TaskId; AttemptId = review.AttemptId
                                          RequestId = review.ReviewId; OwnerAgentId = review.OwnerAgentId
                                          DecisionSequence = review.DecisionSequence; Status = review.Status
                                          Feedback = null; Acknowledged = false; Version = review.Version } :: feedback
                match failure with
                | Some error -> return! Error error
                | None ->
                    let owned = owned |> List.sortWith (fun left right ->
                        let priority = compare right.Attempt.Priority left.Attempt.Priority
                        if priority <> 0 then priority
                        else
                            let leftKey = left.Attempt.IssuedSequence, left.Task.TaskId, left.Attempt.AttemptId
                            let rightKey = right.Attempt.IssuedSequence, right.Task.TaskId, right.Attempt.AttemptId
                            compare leftKey rightKey)
                    let working =
                        owned |> List.filter (fun entry ->
                            matchesAssignment entry.Task.TaskId entry.Attempt agent
                            && agent.Execution = "Working" && entry.Attempt.Phase = "Started")
                    if working.Length > 1
                       || (match working with
                           | [ item ] -> agent.ActiveAttemptId <> item.Attempt.AttemptId
                           | [] -> not (String.IsNullOrWhiteSpace agent.ActiveAttemptId)
                           | _ -> true) then
                        return! Error(LedgerIntegrityError "Agent slot disagrees with the task's Working attempts.")
                    let active = working |> List.tryHead
                    let focused =
                        owned |> List.tryFind (fun entry ->
                            matchesAssignment entry.Task.TaskId entry.Attempt agent)
                    let focusValid =
                        match agent.State, focused with
                        | "Parked", None | "NeedsWork", None -> active.IsNone
                        | "Ready", Some entry ->
                            List.contains entry.Attempt.Phase [ "Queued"; "Started" ] && active.IsNone
                        | "Working", Some entry ->
                            entry.Attempt.Phase = "Started"
                            && agent.ActiveAttemptId = entry.Attempt.AttemptId
                        | _ -> false
                    if not focusValid then
                        return! Error(LedgerIntegrityError "Agent state/focused attempt mismatch.")
                    let runnable = owned |> List.filter (fun entry ->
                        matchesAssignment entry.Task.TaskId entry.Attempt agent
                        && List.contains entry.Attempt.Phase [ "Queued"; "Started" ]
                        && agent.Execution <> "Working")
                    let parked = owned |> List.filter (fun entry -> entry.Attempt.Phase = "Deferred")
                    let blocked = owned |> List.filter (fun entry -> entry.Attempt.Phase = "Blocked")
                    let reviewOrder (review: BranchReview) =
                        review.CreatedSequence, review.TaskId, review.ReviewId
                    let pending = pending |> List.sortBy reviewOrder
                    let available = available |> List.sortBy reviewOrder
                    return
                        { SnapshotCommit = snapshot.Commit
                          Agent = agent
                          Focused = focused
                          OperatorActionRequired = agent.State = "Ready"
                          NeedsOrchestratorAssignment = agent.State = "NeedsWork"
                          Working = active
                          RunnableQueue = runnable
                          Parked = parked
                          Blocked = blocked
                          PendingReviews = pending
                          AvailableReviews = available
                          UnacknowledgedFeedback = feedback |> List.sortBy (fun notice -> notice.DecisionSequence)
                          PendingNotifications = notifications
                          IntegrationSubmissions = integrationRecords |> List.sortBy (fun item -> item.CreatedSequence)
                          SlotAvailable = isNull (box agent.CurrentAssignment)
                          Unassigned = isNull (box agent.CurrentAssignment) }
            }

    let getAgentInbox binding agentId =
        // Retain pre-fetch validation of malformed agent IDs.
        if not (validAgent agentId) then Error(InvalidBinding "Invalid inbox agent ID.")
        else
            LedgerStore.fetch binding
            |> Result.bind (fun snapshot -> getAgentInboxAt binding snapshot agentId)

    /// Verify both observer registration and target canonical state against
    /// ONE immutable ledger snapshot. No agent activity or state mutation.
    /// The dashboard must not pay two remote fetches per observed agent.
    let getObservedAgentInboxAt binding snapshot observerAgentId targetAgentId =
        result {
            let! _ = getAgentWorkingTurnAt binding snapshot observerAgentId
            return! getAgentInboxAt binding snapshot targetAgentId
        }

    let getBranchAgentInbox binding agentId = getAgentInbox binding agentId

    // Branch-first reviews: one canonical record, with source integration
    // inferred from Git rather than duplicated as ledger state.
    let private branchReviewsForTask binding snapshot taskId =
        result {
            let! paths = LedgerStore.listAt binding snapshot "tasks"
            let prefix = $"tasks/{taskId}/reviews/"
            let mutable found = []
            for path in paths do
                if path.StartsWith(prefix, StringComparison.Ordinal)
                   && path.EndsWith("/review.json", StringComparison.Ordinal) then
                    let! record, _ = required<BranchReview> binding snapshot path
                    if record.TaskId <> taskId || path <> branchReviewPath taskId record.ReviewId then
                        return! Error(LedgerIntegrityError "Invalid branch review identity.")
                    found <- record :: found
            return found |> List.sortBy (fun entry -> entry.CreatedSequence, entry.ReviewId)
        }

    let getBranchReviewAt binding snapshot taskId reviewId =
        if not (validTaskId taskId) || reviewId = Guid.Empty then
            Error(InvalidBinding "Invalid review identifier.")
        else result {
            let! review, _ = required<BranchReview> binding snapshot
                                (branchReviewPath taskId (reviewId.ToString("N")))
            if review.TaskId <> taskId
               || not (String.Equals(review.ReviewId, reviewId.ToString("N"), StringComparison.OrdinalIgnoreCase)) then
                return! Error(LedgerIntegrityError "Canonical review does not match its ledger task/review path.")
            return review
        }

    let getBranchReview binding taskId reviewId =
        result {
            let! snapshot = LedgerStore.fetch binding
            let! review = getBranchReviewAt binding snapshot taskId reviewId
            let! advertised = LedgerStore.inspectRemoteReviewRef binding review.ReviewRef
            return { SnapshotCommit = snapshot.Commit; Review = review
                     HeadCommit = advertised |> Option.defaultValue null }
        }

    // Review publication is an independent operation. Task ID is an optional
    // association/namespace, not a task-phase or owner authorization check.
    let submitBranchReview binding operationId taskId requesterId reviewId
                           sourceRef sourceHead (summary: string) purpose targetRef =
        if not (validTaskId taskId) || reviewId = Guid.Empty || not (validAgent requesterId)
           || (not (String.IsNullOrWhiteSpace sourceRef) && not (validBranch sourceRef))
           || isNull summary || Encoding.UTF8.GetByteCount summary > 4096
           || (not (String.IsNullOrEmpty purpose) && not (validText 256 purpose))
           || (not (String.IsNullOrWhiteSpace targetRef) && not (validBranch targetRef)) then
            Error(InvalidBinding "Invalid review request.")
        else
            let id = reviewId.ToString("N")
            let refName = reviewRef taskId id 1
            let action = intent operationId requesterId "SubmitBranchReview"
                            {| taskId = taskId; reviewId = id; ownerId = requesterId
                               sourceRef = sourceRef; sourceHead = sourceHead; summary = summary
                               kind = purpose; targetRef = targetRef |}
            match LedgerStore.findApplied binding action with
            | Error failure -> Error failure
            | Ok(Some applied) -> Ok applied
            | Ok None -> result {
                let! snapshot = LedgerStore.fetch binding
                let! baseOid =
                    if String.IsNullOrWhiteSpace targetRef then Ok sourceHead
                    else LedgerStore.reviewMergeBase binding targetRef sourceHead
                do! LedgerStore.verifySourceObject binding sourceHead
                let! existing = read<BranchReview> binding snapshot (branchReviewPath taskId id)
                if existing.IsSome then
                    return! Error(StaleEntityVersion "Review ID already exists.")
                do! LedgerStore.publishReviewRef binding refName sourceHead
                let writeResult = LedgerStore.transact binding action (fun current -> result {
                    let! existing = read<BranchReview> binding current (branchReviewPath taskId id)
                    if existing.IsSome then
                        return! Error(StaleEntityVersion "Review ID already exists.")
                    let review =
                        { ReviewId = id; TaskId = taskId; AttemptId = null
                          OwnerAgentId = requesterId; Kind = (if isNull purpose then "" else purpose)
                          TaskRef = sourceRef; ReviewRef = refName; BaseCommit = baseOid
                          TargetRef = targetRef; Status = "Unclaimed"; ReviewerId = null
                          Version = 1; CreatedSequence = current.Header.NextSequence
                          DecisionSequence = 0L; Acknowledged = false }
                    return [ change (branchReviewPath taskId id) None review
                             { Path = reviewDocumentPath taskId id "summary.md"
                               ExpectedBlobOid = None; Content = summary } ]
                })
                match writeResult with
                | Error(StaleEntityVersion _ | ReviewVersionConflict _ | ReviewAlreadyClaimed _) ->
                    LedgerStore.deleteReservedReviewRef binding refName sourceHead |> ignore
                    return! writeResult
                | _ -> return! writeResult
            }

    let replaceBranchReview binding operationId taskId requesterId reviewId expectedVersion
                            sourceRef sourceHead (summary: string) =
        if not (validTaskId taskId) || reviewId = Guid.Empty || not (validAgent requesterId)
           || expectedVersion < 1
           || (not (String.IsNullOrWhiteSpace sourceRef) && not (validBranch sourceRef))
           || isNull summary || Encoding.UTF8.GetByteCount summary > 4096 then
            Error(InvalidBinding "Invalid replacement review.")
        else
            let id = reviewId.ToString("N")
            let refName = reviewRef taskId id (expectedVersion + 1)
            let action = intent operationId requesterId "ReplaceBranchReview"
                            {| taskId = taskId; reviewId = id; requesterId = requesterId
                               expectedVersion = expectedVersion
                               sourceRef = sourceRef; sourceHead = sourceHead; summary = summary |}
            match LedgerStore.findApplied binding action with
            | Error failure -> Error failure
            | Ok(Some applied) -> Ok applied
            | Ok None -> result {
                let! snapshot = LedgerStore.fetch binding
                let! existing, _ = required<BranchReview> binding snapshot (branchReviewPath taskId id)
                if existing.Version <> expectedVersion || existing.Status <> "Unclaimed" then
                    return! Error(ReviewAlreadyClaimed "Review is no longer unclaimed.")
                let! baseOid =
                    if String.IsNullOrWhiteSpace existing.TargetRef then Ok sourceHead
                    else LedgerStore.reviewMergeBase binding existing.TargetRef sourceHead
                do! LedgerStore.verifySourceObject binding sourceHead
                do! LedgerStore.publishReviewRef binding refName sourceHead
                let writeResult =
                    LedgerStore.transact binding action (fun current -> result {
                        let! review, reviewOid =
                            required<BranchReview> binding current (branchReviewPath taskId id)
                        if review.Version <> expectedVersion || review.Status <> "Unclaimed" then
                            return! Error(ReviewAlreadyClaimed "Review was claimed or replaced.")
                        let! _, summaryOid =
                            LedgerStore.readAt binding current (reviewDocumentPath taskId id "summary.md")
                        return [ change (branchReviewPath taskId id) (Some reviewOid)
                                   { review with Version = review.Version + 1
                                                 ReviewRef = refName; BaseCommit = baseOid; TaskRef = if String.IsNullOrWhiteSpace sourceRef then review.TaskRef else sourceRef }
                                 { Path = reviewDocumentPath taskId id "summary.md"
                                   ExpectedBlobOid = summaryOid; Content = summary } ]
                    })
                match writeResult with
                | Error(StaleEntityVersion _ | ReviewVersionConflict _ | ReviewAlreadyClaimed _) ->
                    LedgerStore.deleteReservedReviewRef binding refName sourceHead |> ignore
                    return! writeResult
                | _ -> return! writeResult
            }

    let claimBranchReview binding operationId taskId reviewId reviewerId expectedVersion =
        if not (validTaskId taskId) || reviewId = Guid.Empty || not (validAgent reviewerId)
           || expectedVersion < 1 then Error(InvalidBinding "Invalid review claim.")
        else
            let id = reviewId.ToString("N")
            LedgerStore.transact binding
                (intent operationId reviewerId "ClaimBranchReview"
                    {| taskId = taskId; reviewId = id; reviewerId = reviewerId
                       expectedVersion = expectedVersion |})
                (fun snapshot -> result {
                    let! review, oid = required<BranchReview> binding snapshot (branchReviewPath taskId id)
                    if review.Version <> expectedVersion || review.Status <> "Unclaimed" then
                        return! Error(ReviewAlreadyClaimed "Review is no longer unclaimed.")
                    return [ change (branchReviewPath taskId id) (Some oid)
                                { review with Status = "Claimed"; ReviewerId = reviewerId
                                              Version = review.Version + 1 } ]
                })

    let decideBranchReview binding operationId taskId reviewId reviewerId expectedVersion verdict =
        if not (validTaskId taskId) || reviewId = Guid.Empty || not (validAgent reviewerId)
           || expectedVersion < 1 then Error(InvalidBinding "Invalid review decision.")
        else
            let status = match verdict with Accept -> "Accepted" | RequestChanges -> "ChangesRequested"
            let id = reviewId.ToString("N")
            LedgerStore.transact binding
                (intent operationId reviewerId "DecideBranchReview"
                    {| taskId = taskId; reviewId = id; reviewerId = reviewerId
                       expectedVersion = expectedVersion; status = status |})
                (fun snapshot -> result {
                    let! review, oid = required<BranchReview> binding snapshot (branchReviewPath taskId id)
                    if review.Status <> "Claimed" || review.Version <> expectedVersion
                       || review.ReviewerId <> reviewerId then
                        return! Error(ReviewVersionConflict "Review claim has changed.")
                    return [ change (branchReviewPath taskId id) (Some oid)
                                { review with Version = review.Version + 1; Status = status
                                              DecisionSequence = snapshot.Header.NextSequence
                                              Acknowledged = false } ]
                })

    let acknowledgeBranchReview binding operationId taskId ownerId decisionSequence =
        if not (validTaskId taskId) || not (validAgent ownerId) || decisionSequence < 1L then
            Error(InvalidBinding "Invalid review acknowledgement.")
        else
            LedgerStore.transact binding
                (intent operationId ownerId "AcknowledgeBranchReview"
                    {| taskId = taskId; ownerId = ownerId; decisionSequence = decisionSequence |})
                (fun snapshot -> result {
                    let! reviews = branchReviewsForTask binding snapshot taskId
                    match reviews |> List.filter (fun review ->
                        review.DecisionSequence = decisionSequence
                        && review.OwnerAgentId = ownerId
                        && List.contains review.Status [ "Accepted"; "ChangesRequested" ]) with
                    | [ review ] when not review.Acknowledged ->
                        let! _, oid = required<BranchReview> binding snapshot
                                         (branchReviewPath taskId review.ReviewId)
                        return [ change (branchReviewPath taskId review.ReviewId) (Some oid)
                                    { review with Acknowledged = true; Version = review.Version + 1 } ]
                    | _ -> return! Error(StaleEntityVersion "No unacknowledged review matches the sequence.")
                })

    let releaseBranchReview binding operationId taskId reviewId actorId expectedVersion =
        if not (validTaskId taskId) || reviewId = Guid.Empty
           || not (validAgent actorId) || expectedVersion < 1 then
            Error(InvalidBinding "Invalid review claim release.")
        else
            let id = reviewId.ToString("N")
            LedgerStore.transact binding
                (intent operationId actorId "ReleaseBranchReview"
                    {| taskId = taskId; reviewId = id; actorId = actorId
                       expectedVersion = expectedVersion |})
                (fun snapshot -> result {
                    let! review, oid = required<BranchReview> binding snapshot (branchReviewPath taskId id)
                    if review.Version <> expectedVersion || review.Status <> "Claimed" then
                        return! Error(ReviewVersionConflict "Review claim has changed.")
                    return [ change (branchReviewPath taskId id) (Some oid)
                                { review with Version = review.Version + 1
                                              Status = "Unclaimed"; ReviewerId = null } ]
                })

    let withdrawBranchReview binding operationId taskId reviewId actorId expectedVersion =
        if not (validTaskId taskId) || reviewId = Guid.Empty || not (validAgent actorId)
           || expectedVersion < 1 then Error(InvalidBinding "Invalid review withdrawal.")
        else
            let id = reviewId.ToString("N")
            LedgerStore.transact binding
                (intent operationId actorId "WithdrawBranchReview"
                    {| taskId = taskId; reviewId = id; actorId = actorId
                       expectedVersion = expectedVersion |})
                (fun snapshot -> result {
                    let! review, oid = required<BranchReview> binding snapshot (branchReviewPath taskId id)
                    if review.Version <> expectedVersion
                       || not (List.contains review.Status [ "Unclaimed"; "Claimed" ])
                       then
                        return! Error(ReviewVersionConflict "Review status or version changed.")
                    return [ change (branchReviewPath taskId id) (Some oid)
                                { review with Version = review.Version + 1; Status = "Withdrawn" } ]
                })

    let branchReviewerQueue binding reviewerId =
        if not (validAgent reviewerId) then Error(InvalidBinding "Invalid reviewer.")
        else result {
            let! snapshot = LedgerStore.fetch binding
            let! paths = LedgerStore.listAt binding snapshot "tasks"
            let mutable reviews = []
            for path in paths do
                if path.EndsWith("/review.json", StringComparison.Ordinal) then
                    let! review, _ = required<BranchReview> binding snapshot path
                    if review.Status = "Unclaimed"
                       || (review.Status = "Claimed" && review.ReviewerId = reviewerId) then
                        reviews <- review :: reviews
            return snapshot.Commit, reviews |> List.sortBy (fun review ->
                review.CreatedSequence, review.TaskId, review.ReviewId)
        }

    // A task is closed by an explicit caller decision, not by review policy
    // or a Git ancestry calculation.
    let closeTaskAs binding actor operationId taskId expectedTaskVersion =
        if not (validTaskId taskId) || expectedTaskVersion < 1 then
            Error(InvalidBinding "Invalid task closure.")
        else
            LedgerStore.transact binding
                (intent operationId actor "CloseTask"
                    {| taskId = taskId; expectedTaskVersion = expectedTaskVersion |})
                (fun snapshot -> result {
                    let! task, taskOid = required<ReviewTask> binding snapshot (taskPath taskId)
                    if task.Version <> expectedTaskVersion
                       || not (List.contains task.Lifecycle [ "Open"; "InProgress"; "Implemented"; "Integrated" ]) then
                        return! Error(StaleEntityVersion "Task is not open or its version changed.")
                    return [ change (taskPath taskId) (Some taskOid)
                                { task with Version = task.Version + 1; Lifecycle = "Closed" }
                             change (lifecycleEventPath taskId snapshot.Header.NextSequence) None
                                 (adminEvent actor task None operationId snapshot.Header.NextSequence
                                      "CloseTask" "Explicit closure" "Closed" null null null "None" null) ]
                })

    // Simple project-wide read independent of current agent focus.

    let closeTask binding operationId taskId expectedTaskVersion =
        closeTaskAs binding legacyAdministrativeActor operationId taskId expectedTaskVersion

    let listProjectTasks binding =
        result {
            let! snapshot = LedgerStore.fetch binding
            let! paths = LedgerStore.listAt binding snapshot "tasks"
            let mutable tasks = []
            let mutable reviews = []
            for path in paths do
                if path.EndsWith("/task.json", StringComparison.Ordinal) then
                    let! task, _ = required<ReviewTask> binding snapshot path
                    let! attempt =
                        if String.IsNullOrWhiteSpace task.AttemptId then Ok None
                        else required<ReviewAttempt> binding snapshot
                                 (attemptPath task.TaskId task.AttemptId)
                             |> Result.map (fun (item, _) -> Some item)
                    tasks <- {| Task = task; Attempt = attempt |} :: tasks
                elif path.EndsWith("/review.json", StringComparison.Ordinal) then
                    let! review, _ = required<BranchReview> binding snapshot path
                    reviews <- review :: reviews
            return {| SnapshotCommit = snapshot.Commit
                      Tasks = tasks |> List.sortBy (fun item -> item.Task.TaskId)
                      Reviews = reviews |> List.sortBy (fun item -> item.CreatedSequence, item.ReviewId) |}
        }
