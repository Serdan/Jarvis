module Client.ReviewWorkflow

open System
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
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
type ReviewAgent =
    { AgentId: string
      Version: int
      State: string
      FocusAttemptId: string
      ActiveAttemptId: string
      StateReason: string
      StateSequence: int64 }

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
      PublishedSourceOid: string
      CheckpointSequence: int64
      TestEvidence: string
      EvidenceStatus: string }

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
      SourceRemote: string
      PlannedSequence: int64
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
                    | "AssignmentIssue" ->
                        status "reason" [ "Assignment"; "ReviewChanges"; "CompletionRequeue"; "Recovery"; "ExplicitReissue" ]
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
    let private integrationPath taskId actionId = $"tasks/{taskId}/integrations/{actionId}.json"
    let private feedbackPath taskId requestId = $"tasks/{taskId}/reviews/{requestId}/notification.json"
    let private revisionPath taskId requestId number = $"tasks/{taskId}/reviews/{requestId}/revisions/{number}.json"
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
        validText (128 * 1024) value
        && not (Regex.IsMatch(value,
            "<\\s*(script|iframe|object|embed|base|form|svg|math)\\b|\\bon[a-z]+\\s*=|javascript\\s*:",
            RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant))
    let private validBranch (value: string) =
        not (isNull value) && refPattern.IsMatch value
        && not (value.Contains("..")) && not (value.EndsWith("/"))

    let private intent operationId actor kind payload =
        { OperationId = operationId
          Actor = actor
          Kind = kind
          RequestJson = json payload }

    let private change path blob value =
        { Path = path; ExpectedBlobOid = blob; Content = entityJson value }

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
                    {| taskId = taskId; title = title; specHtml = html |})
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

    let reviseTaskSpec binding operationId taskId expectedTaskVersion title html =
        if not (validTaskId taskId) || expectedTaskVersion < 1
           || not (validText 512 title) || not (validText (128 * 1024) html) then
            Error(InvalidBinding "Invalid task spec revision.")
        else
            LedgerStore.transact binding
                (intent operationId "Orchestrator" "ReviseTaskSpec"
                    {| taskId = taskId; expectedTaskVersion = expectedTaskVersion
                       title = title; specHtml = html |})
                (fun snapshot ->
                    result {
                        let! task, taskOid = required<ReviewTask> binding snapshot (taskPath taskId)
                        let! _, specOid = LedgerStore.readAt binding snapshot (specPath taskId)
                        if task.Lifecycle <> "Open" || task.Version <> expectedTaskVersion
                           || specOid.IsNone then
                            return! Error(StaleEntityVersion "Only an unassigned Open task's spec can be revised.")
                        return
                            [ change (taskPath taskId) (Some taskOid)
                                { task with Version = task.Version + 1
                                            SpecVersion = task.SpecVersion + 1; Title = title }
                              { Path = specPath taskId; ExpectedBlobOid = specOid; Content = html } ]
                    })

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
                              StateSequence = snapshot.Header.NextSequence }
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
                                { agent with Version = agent.Version + 1; State = "Assigned"
                                             FocusAttemptId = attemptIdText
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

    let beginAttempt binding operationId taskId ownerId expectedVersion generation =
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
                                { agent with Version = agent.Version + 1
                                             State = "Working"; ActiveAttemptId = attempt.AttemptId
                                             StateReason = $"Started task {taskId}"
                                             StateSequence = snapshot.Header.NextSequence } ]
                            @ (if attempt.Phase = "Queued" then
                                   [ change (receiptPath taskId attempt.AttemptId generation) None
                                       { TaskId = taskId; AttemptId = attempt.AttemptId
                                         Generation = generation; OwnerAgentId = ownerId
                                         AcknowledgedSequence = snapshot.Header.NextSequence
                                         SpecBlobOid = issuance.SpecBlobOid
                                         IssueSequence = issuance.IssuedSequence } ]
                               else [])
                    })


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
                                { agent with Version = agent.Version + 1
                                             State = "Ready"; ActiveAttemptId = null
                                             StateReason = nextAction
                                             StateSequence = snapshot.Header.NextSequence } ]
                    })

    let resumeAgent binding operationId taskId ownerId expectedAttemptVersion expectedAgentVersion generation =
        if not (validTaskId taskId) || not (validAgent ownerId)
           || expectedAttemptVersion < 1 || expectedAgentVersion < 1 || generation < 1 then
            Error(InvalidBinding "Invalid ResumeAgent version or generation.")
        else
            LedgerStore.transact binding
                (intent operationId ownerId "ResumeAgent"
                    {| taskId = taskId; ownerId = ownerId
                       expectedAttemptVersion = expectedAttemptVersion
                       expectedAgentVersion = expectedAgentVersion; generation = generation |})
                (fun snapshot ->
                    result {
                        let! task, _, attempt, attemptOid = getAttempt binding snapshot taskId
                        let! agent, agentOid = required<ReviewAgent> binding snapshot (agentPath ownerId)
                        if task.Lifecycle <> "InProgress" || attempt.OwnerAgentId <> ownerId
                           || attempt.Version <> expectedAttemptVersion
                           || attempt.AssignmentGeneration <> generation
                           || attempt.Phase <> "Paused"
                           || agent.State <> "Ready" || agent.Version <> expectedAgentVersion
                           || agent.FocusAttemptId <> attempt.AttemptId
                           || not (String.IsNullOrWhiteSpace agent.ActiveAttemptId) then
                            return! Error(StaleEntityVersion "Resume requires the exact Ready assignment generation.")
                        let! _ = requiredIssue binding snapshot taskId attempt
                        return
                            [ change (attemptPath taskId attempt.AttemptId) (Some attemptOid)
                                { attempt with Version = attempt.Version + 1; Phase = "Working" }
                              change (agentPath ownerId) (Some agentOid)
                                { agent with Version = agent.Version + 1
                                             State = "Working"; ActiveAttemptId = attempt.AttemptId
                                             StateReason = $"Resumed task {taskId}"
                                             StateSequence = snapshot.Header.NextSequence } ]
                    })

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
                                { agent with Version = agent.Version + 1
                                             State = "Completed"; ActiveAttemptId = null
                                             StateReason = summary
                                             StateSequence = snapshot.Header.NextSequence } ]
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
                                { agent with Version = agent.Version + 1
                                             State = "Parked"; FocusAttemptId = null
                                             StateReason = reason
                                             StateSequence = snapshot.Header.NextSequence } ]
                    })

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
                        do! LedgerStore.verifySourceObject binding baseOid
                        return [ change (attemptPath taskId attempt.AttemptId) (Some attemptOid)
                                    { attempt with Version = attempt.Version + 1; BaseCommit = baseOid
                                                   TargetRef = targetRef } ]
                    })

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
                            do! LedgerStore.fetchReviewRef binding published headOid
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
                                  SourceRepositoryId = binding.ProjectId.ToString("D")
                                  SourceRemote = binding.ApprovedFetchUrl
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
                            do! LedgerStore.fetchReviewRef binding published headOid
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
                                  SourceRepositoryId = binding.ProjectId.ToString("D")
                                  SourceRemote = binding.ApprovedFetchUrl
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
                        do! LedgerStore.fetchReviewRef binding revision.ReviewRef revision.HeadCommit
                        do! LedgerStore.verifyReviewDiff binding revision.BaseCommit revision.HeadCommit
                        return [ change (requestPath taskId id) (Some requestOid)
                                    { request with Version = request.Version + 1; Status = "Claimed"
                                                   ReviewerId = reviewerId; ClaimantId = reviewerId
                                                   ClaimedRevision = expectedRevision } ]
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
                            if request.Status = "Unclaimed" then actor = request.OwnerAgentId
                            else actor = request.ClaimantId && actor <> request.OwnerAgentId
                        if not authorized then
                            return! Error(ReviewAccessDenied "Withdrawal of a claimed review requires the claimant.")
                        return
                            [ change (requestPath taskId id) (Some requestOid)
                                { request with Version = request.Version + 1; Status = "Withdrawn"
                                               DecisionSequence = snapshot.Header.NextSequence; Feedback = reason }
                              change (attemptPath taskId attempt.AttemptId) (Some attemptOid)
                                { attempt with Version = attempt.Version + 1; ActiveReviewRequestId = null } ]
                    })

    // Handoff preserves the claimed revision. The implementation owner cannot reopen it.
    let reassignReviewer binding operationId taskId requestId actor nextReviewer expectedVersion =
        if not (validTaskId taskId) || requestId = Guid.Empty
           || not (validAgent actor) || not (validAgent nextReviewer) || expectedVersion < 1 then
            Error(InvalidBinding "Invalid reviewer handoff.")
        else
            let id = requestId.ToString("N")
            LedgerStore.transact binding
                (intent operationId actor "ReassignReviewer"
                    {| taskId = taskId; requestId = requestId; actor = actor
                       nextReviewer = nextReviewer; expectedVersion = expectedVersion |})
                (fun snapshot ->
                    result {
                        let! _, _, attempt, _ = getAttempt binding snapshot taskId
                        let! request, requestOid = required<ReviewRequest> binding snapshot (requestPath taskId id)
                        let! _ = required<ReviewAgent> binding snapshot (agentPath nextReviewer)
                        if request.Status <> "Claimed" || request.Version <> expectedVersion
                           || attempt.ActiveReviewRequestId <> id then
                            return! Error(ReviewVersionConflict "Claimed review has changed.")
                        if actor <> request.ClaimantId || nextReviewer = request.OwnerAgentId
                           || nextReviewer = actor then
                            return! Error(ReviewAccessDenied "Only claimant can hand off to an independent reviewer.")
                        return
                            [ change (requestPath taskId id) (Some requestOid)
                                { request with Version = request.Version + 1
                                               ReviewerId = nextReviewer; ClaimantId = nextReviewer } ]
                    })

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
                              ExpectedTargetHead = observed; SourceRemote = binding.ApprovedFetchUrl
                              PlannedSequence = snapshot.Header.NextSequence
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
                                { agent with Version = agent.Version + 1; State = "Assigned"
                                             FocusAttemptId = attempt.AttemptId; ActiveAttemptId = null
                                             StateReason = reason
                                             StateSequence = snapshot.Header.NextSequence } ]
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
                       specHtml = newSpecHtml |})
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
                                { agent with Version = agent.Version + 1
                                             State = "Assigned"; ActiveAttemptId = null
                                             StateReason = reason
                                             StateSequence = snapshot.Header.NextSequence }
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
