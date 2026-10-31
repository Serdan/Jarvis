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
      HeadCommit: string
      Integrated: bool }

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
type PendingIntegration =
    { Task: ReviewTask
      Attempt: ReviewAttempt
      Review: BranchReview }

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
      AcceptedPendingIntegration: PendingIntegration list
      PendingReviews: BranchReview list
      AvailableReviews: BranchReview list
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
                    | "BranchReview" -> status "status" [ "Unclaimed"; "Claimed"; "Accepted"; "ChangesRequested"; "Withdrawn" ]
                    | "ReviewFeedbackNotice" -> status "status" [ "Accepted"; "ChangesRequested" ]
                    | "AgentCheckpoint" -> status "kind" [ "Yield"; "Completion"; "Recovery"; "Progress"; "Submission"; "Revision" ]
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
    let private nonSourceApprovalPath taskId sequence =
        $"tasks/{taskId}/approvals/non-source/{sequence}.json"
    let private reviewRef taskId requestId number = $"refs/heads/jarvis-review/{taskId}/{requestId}/r{number}"
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

    let assignTask binding operationId taskId attemptId ownerId =
        if not (validTaskId taskId) || attemptId = Guid.Empty
           || not (validAgent ownerId) then
            Error(InvalidBinding "Invalid task assignment.")
        else
            let attemptIdText = attemptId.ToString("N")
            LedgerStore.transact binding
                (intent operationId "Orchestrator" "AssignTask"
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
                        if not (List.contains agent.State [ "Parked"; "Completed" ])
                           || not (String.IsNullOrWhiteSpace agent.ActiveAttemptId) then
                            return! Error(AgentBusy "Agent must be Parked or Completed before receiving an assignment.")
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
               && attempt.AssignmentGeneration = generation
               && agent.State = "Working"
               && attempt.Phase = "Working"
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
                       assignmentGeneration = generation
                       head = snapshot.Commit |}
            else return None
        }

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
                            // Only a new execution can report a recovery. Existing
                            // sessions have read-only status through V2TaskApi.
                            // Do not silently reactivate old IDs or change versions.
                            let previous =
                                if isNull (box agent.AssociatedSessionIds) then []
                                else agent.AssociatedSessionIds
                            if String.IsNullOrWhiteSpace sessionId
                               || List.contains sessionId previous then
                                return! Error(StaleEntityVersion
                                    $"Session already associated (current: {agent.ActiveSessionId}); inspect current assignment before resuming.")
                            let updated =
                                { (associateSession sessionId agent) with
                                    Version = agent.Version + 1
                                    WorkingTurnStartedAtUtc = DateTimeOffset.UtcNow.ToString("O")
                                    StateReason = $"Recovered stalled task {taskId}"
                                    StateSequence = snapshot.Header.NextSequence }
                            return
                                [ change (agentPath ownerId) (Some agentOid) updated
                                  change ($"tasks/{taskId}/attempts/{attempt.AttemptId}/events/{snapshot.Header.NextSequence}.json") None
                                    {| taskId = taskId; attemptId = attempt.AttemptId
                                       ownerAgentId = ownerId; assignmentGeneration = generation
                                       kind = "AgentStallRecovered"; sessionId = sessionId
                                       previousSessionId = agent.ActiveSessionId
                                       reason = "Explicit resume of Working agent"
                                       sequence = snapshot.Header.NextSequence |} ]
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
                        let! paths = LedgerStore.listAt binding snapshot "tasks"
                        let mutable branchReviews = []
                        for path in paths do
                            if path.StartsWith($"tasks/{taskId}/reviews/", StringComparison.Ordinal)
                               && path.EndsWith("/review.json", StringComparison.Ordinal) then
                                let! review, _ = required<BranchReview> binding snapshot path
                                if review.AttemptId = attempt.AttemptId then
                                    branchReviews <- review :: branchReviews
                        let latest = branchReviews |> List.sortBy (fun review ->
                            review.CreatedSequence, review.ReviewId) |> List.tryLast
                        let requeue =
                            latest |> Option.exists (fun review ->
                                review.Status = "ChangesRequested"
                                && not (branchReviews |> List.exists (fun item ->
                                    List.contains item.Status [ "Unclaimed"; "Claimed" ])))
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
                                { agent with
                                    Version = agent.Version + 1
                                    State = "Parked"; FocusAttemptId = null
                                    StateEnteredAtUtc = if agent.State = "Parked" then agent.StateEnteredAtUtc else DateTimeOffset.UtcNow.ToString("O")
                                    StateReason = reason
                                    StateSequence = snapshot.Header.NextSequence
                                } ]
                    })

    // Source repository identity is an endpoint fingerprint independent from
    // the ledger UUID. An agent's checkout path never enters the owner key.
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
                           || not ((attempt.Phase = "Working" && agent.State = "Working"
                                    && agent.ActiveAttemptId = attempt.AttemptId)
                                   || (attempt.Phase = "Paused" && agent.State = "Ready"
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

    // Initial source information for the attempt. Each individual review
    // computes its own merge-base against the current target.
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
                let! _ = verifySourceRepository binding attempt
                if String.IsNullOrWhiteSpace attempt.SourceRef then
                    return snapshot.Commit, attempt, None
                else
                    let! observation = LedgerStore.inspectSourceWorktree binding attempt.SourceRef
                    return snapshot.Commit, attempt, Some observation
        }

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
                           then
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
                           then
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
    // Administrative handoffs withdraw the active canonical review in the
    // same ledger commit. Accepted historical reviews remain untouched.
    let private settleAdministrativeReview binding snapshot taskId (attempt: ReviewAttempt) reason operationId =
        result {
            let! paths = LedgerStore.listAt binding snapshot "tasks"
            let mutable changes = []
            let mutable withdrawn = []
            for path in paths do
                if path.StartsWith($"tasks/{taskId}/reviews/", StringComparison.Ordinal)
                   && path.EndsWith("/review.json", StringComparison.Ordinal) then
                    let! review, oid = required<BranchReview> binding snapshot path
                    if review.AttemptId = attempt.AttemptId
                       && List.contains review.Status [ "Unclaimed"; "Claimed" ] then
                        changes <- change path (Some oid)
                            { review with Version = review.Version + 1; Status = "Withdrawn" } :: changes
                        withdrawn <- review.ReviewId :: withdrawn
            return changes, (withdrawn |> List.tryHead |> Option.defaultValue null),
                   (if List.isEmpty withdrawn then "None" else "Withdrawn")
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
                                       }
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

    let transferAttemptOwner binding operationId taskId newAttemptId newOwner
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
                (intent operationId "Orchestrator" "TransferOwner"
                    {| taskId = taskId; newAttemptId = newAttemptId; newOwner = newOwner
                       expectedTaskVersion = expectedTaskVersion
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
                    // Reviews belong to the task and do not block an owner handoff.
                    // The new owner can inspect outstanding accepted branches in Git.
                    let! successor = read<ReviewAttempt> binding snapshot (attemptPath taskId newId)
                    if successor.IsSome then
                        return! Error(StaleEntityVersion "Transfer attempt ID is not unique.")
                    let! nextOwnerAgent, nextOwnerOid = required<ReviewAgent> binding snapshot (agentPath newOwner)
                    if not (List.contains nextOwnerAgent.State [ "Completed"; "Parked" ])
                       || not (String.IsNullOrWhiteSpace nextOwnerAgent.ActiveAttemptId) then
                        return! Error(AgentBusy "New owner is not idle.")
                    let! previousAgentChanges =
                        clearAdministrativeFocus binding snapshot attempt expectedAgentVersion reason
                    let! reviewChanges, reviewId, disposition =
                        settleAdministrativeReview binding snapshot taskId attempt reason operationId
                    let next =
                        { attempt with AttemptId = newId; Version = 1
                                       OwnerAgentId = newOwner; Phase = "Queued"
                                       AssignmentGeneration = 1; IssuedSequence = snapshot.Header.NextSequence
                                       BaseCommit = null; TargetRef = null; SourceRepositoryId = null
                                       SourceRemote = null; SourcePushRemote = null; SourceRef = null
                                       }
                    let audit =
                        { adminEvent task (Some attempt) operationId snapshot.Header.NextSequence
                              "TransferOwner" reason "InProgress" "Queued" newOwner reviewId disposition null
                          with NewGeneration = 1 }
                    return [ change (taskPath taskId) (Some taskOid)
                                 { task with Version = task.Version + 1; AttemptId = newId }
                             change (attemptPath taskId attempt.AttemptId) (Some attemptOid)
                                 { attempt with Version = attempt.Version + 1; Phase = "Superseded"
                                                }
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
                    | Some attempt when List.contains attempt.Phase [ "Queued"; "Acknowledged"; "Working"; "Paused"; "Parked"; "Blocked" ] ->
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
                    let! status, ownerId, claimantId, assignedReviewer =
                        requireDocumentReview binding snapshot taskId id
                    if status = "Withdrawn" then
                        return! Error(StaleEntityVersion "Cannot edit a withdrawn review.")
                    if actorId <> ownerId
                       && actorId <> claimantId
                       && actorId <> assignedReviewer then
                        return! Error(ReviewAccessDenied "Not associated with this review.")
                    return [ { Path = reviewDocumentPath taskId id documentPath
                               ExpectedBlobOid = expected; Content = content } ]
                })

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

    // Resolve the canonical agent association when a client restarts and loses
    // its purely optional in-memory routing hints. Only current Working agents
    // are relevant to cutoff/supersession notices.
    let findWorkingAgentBySession binding sessionId =
        if String.IsNullOrWhiteSpace sessionId then Ok None
        else result {
            let! snapshot = LedgerStore.fetch binding
            let! paths = LedgerStore.listAt binding snapshot "agents"
            let mutable matches = []
            for path in paths do
                let! agent, _ = required<ReviewAgent> binding snapshot path
                if agent.State = "Working"
                   && (agent.ActiveSessionId = sessionId
                       || (not (isNull (box agent.AssociatedSessionIds))
                           && List.contains sessionId agent.AssociatedSessionIds)) then
                    matches <- agent :: matches
            // Never guess which agent owns a session if more than one matches.
            return match matches with [ agent ] -> Some agent | _ -> None
        }

    let getAgentInbox binding agentId =
        if not (validAgent agentId) then Error(InvalidBinding "Invalid inbox agent ID.")
        else
            result {
                let! snapshot = LedgerStore.fetch binding
                let! agent, _ = required<ReviewAgent> binding snapshot (agentPath agentId)
                let! paths = LedgerStore.listAt binding snapshot "tasks"
                let taskPaths = paths |> List.filter (fun path -> path.EndsWith("/task.json", StringComparison.Ordinal))
                let reviewPaths = paths |> List.filter (fun path -> path.EndsWith("/review.json", StringComparison.Ordinal))
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
                    let! review, _ = required<BranchReview> binding snapshot path
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
                    let working = owned |> List.filter (fun entry -> entry.Attempt.Phase = "Working")
                    // Derive integration directly from source Git ancestry.
                    let mutable accepted = []
                    for path in reviewPaths do
                        let! review, _ = required<BranchReview> binding snapshot path
                        if review.Status = "Accepted" && review.OwnerAgentId = agentId then
                            match owned |> List.tryFind (fun entry ->
                                entry.Task.TaskId = review.TaskId
                                && entry.Attempt.AttemptId = review.AttemptId
                                && List.contains entry.Task.Lifecycle [ "InProgress"; "Implemented"; "Integrated" ]) with
                            | None -> ()
                            | Some entry ->
                                let! head = LedgerStore.inspectRemoteReviewRef binding review.ReviewRef
                                match head with
                                | None -> return! Error(SourceReviewRefUnavailable "Review branch is missing.")
                                | Some reviewedHead ->
                                    let! targetHead = LedgerStore.readRemoteTarget binding review.TargetRef
                                    let! integrated = LedgerStore.sourceContainsCommit binding reviewedHead targetHead
                                    if not integrated then
                                        accepted <- { Task = entry.Task; Attempt = entry.Attempt
                                                      Review = review } :: accepted
                    accepted <- accepted |> List.sortBy (fun item ->
                        item.Review.CreatedSequence, item.Task.TaskId, item.Review.ReviewId)
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
                    let reviewOrder (review: BranchReview) =
                        review.CreatedSequence, review.TaskId, review.ReviewId
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

    let private branchHead binding (review: BranchReview) =
        result {
            let! advertised = LedgerStore.inspectRemoteReviewRef binding review.ReviewRef
            match advertised with
            | None -> return! Error(SourceReviewRefUnavailable "Review branch no longer exists.")
            | Some oid ->
                do! LedgerStore.fetchReviewRef binding review.ReviewRef oid
                do! LedgerStore.verifyReviewDiff binding review.BaseCommit oid
                return oid
        }

    let private branchIntegrated binding (review: BranchReview) =
        result {
            let! head = branchHead binding review
            let! target = LedgerStore.readRemoteTarget binding review.TargetRef
            let! integrated = LedgerStore.sourceContainsCommit binding head target
            return head, integrated
        }

    let getBranchReview binding taskId reviewId =
        if not (validTaskId taskId) || reviewId = Guid.Empty then
            Error(InvalidBinding "Invalid review identifier.")
        else result {
            let! snapshot = LedgerStore.fetch binding
            let! review, _ = required<BranchReview> binding snapshot
                                (branchReviewPath taskId (reviewId.ToString("N")))
            let! head, integrated = branchIntegrated binding review
            return { SnapshotCommit = snapshot.Commit; Review = review
                     HeadCommit = head; Integrated = integrated }
        }

    let submitBranchReview binding operationId taskId ownerId expectedAttemptVersion generation
                           reviewId sourceRef sourceHead summary =
        if not (validTaskId taskId) || reviewId = Guid.Empty || not (validAgent ownerId)
           || expectedAttemptVersion < 1 || generation < 1
           || not (validBranch sourceRef) || not (validText 4096 summary) then
            Error(InvalidBinding "Invalid branch review submission.")
        else
            let id = reviewId.ToString("N")
            let refName = reviewRef taskId id 1
            let action = intent operationId ownerId "SubmitBranchReview"
                            {| taskId = taskId; reviewId = id; ownerId = ownerId
                               expectedAttemptVersion = expectedAttemptVersion; generation = generation
                               sourceRef = sourceRef; sourceHead = sourceHead; summary = summary |}
            match LedgerStore.findApplied binding action with
            | Error failure -> Error failure
            | Ok(Some applied) -> Ok applied
            | Ok None -> result {
                let! snapshot = LedgerStore.fetch binding
                let! _, _, attempt, _ = getAttempt binding snapshot taskId
                if attempt.OwnerAgentId <> ownerId || attempt.Version <> expectedAttemptVersion
                   || attempt.AssignmentGeneration <> generation || attempt.Phase <> "Working"
                   || attempt.TargetRef = null then
                    return! Error(StaleEntityVersion "Task owner or source context changed.")
                let! baseOid = LedgerStore.reviewMergeBase binding attempt.TargetRef sourceHead
                do! LedgerStore.verifySourceCommit binding sourceRef sourceHead
                do! LedgerStore.verifyReviewDiff binding baseOid sourceHead
                do! LedgerStore.publishReviewRef binding refName sourceHead
                return!
                    LedgerStore.transact binding action (fun current -> result {
                        let! task, _, active, _ = getAttempt binding current taskId
                        if task.Lifecycle <> "InProgress" || active.Version <> expectedAttemptVersion
                           || active.OwnerAgentId <> ownerId || active.Phase <> "Working"
                           || active.AssignmentGeneration <> generation
                           || active.TargetRef <> attempt.TargetRef then
                            return! Error(StaleEntityVersion "Review submission is stale.")
                        let! existing = read<BranchReview> binding current (branchReviewPath taskId id)
                        if existing.IsSome then
                            return! Error(StaleEntityVersion "Review ID already exists.")
                        let! reviews = branchReviewsForTask binding current taskId
                        if reviews |> List.exists (fun review ->
                            review.AttemptId = active.AttemptId
                            && List.contains review.Status [ "Unclaimed"; "Claimed" ]) then
                            return! Error(ReviewVersionConflict "Another review is active on this task.")
                        let review =
                            { ReviewId = id; TaskId = taskId; AttemptId = active.AttemptId
                              OwnerAgentId = ownerId; ReviewRef = refName; BaseCommit = baseOid
                              TargetRef = active.TargetRef; Status = "Unclaimed"; ReviewerId = null
                              Version = 1; CreatedSequence = current.Header.NextSequence
                              DecisionSequence = 0L; Acknowledged = false }
                        return [ change (branchReviewPath taskId id) None review
                                 { Path = reviewDocumentPath taskId id "summary.md"
                                   ExpectedBlobOid = None; Content = summary } ]
                    })
            }

    let replaceBranchReview binding operationId taskId ownerId reviewId expectedVersion
                            generation sourceRef sourceHead summary =
        if not (validTaskId taskId) || reviewId = Guid.Empty || not (validAgent ownerId)
           || expectedVersion < 1 || generation < 1 || not (validBranch sourceRef)
           || not (validText 4096 summary) then
            Error(InvalidBinding "Invalid replacement review.")
        else
            let id = reviewId.ToString("N")
            let refName = reviewRef taskId id (expectedVersion + 1)
            let action = intent operationId ownerId "ReplaceBranchReview"
                            {| taskId = taskId; reviewId = id; ownerId = ownerId
                               expectedVersion = expectedVersion; generation = generation
                               sourceRef = sourceRef; sourceHead = sourceHead; summary = summary |}
            match LedgerStore.findApplied binding action with
            | Error failure -> Error failure
            | Ok(Some applied) -> Ok applied
            | Ok None -> result {
                let! snapshot = LedgerStore.fetch binding
                let! existing, _ = required<BranchReview> binding snapshot (branchReviewPath taskId id)
                if existing.Version <> expectedVersion || existing.Status <> "Unclaimed"
                   || existing.OwnerAgentId <> ownerId then
                    return! Error(ReviewAlreadyClaimed "Only the owner can replace an unclaimed review.")
                let! _, _, attempt, _ = getAttempt binding snapshot taskId
                if attempt.AssignmentGeneration <> generation || attempt.AttemptId <> existing.AttemptId
                   || attempt.Phase <> "Working" then
                    return! Error(StaleEntityVersion "Source attempt has changed.")
                let! baseOid = LedgerStore.reviewMergeBase binding existing.TargetRef sourceHead
                do! LedgerStore.verifySourceCommit binding sourceRef sourceHead
                do! LedgerStore.verifyReviewDiff binding baseOid sourceHead
                do! LedgerStore.publishReviewRef binding refName sourceHead
                return!
                    LedgerStore.transact binding action (fun current -> result {
                        let! review, reviewOid =
                            required<BranchReview> binding current (branchReviewPath taskId id)
                        if review.Version <> expectedVersion || review.Status <> "Unclaimed" then
                            return! Error(ReviewAlreadyClaimed "Review was claimed or replaced.")
                        let! _, summaryOid =
                            LedgerStore.readAt binding current (reviewDocumentPath taskId id "summary.md")
                        return [ change (branchReviewPath taskId id) (Some reviewOid)
                                   { review with Version = review.Version + 1
                                                 ReviewRef = refName; BaseCommit = baseOid }
                                 { Path = reviewDocumentPath taskId id "summary.md"
                                   ExpectedBlobOid = summaryOid; Content = summary } ]
                    })
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
                    if reviewerId = review.OwnerAgentId then
                        return! Error(ReviewAccessDenied "Owner cannot review own work.")
                    let! _ = required<ReviewAgent> binding snapshot (agentPath reviewerId)
                    let! _ = branchHead binding review
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
                    let! _ = branchHead binding review
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
                       || (review.Status = "Unclaimed" && actorId <> review.OwnerAgentId)
                       || (review.Status = "Claimed" && actorId <> review.ReviewerId) then
                        return! Error(ReviewAccessDenied "Only the owner or claimed reviewer can withdraw an active review.")
                    return [ change (branchReviewPath taskId id) (Some oid)
                                { review with Version = review.Version + 1; Status = "Withdrawn" } ]
                })

    let branchReviewerQueue binding reviewerId =
        if not (validAgent reviewerId) then Error(InvalidBinding "Invalid reviewer.")
        else result {
            let! snapshot = LedgerStore.fetch binding
            let! _ = required<ReviewAgent> binding snapshot (agentPath reviewerId)
            let! paths = LedgerStore.listAt binding snapshot "tasks"
            let mutable reviews = []
            for path in paths do
                if path.EndsWith("/review.json", StringComparison.Ordinal) then
                    let! review, _ = required<BranchReview> binding snapshot path
                    if review.OwnerAgentId <> reviewerId
                       && (review.Status = "Unclaimed"
                           || (review.Status = "Claimed" && review.ReviewerId = reviewerId)) then
                        reviews <- review :: reviews
            return snapshot.Commit, reviews |> List.sortBy (fun review ->
                review.CreatedSequence, review.TaskId, review.ReviewId)
        }

    let finalizeBranchReview binding operationId taskId ownerId expectedAttemptVersion reviewId =
        if not (validTaskId taskId) || not (validAgent ownerId) || expectedAttemptVersion < 1
           || reviewId = Guid.Empty then Error(InvalidBinding "Invalid final review.")
        else
            let id = reviewId.ToString("N")
            LedgerStore.transact binding
                (intent operationId ownerId "FinalizeBranchReview"
                    {| taskId = taskId; ownerId = ownerId; expectedAttemptVersion = expectedAttemptVersion
                       reviewId = id |})
                (fun snapshot -> result {
                    let! task, taskOid, attempt, attemptOid = getAttempt binding snapshot taskId
                    if task.Lifecycle <> "InProgress" || attempt.Version <> expectedAttemptVersion
                       || attempt.OwnerAgentId <> ownerId || attempt.Phase <> "Finished" then
                        return! Error(StaleEntityVersion "Task is not ready for final review.")
                    let! review, _ = required<BranchReview> binding snapshot (branchReviewPath taskId id)
                    if review.AttemptId <> attempt.AttemptId || review.OwnerAgentId <> ownerId
                       || review.Status <> "Accepted" then
                        return! Error(ReviewVersionConflict "Final review is not accepted for this attempt.")
                    let! reviews = branchReviewsForTask binding snapshot taskId
                    let latest =
                        reviews |> List.filter (fun item -> item.AttemptId = attempt.AttemptId)
                                |> List.tryLast
                    if latest |> Option.exists (fun item -> item.ReviewId = id) |> not then
                        return! Error(ReviewVersionConflict "A later review supersedes this final review.")
                    let! _, integrated = branchIntegrated binding review
                    return [ change (taskPath taskId) (Some taskOid)
                                { task with Version = task.Version + 1
                                            Lifecycle = if integrated then "Integrated" else "Implemented" }
                             change (attemptPath taskId attempt.AttemptId) (Some attemptOid)
                                { attempt with Version = attempt.Version + 1; Phase = "Accepted" } ]
                })

    let closeBranchTask binding operationId taskId expectedTaskVersion =
        if not (validTaskId taskId) || expectedTaskVersion < 1 then
            Error(InvalidBinding "Invalid source task closure.")
        else
            LedgerStore.transact binding
                (intent operationId "Orchestrator" "CloseBranchTask"
                    {| taskId = taskId; expectedTaskVersion = expectedTaskVersion |})
                (fun snapshot -> result {
                    let! task, taskOid, attempt, _ = getAttempt binding snapshot taskId
                    if task.Version <> expectedTaskVersion
                       || not (List.contains task.Lifecycle [ "Implemented"; "Integrated" ])
                       || attempt.Phase <> "Accepted" then
                        return! Error(StaleEntityVersion "Task has not passed final review.")
                    let! reviews = branchReviewsForTask binding snapshot taskId
                    let latest =
                        reviews |> List.filter (fun item -> item.AttemptId = attempt.AttemptId)
                                |> List.tryLast
                    match latest with
                    | Some review when review.Status = "Accepted" ->
                        let! _, integrated = branchIntegrated binding review
                        if not integrated then
                            return! Error(StaleEntityVersion "Final accepted branch has not been merged into target.")
                        return [ change (taskPath taskId) (Some taskOid)
                                    { task with Version = task.Version + 1; Lifecycle = "Closed" } ]
                    | _ -> return! Error(ReviewVersionConflict "No final accepted review.")
                })
