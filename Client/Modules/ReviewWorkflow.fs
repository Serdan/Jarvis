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
type ReviewAgent =
    { AgentId: string
      Version: int
      State: string
      FocusAttemptId: string
      ActiveAttemptId: string
      StateReason: string
      StateSequence: int64
      StateEnteredAtUtc: string
      WorkingTurnStartedAtUtc: string
      ActiveSessionId: string
      AssociatedSessionIds: string list }

[<CLIMutable>]
type ReviewAttempt =
    { TaskId: string
      AttemptId: string
      Version: int
      OwnerAgentId: string
      ReviewerId: string
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
      SourceRef: string
      ActiveReviewRequestId: string
      LatestReviewRequestId: string
      AcceptedReviewRequestId: string
      AcceptedReviewRevision: int
      AcceptedHead: string
      IntegrationActionId: string
      ReviewFeedback: string
      FeedbackSequence: int64
      AcknowledgedFeedbackSequence: int64 }

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
type ReviewRequest =
    { RequestId: string
      TaskId: string
      AttemptId: string
      OwnerAgentId: string
      ReviewerId: string
      Version: int
      CurrentRevision: int
      Status: string
      ClaimantId: string
      ClaimedRevision: int
      CreatedSequence: int64
      DecisionSequence: int64
      Feedback: string }

[<CLIMutable>]
type ReviewRevision =
    { RequestId: string
      Revision: int
      TaskId: string
      AttemptId: string
      BaseCommit: string
      HeadCommit: string
      SourceRef: string
      TargetRef: string
      ReviewRef: string
      Summary: string
      CreatedSequence: int64
      SpecBlobOid: string
      SourceRepositoryId: string
      SourceRemote: string
      SourcePushRemote: string
      PublishedSourceOid: string
      CheckpointSequence: int64
      TestEvidence: string
      EvidenceStatus: string }

[<CLIMutable>]
type OrphanReviewRef =
    { TaskId: string
      RequestId: string
      Revision: int
      ReviewRef: string
      ObservedHead: string
      ExpectedHead: string
      RemoteState: string
      Reserved: bool
      ReservationSequence: int64 }

[<CLIMutable>]
type OrphanReviewReservation =
    { TaskId: string
      RequestId: string
      Revision: int
      ReviewRef: string
      ExpectedHead: string
      ActorId: string
      Reason: string
      Sequence: int64 }

[<CLIMutable>]
type ReviewClaimEvent =
    { TaskId: string
      RequestId: string
      AttemptId: string
      Revision: int
      RequestVersionBefore: int
      Sequence: int64
      OperationId: string
      Action: string
      ActorId: string
      PreviousClaimantId: string
      NewClaimantId: string
      Reason: string
      HeadCommit: string
      ReviewRef: string
      SpecBlobOid: string }

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
      Evidence: string }

[<CLIMutable>]
type NonSourceClosureApproval =
    { TaskId: string
      AttemptId: string
      OperationId: string
      Sequence: int64
      ApproverId: string
      OwnerAgentId: string
      SpecBlobOid: string
      Criteria: string
      Evidence: string
      NoSourceIntegration: bool }

[<CLIMutable>]
type ReviewDecisionEvidence =
    { RequestId: string
      Revision: int
      TaskId: string
      AttemptId: string
      ReviewerId: string
      Sequence: int64
      Status: string
      Inspected: bool
      Tested: bool
      TestDetails: string
      Feedback: string
      ReviewedHead: string
      ReviewRef: string
      SpecBlobOid: string
      SourceRemote: string }

[<CLIMutable>]
type ReviewIntegration =
    { ActionId: string
      TaskId: string
      AttemptId: string
      Version: int
      Status: string
      ReviewRequestId: string
      ReviewRevision: int
      BaseCommit: string
      ApprovedHead: string
      ReviewRef: string
      TargetRef: string
      ExpectedTargetHead: string
      SourceRepositoryId: string
      SourceRemote: string
      SourcePushRemote: string
      PlannedSequence: int64
      PlannedAtUtc: string
      ObservedTargetHead: string
      VerifiedSequence: int64
      VerifiedAtUtc: string
      VerificationMethod: string }

[<CLIMutable>]
type IntegrationInspection =
    { LedgerCommit: string
      Integration: ReviewIntegration
      ObservedTargetHead: string
      RemoteState: string }

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
      AcceptedPendingIntegration: InboxTask list
      PendingReviews: ReviewRequest list
      AvailableReviews: ReviewRequest list
      UnacknowledgedFeedback: ReviewFeedbackNotice list
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
        if agent.State = state then agent
        else
            let now = DateTimeOffset.UtcNow.ToString("O")
            { agent with State = state; StateEnteredAtUtc = now
                         WorkingTurnStartedAtUtc = if state = "Working" then now else null
                         ActiveSessionId = if state = "Working" then agent.ActiveSessionId else null }

    let private associateSession (sessionId: string) (agent: ReviewAgent) =
        if String.IsNullOrWhiteSpace sessionId then agent
        else
            let ids = if isNull (box agent.AssociatedSessionIds) then [] else agent.AssociatedSessionIds
            { agent with ActiveSessionId = sessionId
                         AssociatedSessionIds = if List.contains sessionId ids then ids else ids @ [ sessionId ] }

    let private resetSessions (agent: ReviewAgent) =
        { agent with ActiveSessionId = null
                     AssociatedSessionIds = []
                     WorkingTurnStartedAtUtc = null }

    // An entity's schema is explicit on disk without adding boilerplate to
    // each in-memory record. Changing it requires an intentional upgrade path.
    let private entityJson value =
        let content = json value
        if not (content.StartsWith("{", StringComparison.Ordinal)) then
            invalidArg "value" "A ledger entity must be a JSON object."
        "{\"schemaVersion\":2," + content.Substring(1)

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
                    && schema.ValueKind = JsonValueKind.Number && schema.GetInt32() = 2
                let required =
                    typeof<'t>.GetProperties()
                    |> Array.map (fun property -> options.PropertyNamingPolicy.ConvertName(property.Name))
                let complete =
                    required |> Array.forall (fun property ->
                        let mutable memberValue = Unchecked.defaultof<JsonElement>
                        root.TryGetProperty(property, &memberValue))
                let status (name: string) (allowed: string list) =
                    let mutable field = Unchecked.defaultof<JsonElement>
                    root.TryGetProperty(name, &field)
                    && field.ValueKind = JsonValueKind.String
                    && List.contains (field.GetString()) allowed
                let stateValid =
                    match typeof<'t>.Name with
                    | "ReviewTask" -> status "lifecycle" [ "Open"; "InProgress"; "Implemented"; "Integrated"; "Closed"; "Cancelled" ]
                    | "ReviewAgent" -> status "state" [ "Ready"; "Assigned"; "Working"; "Completed"; "Parked" ]
                    | "ReviewAttempt" -> status "phase" [ "Queued"; "Acknowledged"; "Working"; "Paused"; "Finished"; "Parked"; "Blocked";
                          "Accepted"; "Superseded"; "Abandoned" ]
                    | "ReviewRequest" -> status "status" [ "Unclaimed"; "Claimed"; "Accepted"; "ChangesRequested"; "Withdrawn" ]
                    | "ReviewIntegration" -> status "status" [ "PendingSourceAction"; "PendingPush"; "Recorded" ]
                    | "ReviewFeedbackNotice" -> status "status" [ "Accepted"; "ChangesRequested" ]
                    | "AgentCheckpoint" -> status "kind" [ "Yield"; "Completion"; "Recovery"; "Progress"; "Submission"; "Revision" ]
                    | "ReviewRevision" -> status "evidenceStatus" [ "NotProvided"; "OwnerReported"; "ReviewerVerified" ]
                    | "ReviewDecisionEvidence" -> status "status" [ "Accepted"; "ChangesRequested" ]
                    | "ReviewClaimEvent" ->
                        status "action" [ "Claim"; "Release"; "Reassign"; "Withdraw"; "Decision" ]
                    | "TaskLifecycleEvent" ->
                        status "action" [ "ParkAttempt"; "BlockAttempt"; "ResumeAttempt"; "CancelTask"
                                          "AbandonAttempt"; "TransferOwner"; "CloseNonSourceTask"; "ReopenTask" ]
                    | "NonSourceClosureApproval" -> true
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
    let private requestPath taskId requestId = $"tasks/{taskId}/reviews/{requestId}/request.json"
    let private decisionPath taskId requestId sequence =
        $"tasks/{taskId}/reviews/{requestId}/decisions/{sequence}.json"
    let private reviewEventPath taskId requestId sequence =
        $"tasks/{taskId}/reviews/{requestId}/events/{sequence}.json"
    let private lifecycleEventPath taskId sequence =
        $"tasks/{taskId}/lifecycle/{sequence}.json"
    let private nonSourceApprovalPath taskId sequence =
        $"tasks/{taskId}/approvals/non-source/{sequence}.json"
    let private orphanReservationPath taskId requestId revision =
        $"tasks/{taskId}/orphan-cleanups/{requestId}/r{revision}.json"
    let private integrationPath taskId actionId = $"tasks/{taskId}/integrations/{actionId}.json"
    let private feedbackPath taskId requestId = $"tasks/{taskId}/reviews/{requestId}/notification.json"
    let private revisionPath taskId requestId number = $"tasks/{taskId}/reviews/{requestId}/revisions/{number}.json"
    let private reviewRef taskId requestId number = $"refs/heads/jarvis-review/{taskId}/{requestId}/r{number}"
    let private claimEvent taskId (request: ReviewRequest) (revision: ReviewRevision) sequence (operationId: Guid) action actor previous next reason =
        { TaskId = taskId; RequestId = request.RequestId; AttemptId = request.AttemptId
          Revision = revision.Revision; RequestVersionBefore = request.Version
          Sequence = sequence; OperationId = operationId.ToString("D")
          Action = action; ActorId = actor
          PreviousClaimantId = previous; NewClaimantId = next; Reason = reason
          HeadCommit = revision.HeadCommit; ReviewRef = revision.ReviewRef
          SpecBlobOid = revision.SpecBlobOid }

    let private orphanReviewRef taskId requestId revision =
        reviewRef taskId requestId revision

    let private parseRemoteReviewRef taskId (gitRef: string) =
        let prefix = $"refs/heads/jarvis-review/{taskId}/"
        if not (gitRef.StartsWith(prefix, StringComparison.Ordinal)) then None
        else
            let tokens = gitRef.Substring(prefix.Length).Split('/')
            if tokens.Length <> 2 || not (Regex.IsMatch(tokens[0], "^[0-9a-f]{32}$"))
               || not (Regex.IsMatch(tokens[1], "^r[1-9][0-9]*$")) then None
            else
                match Int32.TryParse(tokens[1].Substring(1)) with
                | true, number when number > 0 -> Some(tokens[0], number)
                | _ -> None

    let private agentPath (agentId: string) =
        let safe =
            Convert.ToBase64String(Encoding.UTF8.GetBytes agentId)
                .TrimEnd('=').Replace('+', '-').Replace('/', '_')
        $"agents/{safe}.json"

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

    let private read<'t> binding snapshot path : Result<('t * string) option, LedgerFailure> =
        result {
            let! content, blobOid = LedgerStore.readAt binding snapshot path
            match content, blobOid with
            | None, None -> return None
            | Some content, Some blobOid ->
                try
                    match validEntity<'t> content with
                    | Error message ->
                        return! Error(LedgerIntegrityError $"Invalid ledger entity {path}: {message}")
                    | Ok () ->
                        let parsed = JsonSerializer.Deserialize<'t>(content, options)
                        if isNull (box parsed) then
                            return! Error(LedgerIntegrityError $"Null ledger entity: {path}")
                        else
                            return Some(parsed, blobOid)
                with ex ->
                    return! Error(LedgerIntegrityError $"Invalid ledger entity {path}: {ex.Message}")
            | _ -> return! Error(LedgerIntegrityError $"Inconsistent ledger entity: {path}")
        }

    let private required<'t> binding snapshot path : Result<'t * string, LedgerFailure> =
        result {
            let! value = read<'t> binding snapshot path
            match value with
            | Some record -> return record
            | None -> return! Error(StaleEntityVersion $"Missing ledger entity: {path}")
        }

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

    let createTaskWithSpec binding operationId taskId title html =
        if not (validTaskId taskId) || not (validText 512 title)
           || not (validTaskHtml html) then
            Error(InvalidBinding "Invalid task ID, title or canonical HTML specification.")
        else
            LedgerStore.transact binding
                (intent operationId "Orchestrator" "CreateTask"
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
    let createTask binding operationId taskId title =
        createTaskWithSpec binding operationId taskId title
            ($"<h1>{System.Net.WebUtility.HtmlEncode(title)}</h1>")

    let reviseTaskSpecPinned binding operationId taskId expectedTaskVersion expectedSpecBlobOid title html =
        if not (validTaskId taskId) || expectedTaskVersion < 1
           || not (validText 512 title) || not (validTaskHtml html) then
            Error(InvalidBinding "Invalid task spec revision.")
        else
            LedgerStore.transact binding
                (intent operationId "Orchestrator"
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
    let reviseTaskSpec binding operationId taskId expectedTaskVersion title html =
        reviseTaskSpecPinned binding operationId taskId expectedTaskVersion null title html

    // Split preflight from publication to exercise the real race boundary
    // deterministically: every CAS retry rechecks the source snapshot.
    let importTaskSpecPrepared binding operationId taskId expectedTaskVersion
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
                    (intent operationId "Orchestrator" "ImportTaskSpec"
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
                              OperationId = operationId.ToString("D"); ActorId = "Orchestrator"
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

    let importTaskSpec binding operationId taskId expectedTaskVersion
                       expectedSpecBlobOid expectedSourceBlobOid title relativePath =
        result {
            let! original = LedgerStore.readRegisteredSourceSpec binding relativePath
            return! importTaskSpecPrepared binding operationId taskId expectedTaskVersion
                        expectedSpecBlobOid expectedSourceBlobOid title relativePath original
        }

    // In-progress spec updates never rewrite issued instructions: only an
    // explicit generation reissue can adopt the new canonical version.
    let adoptTaskSpec binding operationId taskId ownerId expectedTaskVersion
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
                (intent operationId "Orchestrator" "AdoptTaskSpec"
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
                       || not (List.contains attempt.Phase [ "Queued"; "Acknowledged" ])
                       || agent.State <> "Assigned" || agent.Version <> expectedAgentVersion
                       || agent.FocusAttemptId <> attempt.AttemptId
                       || not (String.IsNullOrWhiteSpace agent.ActiveAttemptId)
                       || not (String.IsNullOrWhiteSpace attempt.ActiveReviewRequestId)
                       || not (String.IsNullOrWhiteSpace attempt.LatestReviewRequestId) then
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
                            (issue taskId next (generation + 1) "Orchestrator" "SpecAdoption"
                                instructions snapshot.Header.NextSequence)
                        change (agentPath ownerId) (Some agentOid)
                            { agent with Version = agent.Version + 1; StateReason = instructions
                                         StateSequence = snapshot.Header.NextSequence } ]
                })

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

    let registerAgent binding operationId agentId =
        if not (validAgent agentId) then Error(InvalidBinding "Invalid agent ID.")
        else
            LedgerStore.transact binding
                (intent operationId "Orchestrator" "RegisterAgent" {| agentId = agentId |})
                (fun snapshot ->
                    result {
                        let! existing = read<ReviewAgent> binding snapshot (agentPath agentId)
                        if existing.IsSome then return! Error(StaleEntityVersion "Agent already registered.")
                        let agent =
                            { AgentId = agentId; Version = 1; State = "Parked"
                              FocusAttemptId = null; ActiveAttemptId = null
                              StateReason = "Registered without an assignment"
                              StateSequence = snapshot.Header.NextSequence
                              StateEnteredAtUtc = DateTimeOffset.UtcNow.ToString("O")
                              WorkingTurnStartedAtUtc = null; ActiveSessionId = null
                              AssociatedSessionIds = [] }
                        return [ change (agentPath agentId) None agent ]
                    })

    let assignTask binding operationId taskId attemptId ownerId reviewerId =
        if not (validTaskId taskId) || attemptId = Guid.Empty
           || not (validAgent ownerId) || not (validAgent reviewerId) || ownerId = reviewerId then
            Error(InvalidBinding "Invalid task assignment.")
        else
            let attemptIdText = attemptId.ToString("N")
            LedgerStore.transact binding
                (intent operationId "Orchestrator" "AssignTask"
                    {| taskId = taskId; attemptId = attemptId; ownerId = ownerId; reviewerId = reviewerId |})
                (fun snapshot ->
                    result {
                        let! task, taskOid = required<ReviewTask> binding snapshot (taskPath taskId)
                        let! agent, agentOid = required<ReviewAgent> binding snapshot (agentPath ownerId)
                        let! _ = required<ReviewAgent> binding snapshot (agentPath reviewerId)
                        let! specContent, specOid = LedgerStore.readAt binding snapshot (specPath taskId)
                        if specContent.IsNone || specOid.IsNone then
                            return! Error(LedgerIntegrityError "Canonical task spec is missing.")
                        if task.Lifecycle <> "Open" || not (String.IsNullOrWhiteSpace task.AttemptId) then
                            return! Error(StaleEntityVersion "Task already has an implementation owner.")
                        if not (List.contains agent.State [ "Parked"; "Completed" ])
                           || not (String.IsNullOrWhiteSpace agent.ActiveAttemptId) then
                            return! Error(AgentBusy "Agent must be Parked or Completed before receiving an assignment.")
                        let attempt =
                            { TaskId = taskId; AttemptId = attemptIdText; Version = 1
                              OwnerAgentId = ownerId; ReviewerId = reviewerId; Phase = "Queued"
                              AssignmentGeneration = 1; Priority = 0
                              IssuedSequence = snapshot.Header.NextSequence
                              SpecBlobOid = specOid.Value
                              BaseCommit = null; TargetRef = null
                              SourceRepositoryId = null; SourceRemote = null
                              SourcePushRemote = null; SourceRef = null
                              ActiveReviewRequestId = null; LatestReviewRequestId = null
                              AcceptedReviewRequestId = null
                              AcceptedReviewRevision = 0; AcceptedHead = null
                              IntegrationActionId = null
                              ReviewFeedback = null; FeedbackSequence = 0L
                              AcknowledgedFeedbackSequence = 0L }
                        return
                            [ change (taskPath taskId) (Some taskOid)
                                { task with Version = task.Version + 1; Lifecycle = "InProgress"; AttemptId = attemptIdText }
                              change (attemptPath taskId attemptIdText) None attempt
                              change (issuePath taskId attemptIdText 1) None
                                (issue taskId attempt 1 "Orchestrator" "Assignment"
                                    $"Implement task {taskId} against the pinned task specification."
                                    snapshot.Header.NextSequence)
                              change (agentPath ownerId) (Some agentOid)
                                { (resetSessions agent) with
                                    Version = agent.Version + 1
                                    State = "Assigned"
                                    FocusAttemptId = attemptIdText
                                    StateEnteredAtUtc = if agent.State = "Assigned" then agent.StateEnteredAtUtc else DateTimeOffset.UtcNow.ToString("O")
                                    ActiveAttemptId = null
                                    StateReason = $"Assigned task {taskId}; awaiting operator start"
                                    StateSequence = snapshot.Header.NextSequence } ]
                    })

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
                           || agent.State <> "Assigned"
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
                                { attempt with Version = attempt.Version + 1; Phase = "Acknowledged" } ]
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
                           || not (List.contains attempt.Phase [ "Queued"; "Acknowledged" ]) then
                            return! Error(StaleEntityVersion "Attempt is not available for Begin.")
                        let! issuance = requiredIssue binding snapshot taskId attempt
                        let! existingAcknowledgment =
                            read<AssignmentReceipt> binding snapshot
                                (receiptPath taskId attempt.AttemptId generation)
                        if attempt.Phase = "Acknowledged" then
                            match existingAcknowledgment with
                            | Some (ack, _) when
                                ack.Generation = generation
                                && ack.SpecBlobOid = issuance.SpecBlobOid
                                && ack.IssueSequence = issuance.IssuedSequence
                                && ack.OwnerAgentId = ownerId -> ()
                            | _ ->
                                return! Error(LedgerIntegrityError "Acknowledgment is not for the current assignment.")
                        elif existingAcknowledgment.IsSome then
                            return! Error(LedgerIntegrityError "Queued generation already has an acknowledgment.")
                        if agent.State <> "Assigned" || agent.FocusAttemptId <> attempt.AttemptId then
                            return! Error(StaleEntityVersion "Agent has not been assigned this attempt for startup.")
                        if not (String.IsNullOrWhiteSpace agent.ActiveAttemptId) then
                            return! Error(AgentBusy "Agent is already Working.")
                        return
                            [ change (attemptPath taskId attempt.AttemptId) (Some attemptOid)
                                { attempt with Version = attempt.Version + 1; Phase = "Working" }
                              change (agentPath ownerId) (Some agentOid)
                                (enterState "Working" agent |> associateSession sessionId |> fun updated -> { updated with Version = agent.Version + 1; State = "Working"; ActiveAttemptId = attempt.AttemptId; StateReason = $"Started task {taskId}"; StateSequence = snapshot.Header.NextSequence }) ]
                            @ (if attempt.Phase = "Queued" then
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

    // A Ready agent has explicitly yielded at a durable checkpoint. Mere inactivity
    // never changes agent state or frees its Working slot.
    let yieldAgent binding operationId taskId ownerId expectedAttemptVersion expectedAgentVersion
                   generation summary nextAction =
        if not (validTaskId taskId) || not (validAgent ownerId)
           || expectedAttemptVersion < 1 || expectedAgentVersion < 1 || generation < 1
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
                        if task.Lifecycle <> "InProgress" || attempt.OwnerAgentId <> ownerId
                           || attempt.Version <> expectedAttemptVersion
                           || attempt.AssignmentGeneration <> generation
                           || attempt.Phase <> "Working" || agent.State <> "Working"
                           || agent.Version <> expectedAgentVersion
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
                                { attempt with Version = attempt.Version + 1; Phase = "Paused" }
                              change (agentPath ownerId) (Some agentOid)
                                (enterState "Ready" agent |> fun updated -> { updated with Version = agent.Version + 1; State = "Ready"; ActiveAttemptId = null; StateReason = nextAction; StateSequence = snapshot.Header.NextSequence }) ]
                    })

    let resumeAgentWithSession binding operationId taskId ownerId expectedAttemptVersion expectedAgentVersion generation sessionId =
        if not (validTaskId taskId) || not (validAgent ownerId)
           || expectedAttemptVersion < 1 || expectedAgentVersion < 1 || generation < 1 then
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
                        if task.Lifecycle <> "InProgress" || attempt.OwnerAgentId <> ownerId
                           || attempt.Version <> expectedAttemptVersion
                           || attempt.AssignmentGeneration <> generation
                           || agent.Version <> expectedAgentVersion
                           || agent.FocusAttemptId <> attempt.AttemptId then
                            return! Error(StaleEntityVersion "Current task/attempt/owner changed; reread assignment.")
                        let! _ = requiredIssue binding snapshot taskId attempt
                        if agent.State = "Ready" && attempt.Phase = "Paused"
                           && String.IsNullOrWhiteSpace agent.ActiveAttemptId then
                            return
                                [ change (attemptPath taskId attempt.AttemptId) (Some attemptOid)
                                    { attempt with Version = attempt.Version + 1; Phase = "Working" }
                                  change (agentPath ownerId) (Some agentOid)
                                    (enterState "Working" agent |> associateSession sessionId
                                     |> fun updated ->
                                        { updated with
                                            Version = agent.Version + 1
                                            ActiveAttemptId = attempt.AttemptId
                                            StateReason = $"Resumed task {taskId}"
                                            StateSequence = snapshot.Header.NextSequence }) ]
                        elif agent.State = "Working" && attempt.Phase = "Working"
                             && agent.ActiveAttemptId = attempt.AttemptId then
                            let same = String.IsNullOrWhiteSpace sessionId || agent.ActiveSessionId = sessionId
                            let now = DateTimeOffset.UtcNow.ToString("O")
                            let updated =
                                { (associateSession sessionId agent) with
                                    Version = agent.Version + 1
                                    WorkingTurnStartedAtUtc = if same then agent.WorkingTurnStartedAtUtc else now
                                    StateReason = if same then agent.StateReason else $"Recovered stalled task {taskId}"
                                    StateSequence = if same then agent.StateSequence else snapshot.Header.NextSequence }
                            return
                                [ change (agentPath ownerId) (Some agentOid) updated ]
                                @ (if same then [] else
                                      [ change ($"tasks/{taskId}/attempts/{attempt.AttemptId}/events/{snapshot.Header.NextSequence}.json") None
                                          {| taskId = taskId; attemptId = attempt.AttemptId
                                             ownerAgentId = ownerId; assignmentGeneration = generation
                                             kind = "AgentStallRecovered"; sessionId = sessionId
                                             reason = "Explicit resume of Working agent"
                                             sequence = snapshot.Header.NextSequence |} ])
                        else
                            return! Error(StaleEntityVersion "Resume requires Ready or Working on the same assignment.")
                    })

    let resumeAgent binding operationId taskId ownerId expectedAttemptVersion expectedAgentVersion generation =
        resumeAgentWithSession binding operationId taskId ownerId expectedAttemptVersion expectedAgentVersion generation ""

    let completeAgentWork binding operationId taskId ownerId expectedAttemptVersion expectedAgentVersion
                          generation summary =
        if not (validTaskId taskId) || not (validAgent ownerId)
           || expectedAttemptVersion < 1 || expectedAgentVersion < 1 || generation < 1
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
                        let validPhase =
                            (agent.State = "Working" && attempt.Phase = "Working"
                             && agent.ActiveAttemptId = attempt.AttemptId)
                            || (agent.State = "Ready" && attempt.Phase = "Paused"
                                && String.IsNullOrWhiteSpace agent.ActiveAttemptId)
                        if task.Lifecycle <> "InProgress" || attempt.OwnerAgentId <> ownerId
                           || attempt.Version <> expectedAttemptVersion
                           || attempt.AssignmentGeneration <> generation
                           || agent.Version <> expectedAgentVersion
                           || agent.FocusAttemptId <> attempt.AttemptId || not validPhase then
                            return! Error(StaleEntityVersion "Only the current Working or Ready agent can complete this assignment.")
                        let! lastReview =
                            if String.IsNullOrWhiteSpace attempt.LatestReviewRequestId then Ok None
                            else read<ReviewRequest> binding snapshot
                                     (requestPath taskId attempt.LatestReviewRequestId)
                        let requeue =
                            match lastReview with
                            | Some(review, _) ->
                                review.Status = "ChangesRequested"
                                && String.IsNullOrWhiteSpace attempt.ActiveReviewRequestId
                            | None -> false
                        let checkpoint =
                            { TaskId = taskId; AttemptId = attempt.AttemptId
                              OwnerAgentId = ownerId; AssignmentGeneration = generation
                              Sequence = snapshot.Header.NextSequence
                              Summary = summary
                              NextAction = if requeue then "Requested changes require reissuance" else "Await review or a new assignment"
                              Kind = "Completion"
                              SourceCommit = null; Evidence = null; EvidenceStatus = "NotProvided" }
                        return
                            [ change (checkpointPath taskId attempt.AttemptId snapshot.Header.NextSequence) None checkpoint
                              change (attemptPath taskId attempt.AttemptId) (Some attemptOid)
                                { attempt with Version = attempt.Version + 1
                                               Phase = if requeue then "Queued" else "Finished"
                                               AssignmentGeneration =
                                                   if requeue then attempt.AssignmentGeneration + 1
                                                   else attempt.AssignmentGeneration
                                               IssuedSequence =
                                                   if requeue then snapshot.Header.NextSequence else attempt.IssuedSequence }
                              change (agentPath ownerId) (Some agentOid)
                                (enterState "Completed" agent |> fun updated -> { updated with Version = agent.Version + 1; State = "Completed"; ActiveAttemptId = null; StateReason = summary; StateSequence = snapshot.Header.NextSequence }) ]
                            @ (if requeue then
                                   [ change (issuePath taskId attempt.AttemptId
                                                 (attempt.AssignmentGeneration + 1)) None
                                       (issue taskId attempt (attempt.AssignmentGeneration + 1)
                                           "Orchestrator" "CompletionRequeue"
                                           "Complete the review-requested changes on the same task."
                                           snapshot.Header.NextSequence) ]
                               else [])
                    })

    // A progress checkpoint never yields the Working execution slot. Its
    // source/test evidence is owner-reported, not independently verified.
    let checkpointAttempt binding operationId taskId ownerId expectedAttemptVersion
                          expectedAgentVersion generation summary nextAction sourceCommit evidence =
        if not (validTaskId taskId) || not (validAgent ownerId)
           || expectedAttemptVersion < 1 || expectedAgentVersion < 1 || generation < 1
           || not (validText 8192 summary) || not (validText 8192 nextAction)
           || (not (String.IsNullOrEmpty evidence) && Encoding.UTF8.GetByteCount evidence > 8192) then
            Error(InvalidBinding "Invalid Working checkpoint metadata.")
        else
            LedgerStore.transact binding
                (intent operationId ownerId "CheckpointAttempt"
                    {| taskId = taskId; ownerId = ownerId; expectedAttemptVersion = expectedAttemptVersion
                       expectedAgentVersion = expectedAgentVersion; generation = generation
                       summary = summary; nextAction = nextAction
                       sourceCommit = sourceCommit; evidence = evidence |})
                (fun snapshot ->
                    result {
                        let! task, _, attempt, attemptOid = getAttempt binding snapshot taskId
                        let! agent, _ = required<ReviewAgent> binding snapshot (agentPath ownerId)
                        if task.Lifecycle <> "InProgress" || attempt.OwnerAgentId <> ownerId
                           || attempt.Phase <> "Working" || attempt.Version <> expectedAttemptVersion
                           || attempt.AssignmentGeneration <> generation
                           || agent.Version <> expectedAgentVersion || agent.State <> "Working"
                           || agent.ActiveAttemptId <> attempt.AttemptId then
                            return! Error(StaleEntityVersion "Checkpoint requires exact current Working versions and generation.")
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

    let parkAgent binding operationId agentId expectedAgentVersion reason =
        if not (validAgent agentId) || expectedAgentVersion < 1 || not (validText 2048 reason) then
            Error(InvalidBinding "Parking requires an agent version and reason.")
        else
            LedgerStore.transact binding
                (intent operationId "Orchestrator" "ParkAgent"
                    {| agentId = agentId; expectedAgentVersion = expectedAgentVersion
                       reason = reason |})
                (fun snapshot ->
                    result {
                        let! agent, agentOid = required<ReviewAgent> binding snapshot (agentPath agentId)
                        if agent.Version <> expectedAgentVersion
                           || not (List.contains agent.State [ "Completed"; "Parked" ])
                           || not (String.IsNullOrWhiteSpace agent.ActiveAttemptId) then
                            return! Error(StaleEntityVersion "Park may not interrupt an assigned or Working agent.")
                        return
                            [ change (agentPath agentId) (Some agentOid)
                                { (resetSessions agent) with
                                    Version = agent.Version + 1
                                    State = "Parked"; FocusAttemptId = null
                                    StateEnteredAtUtc = if agent.State = "Parked" then agent.StateEnteredAtUtc else DateTimeOffset.UtcNow.ToString("O")
                                    StateReason = reason
                                    StateSequence = snapshot.Header.NextSequence
                                } ]
                    })

    // Source repository identity is an endpoint fingerprint independent from
    // the ledger UUID. An agent's checkout path never enters the owner key.
    let private verifyPinnedSource binding (attempt: ReviewAttempt) sourceRef =
        result {
            let! source = LedgerStore.sourceRepositoryIdentity binding
            if String.IsNullOrWhiteSpace attempt.SourceRepositoryId
               || attempt.SourceRepositoryId <> source.RepositoryId
               || attempt.SourceRemote <> source.FetchUrl
               || attempt.SourcePushRemote <> source.PushUrl then
                return! Error(SourceUnavailable "Source repository differs from the attempt's approved identity.")
            if not (String.IsNullOrWhiteSpace attempt.SourceRef)
               && attempt.SourceRef <> sourceRef then
                return! Error(SourceUnavailable "Source branch differs from pinned attempt binding.")
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
                        if attempt.OwnerAgentId <> ownerId || attempt.Phase <> "Working"
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

    // Renaming or recreating a source branch is an explicit versioned mutation.
    // Immutable review revisions retain their original source refs and OIDs.
    // Neither a worktree rename nor a missing local checkout changes ownership.
    let rebindAttemptSource binding operationId taskId ownerId expectedVersion generation
                            sourceRef allowDetached allowUncommitted =
        if not (validTaskId taskId) || not (validAgent ownerId)
           || not (validBranch sourceRef)
           || sourceRef = "refs/heads/jarvis-orchestration"
           || sourceRef.StartsWith("refs/heads/jarvis-review/", StringComparison.Ordinal) then
            Error(InvalidBinding "Invalid replacement source ref.")
        else
            LedgerStore.transact binding
                (intent operationId ownerId "RebindAttemptSource"
                    {| taskId = taskId; ownerId = ownerId; expectedVersion = expectedVersion
                       generation = generation; sourceRef = sourceRef
                       allowDetached = allowDetached; allowUncommitted = allowUncommitted |})
                (fun snapshot ->
                    result {
                        let! _, _, attempt, attemptOid = getAttempt binding snapshot taskId
                        if attempt.OwnerAgentId <> ownerId
                           || attempt.Phase <> "Working"
                           || attempt.Version <> expectedVersion
                           || attempt.AssignmentGeneration <> generation
                           || String.IsNullOrWhiteSpace attempt.SourceRepositoryId
                           || String.IsNullOrWhiteSpace attempt.BaseCommit
                           || String.IsNullOrWhiteSpace attempt.TargetRef
                           || String.IsNullOrWhiteSpace attempt.SourceRef then
                            return! Error(StaleEntityVersion "Only the owning Working attempt can rebind its pinned source.")
                        let! _ = verifyPinnedSource binding attempt attempt.SourceRef
                        let! observation = LedgerStore.inspectSourceWorktree binding sourceRef
                        if observation.State = "SourceUnavailable" then
                            return! Error(SourceUnavailable "Replacement source branch does not exist.")
                        if observation.State = "StaleDetachedCheckout" then
                            return! Error(SourceUnavailable "Detached checkout HEAD differs from the replacement source head.")
                        if observation.SelectedWorktreeDetached && not allowDetached then
                            return! Error(SourceUnavailable "Detached checkout requires explicit consent.")
                        if observation.SelectedWorktreeDirty && not allowUncommitted then
                            return! Error(SourceUnavailable "Uncommitted checkout requires explicit consent.")
                        do! LedgerStore.verifySourceCommit binding sourceRef observation.SourceHead
                        do! LedgerStore.verifySourceAncestry binding attempt.BaseCommit observation.SourceHead
                        return [ change (attemptPath taskId attempt.AttemptId) (Some attemptOid)
                                    { attempt with Version = attempt.Version + 1; SourceRef = sourceRef } ]
                    })

    // Legacy experimental baseline mutation retains independent remote identity
    // without implicitly guessing the working branch. New clients bind both.
    // The task's review base remains fixed across rejections and resubmissions.
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
                        if attempt.OwnerAgentId <> ownerId || attempt.Phase <> "Working"
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
                let! _ = verifyPinnedSource binding attempt attempt.SourceRef
                if String.IsNullOrWhiteSpace attempt.SourceRef then
                    return snapshot.Commit, attempt, None
                else
                    let! observation = LedgerStore.inspectSourceWorktree binding attempt.SourceRef
                    return snapshot.Commit, attempt, Some observation
        }

    let private preparedSource binding taskId requestId number baseOid headOid sourceRef =
        result {
            if not (validBranch sourceRef) then
                return! Error(InvalidBinding "Invalid source working branch.")
            do! LedgerStore.verifySourceCommit binding sourceRef headOid
            do! LedgerStore.verifyReviewDiff binding baseOid headOid
            let gitRef = reviewRef taskId requestId number
            do! LedgerStore.publishReviewRef binding gitRef headOid
            return gitRef
        }

    let submitReviewWithEvidence binding operationId taskId ownerId expectedVersion generation requestId sourceRef headOid summary testEvidence =
        if not (validTaskId taskId) || not (validAgent ownerId) || requestId = Guid.Empty
           || expectedVersion < 1 || generation < 1 || not (validText 4096 summary)
           || (not (String.IsNullOrEmpty testEvidence) && Encoding.UTF8.GetByteCount testEvidence > 8192) then
            Error(InvalidBinding "Invalid review submission.")
        else
            let requestIdText = requestId.ToString("N")
            let action =
                intent operationId ownerId "SubmitForReview"
                    {| taskId = taskId; ownerId = ownerId; expectedVersion = expectedVersion
                       generation = generation; requestId = requestId; sourceRef = sourceRef
                       headOid = headOid; summary = summary; testEvidence = testEvidence |}
            // Preflight is intentionally read-only. The ledger transition revalidates the same data.
            // The published source ref is an orphan candidate if the subsequent ledger CAS fails.
            match LedgerStore.findApplied binding action with
            | Error failure -> Error failure
            | Ok(Some applied) -> Ok applied
            | Ok None -> result {
                let! snapshot = LedgerStore.fetch binding
                let! _, _, preAttempt, _ = getAttempt binding snapshot taskId
                if preAttempt.Version <> expectedVersion || preAttempt.OwnerAgentId <> ownerId
                   || preAttempt.Phase <> "Working" || preAttempt.AssignmentGeneration <> generation
                   || not (String.IsNullOrEmpty preAttempt.ActiveReviewRequestId)
                   || String.IsNullOrEmpty preAttempt.BaseCommit then
                    return! Error(StaleEntityVersion "Working attempt or review base is no longer available.")
                let! _ = verifyPinnedSource binding preAttempt sourceRef
                let! published = preparedSource binding taskId requestIdText 1 preAttempt.BaseCommit headOid sourceRef
                return!
                    LedgerStore.transact binding action (fun current ->
                        result {
                            let! task, _, attempt, attemptOid = getAttempt binding current taskId
                            let! agent, _ = required<ReviewAgent> binding current (agentPath ownerId)
                            let! existing = read<ReviewRequest> binding current (requestPath taskId requestIdText)
                            if task.Lifecycle <> "InProgress" || attempt.OwnerAgentId <> ownerId
                               || attempt.Phase <> "Working" || attempt.AssignmentGeneration <> generation
                               || attempt.Version <> expectedVersion
                               || agent.ActiveAttemptId <> attempt.AttemptId
                               || not (String.IsNullOrEmpty attempt.ActiveReviewRequestId)
                               || existing.IsSome
                               || attempt.BaseCommit <> preAttempt.BaseCommit then
                                return! Error(StaleEntityVersion "Review submission conflicts with task state.")
                            let! _ = verifyPinnedSource binding attempt sourceRef
                            do! LedgerStore.fetchReviewRef binding published headOid
                            let! retired = read<OrphanReviewReservation> binding current
                                                (orphanReservationPath taskId requestIdText 1)
                            if retired.IsSome then
                                return! Error(SourceReviewRefUnavailable "Review ref reserved for orphan cleanup.")
                            let request =
                                { RequestId = requestIdText; TaskId = taskId; AttemptId = attempt.AttemptId
                                  OwnerAgentId = ownerId; ReviewerId = attempt.ReviewerId; Version = 1
                                  CurrentRevision = 1; Status = "Unclaimed"; ClaimantId = null
                                  ClaimedRevision = 0; CreatedSequence = current.Header.NextSequence
                                  DecisionSequence = 0L; Feedback = null }
                            let evidenceStatus =
                                if String.IsNullOrWhiteSpace testEvidence then "NotProvided" else "OwnerReported"
                            let finalCheckpoint =
                                { TaskId = taskId; AttemptId = attempt.AttemptId
                                  OwnerAgentId = ownerId; AssignmentGeneration = generation
                                  Sequence = current.Header.NextSequence; Summary = summary
                                  NextAction = "Await independent review"; Kind = "Submission"
                                  SourceCommit = headOid; Evidence = testEvidence
                                  EvidenceStatus = evidenceStatus }
                            let revision =
                                { RequestId = requestIdText; Revision = 1; TaskId = taskId
                                  AttemptId = attempt.AttemptId; BaseCommit = attempt.BaseCommit
                                  HeadCommit = headOid; SourceRef = sourceRef; TargetRef = attempt.TargetRef
                                  ReviewRef = published; Summary = summary
                                  CreatedSequence = current.Header.NextSequence
                                  SpecBlobOid = attempt.SpecBlobOid
                                  SourceRepositoryId = attempt.SourceRepositoryId
                                  SourceRemote = attempt.SourceRemote
                                  SourcePushRemote = attempt.SourcePushRemote
                                  PublishedSourceOid = headOid
                                  CheckpointSequence = current.Header.NextSequence
                                  TestEvidence = testEvidence; EvidenceStatus = evidenceStatus }
                            return
                                [ change (requestPath taskId requestIdText) None request
                                  change (revisionPath taskId requestIdText 1) None revision
                                  change (checkpointPath taskId attempt.AttemptId current.Header.NextSequence) None finalCheckpoint
                                  change (attemptPath taskId attempt.AttemptId) (Some attemptOid)
                                    { attempt with Version = attempt.Version + 1
                                                   ActiveReviewRequestId = requestIdText
                                                   LatestReviewRequestId = requestIdText } ]
                        })
            }

    let submitReview binding operationId taskId ownerId expectedVersion generation requestId sourceRef headOid summary =
        submitReviewWithEvidence binding operationId taskId ownerId expectedVersion generation
            requestId sourceRef headOid summary null

    let updateReviewWithEvidence binding operationId taskId ownerId generation requestId expectedRequestVersion
                                 sourceRef headOid summary testEvidence =
        if not (validTaskId taskId) || not (validAgent ownerId) || requestId = Guid.Empty
           || expectedRequestVersion < 1 || generation < 1 || not (validText 4096 summary)
           || (not (String.IsNullOrEmpty testEvidence) && Encoding.UTF8.GetByteCount testEvidence > 8192) then
            Error(InvalidBinding "Invalid review update.")
        else
            let id = requestId.ToString("N")
            let action =
                intent operationId ownerId "UpdateReviewRequest"
                    {| taskId = taskId; ownerId = ownerId; generation = generation
                       requestId = requestId; expectedRequestVersion = expectedRequestVersion
                       sourceRef = sourceRef; headOid = headOid; summary = summary
                       testEvidence = testEvidence |}
            match LedgerStore.findApplied binding action with
            | Error failure -> Error failure
            | Ok(Some applied) -> Ok applied
            | Ok None -> result {
                let! snapshot = LedgerStore.fetch binding
                let! _, _, attempt, _ = getAttempt binding snapshot taskId
                let! original, _ = required<ReviewRequest> binding snapshot (requestPath taskId id)
                if original.Status = "Claimed" then
                    return! Error(ReviewAlreadyClaimed "Reviewer already claimed this request.")
                if original.Status <> "Unclaimed" || original.Version <> expectedRequestVersion
                   || attempt.OwnerAgentId <> ownerId || attempt.AssignmentGeneration <> generation
                   || attempt.ActiveReviewRequestId <> id
                   || String.IsNullOrWhiteSpace attempt.BaseCommit then
                    return! Error(ReviewVersionConflict "Review is not the current editable request.")
                let! _ = verifyPinnedSource binding attempt sourceRef
                // Source publication may precede a losing claim; it never mutates an older ref.
                let nextNumber = expectedRequestVersion + 1
                let! published =
                    preparedSource binding taskId id nextNumber attempt.BaseCommit headOid sourceRef
                return!
                    LedgerStore.transact binding action (fun current ->
                        result {
                            let! task, _, liveAttempt, _ = getAttempt binding current taskId
                            let! request, requestOid = required<ReviewRequest> binding current (requestPath taskId id)
                            if request.Status = "Claimed" then
                                return! Error(ReviewAlreadyClaimed "Reviewer has claimed this immutable revision.")
                            if request.Version <> expectedRequestVersion || request.CurrentRevision <> nextNumber - 1 then
                                return! Error(ReviewVersionConflict "Review request has changed.")
                            if task.Lifecycle <> "InProgress"
                               || liveAttempt.ActiveReviewRequestId <> id
                               || liveAttempt.OwnerAgentId <> ownerId
                               || liveAttempt.AssignmentGeneration <> generation
                               || request.OwnerAgentId <> ownerId || request.Status <> "Unclaimed"
                               || liveAttempt.BaseCommit <> attempt.BaseCommit then
                                return! Error(StaleEntityVersion "Review is no longer an editable request.")
                            let! _ = verifyPinnedSource binding liveAttempt sourceRef
                            do! LedgerStore.fetchReviewRef binding published headOid
                            let! retired = read<OrphanReviewReservation> binding current
                                                (orphanReservationPath taskId id nextNumber)
                            if retired.IsSome then
                                return! Error(SourceReviewRefUnavailable "Review ref reserved for orphan cleanup.")
                            let evidenceStatus =
                                if String.IsNullOrWhiteSpace testEvidence then "NotProvided" else "OwnerReported"
                            let finalCheckpoint =
                                { TaskId = taskId; AttemptId = liveAttempt.AttemptId
                                  OwnerAgentId = ownerId; AssignmentGeneration = generation
                                  Sequence = current.Header.NextSequence; Summary = summary
                                  NextAction = "Await independent review"; Kind = "Revision"
                                  SourceCommit = headOid; Evidence = testEvidence
                                  EvidenceStatus = evidenceStatus }
                            let revision =
                                { RequestId = id; Revision = nextNumber; TaskId = taskId
                                  AttemptId = liveAttempt.AttemptId; BaseCommit = liveAttempt.BaseCommit
                                  HeadCommit = headOid; SourceRef = sourceRef; TargetRef = liveAttempt.TargetRef
                                  ReviewRef = published; Summary = summary
                                  CreatedSequence = current.Header.NextSequence
                                  SpecBlobOid = liveAttempt.SpecBlobOid
                                  SourceRepositoryId = liveAttempt.SourceRepositoryId
                                  SourceRemote = liveAttempt.SourceRemote
                                  SourcePushRemote = liveAttempt.SourcePushRemote
                                  PublishedSourceOid = headOid
                                  CheckpointSequence = current.Header.NextSequence
                                  TestEvidence = testEvidence; EvidenceStatus = evidenceStatus }
                            return
                                [ change (requestPath taskId id) (Some requestOid)
                                    { request with Version = request.Version + 1; CurrentRevision = nextNumber }
                                  change (revisionPath taskId id nextNumber) None revision
                                  change (checkpointPath taskId liveAttempt.AttemptId current.Header.NextSequence) None finalCheckpoint ]
                        })
            }

    let updateReview binding operationId taskId ownerId generation requestId expectedRequestVersion
                     sourceRef headOid summary =
        updateReviewWithEvidence binding operationId taskId ownerId generation requestId
            expectedRequestVersion sourceRef headOid summary null

    // All immutable revisions protect their source refs, including superseded,
    // withdrawn and accepted snapshots. Compute the reference set once for
    // a full orphan inspection rather than rereading the whole ledger per ref.
    let private referencedReviewRefs binding snapshot =
        result {
            let! paths = LedgerStore.listAt binding snapshot "tasks"
            let revisions =
                paths |> List.filter (fun path ->
                    path.Contains("/reviews/", StringComparison.Ordinal)
                    && path.Contains("/revisions/", StringComparison.Ordinal)
                    && path.EndsWith(".json", StringComparison.Ordinal))
            let mutable refs = Set.empty
            for path in revisions do
                let! revision, _ = required<ReviewRevision> binding snapshot path
                refs <- Set.add revision.ReviewRef refs
            return refs
        }

    let private orphanReferenced binding snapshot gitRef =
        referencedReviewRefs binding snapshot
        |> Result.map (Set.contains gitRef)

    let inspectOrphanReviewRefs binding taskId =
        if not (validTaskId taskId) then Error(InvalidBinding "Invalid task ID.")
        else result {
            let! snapshot = LedgerStore.fetch binding
            let! refs = referencedReviewRefs binding snapshot
            let! remote = LedgerStore.listRemoteReviewRefs binding taskId
            let! paths = LedgerStore.listAt binding snapshot "tasks"
            let reservationPaths =
                paths |> List.filter (fun path ->
                    path.StartsWith($"tasks/{taskId}/orphan-cleanups/", StringComparison.Ordinal)
                    && path.EndsWith(".json", StringComparison.Ordinal))
            let mutable reservations = Map.empty
            for path in reservationPaths do
                let! reservation, _ = required<OrphanReviewReservation> binding snapshot path
                if reservation.TaskId <> taskId
                   || path <> orphanReservationPath taskId reservation.RequestId reservation.Revision
                   || reservation.ReviewRef <> orphanReviewRef taskId reservation.RequestId reservation.Revision
                   || (parseRemoteReviewRef taskId reservation.ReviewRef
                       <> Some(reservation.RequestId, reservation.Revision)) then
                    return! Error(LedgerIntegrityError "Malformed orphan cleanup reservation identity.")
                if Set.contains reservation.ReviewRef refs then
                    return! Error(LedgerIntegrityError "Reserved orphan unexpectedly has a ledger revision.")
                reservations <- Map.add reservation.ReviewRef reservation reservations
            let mutable orphans = []
            for gitRef, head in remote do
                match parseRemoteReviewRef taskId gitRef with
                | None -> return! Error(LedgerIntegrityError "Invalid advertised review ref.")
                | Some(requestId, revision) ->
                    if not (Set.contains gitRef refs) then
                        let reserved = Map.tryFind gitRef reservations
                        orphans <-
                            { TaskId = taskId; RequestId = requestId; Revision = revision
                              ReviewRef = gitRef; ObservedHead = head
                              ExpectedHead = reserved |> Option.map (fun r -> r.ExpectedHead) |> Option.defaultValue null
                              RemoteState =
                                match reserved with
                                | None -> "Unreserved"
                                | Some reservation when reservation.ExpectedHead = head -> "Reserved"
                                | Some _ -> "Retargeted"
                              Reserved = reserved.IsSome
                              ReservationSequence = reserved |> Option.map (fun r -> r.Sequence) |> Option.defaultValue 0L } :: orphans
            let remoteRefs = remote |> List.map fst |> Set.ofList
            for KeyValue(gitRef, reservation) in reservations do
                if not (Set.contains gitRef remoteRefs) then
                    orphans <-
                        { TaskId = taskId; RequestId = reservation.RequestId
                          Revision = reservation.Revision; ReviewRef = gitRef
                          ObservedHead = null; ExpectedHead = reservation.ExpectedHead
                          RemoteState = "Absent"
                          Reserved = true; ReservationSequence = reservation.Sequence } :: orphans
            return snapshot.Commit, orphans |> List.sortBy (fun o -> o.RequestId, o.Revision)
        }

    // Ledger reservation serializes cleanup against a concurrent Submit/Update
    // CAS. A source ref is never removed merely because a ledger push lost.
    let reserveOrphanReviewRef binding operationId taskId requestId revision expectedHead actor reason =
        if not (validTaskId taskId) || requestId = Guid.Empty || revision < 1
           || not (validAgent actor) || not (validText 2048 reason)
           || isNull expectedHead || not (Regex.IsMatch(expectedHead, "^[0-9a-f]{40}([0-9a-f]{24})?$")) then
            Error(InvalidBinding "Orphan reservation requires exact ref identity, SHA, actor and reason.")
        else
            let id = requestId.ToString("N")
            let gitRef = orphanReviewRef taskId id revision
            LedgerStore.transact binding
                (intent operationId actor "ReserveOrphanReviewRef"
                    {| taskId = taskId; requestId = requestId; revision = revision
                       expectedHead = expectedHead; actor = actor; reason = reason |})
                (fun snapshot ->
                    result {
                        let! referenced = orphanReferenced binding snapshot gitRef
                        if referenced then
                            return! Error(ReviewAccessDenied "Ledger revision protects this source ref; it is not an orphan.")
                        let! reservation = read<OrphanReviewReservation> binding snapshot
                                                (orphanReservationPath taskId id revision)
                        if reservation.IsSome then
                            return! Error(StaleEntityVersion "Orphan cleanup reservation already exists.")
                        let! actual = LedgerStore.inspectRemoteReviewRef binding gitRef
                        if actual <> Some expectedHead then
                            return! Error(SourceReviewRefUnavailable "Orphan SHA changed or source ref vanished.")
                        let record =
                            { TaskId = taskId; RequestId = id; Revision = revision
                              ReviewRef = gitRef; ExpectedHead = expectedHead
                              ActorId = actor; Reason = reason
                              Sequence = snapshot.Header.NextSequence }
                        return [ change (orphanReservationPath taskId id revision) None record ]
                    })

    // An explicit second step; never infer a safe deletion from a timeout.
    // The ledger tombstone is permanent and prevents the source ref being
    // adopted by a delayed or retried review submission.
    let deleteReservedOrphanReviewRef binding taskId requestId revision expectedHead =
        if not (validTaskId taskId) || requestId = Guid.Empty || revision < 1 then
            Error(InvalidBinding "Invalid reserved source cleanup request.")
        else result {
            let! snapshot = LedgerStore.fetch binding
            let id = requestId.ToString("N")
            let gitRef = orphanReviewRef taskId id revision
            let! reservation, _ = required<OrphanReviewReservation> binding snapshot
                                    (orphanReservationPath taskId id revision)
            if reservation.ExpectedHead <> expectedHead || reservation.ReviewRef <> gitRef
               || reservation.TaskId <> taskId || reservation.RequestId <> id
               || reservation.Revision <> revision then
                return! Error(LedgerIntegrityError "Orphan reservation does not match requested deletion.")
            let! referenced = orphanReferenced binding snapshot gitRef
            if referenced then
                return! Error(LedgerIntegrityError "Reserved ref became referenced; refusing cleanup.")
            let! result = LedgerStore.deleteReservedReviewRef binding gitRef expectedHead
            return snapshot.Commit, result
        }

    let claimReview binding operationId taskId requestId reviewerId expectedVersion expectedRevision =
        if not (validTaskId taskId) || requestId = Guid.Empty || not (validAgent reviewerId)
           || expectedVersion < 1 || expectedRevision < 1 then
            Error(InvalidBinding "Invalid review claim.")
        else
            let id = requestId.ToString("N")
            LedgerStore.transact binding
                (intent operationId reviewerId "ClaimReview"
                    {| taskId = taskId; requestId = requestId; reviewerId = reviewerId
                       expectedVersion = expectedVersion; expectedRevision = expectedRevision |})
                (fun snapshot ->
                    result {
                        let! task, _, attempt, _ = getAttempt binding snapshot taskId
                        let! request, requestOid = required<ReviewRequest> binding snapshot (requestPath taskId id)
                        let! _ = required<ReviewAgent> binding snapshot (agentPath reviewerId)
                        if reviewerId = attempt.OwnerAgentId then
                            return! Error(ReviewAccessDenied "Implementation owner cannot claim own review.")
                        if request.Status <> "Unclaimed" || request.Version <> expectedVersion
                           || request.CurrentRevision <> expectedRevision
                           || attempt.ActiveReviewRequestId <> id || task.Lifecycle <> "InProgress" then
                            return! Error(ReviewVersionConflict "Review was replaced, claimed or closed.")
                        let! revision, _ =
                            required<ReviewRevision> binding snapshot (revisionPath taskId id expectedRevision)
                        let! sourceIdentity = verifyPinnedSource binding attempt attempt.SourceRef
                        if revision.SourceRepositoryId <> sourceIdentity.RepositoryId
                           || revision.SourceRemote <> sourceIdentity.FetchUrl
                           || revision.SourcePushRemote <> sourceIdentity.PushUrl then
                            return! Error(SourceUnavailable "Review source identity does not match the attempt.")
                        do! LedgerStore.fetchReviewRef binding revision.ReviewRef revision.HeadCommit
                        do! LedgerStore.verifyReviewDiff binding revision.BaseCommit revision.HeadCommit
                        return [ change (requestPath taskId id) (Some requestOid)
                                    { request with Version = request.Version + 1; Status = "Claimed"
                                                   ReviewerId = reviewerId; ClaimantId = reviewerId
                                                   ClaimedRevision = expectedRevision }
                                 change (reviewEventPath taskId id snapshot.Header.NextSequence) None
                                    (claimEvent taskId request revision snapshot.Header.NextSequence
                                        operationId "Claim" reviewerId null reviewerId "Claimed frozen review revision") ]
                    })

    let decideWithEvidence binding operationId taskId requestId reviewerId expectedVersion verdict feedback inspected tested testDetails =
        if not (validTaskId taskId) || requestId = Guid.Empty || not (validAgent reviewerId)
           || expectedVersion < 1
           || (verdict = RequestChanges && not (validText 4096 feedback))
           || (not (isNull feedback) && Encoding.UTF8.GetByteCount feedback > 4096)
           || (tested && (not (validText 8192 testDetails)))
           || (not (isNull testDetails) && Encoding.UTF8.GetByteCount testDetails > 8192) then
            Error(InvalidBinding "Invalid review decision.")
        else
            let id = requestId.ToString("N")
            let status = match verdict with | Accept -> "Accepted" | RequestChanges -> "ChangesRequested"
            LedgerStore.transact binding
                (intent operationId reviewerId "ReviewDecision"
                    {| taskId = taskId; requestId = requestId; reviewerId = reviewerId
                       expectedVersion = expectedVersion; status = status; feedback = feedback
                       inspected = inspected; tested = tested; testDetails = testDetails |})
                (fun snapshot ->
                    result {
                        let! _, _, attempt, attemptOid = getAttempt binding snapshot taskId
                        let! request, requestOid = required<ReviewRequest> binding snapshot (requestPath taskId id)
                        if reviewerId <> request.ClaimantId || reviewerId = request.OwnerAgentId then
                            return! Error(ReviewAccessDenied "Reviewer must hold the claim.")
                        if request.Status <> "Claimed" || request.Version <> expectedVersion
                           || request.ClaimedRevision <> request.CurrentRevision
                           || attempt.ActiveReviewRequestId <> id then
                            return! Error(ReviewVersionConflict "Review is not the current claimed revision.")
                        let! revision, _ =
                            required<ReviewRevision> binding snapshot
                                (revisionPath taskId id request.ClaimedRevision)
                        let! sourceIdentity = verifyPinnedSource binding attempt attempt.SourceRef
                        if revision.SourceRepositoryId <> sourceIdentity.RepositoryId
                           || revision.SourceRemote <> sourceIdentity.FetchUrl
                           || revision.SourcePushRemote <> sourceIdentity.PushUrl then
                            return! Error(SourceUnavailable "Review source identity does not match the attempt.")
                        do! LedgerStore.fetchReviewRef binding revision.ReviewRef revision.HeadCommit
                        do! LedgerStore.verifyReviewDiff binding revision.BaseCommit revision.HeadCommit
                        if revision.BaseCommit <> attempt.BaseCommit then
                            return! Error(StaleEntityVersion "Reviewed revision does not cover the pinned task base.")
                        let requeue = verdict = RequestChanges && attempt.Phase = "Finished"
                        let nextAttempt =
                            { attempt with Version = attempt.Version + 1
                                           Phase = if requeue then "Queued" else attempt.Phase
                                           AssignmentGeneration =
                                               if requeue then attempt.AssignmentGeneration + 1
                                               else attempt.AssignmentGeneration
                                           IssuedSequence =
                                               if requeue then snapshot.Header.NextSequence else attempt.IssuedSequence
                                           ActiveReviewRequestId = null
                                           ReviewFeedback = feedback
                                           FeedbackSequence = snapshot.Header.NextSequence
                                           AcceptedReviewRequestId =
                                               if verdict = Accept then id else null
                                           AcceptedReviewRevision =
                                               if verdict = Accept then request.CurrentRevision else 0
                                           AcceptedHead =
                                               if verdict = Accept then revision.HeadCommit else null }
                        let review =
                            { request with Version = request.Version + 1; Status = status
                                           DecisionSequence = snapshot.Header.NextSequence; Feedback = feedback }
                        let notice =
                            { TaskId = taskId; AttemptId = attempt.AttemptId; RequestId = id
                              OwnerAgentId = attempt.OwnerAgentId
                              DecisionSequence = snapshot.Header.NextSequence
                              Status = status; Feedback = feedback
                              Acknowledged = false; Version = 1 }
                        let decisionEvidence =
                            { RequestId = id; Revision = request.ClaimedRevision
                              TaskId = taskId; AttemptId = attempt.AttemptId
                              ReviewerId = reviewerId; Sequence = snapshot.Header.NextSequence
                              Status = status; Inspected = inspected; Tested = tested
                              TestDetails = testDetails; Feedback = feedback
                              ReviewedHead = revision.HeadCommit
                              ReviewRef = revision.ReviewRef
                              SpecBlobOid = revision.SpecBlobOid
                              SourceRemote = revision.SourceRemote }
                        let instructions =
                            if String.IsNullOrWhiteSpace feedback then
                                "Address the changes requested in the review."
                            else feedback
                        return
                            [ change (requestPath taskId id) (Some requestOid) review
                              change (decisionPath taskId id snapshot.Header.NextSequence) None decisionEvidence
                              change (reviewEventPath taskId id snapshot.Header.NextSequence) None
                                  (claimEvent taskId request revision snapshot.Header.NextSequence
                                      operationId "Decision" reviewerId reviewerId reviewerId
                                      (if String.IsNullOrWhiteSpace feedback then status else feedback))
                              change (feedbackPath taskId id) None notice
                              change (attemptPath taskId attempt.AttemptId) (Some attemptOid) nextAttempt ]
                            @ (if requeue then
                                   [ change (issuePath taskId attempt.AttemptId nextAttempt.AssignmentGeneration) None
                                       (issue taskId nextAttempt nextAttempt.AssignmentGeneration reviewerId
                                           "ReviewChanges" instructions snapshot.Header.NextSequence) ]
                               else [])
                    })


    let decide binding operationId taskId requestId reviewerId expectedVersion verdict feedback =
        decideWithEvidence binding operationId taskId requestId reviewerId expectedVersion
            verdict feedback false false null

    let getDecisionEvidence binding taskId requestId =
        if not (validTaskId taskId) || requestId = Guid.Empty then
            Error(InvalidBinding "Invalid review evidence lookup.")
        else result {
            let! snapshot = LedgerStore.fetch binding
            let id = requestId.ToString("N")
            let! request, _ = required<ReviewRequest> binding snapshot (requestPath taskId id)
            if request.DecisionSequence < 1L then
                return! Error(StaleEntityVersion "Review has no decision evidence.")
            let! evidence, _ =
                required<ReviewDecisionEvidence> binding snapshot
                    (decisionPath taskId id request.DecisionSequence)
            if evidence.Revision <> request.ClaimedRevision
               || evidence.RequestId <> id
               || evidence.Status <> request.Status then
                return! Error(LedgerIntegrityError "Review decision evidence disagrees with claimed revision.")
            return snapshot.Commit, evidence
        }

    let withdrawReview binding operationId taskId requestId actor expectedVersion reason =
        if not (validTaskId taskId) || requestId = Guid.Empty || not (validAgent actor)
           || expectedVersion < 1 || not (validText 4096 reason) then
            Error(InvalidBinding "Invalid review withdrawal.")
        else
            let id = requestId.ToString("N")
            LedgerStore.transact binding
                (intent operationId actor "WithdrawReview"
                    {| taskId = taskId; requestId = requestId; actor = actor
                       expectedVersion = expectedVersion; reason = reason |})
                (fun snapshot ->
                    result {
                        let! _, _, attempt, attemptOid = getAttempt binding snapshot taskId
                        let! request, requestOid = required<ReviewRequest> binding snapshot (requestPath taskId id)
                        if request.Version <> expectedVersion
                           || attempt.ActiveReviewRequestId <> id
                           || (request.Status <> "Unclaimed" && request.Status <> "Claimed") then
                            return! Error(ReviewVersionConflict "Review is no longer withdrawable.")
                        let authorized =
                            if request.Status = "Unclaimed" then actor = request.OwnerAgentId || actor = "Orchestrator"
                            else (actor = request.ClaimantId && actor <> request.OwnerAgentId)
                                 || actor = "Orchestrator"
                        if not authorized then
                            return! Error(ReviewAccessDenied "Withdrawal of a claimed review requires the claimant.")
                        let! revision, _ = required<ReviewRevision> binding snapshot
                                                (revisionPath taskId id request.CurrentRevision)
                        return
                            [ change (reviewEventPath taskId id snapshot.Header.NextSequence) None
                                (claimEvent taskId request revision snapshot.Header.NextSequence
                                    operationId "Withdraw" actor request.ClaimantId null reason)
                              change (requestPath taskId id) (Some requestOid)
                                { request with Version = request.Version + 1; Status = "Withdrawn"
                                               DecisionSequence = snapshot.Header.NextSequence; Feedback = reason }
                              change (attemptPath taskId attempt.AttemptId) (Some attemptOid)
                                { attempt with Version = attempt.Version + 1; ActiveReviewRequestId = null } ]
                    })

    // Releasing reviewer occupancy never returns a frozen revision to Unclaimed.
    let releaseReviewClaim binding operationId taskId requestId actor expectedVersion reason =
        if not (validTaskId taskId) || requestId = Guid.Empty || not (validAgent actor)
           || expectedVersion < 1 || not (validText 2048 reason) then
            Error(InvalidBinding "Release requires exact review version, actor and reason.")
        else
            let id = requestId.ToString("N")
            LedgerStore.transact binding
                (intent operationId actor "ReleaseReviewClaim"
                    {| taskId = taskId; requestId = requestId; actor = actor
                       expectedVersion = expectedVersion; reason = reason |})
                (fun snapshot ->
                    result {
                        let! _, _, attempt, _ = getAttempt binding snapshot taskId
                        let! request, oid = required<ReviewRequest> binding snapshot (requestPath taskId id)
                        if request.Status <> "Claimed" || request.Version <> expectedVersion
                           || attempt.ActiveReviewRequestId <> id
                           || String.IsNullOrWhiteSpace request.ClaimantId then
                            return! Error(ReviewVersionConflict "Claim already released, transferred or decided.")
                        if actor <> request.ClaimantId && actor <> "Orchestrator" then
                            return! Error(ReviewAccessDenied "Only claimant or orchestrator may release a review.")
                        // Releasing occupancy is ledger-only. The source
                        // snapshot may be temporarily unavailable; no source
                        // approval or reviewer claim is granted by release.
                        let! revision, _ = required<ReviewRevision> binding snapshot
                                                (revisionPath taskId id request.ClaimedRevision)
                        return
                            [ change (requestPath taskId id) (Some oid)
                                { request with Version = request.Version + 1; ClaimantId = null }
                              change (reviewEventPath taskId id snapshot.Header.NextSequence) None
                                (claimEvent taskId request revision snapshot.Header.NextSequence
                                    operationId "Release" actor request.ClaimantId null reason) ]
                    })

    let private reassignReviewerCore binding operationId taskId requestId actor nextReviewer expectedVersion reason includeReason =
        if not (validTaskId taskId) || requestId = Guid.Empty
           || not (validAgent actor) || not (validAgent nextReviewer) || expectedVersion < 1
           || not (validText 2048 reason) then
            Error(InvalidBinding "Handoff requires exact version, next reviewer and reason.")
        else
            let id = requestId.ToString("N")
            let operation =
                if includeReason then
                    intent operationId actor "ReassignReviewer"
                        {| taskId = taskId; requestId = requestId; actor = actor
                           nextReviewer = nextReviewer; expectedVersion = expectedVersion; reason = reason |}
                else
                    intent operationId actor "ReassignReviewer"
                        {| taskId = taskId; requestId = requestId; actor = actor
                           nextReviewer = nextReviewer; expectedVersion = expectedVersion |}
            LedgerStore.transact binding operation
                (fun snapshot ->
                    result {
                        let! _, _, attempt, _ = getAttempt binding snapshot taskId
                        let! request, requestOid = required<ReviewRequest> binding snapshot (requestPath taskId id)
                        let! _ = required<ReviewAgent> binding snapshot (agentPath nextReviewer)
                        if request.Status <> "Claimed" || request.Version <> expectedVersion
                           || attempt.ActiveReviewRequestId <> id then
                            return! Error(ReviewVersionConflict "Claimed review has changed.")
                        if (actor <> request.ClaimantId && actor <> "Orchestrator")
                           || (String.IsNullOrWhiteSpace request.ClaimantId && actor <> "Orchestrator")
                           || nextReviewer = request.OwnerAgentId
                           || nextReviewer = request.ClaimantId then
                            return! Error(ReviewAccessDenied "Only claimant or orchestrator may hand off to an independent reviewer.")
                        let! revision, _ = required<ReviewRevision> binding snapshot
                                                (revisionPath taskId id request.ClaimedRevision)
                        do! LedgerStore.fetchReviewRef binding revision.ReviewRef revision.HeadCommit
                        return
                            [ change (requestPath taskId id) (Some requestOid)
                                { request with Version = request.Version + 1
                                               ReviewerId = nextReviewer; ClaimantId = nextReviewer }
                              change (reviewEventPath taskId id snapshot.Header.NextSequence) None
                                (claimEvent taskId request revision snapshot.Header.NextSequence
                                    operationId "Reassign" actor request.ClaimantId nextReviewer reason) ]
                    })

    let reassignReviewerWithReason binding operationId taskId requestId actor nextReviewer expectedVersion reason =
        reassignReviewerCore binding operationId taskId requestId actor nextReviewer
            expectedVersion reason true

    // Historical V2 handoff shape remains replayable under its original UUID.
    let reassignReviewer binding operationId taskId requestId actor nextReviewer expectedVersion =
        reassignReviewerCore binding operationId taskId requestId actor nextReviewer
            expectedVersion "Explicit reviewer handoff" false

    let getReviewClaimEvents binding taskId requestId =
        if not (validTaskId taskId) || requestId = Guid.Empty then
            Error(InvalidBinding "Invalid review event history query.")
        else result {
            let! snapshot = LedgerStore.fetch binding
            let id = requestId.ToString("N")
            let! _ = required<ReviewRequest> binding snapshot (requestPath taskId id)
            let! paths = LedgerStore.listAt binding snapshot "tasks"
            let history = paths |> List.filter (fun path ->
                path.StartsWith($"tasks/{taskId}/reviews/{id}/events/", StringComparison.Ordinal)
                && path.EndsWith(".json", StringComparison.Ordinal))
            let mutable events = []
            for path in history do
                let! event, _ = required<ReviewClaimEvent> binding snapshot path
                if path <> reviewEventPath taskId id event.Sequence
                   || event.TaskId <> taskId || event.RequestId <> id then
                    return! Error(LedgerIntegrityError "Review claim history has an inconsistent identity.")
                events <- event :: events
            return snapshot.Commit, events |> List.sortBy (fun event -> event.Sequence)
        }

    let awaitingReviewReassignment binding =
        result {
            let! snapshot = LedgerStore.fetch binding
            let! paths = LedgerStore.listAt binding snapshot "tasks"
            let mutable requests = []
            for path in paths do
                if path.EndsWith("/request.json", StringComparison.Ordinal) then
                    let! request, _ = required<ReviewRequest> binding snapshot path
                    if request.Status = "Claimed"
                       && String.IsNullOrWhiteSpace request.ClaimantId then
                        requests <- request :: requests
            return snapshot.Commit,
                (requests |> List.sortBy (fun r -> r.CreatedSequence, r.TaskId, r.RequestId))
        }

    let finalizeImplementation binding operationId taskId ownerId expectedAttemptVersion requestId =
        if not (validTaskId taskId) || requestId = Guid.Empty || not (validAgent ownerId) then
            Error(InvalidBinding "Invalid finalize request.")
        else
            let id = requestId.ToString("N")
            LedgerStore.transact binding
                (intent operationId ownerId "FinalizeImplementation"
                    {| taskId = taskId; ownerId = ownerId; expectedAttemptVersion = expectedAttemptVersion
                       requestId = requestId |})
                (fun snapshot ->
                    result {
                        let! task, taskOid, attempt, attemptOid = getAttempt binding snapshot taskId
                        let! request, _ = required<ReviewRequest> binding snapshot (requestPath taskId id)
                        if task.Lifecycle <> "InProgress" || attempt.OwnerAgentId <> ownerId
                           || attempt.Phase <> "Finished"
                           || attempt.Version <> expectedAttemptVersion
                           || not (String.IsNullOrEmpty attempt.ActiveReviewRequestId)
                           || request.Status <> "Accepted" || attempt.AcceptedReviewRequestId <> id
                           || attempt.LatestReviewRequestId <> id
                           || attempt.AcceptedReviewRevision <> request.CurrentRevision then
                            return! Error(StaleEntityVersion "Implementation has no complete accepted review.")
                        let! revision, _ =
                            required<ReviewRevision> binding snapshot
                                (revisionPath taskId id request.CurrentRevision)
                        if revision.BaseCommit <> attempt.BaseCommit || revision.HeadCommit <> attempt.AcceptedHead then
                            return! Error(LedgerIntegrityError "Finalization does not match approved cumulative patch.")
                        let! sourceIdentity = verifyPinnedSource binding attempt attempt.SourceRef
                        if revision.SourceRepositoryId <> sourceIdentity.RepositoryId
                           || revision.SourceRemote <> sourceIdentity.FetchUrl
                           || revision.SourcePushRemote <> sourceIdentity.PushUrl then
                            return! Error(SourceUnavailable "Review source identity does not match the attempt.")
                        do! LedgerStore.fetchReviewRef binding revision.ReviewRef revision.HeadCommit
                        // Finalization is a task/review operation, never an agent-state change.
                        // The owner may already be Working on its next assignment.
                        return
                            [ change (taskPath taskId) (Some taskOid)
                                { task with Version = task.Version + 1; Lifecycle = "Implemented" }
                              change (attemptPath taskId attempt.AttemptId) (Some attemptOid)
                                { attempt with Version = attempt.Version + 1; Phase = "Accepted" } ]
                    })


    // Integration only handles the strictly provable fast-forward case. The
    // observed remote head must equal the pinned approved review commit.
    // Rewritten commits, merge commits and cherry-pick equivalence require a
    // separately designed proof workflow; never assume equivalence from refs.
    let planIntegration binding operationId taskId actionId requestId expectedAttemptVersion
                        targetRef expectedTargetHead =
        if not (validTaskId taskId) || actionId = Guid.Empty || requestId = Guid.Empty
           || expectedAttemptVersion < 1
           || not (validBranch targetRef)
           || targetRef = "refs/heads/jarvis-orchestration"
           || targetRef.StartsWith("refs/heads/jarvis-review/", StringComparison.Ordinal) then
            Error(InvalidBinding "Invalid integration plan identifiers or source target.")
        else
            let actionText = actionId.ToString("N")
            let reviewText = requestId.ToString("N")
            LedgerStore.transact binding
                (intent operationId "Orchestrator" "PlanIntegration"
                    {| taskId = taskId; actionId = actionId; requestId = requestId
                       expectedAttemptVersion = expectedAttemptVersion
                       targetRef = targetRef; expectedTargetHead = expectedTargetHead |})
                (fun snapshot ->
                    result {
                        let! task, _, attempt, attemptOid = getAttempt binding snapshot taskId
                        let! request, _ =
                            required<ReviewRequest> binding snapshot (requestPath taskId reviewText)
                        if task.Lifecycle <> "Implemented" || attempt.Phase <> "Accepted"
                           || attempt.Version <> expectedAttemptVersion
                           || attempt.LatestReviewRequestId <> reviewText
                           || attempt.AcceptedReviewRequestId <> reviewText
                           || request.Status <> "Accepted"
                           || attempt.AcceptedReviewRevision <> request.CurrentRevision
                           || not (String.IsNullOrEmpty attempt.ActiveReviewRequestId)
                           || not (String.IsNullOrEmpty attempt.IntegrationActionId)
                           || targetRef <> attempt.TargetRef then
                            return! Error(StaleEntityVersion "Task is not finalized with an unplanned accepted review.")
                        let! revision, _ =
                            required<ReviewRevision> binding snapshot
                                (revisionPath taskId reviewText request.CurrentRevision)
                        if revision.HeadCommit <> attempt.AcceptedHead
                           || revision.BaseCommit <> attempt.BaseCommit
                           || revision.TargetRef <> targetRef then
                            return! Error(LedgerIntegrityError "Integration candidate differs from accepted cumulative review.")
                        let! existing = read<ReviewIntegration> binding snapshot (integrationPath taskId actionText)
                        if existing.IsSome then
                            return! Error(StaleEntityVersion "Integration action ID is already used.")
                        let! sourceIdentity = verifyPinnedSource binding attempt attempt.SourceRef
                        if revision.SourceRepositoryId <> sourceIdentity.RepositoryId
                           || revision.SourceRemote <> sourceIdentity.FetchUrl
                           || revision.SourcePushRemote <> sourceIdentity.PushUrl then
                            return! Error(SourceUnavailable "Review source identity does not match the attempt.")
                        do! LedgerStore.fetchReviewRef binding revision.ReviewRef revision.HeadCommit
                        do! LedgerStore.verifyReviewDiff binding revision.BaseCommit revision.HeadCommit
                        let! observed = LedgerStore.readRemoteTarget binding targetRef
                        if observed <> expectedTargetHead then
                            return! Error(StaleEntityVersion "Remote target changed before integration planning.")
                        do! LedgerStore.verifySourceAncestry binding revision.BaseCommit observed
                        do! LedgerStore.verifySourceAncestry binding observed revision.HeadCommit
                        let plan =
                            { ActionId = actionText; TaskId = taskId; AttemptId = attempt.AttemptId
                              Version = 1; Status = "PendingSourceAction"
                              ReviewRequestId = reviewText; ReviewRevision = request.CurrentRevision
                              BaseCommit = revision.BaseCommit; ApprovedHead = revision.HeadCommit
                              ReviewRef = revision.ReviewRef; TargetRef = targetRef
                              ExpectedTargetHead = observed
                              SourceRepositoryId = revision.SourceRepositoryId
                              SourceRemote = revision.SourceRemote
                              SourcePushRemote = revision.SourcePushRemote
                              PlannedSequence = snapshot.Header.NextSequence
                              PlannedAtUtc = DateTimeOffset.UtcNow.ToString("O")
                              ObservedTargetHead = null; VerifiedSequence = 0L
                              VerifiedAtUtc = null; VerificationMethod = null }
                        return
                            [ change (integrationPath taskId actionText) None plan
                              change (attemptPath taskId attempt.AttemptId) (Some attemptOid)
                                { attempt with Version = attempt.Version + 1; IntegrationActionId = actionText } ]
                    })

    let markIntegrationPendingPush binding operationId taskId actionId expectedVersion =
        if not (validTaskId taskId) || actionId = Guid.Empty || expectedVersion < 1 then
            Error(InvalidBinding "Invalid integration pending-push transition.")
        else
            let id = actionId.ToString("N")
            LedgerStore.transact binding
                (intent operationId "Orchestrator" "MarkIntegrationPendingPush"
                    {| taskId = taskId; actionId = actionId; expectedVersion = expectedVersion |})
                (fun snapshot ->
                    result {
                        let! task, _, attempt, _ = getAttempt binding snapshot taskId
                        let! action, actionOid =
                            required<ReviewIntegration> binding snapshot (integrationPath taskId id)
                        if task.Lifecycle <> "Implemented" || attempt.IntegrationActionId <> id
                           || action.Status <> "PendingSourceAction" || action.Version <> expectedVersion then
                            return! Error(StaleEntityVersion "Integration is not awaiting an explicit source push.")
                        return
                            [ change (integrationPath taskId id) (Some actionOid)
                                { action with Version = action.Version + 1; Status = "PendingPush" } ]
                    })

    let inspectIntegration binding taskId actionId =
        if not (validTaskId taskId) || actionId = Guid.Empty then
            Error(InvalidBinding "Invalid integration inspection identifier.")
        else
            result {
                let! snapshot = LedgerStore.fetch binding
                let! action, _ =
                    required<ReviewIntegration> binding snapshot
                        (integrationPath taskId (actionId.ToString("N")))
                let! actual = LedgerStore.readRemoteTarget binding action.TargetRef
                let state =
                    if action.Status = "Recorded" then
                        if actual = action.ApprovedHead then "Recorded"
                        else "RecordedTargetMoved"
                    elif actual = action.ApprovedHead then "ReadyToRecord"
                    elif actual = action.ExpectedTargetHead then "PendingPush"
                    else "TargetChanged"
                return
                    { LedgerCommit = snapshot.Commit
                      Integration = action; ObservedTargetHead = actual
                      RemoteState = state }
            }

    let recordIntegration binding operationId taskId actionId expectedVersion =
        if not (validTaskId taskId) || actionId = Guid.Empty || expectedVersion < 1 then
            Error(InvalidBinding "Invalid integration recording request.")
        else
            let id = actionId.ToString("N")
            LedgerStore.transact binding
                (intent operationId "Orchestrator" "RecordIntegration"
                    {| taskId = taskId; actionId = actionId; expectedVersion = expectedVersion |})
                (fun snapshot ->
                    result {
                        let! task, taskOid, attempt, _ = getAttempt binding snapshot taskId
                        let! action, actionOid =
                            required<ReviewIntegration> binding snapshot (integrationPath taskId id)
                        if task.Lifecycle <> "Implemented" || attempt.Phase <> "Accepted"
                           || attempt.IntegrationActionId <> id
                           || action.Version <> expectedVersion
                           || not (List.contains action.Status [ "PendingSourceAction"; "PendingPush" ])
                           || action.SourceRemote <> binding.ApprovedFetchUrl
                           || action.SourcePushRemote <> binding.ApprovedPushUrl
                           || action.SourceRepositoryId <> attempt.SourceRepositoryId
                           || action.TaskId <> taskId || action.AttemptId <> attempt.AttemptId
                           || action.ReviewRequestId <> attempt.AcceptedReviewRequestId
                           || action.ReviewRevision <> attempt.AcceptedReviewRevision
                           || action.ApprovedHead <> attempt.AcceptedHead
                           || action.BaseCommit <> attempt.BaseCommit
                           || action.TargetRef <> attempt.TargetRef then
                            return! Error(StaleEntityVersion "Current integration no longer matches finalized task.")
                        let! revision, _ =
                            required<ReviewRevision> binding snapshot
                                (revisionPath taskId action.ReviewRequestId action.ReviewRevision)
                        if revision.HeadCommit <> action.ApprovedHead
                           || revision.BaseCommit <> action.BaseCommit
                           || revision.ReviewRef <> action.ReviewRef
                           || revision.TargetRef <> action.TargetRef then
                            return! Error(LedgerIntegrityError "Accepted revision evidence differs from integration plan.")
                        let! _ = verifyPinnedSource binding attempt attempt.SourceRef
                        if revision.SourceRepositoryId <> action.SourceRepositoryId
                           || revision.SourcePushRemote <> action.SourcePushRemote
                           || revision.SourceRemote <> action.SourceRemote then
                            return! Error(SourceUnavailable "Integration source differs from accepted revision.")
                        do! LedgerStore.fetchReviewRef binding action.ReviewRef action.ApprovedHead
                        do! LedgerStore.verifyReviewDiff binding action.BaseCommit action.ApprovedHead
                        do! LedgerStore.verifySourceAncestry binding action.ExpectedTargetHead action.ApprovedHead
                        let! observed = LedgerStore.readRemoteTarget binding action.TargetRef
                        if observed <> action.ApprovedHead then
                            return! Error(StaleEntityVersion
                                "Remote target does not equal approved head. Push only the accepted commit and retry verification.")
                        let integrated =
                            { action with Version = action.Version + 1; Status = "Recorded"
                                          ObservedTargetHead = observed
                                          VerifiedSequence = snapshot.Header.NextSequence
                                          VerifiedAtUtc = DateTimeOffset.UtcNow.ToString("O")
                                          VerificationMethod = "RemoteExactFastForwardCommit" }
                        return
                            [ change (integrationPath taskId id) (Some actionOid) integrated
                              change (taskPath taskId) (Some taskOid)
                                { task with Version = task.Version + 1; Lifecycle = "Integrated" } ]
                    })

    let closeIntegratedTask binding operationId taskId actionId expectedTaskVersion =
        if not (validTaskId taskId) || actionId = Guid.Empty || expectedTaskVersion < 1 then
            Error(InvalidBinding "Invalid task closure request.")
        else
            let id = actionId.ToString("N")
            LedgerStore.transact binding
                (intent operationId "Orchestrator" "CloseIntegratedTask"
                    {| taskId = taskId; actionId = actionId; expectedTaskVersion = expectedTaskVersion |})
                (fun snapshot ->
                    result {
                        let! task, taskOid, attempt, _ = getAttempt binding snapshot taskId
                        let! action, _ =
                            required<ReviewIntegration> binding snapshot (integrationPath taskId id)
                        if task.Version <> expectedTaskVersion || task.Lifecycle <> "Integrated"
                           || attempt.IntegrationActionId <> id || action.Status <> "Recorded"
                           || action.ObservedTargetHead <> action.ApprovedHead then
                            return! Error(StaleEntityVersion "Task does not have a verified recorded integration.")
                        return
                            [ change (taskPath taskId) (Some taskOid)
                                { task with Version = task.Version + 1; Lifecycle = "Closed" } ]
                    })

    let acknowledgeReviewFeedback binding operationId taskId ownerId decisionSequence =
        if not (validTaskId taskId) || not (validAgent ownerId) || decisionSequence <= 0L then
            Error(InvalidBinding "Invalid review feedback acknowledgment.")
        else
            LedgerStore.transact binding
                (intent operationId ownerId "AcknowledgeReviewFeedback"
                    {| taskId = taskId; ownerId = ownerId; decisionSequence = decisionSequence |})
                (fun snapshot ->
                    result {
                        let! _, _, attempt, attemptOid = getAttempt binding snapshot taskId
                        if attempt.OwnerAgentId <> ownerId then
                            return! Error(ReviewAccessDenied "Only the task owner can acknowledge feedback.")
                        let! paths = LedgerStore.listAt binding snapshot "tasks"
                        let notices =
                            paths |> List.filter (fun path ->
                                path.StartsWith($"tasks/{taskId}/reviews/", StringComparison.Ordinal)
                                && path.EndsWith("/notification.json", StringComparison.Ordinal))
                        let mutable found = []
                        for path in notices do
                            let! notification, oid = required<ReviewFeedbackNotice> binding snapshot path
                            if notification.DecisionSequence = decisionSequence then
                                found <- (path, notification, oid) :: found
                        match found with
                        | [ (path, notification, oid) ] when
                              notification.OwnerAgentId = ownerId
                              && notification.TaskId = taskId
                              && notification.AttemptId = attempt.AttemptId
                              && not notification.Acknowledged ->
                            return
                                [ change path (Some oid)
                                    { notification with Acknowledged = true
                                                        Version = notification.Version + 1 }
                                  change (attemptPath taskId attempt.AttemptId) (Some attemptOid)
                                    { attempt with Version = attempt.Version + 1
                                                   AcknowledgedFeedbackSequence =
                                                       max attempt.AcknowledgedFeedbackSequence decisionSequence } ]
                        | _ -> return! Error(StaleEntityVersion "Feedback decision was missing or already acknowledged.")
                    })

    // An earlier Finished attempt becomes Queued upon RequestChanges, but does not
    // take over the agent's current focus. Orchestrator explicitly selects it later.
    let reissueQueuedAttempt binding operationId taskId ownerId expectedAttemptVersion
                             expectedAgentVersion generation reason =
        if not (validTaskId taskId) || not (validAgent ownerId)
           || expectedAttemptVersion < 1 || expectedAgentVersion < 1 || generation < 1
           || not (validText 2048 reason) then
            Error(InvalidBinding "Reissue requires versions, generation and a reason.")
        else
            LedgerStore.transact binding
                (intent operationId "Orchestrator" "ReissueQueuedAttempt"
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
                           || not (String.IsNullOrEmpty attempt.ActiveReviewRequestId) then
                            return! Error(StaleEntityVersion "Task is not a current queued reissue.")
                        if agent.Version <> expectedAgentVersion
                           || not (List.contains agent.State [ "Parked"; "Completed" ])
                           || not (String.IsNullOrWhiteSpace agent.ActiveAttemptId) then
                            return! Error(AgentBusy "Cannot preempt an Assigned, Ready or Working agent.")
                        return
                            [ change (attemptPath taskId attempt.AttemptId) (Some attemptOid)
                                { attempt with Version = attempt.Version + 1 }
                              change (agentPath ownerId) (Some agentOid)
                                { (resetSessions agent) with
                                    Version = agent.Version + 1
                                    State = "Assigned"
                                    FocusAttemptId = attempt.AttemptId; ActiveAttemptId = null
                                    StateEnteredAtUtc = if agent.State = "Assigned" then agent.StateEnteredAtUtc else DateTimeOffset.UtcNow.ToString("O")
                                    StateReason = reason
                                    StateSequence = snapshot.Header.NextSequence
                                } ]
                    })

    // Explicitly issue new instructions, optionally adopting a new canonical
    // HTML spec, while the same owner is Assigned but has not started work.
    // Prior issuance and acknowledgments stay immutable in ledger history.
    let reissueAssignment binding operationId taskId ownerId expectedAttemptVersion
                          expectedAgentVersion generation instructions newSpecHtml =
        if not (validTaskId taskId) || not (validAgent ownerId)
           || expectedAttemptVersion < 1 || expectedAgentVersion < 1 || generation < 1
           || not (validText 8192 instructions)
           || (not (String.IsNullOrEmpty newSpecHtml) && not (validTaskHtml newSpecHtml)) then
            Error(InvalidBinding "Explicit reissue requires current versions, instructions and safe optional HTML.")
        else
            LedgerStore.transact binding
                (intent operationId "Orchestrator" "ReissueAssignment"
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
                           || not (List.contains attempt.Phase [ "Queued"; "Acknowledged" ])
                           || agent.State <> "Assigned"
                           || agent.Version <> expectedAgentVersion
                           || agent.FocusAttemptId <> attempt.AttemptId
                           || not (String.IsNullOrWhiteSpace agent.ActiveAttemptId)
                           || not (String.IsNullOrWhiteSpace attempt.ActiveReviewRequestId) then
                            return! Error(StaleEntityVersion "Only an unstarted Assigned generation can be reissued.")
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
                                (issue taskId next (generation + 1) "Orchestrator"
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
    let recoverAgentAssignment binding operationId taskId ownerId expectedAttemptVersion
                               expectedAgentVersion generation reason =
        if not (validTaskId taskId) || not (validAgent ownerId)
           || expectedAttemptVersion < 1 || expectedAgentVersion < 1 || generation < 1
           || not (validText 2048 reason) then
            Error(InvalidBinding "Recovery requires exact versions and an explicit reason.")
        else
            LedgerStore.transact binding
                (intent operationId "Orchestrator" "RecoverAgentAssignment"
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
                           || not (List.contains agent.State [ "Assigned"; "Ready"; "Working" ])
                           || not (List.contains attempt.Phase [ "Queued"; "Paused"; "Working" ])
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
                                (enterState "Assigned" agent |> resetSessions |> fun updated -> { updated with Version = agent.Version + 1; State = "Assigned"; ActiveAttemptId = null; StateReason = reason; StateSequence = snapshot.Header.NextSequence })
                              change (issuePath taskId attempt.AttemptId
                                          (attempt.AssignmentGeneration + 1)) None
                                (issue taskId attempt (attempt.AssignmentGeneration + 1)
                                    "Orchestrator" "Recovery" reason snapshot.Header.NextSequence) ]
                    })

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
    let private adminEvent (task: ReviewTask) (attempt: ReviewAttempt option)
                           (operationId: Guid) sequence action reason nextLifecycle nextPhase nextOwner reviewId reviewDisposition evidence =
        { TaskId = task.TaskId
          AttemptId = attempt |> Option.map (fun a -> a.AttemptId) |> Option.defaultValue null
          OperationId = operationId.ToString("D")
          Sequence = sequence; Action = action; ActorId = "Orchestrator"; Reason = reason
          PreviousLifecycle = task.Lifecycle; NewLifecycle = nextLifecycle
          PreviousPhase = attempt |> Option.map (fun a -> a.Phase) |> Option.defaultValue null
          NewPhase = nextPhase
          PreviousOwnerId = attempt |> Option.map (fun a -> a.OwnerAgentId) |> Option.defaultValue null
          NewOwnerId = nextOwner
          PreviousGeneration = attempt |> Option.map (fun a -> a.AssignmentGeneration) |> Option.defaultValue 0
          NewGeneration = attempt |> Option.map (fun a -> a.AssignmentGeneration) |> Option.defaultValue 0
          ReviewRequestId = reviewId; ReviewDisposition = reviewDisposition; Evidence = evidence }

    // Settle an active unclaimed or frozen claimed review in the SAME ledger
    // commit as any administrative cancellation/abandonment/transfer. Keep
    // immutable revision and claim histories, including evidence for reviewer
    // withdrawal without inventing an acceptance or test result.
    let private settleAdministrativeReview binding snapshot taskId (attempt: ReviewAttempt) reason operationId =
        result {
            if String.IsNullOrWhiteSpace attempt.ActiveReviewRequestId then
                return [], null, "None"
            else
                let reviewId = attempt.ActiveReviewRequestId
                let! review, reviewOid = required<ReviewRequest> binding snapshot (requestPath taskId reviewId)
                if review.AttemptId <> attempt.AttemptId
                   || review.OwnerAgentId <> attempt.OwnerAgentId
                   || not (List.contains review.Status [ "Unclaimed"; "Claimed" ]) then
                    return! Error(LedgerIntegrityError "Active review does not match the task attempt.")
                let! revision, _ = required<ReviewRevision> binding snapshot
                                        (revisionPath taskId reviewId review.CurrentRevision)
                let closed = { review with Version = review.Version + 1; Status = "Withdrawn";
                                          DecisionSequence = snapshot.Header.NextSequence; Feedback = reason }
                let audit = claimEvent taskId review revision snapshot.Header.NextSequence
                                operationId "Withdraw" "Orchestrator" review.ClaimantId null reason
                return [ change (requestPath taskId reviewId) (Some reviewOid) closed
                         change (reviewEventPath taskId reviewId snapshot.Header.NextSequence) None audit ],
                       reviewId, "Withdrawn"
        }

    // Only clear a focus that still belongs to this attempt. Never interrupt
    // an owner's newer assignment, or turn agent Parked into an attempt phase.
    let private clearAdministrativeFocus binding snapshot (attempt: ReviewAttempt) expectedAgentVersion reason =
        result {
            let! agent, agentOid = required<ReviewAgent> binding snapshot (agentPath attempt.OwnerAgentId)
            if agent.Version <> expectedAgentVersion then
                return! Error(StaleEntityVersion "Agent version changed before lifecycle operation.")
            if agent.FocusAttemptId <> attempt.AttemptId then
                if agent.ActiveAttemptId = attempt.AttemptId then
                    return! Error(LedgerIntegrityError "Agent has active slot without matching focus.")
                if attempt.Phase = "Working" then
                    return! Error(LedgerIntegrityError "Working attempt has no matching agent slot.")
                return []
            else
                if not (String.IsNullOrWhiteSpace agent.ActiveAttemptId)
                   && agent.ActiveAttemptId <> attempt.AttemptId then
                    return! Error(AgentBusy "Agent is Working on an unrelated attempt.")
                if attempt.Phase = "Working"
                   && (agent.State <> "Working" || agent.ActiveAttemptId <> attempt.AttemptId) then
                    return! Error(LedgerIntegrityError "Working attempt and agent slot disagree.")
                if agent.State = "Working"
                   && (agent.ActiveAttemptId <> attempt.AttemptId || attempt.Phase <> "Working") then
                    return! Error(LedgerIntegrityError "Agent Working slot disagrees with attempt phase/focus.")
                if attempt.Phase <> "Working" && not (String.IsNullOrWhiteSpace agent.ActiveAttemptId) then
                    return! Error(LedgerIntegrityError "Nonworking attempt unexpectedly holds an active slot.")
                return [ change (agentPath attempt.OwnerAgentId) (Some agentOid)
                             (enterState "Completed" agent |> fun updated -> { updated with Version = agent.Version + 1; State = "Completed"; FocusAttemptId = null; ActiveAttemptId = null; StateReason = reason; StateSequence = snapshot.Header.NextSequence }) ]
        }

    let changeAttemptAdministration binding operationId taskId action expectedTaskVersion expectedAttemptVersion
                                    expectedAgentVersion generation reason =
        if not (validTaskId taskId) || expectedTaskVersion < 1 || expectedAttemptVersion < 1
           || expectedAgentVersion < 1 || generation < 1 || not (validText 2048 reason)
           || not (List.contains action [ "ParkAttempt"; "BlockAttempt"; "ResumeAttempt"; "AbandonAttempt"; "CancelTask" ]) then
            Error(InvalidBinding "Invalid administrative task/attempt transition.")
        else
            LedgerStore.transact binding
                (intent operationId "Orchestrator" action
                    {| taskId = taskId; expectedTaskVersion = expectedTaskVersion
                       expectedAttemptVersion = expectedAttemptVersion
                       expectedAgentVersion = expectedAgentVersion
                       generation = generation; reason = reason |})
                (fun snapshot -> result {
                    let! task, taskOid, attempt, attemptOid = getAttempt binding snapshot taskId
                    if task.Version <> expectedTaskVersion || task.Lifecycle <> "InProgress"
                       || attempt.Version <> expectedAttemptVersion
                       || attempt.AssignmentGeneration <> generation then
                        return! Error(StaleEntityVersion "Administrative transition targets a stale task, attempt or generation.")
                    let allowed =
                        if action = "ResumeAttempt" then List.contains attempt.Phase [ "Parked"; "Blocked" ]
                        else List.contains attempt.Phase [ "Queued"; "Acknowledged"; "Working"; "Paused"; "Parked"; "Blocked"; "Finished" ]
                    if not allowed || (action = "ParkAttempt" || action = "BlockAttempt")
                                      && not (List.contains attempt.Phase [ "Queued"; "Acknowledged"; "Working"; "Paused" ]) then
                        return! Error(StaleEntityVersion "Attempt is not in an administratively actionable phase.")
                    let! agent, agentOid =
                        required<ReviewAgent> binding snapshot (agentPath attempt.OwnerAgentId)
                    if agent.Version <> expectedAgentVersion then
                        return! Error(StaleEntityVersion "Agent version differs from administrative precondition.")
                    let resume = action = "ResumeAttempt"
                    let terminal = action = "CancelTask" || action = "AbandonAttempt"
                    let! focusChanges =
                        if resume then
                            if agent.ActiveAttemptId = attempt.AttemptId
                               || agent.FocusAttemptId = attempt.AttemptId then
                                Error(LedgerIntegrityError "Parked/Blocked attempt unexpectedly occupies agent focus or slot.")
                            else Ok []
                        else clearAdministrativeFocus binding snapshot attempt expectedAgentVersion reason
                    let! reviewChanges, reviewId, disposition =
                        if terminal then settleAdministrativeReview binding snapshot taskId attempt reason operationId
                        else Ok ([], null, "Unchanged")
                    let nextPhase =
                        match action with
                        | "ParkAttempt" -> "Parked" | "BlockAttempt" -> "Blocked"
                        | "ResumeAttempt" -> "Queued" | _ -> "Abandoned"
                    let nextLifecycle =
                        match action with
                        | "CancelTask" -> "Cancelled"
                        | "AbandonAttempt" -> "Open"
                        | _ -> "InProgress"
                    let nextGen = if resume then generation + 1 else generation
                    let nextAttempt =
                        { attempt with Version = attempt.Version + 1; Phase = nextPhase
                                       AssignmentGeneration = nextGen
                                       IssuedSequence = if resume then snapshot.Header.NextSequence else attempt.IssuedSequence
                                       ActiveReviewRequestId = if terminal then null else attempt.ActiveReviewRequestId }
                    let audit =
                        { adminEvent task (Some attempt) operationId snapshot.Header.NextSequence
                              action reason nextLifecycle nextPhase attempt.OwnerAgentId reviewId disposition null
                          with NewGeneration = nextGen }
                    let taskUpdates =
                        if terminal then
                            [ change (taskPath taskId) (Some taskOid)
                                { task with Version = task.Version + 1; Lifecycle = nextLifecycle
                                            AttemptId = if action = "AbandonAttempt" then null else task.AttemptId } ]
                        else []
                    return taskUpdates
                           @ [ change (attemptPath taskId attempt.AttemptId) (Some attemptOid) nextAttempt
                               change (lifecycleEventPath taskId snapshot.Header.NextSequence) None audit ]
                           @ focusChanges @ reviewChanges
                           @ (if resume then
                                  [ change (issuePath taskId attempt.AttemptId nextGen) None
                                      (issue taskId nextAttempt nextGen "Orchestrator" "AdministrativeResume"
                                          ("Resume administratively parked or blocked attempt: " + reason)
                                          snapshot.Header.NextSequence) ]
                              else [])
                })

    // Open tasks have no attempt/agent to revoke. Cancellation remains a
    // distinct, audited no-source outcome, not a fabricated integration.
    let cancelOpenTask binding operationId taskId expectedTaskVersion reason =
        if not (validTaskId taskId) || expectedTaskVersion < 1 || not (validText 2048 reason) then
            Error(InvalidBinding "Open-task cancellation requires exact task version and reason.")
        else
            LedgerStore.transact binding
                (intent operationId "Orchestrator" "CancelTask"
                    {| taskId = taskId; expectedTaskVersion = expectedTaskVersion; reason = reason |})
                (fun snapshot -> result {
                    let! task, oid = required<ReviewTask> binding snapshot (taskPath taskId)
                    if task.Version <> expectedTaskVersion || task.Lifecycle <> "Open"
                       || not (String.IsNullOrWhiteSpace task.AttemptId) then
                        return! Error(StaleEntityVersion "Only an unassigned Open task can use open-task cancellation.")
                    return [ change (taskPath taskId) (Some oid)
                                 { task with Version = task.Version + 1; Lifecycle = "Cancelled" }
                             change (lifecycleEventPath taskId snapshot.Header.NextSequence) None
                                 (adminEvent task None operationId snapshot.Header.NextSequence
                                     "CancelTask" reason "Cancelled" null null null "None" null) ]
                })

    let transferAttemptOwner binding operationId taskId newAttemptId newOwner reviewer
                             expectedTaskVersion expectedAttemptVersion expectedAgentVersion
                             generation reason =
        if not (validTaskId taskId) || newAttemptId = Guid.Empty
           || not (validAgent newOwner) || not (validAgent reviewer) || newOwner = reviewer
           || expectedTaskVersion < 1 || expectedAttemptVersion < 1 || expectedAgentVersion < 1
           || generation < 1 || not (validText 2048 reason) then
            Error(InvalidBinding "Owner transfer requires distinct registered owner/reviewer, new attempt and exact versions.")
        else
            let newId = newAttemptId.ToString("N")
            LedgerStore.transact binding
                (intent operationId "Orchestrator" "TransferOwner"
                    {| taskId = taskId; newAttemptId = newAttemptId; newOwner = newOwner
                       reviewer = reviewer; expectedTaskVersion = expectedTaskVersion
                       expectedAttemptVersion = expectedAttemptVersion; expectedAgentVersion = expectedAgentVersion
                       generation = generation; reason = reason |})
                (fun snapshot -> result {
                    let! task, taskOid, attempt, attemptOid = getAttempt binding snapshot taskId
                    if task.Lifecycle <> "InProgress" || task.Version <> expectedTaskVersion
                       || attempt.Version <> expectedAttemptVersion
                       || attempt.AssignmentGeneration <> generation
                       || attempt.OwnerAgentId = newOwner
                       || not (Set.contains attempt.Phase (Set.ofList
                              [ "Queued"; "Acknowledged"; "Working"; "Paused"; "Parked"; "Blocked"; "Finished" ])) then
                        return! Error(StaleEntityVersion "Owner transfer requires the current revocable attempt and versions.")
                    if not (String.IsNullOrWhiteSpace attempt.IntegrationActionId)
                       || not (String.IsNullOrWhiteSpace attempt.AcceptedReviewRequestId) then
                        return! Error(ReviewVersionConflict "Already accepted/integrating work must be explicitly resolved before transfer.")
                    let! successor = read<ReviewAttempt> binding snapshot (attemptPath taskId newId)
                    if successor.IsSome then
                        return! Error(StaleEntityVersion "Transfer attempt ID is not unique.")
                    let! nextOwnerAgent, nextOwnerOid = required<ReviewAgent> binding snapshot (agentPath newOwner)
                    let! _ = required<ReviewAgent> binding snapshot (agentPath reviewer)
                    if not (List.contains nextOwnerAgent.State [ "Completed"; "Parked" ])
                       || not (String.IsNullOrWhiteSpace nextOwnerAgent.ActiveAttemptId) then
                        return! Error(AgentBusy "New owner is not idle.")
                    let! previousAgentChanges =
                        clearAdministrativeFocus binding snapshot attempt expectedAgentVersion reason
                    let! reviewChanges, reviewId, disposition =
                        settleAdministrativeReview binding snapshot taskId attempt reason operationId
                    let next =
                        { attempt with AttemptId = newId; Version = 1
                                       OwnerAgentId = newOwner; ReviewerId = reviewer; Phase = "Queued"
                                       AssignmentGeneration = 1; IssuedSequence = snapshot.Header.NextSequence
                                       BaseCommit = null; TargetRef = null; SourceRepositoryId = null
                                       SourceRemote = null; SourcePushRemote = null; SourceRef = null
                                       ActiveReviewRequestId = null; LatestReviewRequestId = null
                                       AcceptedReviewRequestId = null; AcceptedReviewRevision = 0
                                       AcceptedHead = null; IntegrationActionId = null
                                       ReviewFeedback = null; FeedbackSequence = 0L
                                       AcknowledgedFeedbackSequence = 0L }
                    let audit =
                        { adminEvent task (Some attempt) operationId snapshot.Header.NextSequence
                              "TransferOwner" reason "InProgress" "Queued" newOwner reviewId disposition null
                          with NewGeneration = 1 }
                    return [ change (taskPath taskId) (Some taskOid)
                                 { task with Version = task.Version + 1; AttemptId = newId }
                             change (attemptPath taskId attempt.AttemptId) (Some attemptOid)
                                 { attempt with Version = attempt.Version + 1; Phase = "Superseded"
                                                ActiveReviewRequestId = null }
                             change (attemptPath taskId newId) None next
                             change (issuePath taskId newId 1) None
                                 (issue taskId next 1 "Orchestrator" "OwnershipTransfer"
                                     ("Transfer from " + attempt.OwnerAgentId + ": " + reason)
                                     snapshot.Header.NextSequence)
                             change (agentPath newOwner) (Some nextOwnerOid)
                                 { (resetSessions nextOwnerAgent) with
                                     Version = nextOwnerAgent.Version + 1
                                     State = "Assigned"; FocusAttemptId = newId
                                     StateEnteredAtUtc = if nextOwnerAgent.State = "Assigned" then nextOwnerAgent.StateEnteredAtUtc else DateTimeOffset.UtcNow.ToString("O")
                                     ActiveAttemptId = null; StateReason = reason
                                     StateSequence = snapshot.Header.NextSequence
                                 }
                             change (lifecycleEventPath taskId snapshot.Header.NextSequence) None audit ]
                           @ previousAgentChanges @ reviewChanges
                })

    // Independent non-source closure: no source merge/integration is asserted.
    // A registered reviewer other than the implementation owner must explicitly
    // approve acceptance criteria and observed evidence after owner finishes.
    let closeNonSourceTask binding operationId taskId approver expectedTaskVersion expectedAttemptVersion
                           criteria evidence =
        if not (validTaskId taskId) || not (validAgent approver)
           || expectedTaskVersion < 1 || expectedAttemptVersion < 1
           || not (validText 2048 criteria) || not (validText 8192 evidence) then
            Error(InvalidBinding "Non-source closure requires independent approver and substantive criteria/evidence.")
        else
            LedgerStore.transact binding
                (intent operationId approver "CloseNonSourceTask"
                    {| taskId = taskId; approver = approver; expectedTaskVersion = expectedTaskVersion
                       expectedAttemptVersion = expectedAttemptVersion; criteria = criteria; evidence = evidence |})
                (fun snapshot -> result {
                    let! task, oid, attempt, _ = getAttempt binding snapshot taskId
                    let! _ = required<ReviewAgent> binding snapshot (agentPath approver)
                    if task.Version <> expectedTaskVersion || task.Lifecycle <> "InProgress"
                       || attempt.Version <> expectedAttemptVersion || attempt.Phase <> "Finished"
                       || attempt.OwnerAgentId = approver
                       || not (String.IsNullOrWhiteSpace attempt.ActiveReviewRequestId)
                       || not (String.IsNullOrWhiteSpace attempt.LatestReviewRequestId)
                       || not (String.IsNullOrWhiteSpace attempt.AcceptedReviewRequestId)
                       || not (String.IsNullOrWhiteSpace attempt.IntegrationActionId)
                       || not (String.IsNullOrWhiteSpace attempt.BaseCommit)
                       || not (String.IsNullOrWhiteSpace attempt.SourceRef) then
                        return! Error(StaleEntityVersion "Non-source closure requires finished, independently approved work with no unresolved source review.")
                    let approval = { TaskId = taskId; AttemptId = attempt.AttemptId
                                     OperationId = operationId.ToString("D")
                                     Sequence = snapshot.Header.NextSequence
                                     ApproverId = approver; OwnerAgentId = attempt.OwnerAgentId
                                     SpecBlobOid = attempt.SpecBlobOid; Criteria = criteria
                                     Evidence = evidence; NoSourceIntegration = true }
                    return [ change (taskPath taskId) (Some oid)
                                 { task with Version = task.Version + 1; Lifecycle = "Closed" }
                             change (nonSourceApprovalPath taskId snapshot.Header.NextSequence) None approval
                             change (lifecycleEventPath taskId snapshot.Header.NextSequence) None
                                 { adminEvent task (Some attempt) operationId snapshot.Header.NextSequence
                                       "CloseNonSourceTask" criteria "Closed" "Finished" attempt.OwnerAgentId
                                       null "None" evidence with ActorId = approver } ]
                })

    // Reopening never resurrects an old generation. The old attempt and
    // review/integration evidence stay addressable by immutable paths.
    let reopenTask binding operationId taskId expectedTaskVersion reason =
        if not (validTaskId taskId) || expectedTaskVersion < 1
           || not (validText 2048 reason) then
            Error(InvalidBinding "Reopen requires terminal task version and explanation.")
        else
            LedgerStore.transact binding
                (intent operationId "Orchestrator" "ReopenTask"
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
                    | Some attempt when not (String.IsNullOrWhiteSpace attempt.ActiveReviewRequestId)
                                        || List.contains attempt.Phase [ "Queued"; "Acknowledged"; "Working"; "Paused"; "Parked"; "Blocked" ] ->
                        return! Error(LedgerIntegrityError "Cannot reopen with unresolved active work or review.")
                    | _ -> ()
                    return [ change (taskPath taskId) (Some oid)
                                 { task with Version = task.Version + 1; Lifecycle = "Open"; AttemptId = null }
                             change (lifecycleEventPath taskId snapshot.Header.NextSequence) None
                                 (adminEvent task oldAttempt operationId snapshot.Header.NextSequence
                                     "ReopenTask" reason "Open" null null null "None" null) ]
                })

    let getNonSourceClosureApproval binding taskId sequence =
        if not (validTaskId taskId) || sequence < 1L then
            Error(InvalidBinding "Non-source approval lookup requires exact task and sequence.")
        else result {
            let! snapshot = LedgerStore.fetch binding
            let! approval, _ =
                required<NonSourceClosureApproval> binding snapshot
                    (nonSourceApprovalPath taskId sequence)
            if approval.TaskId <> taskId || approval.Sequence <> sequence
               || not approval.NoSourceIntegration then
                return! Error(LedgerIntegrityError "Non-source approval record does not match its task/sequence.")
            return snapshot.Commit, approval
        }

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

    let getTask binding taskId =
        if not (validTaskId taskId) then Error(InvalidBinding "Invalid task ID.")
        else
            result {
                let! snapshot = LedgerStore.fetch binding
                let! task, _ = required<ReviewTask> binding snapshot (taskPath taskId)
                if String.IsNullOrWhiteSpace task.AttemptId then
                    return snapshot, task, None
                else
                    let! attempt, _ =
                        required<ReviewAttempt> binding snapshot (attemptPath taskId task.AttemptId)
                    return snapshot, task, Some attempt
            }

    let getReview binding taskId requestId =
        if not (validTaskId taskId) || requestId = Guid.Empty then
            Error(InvalidBinding "Invalid review ID.")
        else
            result {
                let! snapshot = LedgerStore.fetch binding
                let id = requestId.ToString("N")
                let! request, _ = required<ReviewRequest> binding snapshot (requestPath taskId id)
                let! revision, _ =
                    required<ReviewRevision> binding snapshot
                        (revisionPath taskId id request.CurrentRevision)
                return snapshot, request, revision
            }

    let reviewerQueue binding reviewerId =
        if not (validAgent reviewerId) then Error(InvalidBinding "Invalid reviewer ID.")
        else
            result {
                let! snapshot = LedgerStore.fetch binding
                let! _ = required<ReviewAgent> binding snapshot (agentPath reviewerId)
                let! paths = LedgerStore.listAt binding snapshot "tasks"
                let paths = paths |> List.filter (fun path -> path.EndsWith("/request.json"))
                let mutable error = None
                let mutable queue = []
                for path in paths do
                    if error.IsNone then
                        match required<ReviewRequest> binding snapshot path with
                        | Error failure -> error <- Some failure
                        | Ok(request, _) when request.OwnerAgentId <> reviewerId
                                              && (request.Status = "Unclaimed"
                                                  || (request.Status = "Claimed" && request.ClaimantId = reviewerId)) ->
                            queue <- request :: queue
                        | Ok _ -> ()
                match error with
                | Some failure -> return! Error failure
                | None -> return snapshot, queue |> List.sortBy (fun r -> r.CreatedSequence, r.TaskId, r.RequestId)
            }


    // Derive every queue and slot observation from a single verified ledger commit.
    // Queue membership is never duplicated in agent.json.
    // Fetch just the canonical agent record for duration guidance.
    let getAgentWorkingTurn binding agentId =
        if not (validAgent agentId) then Error(InvalidBinding "Invalid Working agent ID.")
        else result {
            let! snapshot = LedgerStore.fetch binding
            let! agent, _ = required<ReviewAgent> binding snapshot (agentPath agentId)
            return agent
        }

    let getAgentInbox binding agentId =
        if not (validAgent agentId) then Error(InvalidBinding "Invalid inbox agent ID.")
        else
            result {
                let! snapshot = LedgerStore.fetch binding
                let! agent, _ = required<ReviewAgent> binding snapshot (agentPath agentId)
                let! paths = LedgerStore.listAt binding snapshot "tasks"
                let taskPaths = paths |> List.filter (fun path -> path.EndsWith("/task.json", StringComparison.Ordinal))
                let reviewPaths = paths |> List.filter (fun path -> path.EndsWith("/request.json", StringComparison.Ordinal))
                let feedbackPaths = paths |> List.filter (fun path -> path.EndsWith("/notification.json", StringComparison.Ordinal))
                let mutable failure = None
                let mutable owned = []
                let mutable pending = []
                let mutable available = []
                let mutable feedback = []
                for path in taskPaths do
                    if failure.IsNone then
                        match required<ReviewTask> binding snapshot path with
                        | Error error -> failure <- Some error
                        | Ok(task, _) ->
                            if not (validTaskId task.TaskId) || path <> taskPath task.TaskId
                               || not (List.contains task.Lifecycle
                                   [ "Open"; "InProgress"; "Implemented"; "Integrated"; "Closed"; "Cancelled" ]) then
                                failure <- Some(LedgerIntegrityError "Invalid task identity or lifecycle in inbox.")
                            elif not (String.IsNullOrWhiteSpace task.AttemptId) then
                                match required<ReviewAttempt> binding snapshot (attemptPath task.TaskId task.AttemptId) with
                                | Error error -> failure <- Some error
                                | Ok(attempt, _) ->
                                    if attempt.TaskId <> task.TaskId || attempt.AttemptId <> task.AttemptId
                                       || not (List.contains attempt.Phase
                                           [ "Queued"; "Acknowledged"; "Working"; "Paused"; "Finished"
                                             "Parked"; "Blocked"; "Accepted"; "Superseded"; "Abandoned" ]) then
                                        failure <- Some(LedgerIntegrityError "Invalid task/attempt identity or phase in inbox.")
                                    elif attempt.OwnerAgentId = agentId then
                                        owned <- { Task = task; Attempt = attempt } :: owned
                for path in reviewPaths do
                    if failure.IsNone then
                        match required<ReviewRequest> binding snapshot path with
                        | Error error -> failure <- Some error
                        | Ok(request, _) ->
                            if not (validTaskId request.TaskId)
                               || path <> requestPath request.TaskId request.RequestId
                               || not (List.contains request.Status
                                   [ "Unclaimed"; "Claimed"; "Accepted"; "ChangesRequested"; "Withdrawn" ]) then
                                failure <- Some(LedgerIntegrityError "Invalid review identity or status in inbox.")
                            elif request.Status = "Unclaimed" then
                                if request.OwnerAgentId = agentId then pending <- request :: pending
                                else available <- request :: available
                            elif request.Status = "Claimed"
                                 && (request.OwnerAgentId = agentId || request.ClaimantId = agentId) then
                                pending <- request :: pending
                for path in feedbackPaths do
                    if failure.IsNone then
                        match required<ReviewFeedbackNotice> binding snapshot path with
                        | Error error -> failure <- Some error
                        | Ok(notice, _) ->
                            if not (validTaskId notice.TaskId)
                               || path <> feedbackPath notice.TaskId notice.RequestId
                               || notice.DecisionSequence <= 0L
                               || not (List.contains notice.Status [ "Accepted"; "ChangesRequested" ]) then
                                failure <- Some(LedgerIntegrityError "Invalid feedback notification in agent inbox.")
                            elif notice.OwnerAgentId = agentId && not notice.Acknowledged then
                                feedback <- notice :: feedback
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
                    let working = owned |> List.filter (fun entry -> entry.Attempt.Phase = "Working")
                    let accepted = owned |> List.filter (fun entry ->
                        entry.Attempt.Phase = "Accepted" && entry.Task.Lifecycle = "Implemented")
                    if working.Length > 1
                       || (match working with
                           | [ item ] -> agent.ActiveAttemptId <> item.Attempt.AttemptId
                           | [] -> not (String.IsNullOrWhiteSpace agent.ActiveAttemptId)
                           | _ -> true) then
                        return! Error(LedgerIntegrityError "Agent slot disagrees with the task's Working attempts.")
                    let active = working |> List.tryHead
                    let focused =
                        owned |> List.tryFind (fun entry ->
                            entry.Attempt.AttemptId = agent.FocusAttemptId)
                    let focusValid =
                        match agent.State, focused with
                        | "Parked", None -> String.IsNullOrWhiteSpace agent.ActiveAttemptId
                        | "Assigned", Some entry ->
                            List.contains entry.Attempt.Phase [ "Queued"; "Acknowledged" ] && active.IsNone
                        | "Ready", Some entry ->
                            entry.Attempt.Phase = "Paused" && active.IsNone
                        | "Working", Some entry ->
                            entry.Attempt.Phase = "Working"
                            && agent.ActiveAttemptId = entry.Attempt.AttemptId
                        | "Completed", None ->
                            active.IsNone && String.IsNullOrWhiteSpace agent.ActiveAttemptId
                        | "Completed", Some entry ->
                            (List.contains entry.Attempt.Phase [ "Finished"; "Accepted"; "Queued" ])
                            && active.IsNone
                        | _ -> false
                    if not focusValid then
                        return! Error(LedgerIntegrityError "Agent state/focused attempt mismatch.")
                    let runnable = owned |> List.filter (fun entry ->
                        entry.Attempt.Phase = "Queued" || entry.Attempt.Phase = "Acknowledged")
                    let parked = owned |> List.filter (fun entry -> entry.Attempt.Phase = "Parked")
                    let blocked = owned |> List.filter (fun entry -> entry.Attempt.Phase = "Blocked")
                    let reviewOrder (request: ReviewRequest) =
                        request.CreatedSequence, request.TaskId, request.RequestId
                    let pending = pending |> List.sortBy reviewOrder
                    let available = available |> List.sortBy reviewOrder
                    return
                        { SnapshotCommit = snapshot.Commit
                          Agent = agent
                          Focused = focused
                          OperatorActionRequired = agent.State = "Ready" || agent.State = "Assigned"
                          NeedsOrchestratorAssignment = agent.State = "Completed"
                          Working = active
                          RunnableQueue = runnable
                          Parked = parked
                          Blocked = blocked
                          AcceptedPendingIntegration = accepted
                          PendingReviews = pending
                          AvailableReviews = available
                          UnacknowledgedFeedback = feedback |> List.sortBy (fun notice -> notice.DecisionSequence)
                          SlotAvailable = active.IsNone
                          Unassigned =
                              (owned |> List.forall (fun item ->
                                  not (List.contains item.Task.Lifecycle [ "InProgress"; "Implemented" ])))
                              && pending.IsEmpty && available.IsEmpty && feedback.IsEmpty }
            }
