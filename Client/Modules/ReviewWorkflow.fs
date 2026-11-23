module Client.ReviewWorkflow

open System
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Security.Cryptography
open FsToolkit.ErrorHandling
open Client.LedgerStore
open Client.LegacyDirectWriterFence
open Client.RoleInstructions

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
      ActiveSessionId: string
      AssociatedSessionIds: string list }
    with
        [<System.Text.Json.Serialization.JsonIgnore>]
        member this.State =
            if this.Execution = "Working" then "Working"
            elif not (isNull (box this.CurrentAssignment)) then "Ready"
            elif this.UnassignedDisposition = "Parked" then "Parked"
            else "NeedsWork"
        [<System.Text.Json.Serialization.JsonIgnore>]
        member this.FocusAttemptId =
            if isNull (box this.CurrentAssignment) then null else this.CurrentAssignment.AttemptId
        [<System.Text.Json.Serialization.JsonIgnore>]
        member this.ActiveAttemptId =
            if this.Execution = "Working" then this.FocusAttemptId else null

/// A read-only decision, never a pre-activation mutation or durable operation.
type AutomaticActivationDecision =
    | ActivationEligible of taskId: string
    | ActivationIneligible of reason: string

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
      SourceVersion: int
      ReviewedHeadOid: string
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
type ResponseAlert =
    { AlertId: string
      IssuedBy: string
      Message: string
      QueuedAtUtc: string }

[<CLIMutable>]
type ResponseAlertQueue =
    { AgentId: string
      Version: int
      Pending: ResponseAlert list }

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

[<CLIMutable>]
type BlockedNotificationSender =
    { FromProjectName: string
      FromAgentId: string
      AutoReply: string }

[<CLIMutable>]
type AgentNotificationPreferences =
    { AgentId: string
      Version: int
      MuteAll: bool
      MuteAutoReply: string
      BlockedSenders: BlockedNotificationSender list }

[<CLIMutable>]
type NotificationPreferencesSnapshot =
    { SnapshotCommit: string
      Preferences: AgentNotificationPreferences }

// A blocked operation has one immutable decision receipt, not an inbox
// notification. Retrying its UUID returns the original recipient decision.
[<CLIMutable>]
type NotificationSuppression =
    { NotificationId: string
      FromAgentId: string
      FromProjectName: string
      ToAgentId: string
      AutoReply: string
      CreatedAtUtc: string }

[<CLIMutable>]
type NotificationDelivery =
    { Status: string
      NotificationId: string
      AutoReply: string }

// An explicit typed locator; shared review IDs never fall back to a task lookup.
type IntegrationReviewLocator =
    | TaskBound of reviewId: Guid
    | SharedAccepted of reviewId: Guid * approvalOperationId: Guid * reviewRef: string

// Exact evidence approved by the authenticated local Operator Challenge reply
// route. The approval is an existing immutable canonical operation, not a
// second integration ledger or a caller-supplied "approved" flag.
[<CLIMutable>]
type SharedAcceptedBindingApproval =
    { Kind: string
      TaskId: string
      AttemptId: string
      AssignmentGeneration: int
      SpecBlobOid: string
      ReviewId: string
      ReviewRef: string
      ReviewVersion: int
      ReviewedHeadOid: string
      SourceRef: string
      BaseCommit: string
      TargetRef: string
      OwnerAgentId: string
      ReviewerId: string
      DecisionSequence: int64
      DecisionOperationId: string
      SourceRepositoryId: string }

[<CLIMutable>]
type SharedIntegrationBinding =
    { ApprovalOperationId: string
      SubmissionOperationId: string }

[<CLIMutable>]
type IntegrationSubmission =
    { SubmissionId: string
      TaskId: string
      ActorId: string
      SourceRef: string
      SubmittedCommit: string
      ReviewId: string
      ReviewRef: string
      ReviewVersion: int
      ReviewedHeadOid: string
      IntegrationRef: string
      TargetHeadAtSubmission: string
      TargetRef: string
      Status: string
      CreatedSequence: int64
      IntegratedCommit: string
      CompletedAtUtc: string
      // Additive publication proof for managed submissions. Historical
      // pre-publisher entries deserialize these fields as null.
      PublicationPreviousHead: string
      PublicationObservedHead: string
      PublicationOperationId: string
      PublicationMechanism: string
      // Null/absent only for historical task-bound integrations. Shared
      // reviews must carry their original approval and submission locators.
      SharedBinding: SharedIntegrationBinding option }


[<CLIMutable>]
type AggregateInclusionRecord =
    { TaskId: string
      SubmissionId: string
      ProjectId: string
      LedgerId: string
      Epoch: int
      OperationId: string
      ReviewerId: string
      AggregateTaskId: string
      AggregateSubmissionId: string
      AggregateCommit: string
      AggregateTree: string
      PublishedCommit: string
      ObservedTargetHead: string
      ConstituentCommit: string
      TargetRef: string
      Evidence: Common.AggregateInclusionEvidence }

[<CLIMutable>]
type IntegrationSummary =
    { SnapshotCommit: string
      Latest: IntegrationSubmission option
      Submissions: IntegrationSubmission list
      AggregateInclusions: AggregateInclusionRecord list }

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
type AttemptChallengeWait =
    { TaskId: string
      AttemptId: string
      OwnerAgentId: string
      Reason: string
      ChallengeIds: string list
      Status: string
      Version: int
      BlockedAtUtc: string
      RequeuedAtUtc: string }

// Immutable reverse-index locator. A wait may be requeued and later blocked
// again under a newer assignment generation; old index entries are harmless
// because the canonical wait and current attempt must still match.
[<CLIMutable>]
type AttemptChallengeWaitIndex =
    { TaskId: string
      AttemptId: string
      OwnerAgentId: string
      AssignmentGeneration: int }

[<CLIMutable>]
type InboxChallengeItem =
    { ChallengeId: string
      ConditionId: string
      IssuerAgentId: string
      OwnerAgentId: string
      SuggestedAgentId: string
      Title: string
      DescriptionPreview: string
      ChallengeStatus: string
      ConditionStatus: string
      Version: int
      EvidencePreview: string
      ReasonPreview: string
      UpdatedAtUtc: string }

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
      BlockedChallengeWaits: AttemptChallengeWait list
      ChallengeSubscribed: bool
      ChallengeAssigned: InboxChallengeItem list
      ChallengeTriage: InboxChallengeItem list
      ChallengeRecent: InboxChallengeItem list
      ChallengeDependencies: InboxChallengeItem list
      ChallengeAssignedOmitted: int
      ChallengeTriageOmitted: int
      ChallengeRecentOmitted: int
      ChallengeDependenciesOmitted: int
      PendingReviews: BranchReview list
      AvailableReviews: BranchReview list
      UnacknowledgedFeedback: ReviewFeedbackNotice list
      PendingNotifications: AgentNotification list
      IntegrationSubmissions: IntegrationSubmission list
      InstructionLayers: InstructionResolution
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
            elif execution = "Working" then null
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
                     WorkingTurnStartedAtUtc = null }

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
                // Historical schema-v2 integrations lack all five managed-ref
                // evidence fields, and integration receipts written before the
                // managed publisher lack all four publication-proof fields.
                // Historical schema-v3 reviews lack both pinned source fields.
                // Accept only these exact, all-absent field groups:
                // incomplete or mixed provenance must continue to fail closed.
                let historicalEvidenceFields =
                    if typeof<'t> = typeof<IntegrationSubmission> then
                        [ "reviewRef"; "reviewVersion"; "reviewedHeadOid"
                          "integrationRef"; "targetHeadAtSubmission" ]
                    elif typeof<'t> = typeof<BranchReview> then
                        [ "sourceVersion"; "reviewedHeadOid" ]
                    else []
                let historicalEvidenceShape =
                    not (List.isEmpty historicalEvidenceFields)
                    // The serializer accepts case-insensitive property names.
                    // Use the same equivalence when testing historic absence,
                    // or e.g. ReviewRef bypasses the all-or-none proof gate.
                    && (historicalEvidenceFields |> List.forall (fun name ->
                        not (names.Contains name)))
                let historicalPublicationFields =
                    if typeof<'t> = typeof<IntegrationSubmission> then
                        [ "publicationPreviousHead"; "publicationObservedHead"
                          "publicationOperationId"; "publicationMechanism" ]
                    else []
                let historicalPublicationShape =
                    not (List.isEmpty historicalPublicationFields)
                    && (historicalPublicationFields |> List.forall (fun name ->
                        // See historicalEvidenceShape: prevent mixed-case partial proof.
                        not (names.Contains name)))
                let complete =
                    required |> Array.forall (fun property ->
                        let mutable memberValue = Unchecked.defaultof<JsonElement>
                        root.TryGetProperty(property, &memberValue)
                        // FromProjectName was added after schema v2 notifications
                        // were already stored. Missing means same-project; all
                        // other notification members remain mandatory.
                        || (historicalEvidenceShape && List.contains property historicalEvidenceFields)
                        || (historicalPublicationShape && List.contains property historicalPublicationFields)
                        || (typeof<'t> = typeof<IntegrationSubmission>
                            && property = "sharedBinding")
                        || (typeof<'t> = typeof<AgentNotification>
                            && property = "fromProjectName")
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
                                    (status "execution" [ "Working" ]
                                     && disposition.ValueKind = JsonValueKind.Null)
                                    || (status "execution" [ "Stopped" ]
                                        && status "unassignedDisposition" [ "NeedsWork"; "Parked" ])
                                else current.ValueKind = JsonValueKind.Object
                                     && disposition.ValueKind = JsonValueKind.Null))
                    | "ReviewAttempt" -> status "phase" [ "Queued"; "Started"; "Finished"; "Deferred"; "Blocked";
                          "Superseded"; "Abandoned" ]
                    | "BranchReview" ->
                        status "status" [ "Unclaimed"; "Claimed"; "Accepted"; "ChangesRequested"; "Withdrawn" ]
                        && (let mutable kind = Unchecked.defaultof<JsonElement>
                            root.TryGetProperty("kind", &kind) && kind.ValueKind = JsonValueKind.String)
                    | "IntegrationSubmission" ->
                         status "status" [ "Pending"; "Integrated" ]
                         || (status "status" [ "IncludedInAggregate" ]
                             && not historicalEvidenceShape && not historicalPublicationShape
                             && ([ "integratedCommit"; "completedAtUtc"; "publicationPreviousHead"
                                   "publicationObservedHead"; "publicationOperationId"; "publicationMechanism" ]
                                 |> List.forall (fun name ->
                                     let mutable field = Unchecked.defaultof<JsonElement>
                                     root.TryGetProperty(name, &field) && field.ValueKind = JsonValueKind.Null)))
                    | "AttemptChallengeWait" -> status "status" [ "Waiting"; "Eligible"; "Requeued" ]
                    | "ReviewFeedbackNotice" -> status "status" [ "Accepted"; "ChangesRequested" ]
                    | "AgentCheckpoint" -> status "kind" [ "Yield"; "Completion"; "Recovery"; "Progress"; "Submission"; "Revision" ]
                    | "TaskLifecycleEvent" ->
                        status "action" [ "WaitOnChallenges"; "RequeueChallengeBlockedAttempt";
                                          "ParkAttempt"; "BlockAttempt"; "ResumeAttempt"; "CancelTask";
                                          "AbandonAttempt"; "TransferOwner"; "CloseTask"; "ReopenTask"; "CorrectAgentWorkingState" ]
                    | "AssignmentIssue" ->
                        status "reason" [ "ChallengeUnblock"; "Assignment"; "ReviewChanges"; "CompletionRequeue"; "Recovery"; "ExplicitReissue"; "AdministrativeResume"; "OwnershipTransfer"; "SpecAdoption" ]
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
    let private challengeWaitPath taskId attemptId =
        $"tasks/{taskId}/attempts/{attemptId}/challenge-wait.json"
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

    let private attributionPath taskId (submissionId: Guid) =
        $"records/integration-attributions/{taskId}/{submissionId:N}.json"

    let private notificationPath (notificationId: Guid) =
        "notifications/" + notificationId.ToString("N") + ".json"
    let private suppressionPath (notificationId: Guid) =
        "records/notification-suppressions/" + notificationId.ToString("N") + ".json"

    let private reviewRef taskId requestId number = $"refs/heads/jarvis-review/{taskId}/{requestId}/r{number}"
    let private agentKey (agentId: string) =
        Convert.ToBase64String(Encoding.UTF8.GetBytes agentId)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_')
    let private agentPath (agentId: string) = $"agents/{agentKey agentId}.json"
    let private notificationPreferencesPath agentId =
        $"records/notification-preferences/{agentKey agentId}.json"
    let private responseAlertsPath agentId =
        $"records/response-alerts/{agentKey agentId}.json"

    // Reverse indexes constrain dependency lookups to one challenge or one
    // owner rather than scanning historical task records at every transition.
    let internal challengeWaitIndexPrefix challengeId =
        $"challenge-waits/by-challenge/{challengeId}"
    let internal ownerWaitIndexPrefix (ownerId: string) =
        let safe =
            Convert.ToBase64String(Encoding.UTF8.GetBytes ownerId)
                .TrimEnd('=').Replace('+', '-').Replace('/', '_')
        $"challenge-waits/by-owner/{safe}"
    let internal waitIndexPath prefix taskId attemptId generation =
        $"{prefix}/{taskId}/{attemptId}/{generation}.json"

    let private validTaskId (taskId: string) = not (isNull taskId) && idPattern.IsMatch taskId
    let private validAgent (agent: string) =
        not (String.IsNullOrWhiteSpace agent) && Encoding.UTF8.GetByteCount(agent) <= 120
    let private validText limit (value: string) =
        not (String.IsNullOrWhiteSpace value) && Encoding.UTF8.GetByteCount(value) <= limit
    let private validSenderProject (name: string) =
        not (isNull name) && Encoding.UTF8.GetByteCount(name) <= 256
        && not (name.Contains('/') || name.Contains('\\') || (name |> Seq.exists Char.IsControl))
    let private validAutoReply (value: string) =
        not (isNull value)
        && Encoding.UTF8.GetByteCount(value) <= 512
        && (value = "" || not (String.IsNullOrWhiteSpace value))
        && not (value |> Seq.exists Char.IsControl)
    let private validNotificationPreferences (preferences: AgentNotificationPreferences) =
        validAgent preferences.AgentId && preferences.Version > 0
        && validAutoReply preferences.MuteAutoReply
        && not (isNull (box preferences.BlockedSenders))
        && preferences.BlockedSenders.Length <= 32
        && (preferences.BlockedSenders
            |> List.forall (fun item ->
                validSenderProject item.FromProjectName
                && validAgent item.FromAgentId && validAutoReply item.AutoReply))
        && ((preferences.BlockedSenders
             |> List.map (fun item -> item.FromProjectName, item.FromAgentId)
             |> List.distinct).Length = preferences.BlockedSenders.Length)
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

    // Index corruption is a ledger-integrity failure, not a stale caller CAS.
    let private indexRequired<'t> binding snapshot file : Result<'t * string, LedgerFailure> =
        read<'t> binding snapshot file
        |> Result.bind (function
            | Some value -> Ok value
            | None -> Error(LedgerIntegrityError ("Missing canonical index evidence: " + file)))

    // A reverse edge is a locator, never proof that a wait was issued.
    // Historical generations survive reblocking, but require the immutable
    // original block command and lifecycle event. Future edges have no origin.
    let private issuedWaitDependenciesAt binding snapshot
                                             (locator: AttemptChallengeWaitIndex) = result {
        let! paths = LedgerStore.listAt binding snapshot
                         ($"tasks/{locator.TaskId}/lifecycle")
        let mutable matches: TaskLifecycleEvent list = []
        for path in paths do
            if path.EndsWith(".json", StringComparison.Ordinal) then
                let! event, _ = indexRequired<TaskLifecycleEvent> binding snapshot path
                if event.Action = "WaitOnChallenges"
                   && event.TaskId = locator.TaskId
                   && event.AttemptId = locator.AttemptId
                   && event.NewGeneration = locator.AssignmentGeneration then
                    if path <> lifecycleEventPath locator.TaskId event.Sequence
                       || event.PreviousGeneration <> locator.AssignmentGeneration
                       || event.NewPhase <> "Blocked"
                       || event.PreviousOwnerId <> locator.OwnerAgentId
                       || event.NewOwnerId <> locator.OwnerAgentId
                       || event.ActorId <> locator.OwnerAgentId then
                        return! Error(LedgerIntegrityError "Historical challenge block provenance mismatch.")
                    matches <- event :: matches
        let! event =
            match matches with
            | [ event ] -> Ok event
            | _ -> Error(LedgerIntegrityError "Missing or ambiguous historical challenge block event.")
        let! operationId =
            match Guid.TryParse event.OperationId with
            | true, id when id <> Guid.Empty -> Ok id
            | _ -> Error(LedgerIntegrityError "Invalid historical challenge block operation ID.")
        let! operation =
            match LedgerStore.getOperationAt binding snapshot operationId with
            | Ok(Some value) -> Ok value
            | Ok None -> Error(LedgerIntegrityError "Historical challenge block operation is missing.")
            | Error error -> Error error
        let! _ = LedgerStore.getOperationOriginAt binding snapshot operation
        if operation.Type <> "WaitOnChallenges" || operation.Actor <> locator.OwnerAgentId
           || operation.Sequence <> event.Sequence || isNull operation.Paths
           || not (Array.contains (challengeWaitPath locator.TaskId locator.AttemptId) operation.Paths)
           || not (Array.contains (lifecycleEventPath locator.TaskId event.Sequence) operation.Paths) then
            return! Error(LedgerIntegrityError "Historical block operation does not corroborate its event.")
        let! dependencies =
            try
                use envelope = JsonDocument.Parse operation.CanonicalRequestJson
                if envelope.RootElement.GetProperty("kind").GetString() <> "WaitOnChallenges"
                   || envelope.RootElement.GetProperty("actor").GetString() <> locator.OwnerAgentId
                   || envelope.RootElement.GetProperty("operationId").GetString() <> event.OperationId then
                    Error(LedgerIntegrityError "Historical block request identity mismatch.")
                else
                    use request = JsonDocument.Parse(envelope.RootElement.GetProperty("request").GetString())
                    let data = request.RootElement
                    let dependencies =
                        data.GetProperty("challengeIds").EnumerateArray()
                        |> Seq.map (fun item -> item.GetString()) |> Seq.toList
                    if data.GetProperty("taskId").GetString() <> locator.TaskId
                       || data.GetProperty("attemptId").GetString() <> locator.AttemptId
                       || data.GetProperty("generation").GetInt32() <> locator.AssignmentGeneration then
                        Error(LedgerIntegrityError "Historical block request/locator mismatch.")
                    else Ok dependencies
            with ex -> Error(LedgerIntegrityError ("Invalid historical block request: " + ex.Message))
        return dependencies, operation.Paths
    }

    /// One pinned-snapshot validation for both index directions and all the
    /// sibling edges belonging to the same block. Never infer permission from
    /// an uncorroborated materialized index.
    let internal verifiedWaitIndexAt binding snapshot indexPath
                                     (challengeId: string option)
                                     (ownerId: string option) = result {
        let! locator, _ = indexRequired<AttemptChallengeWaitIndex> binding snapshot indexPath
        if not (validTaskId locator.TaskId)
           || (match Guid.TryParseExact(locator.AttemptId, "N") with | true, _ -> false | _ -> true)
           || not (validAgent locator.OwnerAgentId) || locator.AssignmentGeneration < 1 then
            return! Error(LedgerIntegrityError "Invalid challenge wait-index locator.")
        let prefix =
            match challengeId, ownerId with
            | Some id, None -> challengeWaitIndexPrefix id
            | None, Some id -> ownerWaitIndexPrefix id
            | _ -> ""
        if String.IsNullOrEmpty prefix
           || indexPath <> waitIndexPath prefix locator.TaskId locator.AttemptId locator.AssignmentGeneration
           || (ownerId |> Option.exists ((<>) locator.OwnerAgentId)) then
            return! Error(LedgerIntegrityError "Challenge wait-index path/owner identity mismatch.")
        let path = challengeWaitPath locator.TaskId locator.AttemptId
        let! wait, waitOid = indexRequired<AttemptChallengeWait> binding snapshot path
        let! task, _ = indexRequired<ReviewTask> binding snapshot (taskPath locator.TaskId)
        let! attempt, _ = indexRequired<ReviewAttempt> binding snapshot
                              (attemptPath locator.TaskId locator.AttemptId)
        if wait.TaskId <> locator.TaskId || wait.AttemptId <> locator.AttemptId
           || task.TaskId <> locator.TaskId || attempt.TaskId <> locator.TaskId
           || attempt.AttemptId <> locator.AttemptId
           || locator.AssignmentGeneration > attempt.AssignmentGeneration then
            return! Error(LedgerIntegrityError "Challenge wait-index canonical lineage mismatch.")
        let current =
            attempt.AssignmentGeneration = locator.AssignmentGeneration
            && wait.Status <> "Requeued"
        let! dependencies, recordedPaths =
            if current then
                if wait.OwnerAgentId <> locator.OwnerAgentId
                   || attempt.OwnerAgentId <> locator.OwnerAgentId
                   || isNull (box wait.ChallengeIds) then
                    Error(LedgerIntegrityError "Current challenge wait/index owner mismatch.")
                else result {
                    let! issued, originalPaths = issuedWaitDependenciesAt binding snapshot locator
                    if issued <> wait.ChallengeIds then
                        return! Error(LedgerIntegrityError "Canonical active wait disagrees with original Challenge block.")
                    return issued, originalPaths
                }
            elif locator.AssignmentGeneration < attempt.AssignmentGeneration then
                issuedWaitDependenciesAt binding snapshot locator
            else
                Error(LedgerIntegrityError "Challenge wait-index has no current or historical generation.")
        if List.isEmpty dependencies || dependencies.Length > 16
           || (dependencies |> List.distinct).Length <> dependencies.Length
           || (dependencies |> List.exists (fun id ->
                match Guid.TryParseExact(id, "N") with | true, _ -> false | _ -> true))
           || (challengeId |> Option.exists (fun id -> not (List.contains id dependencies))) then
            return! Error(LedgerIntegrityError "Challenge wait-index dependency provenance mismatch.")
        // An indexed Challenge must still exist at this exact verified head.
        // Cancellation is valid history, but a dangling canonical identity is not.
        for dependency in dependencies do
            let file = "challenges/" + dependency + "/challenge.json"
            let! content, oid = LedgerStore.readAt binding snapshot file
            match content, oid with
            | Some body, Some _ ->
                try
                    use document = JsonDocument.Parse body
                    let root = document.RootElement
                    let challengeTask = root.GetProperty("taskId").GetString()
                    let state = root.GetProperty("status").GetString()
                    if root.GetProperty("schemaVersion").GetInt32() <> 2
                       || root.GetProperty("challengeId").GetString() <> dependency
                       || (not (String.IsNullOrEmpty challengeTask) && challengeTask <> locator.TaskId)
                       || root.GetProperty("completionPolicy").GetString() <> "All"
                       || root.GetProperty("conditions").GetArrayLength() < 1
                       || not (List.contains state [ "Pending"; "Blocked"; "Satisfied"; "Cancelled" ]) then
                        return! Error(LedgerIntegrityError "Indexed canonical Challenge identity mismatch.")
                with ex ->
                    return! Error(LedgerIntegrityError ("Invalid indexed canonical Challenge: " + ex.Message))
            | _ -> return! Error(LedgerIntegrityError ("Indexed canonical Challenge is missing: " + dependency))
        let expectedPaths =
            [ waitIndexPath (ownerWaitIndexPrefix locator.OwnerAgentId)
                  locator.TaskId locator.AttemptId locator.AssignmentGeneration ]
            @ [ for id in dependencies ->
                    waitIndexPath (challengeWaitIndexPrefix id)
                        locator.TaskId locator.AttemptId locator.AssignmentGeneration ]
        if expectedPaths |> List.exists (fun edge ->
            not (Array.contains edge recordedPaths)) then
            return! Error(LedgerIntegrityError "Historical challenge indexes were not issued by the block command.")
        for edge in expectedPaths do
            let! linked, _ = indexRequired<AttemptChallengeWaitIndex> binding snapshot edge
            if linked <> locator then
                return! Error(LedgerIntegrityError "Challenge wait reverse-index pair is missing or inconsistent.")
        return locator, wait, waitOid
    }

    // Verify an owner's issued reverse edges in both directions at one pinned
    // head. Each historically linked Challenge prefix is in scope, including
    // extra future-generation records not reachable from the owner index.
    // Unknown/unrelated Challenge IDs are deliberately not globally scanned.
    let internal verifyOwnerWaitIndexesAt binding snapshot owner = result {
        let! paths = LedgerStore.listIssuedWaitIndexPathsAt binding snapshot (ownerWaitIndexPrefix owner)
        let mutable historicallyLinkedChallenges = Set.empty<string>
        for path in paths do
            if not (path.EndsWith(".json", StringComparison.Ordinal)) then
                return! Error(LedgerIntegrityError "Unexpected owner wait-index record.")
            let! locator, _, _ = verifiedWaitIndexAt binding snapshot path None (Some owner)
            let! dependencyIds, _ = issuedWaitDependenciesAt binding snapshot locator
            historicallyLinkedChallenges <-
                Set.union historicallyLinkedChallenges (Set.ofList dependencyIds)
        // The inverse scan is limited to Challenge IDs proved by this owner's
        // original immutable block operations. Preserve valid N1/N2 history
        // while rejecting unissued N3 links even when no owner mirror exists.
        for challengeId in historicallyLinkedChallenges do
            let! challengePaths =
                LedgerStore.listIssuedWaitIndexPathsAt binding snapshot
                    (challengeWaitIndexPrefix challengeId)
            for indexPath in challengePaths do
                if not (indexPath.EndsWith(".json", StringComparison.Ordinal)) then
                    return! Error(LedgerIntegrityError "Unexpected known Challenge wait-index record.")
                let! _ = verifiedWaitIndexAt binding snapshot indexPath (Some challengeId) None
                ()
        return ()
    }

    // Called by canonical agent-slot release transitions. Eligibility is
    // durable; release requeues at most one blocked attempt and never preempts
    // an occupied owner or enters Working.
    let internal reconcileEligibleOnRelease binding snapshot (operationId: Guid) actor
                                         (released: ReviewAgent) = result {
        let ownerId = released.AgentId
        if not (isNull (box released.CurrentAssignment))
           || released.Execution <> "Stopped"
           || released.UnassignedDisposition <> "NeedsWork" then
            return released, []
        else
            let ownerPrefix = ownerWaitIndexPrefix ownerId
            let! _ = verifyOwnerWaitIndexesAt binding snapshot ownerId
            let! paths = LedgerStore.listAt binding snapshot ownerPrefix
            let indexPaths =
                paths
                |> List.filter (fun p -> p.EndsWith(".json", StringComparison.Ordinal))
                |> List.sort
            let mutable updated = released
            let mutable writes : LedgerFileChange list = []
            let mutable claimed = false
            for indexPath in indexPaths do
                if not claimed then
                    let! locator, _ = required<AttemptChallengeWaitIndex> binding snapshot indexPath
                    if indexPath <> waitIndexPath ownerPrefix locator.TaskId locator.AttemptId locator.AssignmentGeneration
                       || locator.OwnerAgentId <> ownerId
                       || locator.AssignmentGeneration < 1 then
                        return! Error(LedgerIntegrityError "Owner wait-index identity mismatch.")
                    let file = challengeWaitPath locator.TaskId locator.AttemptId
                    let! wait, waitOid = required<AttemptChallengeWait> binding snapshot file
                    if file <> challengeWaitPath wait.TaskId wait.AttemptId then
                        return! Error(LedgerIntegrityError "Challenge wait path/identity mismatch.")
                    if wait.OwnerAgentId = ownerId && wait.Status = "Eligible"
                       && wait.TaskId = locator.TaskId && wait.AttemptId = locator.AttemptId then
                        let! task, _ = required<ReviewTask> binding snapshot (taskPath wait.TaskId)
                        let! attempt, attemptOid =
                            required<ReviewAttempt> binding snapshot (attemptPath wait.TaskId wait.AttemptId)
                        if task.Lifecycle = "InProgress" && task.AttemptId = wait.AttemptId
                           && attempt.TaskId = wait.TaskId
                           && attempt.AttemptId = wait.AttemptId
                           && attempt.AssignmentGeneration = locator.AssignmentGeneration
                           && attempt.OwnerAgentId = ownerId && attempt.Phase = "Blocked" then
                            let mutable satisfied = not (List.isEmpty wait.ChallengeIds)
                            for dependency in wait.ChallengeIds do
                                match Guid.TryParseExact(dependency, "N") with
                                | false, _ -> return! Error(LedgerIntegrityError "Invalid stored challenge UUID.")
                                | true, id ->
                                    let path = "challenges/" + id.ToString("N") + "/challenge.json"
                                    let! content, oid = LedgerStore.readAt binding snapshot path
                                    match content, oid with
                                    | Some body, Some _ ->
                                        try
                                            use doc = JsonDocument.Parse(body)
                                            let root = doc.RootElement
                                            let conditions =
                                                root.GetProperty("conditions").EnumerateArray()
                                                |> Seq.map (fun c -> c.GetProperty("status").GetString())
                                                |> Seq.toList
                                            let state = root.GetProperty("status").GetString()
                                            if root.GetProperty("schemaVersion").GetInt32() <> 2
                                               || root.GetProperty("challengeId").GetString() <> dependency
                                               || root.GetProperty("completionPolicy").GetString() <> "All"
                                               || List.isEmpty conditions
                                               || (state = "Satisfied"
                                                   && not (conditions |> List.forall ((=) "Satisfied"))) then
                                                return! Error(LedgerIntegrityError "Dependency challenge identity mismatch.")
                                            if state <> "Satisfied" then
                                                satisfied <- false
                                        with ex ->
                                            return! Error(LedgerIntegrityError ("Invalid dependency challenge: " + ex.Message))
                                    | _ -> return! Error(LedgerIntegrityError "Dependency challenge is missing.")
                            if satisfied then
                                let sequence = snapshot.Header.NextSequence
                                let timestamp = DateTimeOffset.UtcNow.ToString("O")
                                let generation = attempt.AssignmentGeneration + 1
                                let reason = "Satisfied challenge dependencies requeued after owner release"
                                let readyAttempt =
                                    { attempt with Version = attempt.Version + 1
                                                   Phase = "Queued"
                                                   AssignmentGeneration = generation
                                                   IssuedSequence = sequence }
                                updated <-
                                    { released with CurrentAssignment = { TaskId = wait.TaskId; AttemptId = wait.AttemptId }
                                                    UnassignedDisposition = null
                                                    Execution = "Stopped"
                                                    StateReason = reason; StateSequence = sequence
                                                    StateEnteredAtUtc = timestamp
                                                    WorkingTurnStartedAtUtc = null
                                                    ActiveSessionId = null; AssociatedSessionIds = [] }
                                let audit : TaskLifecycleEvent =
                                    { TaskId = wait.TaskId; AttemptId = wait.AttemptId
                                      OperationId = operationId.ToString("D")
                                      Sequence = sequence; Action = "RequeueChallengeBlockedAttempt"
                                      ActorId = actor; Reason = reason
                                      PreviousLifecycle = task.Lifecycle; NewLifecycle = task.Lifecycle
                                      PreviousPhase = "Blocked"; NewPhase = "Queued"
                                      PreviousOwnerId = ownerId; NewOwnerId = ownerId
                                      PreviousGeneration = attempt.AssignmentGeneration
                                      NewGeneration = generation
                                      ReviewRequestId = null
                                      ReviewDisposition = "Unchanged"; Evidence = null
                                      RequestingAgentId = null; TargetAgentId = null
                                      PreviousAgentState = null; NewAgentState = null
                                      PreviousAgentVersion = 0; NewAgentVersion = 0 }
                                writes <- [
                                    change file (Some waitOid)
                                        { wait with Version = wait.Version + 1
                                                    Status = "Requeued"; RequeuedAtUtc = timestamp }
                                    change (attemptPath wait.TaskId wait.AttemptId) (Some attemptOid) readyAttempt
                                    change (issuePath wait.TaskId wait.AttemptId generation) None
                                        (issue wait.TaskId readyAttempt generation actor "ChallengeUnblock" reason sequence)
                                    change (lifecycleEventPath wait.TaskId sequence) None audit
                                ]
                                claimed <- true
            return updated, writes
    }
    let createTaskWithSpecAs binding actor operationId taskId title html =
        if not (validTaskId taskId) || not (validText 512 title)
           || not (validTaskHtml html) then
            Error(InvalidBinding "Invalid task ID, title or canonical HTML specification.")
        else
            LegacyDirectWriterFence.transact binding
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
            LegacyDirectWriterFence.transact binding
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
                LegacyDirectWriterFence.transact binding
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
            LegacyDirectWriterFence.transact binding
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

    // Snapshot-scoped evidence lookup for composition acceptance. The caller
    // must check review identity and semantic acceptance independently.
    let getIntegrationSubmissionAt binding snapshot taskId submissionId =
        if not (validTaskId taskId) || submissionId = Guid.Empty then
            Error(InvalidBinding "Invalid integration evidence identity.")
        else result {
            let! submission, _ = required<IntegrationSubmission> binding snapshot
                                    (integrationPath taskId submissionId)
            if submission.TaskId <> taskId
               || submission.SubmissionId <> submissionId.ToString("N") then
                return! Error(LedgerIntegrityError "Canonical integration receipt identity mismatch.")
            return submission
        }

    // Use the existing authenticated local Operator Challenge reply as the
    // durable approval. The evidence is a versioned exact identity, not a
    // human-readable similarity match or a new generic ledger write.
    let sharedAcceptedBindingApprovalText (approval: SharedAcceptedBindingApproval) =
        entityJson approval

    let private sharedNamespace = "_reviews"
    let private realIntegrationTaskId taskId =
        validTaskId taskId && taskId <> sharedNamespace

    let private exactGuid (format: string) (value: string) =
        match Guid.TryParseExact(value, format) with
        | true, id when id <> Guid.Empty && id.ToString(format) = value -> Ok id
        | _ -> Error(InvalidBinding "Invalid canonical integration evidence UUID.")

    let private operationOrigin binding snapshot operationId = result {
        let! maybeOperation = LedgerStore.getOperationAt binding snapshot operationId
        let! operation =
            match maybeOperation with
            | Some operation -> Ok operation
            | None -> Error(InvalidBinding "Missing canonical integration evidence operation.")
        let! original = LedgerStore.verifiedIntegrationOperationSnapshotAt binding snapshot operation
        return operation, original
    }

    let private operationPayload (operation: LedgerOperation) =
        try
            use envelope = JsonDocument.Parse(operation.CanonicalRequestJson)
            use payload = JsonDocument.Parse(envelope.RootElement.GetProperty("request").GetString())
            if payload.RootElement.ValueKind <> JsonValueKind.Object then
                Error(InvalidBinding "Canonical integration evidence request must be an object.")
            else Ok(payload.RootElement.Clone())
        with _ -> Error(InvalidBinding "Malformed canonical integration evidence request.")

    let private jsonAt binding snapshot path = result {
        let! body, _ = LedgerStore.readAt binding snapshot path
        match body with
        | None -> return! Error(InvalidBinding "Missing canonical integration evidence record.")
        | Some body ->
            try
                use parsed = JsonDocument.Parse(body)
                return parsed.RootElement.Clone()
            with _ -> return! Error(LedgerIntegrityError "Malformed canonical integration evidence record.")
    }

    let private stringMember (element: JsonElement) (name: string) =
        element.GetProperty(name).GetString()

    let private operationWrote (operation: LedgerOperation) path =
        not (isNull operation.Paths) && Array.contains path operation.Paths

    let private sameAcceptedReview (original: BranchReview) (current: BranchReview) =
        (current = original
         || (not original.Acknowledged && current.Acknowledged
             && current.Version = original.Version + 1
             && { current with Version = original.Version; Acknowledged = false } = original))

    let private verifiedSharedDecision binding snapshot (approval: SharedAcceptedBindingApproval) = result {
        let! reviewId = exactGuid "N" approval.ReviewId
        let path = branchReviewPath sharedNamespace approval.ReviewId
        let! review, _ = required<BranchReview> binding snapshot path
        if review.TaskId <> sharedNamespace || review.ReviewId <> reviewId.ToString("N")
           || review.Status <> "Accepted" || review.SourceVersion < 1
           || review.DecisionSequence <= review.CreatedSequence
           || not (validAgent review.OwnerAgentId && validAgent review.ReviewerId)
           || review.OwnerAgentId = review.ReviewerId
           || not (validBranch review.TaskRef && validBranch review.TargetRef)
           || review.ReviewRef <> reviewRef sharedNamespace approval.ReviewId review.SourceVersion
           || not (LedgerStore.validSourceCommitOid review.ReviewedHeadOid
                   && LedgerStore.validSourceCommitOid review.BaseCommit)
           || approval.ReviewRef <> review.ReviewRef || approval.ReviewVersion <> review.SourceVersion
           || approval.ReviewedHeadOid <> review.ReviewedHeadOid
           || approval.SourceRef <> review.TaskRef || approval.BaseCommit <> review.BaseCommit
           || approval.TargetRef <> review.TargetRef || approval.OwnerAgentId <> review.OwnerAgentId
           || approval.ReviewerId <> review.ReviewerId || approval.DecisionSequence <> review.DecisionSequence then
            return! Error(InvalidBinding "Operator binding does not identify an independent immutable Accepted shared review.")
        let! decisionId = exactGuid "D" approval.DecisionOperationId
        let! decision, original = operationOrigin binding snapshot decisionId
        let! accepted, _ = required<BranchReview> binding original path
        if decision.Sequence <> review.DecisionSequence || not (operationWrote decision path)
           || not (sameAcceptedReview accepted review) || accepted.Version < 3
           || accepted.Acknowledged then
            return! Error(InvalidBinding "Shared review differs from its original canonical decision.")
        do! LedgerStore.verifyIntegrationOperationIntent binding decision
                (intent decisionId review.ReviewerId "DecideBranchReview"
                    {| taskId = sharedNamespace; reviewId = approval.ReviewId
                       reviewerId = review.ReviewerId; expectedVersion = accepted.Version - 1
                       status = "Accepted" |})
        do! LedgerStore.fetchReviewRef binding review.ReviewRef review.ReviewedHeadOid
        do! LedgerStore.verifySourceAncestry binding review.BaseCommit review.ReviewedHeadOid
        return review, original
    }

    let private verifyApprovedTask binding snapshot (review: BranchReview) (approval: SharedAcceptedBindingApproval) = result {
        if not (realIntegrationTaskId approval.TaskId) || approval.AssignmentGeneration < 1 then
            return! Error(InvalidBinding "Shared integration requires a real historical task assignment.")
        let! _ = exactGuid "N" approval.AttemptId
        let! task, _ = required<ReviewTask> binding snapshot (taskPath approval.TaskId)
        let! attempt, _ = required<ReviewAttempt> binding snapshot (attemptPath approval.TaskId approval.AttemptId)
        let! issue, _ = required<AssignmentIssue> binding snapshot
                            (issuePath approval.TaskId approval.AttemptId approval.AssignmentGeneration)
        if task.TaskId <> approval.TaskId || task.Lifecycle = "Cancelled"
           || attempt.TaskId <> approval.TaskId || attempt.AttemptId <> approval.AttemptId
           || attempt.OwnerAgentId <> approval.OwnerAgentId
           || attempt.AssignmentGeneration <> approval.AssignmentGeneration
           || attempt.SpecBlobOid <> approval.SpecBlobOid
           || issue.TaskId <> approval.TaskId || issue.AttemptId <> approval.AttemptId
           || issue.Generation <> approval.AssignmentGeneration
           || issue.IssuedSequence <> attempt.IssuedSequence || issue.IssuedSequence >= review.DecisionSequence
           || issue.OwnerAgentId <> approval.OwnerAgentId || issue.SpecBlobOid <> approval.SpecBlobOid
           || not (LedgerStore.validSourceCommitOid approval.SpecBlobOid) then
            return! Error(InvalidBinding "Operator binding does not match the original task assignment and spec.")
        let! repository = LedgerStore.sourceRepositoryIdentity binding
        if approval.SourceRepositoryId <> repository.RepositoryId then
            return! Error(InvalidBinding "Operator binding refers to another source repository.")
        // A genuinely unbound historical attempt is supported only with this
        // explicit immutable approval. A conflicting or partial lease is not.
        let absent = String.IsNullOrWhiteSpace
        let unbound = [ attempt.SourceRef; attempt.BaseCommit; attempt.TargetRef
                        attempt.SourceRepositoryId; attempt.SourceRemote; attempt.SourcePushRemote ]
                      |> List.forall absent
        if not unbound then
            if attempt.SourceRepositoryId <> repository.RepositoryId
               || attempt.SourceRemote <> repository.FetchUrl
               || attempt.SourcePushRemote <> repository.PushUrl
               || attempt.TargetRef <> approval.TargetRef
               || (not (absent attempt.SourceRef) && attempt.SourceRef <> approval.SourceRef)
               || not (LedgerStore.validSourceCommitOid attempt.BaseCommit) then
                return! Error(InvalidBinding "Operator binding conflicts with the historical source lease.")
            do! LedgerStore.verifySourceAncestry binding attempt.BaseCommit approval.BaseCommit
        return ()
    }

    let private verifiedSharedApproval binding snapshot taskId reviewId approvalOperationId = result {
        let! operation, original = operationOrigin binding snapshot approvalOperationId
        if operation.Type <> "RespondOperatorCondition"
           || not (validAgent operation.Actor)
           || not (operation.Actor.StartsWith("operator:", StringComparison.Ordinal))
           || operation.Actor.Length <= "operator:".Length then
            return! Error(ReviewAccessDenied "Shared integration requires an authenticated Operator approval operation.")
        let! payload = operationPayload operation
        try
            let challengeId = stringMember payload "challengeId"
            let conditionId = stringMember payload "conditionId"
            let version = payload.GetProperty("expectedVersion").GetInt32()
            let evidence = stringMember payload "evidence"
            let! _ = exactGuid "N" challengeId
            let! _ = exactGuid "N" conditionId
            do! LedgerStore.verifyIntegrationOperationIntent binding operation
                    (intent approvalOperationId operation.Actor "RespondOperatorCondition"
                        {| challengeId = challengeId; conditionId = conditionId; expectedVersion = version
                           evidence = evidence; reason = "" |})
            let! approval = decodeEntity<SharedAcceptedBindingApproval> "Operator shared integration approval" evidence
            use evidenceJson = JsonDocument.Parse(evidence)
            if (evidenceJson.RootElement.EnumerateObject() |> Seq.length)
                    <> typeof<SharedAcceptedBindingApproval>.GetProperties().Length + 1
               || approval.Kind <> "SharedAcceptedIntegrationBinding/v1"
               || approval.TaskId <> taskId || approval.ReviewId <> reviewId
               || operation.Sequence <= approval.DecisionSequence then
                return! Error(InvalidBinding "Operator approval names another task, review or decision.")
            let challengePath = $"challenges/{challengeId}/challenge.json"
            let eventPath = $"challenges/{challengeId}/events/{operation.Sequence}.json"
            if not (operationWrote operation challengePath && operationWrote operation eventPath) then
                return! Error(InvalidBinding "Operator approval lacks its original challenge event.")
            let! challenge = jsonAt binding original challengePath
            let! event = jsonAt binding original eventPath
            let conditions =
                challenge.GetProperty("conditions").EnumerateArray()
                |> Seq.filter (fun condition -> stringMember condition "conditionId" = conditionId)
                |> Seq.toList
            if stringMember challenge "challengeId" <> challengeId
               || stringMember challenge "taskId" <> taskId
               || challenge.GetProperty("version").GetInt32() <> version + 1
               || stringMember event "challengeId" <> challengeId
               || stringMember event "conditionId" <> conditionId
               || stringMember event "actorId" <> operation.Actor
               || stringMember event "action" <> operation.Type
               || stringMember event "evidence" <> evidence
               || event.GetProperty("sequence").GetInt64() <> operation.Sequence then
                return! Error(InvalidBinding "Operator approval does not match its original task and event.")
            match conditions with
            | [ condition ] when stringMember condition "ownerKind" = "Operator"
                                 && String.IsNullOrWhiteSpace(stringMember condition "ownerAgentId")
                                 && stringMember condition "status" = "Satisfied"
                                 && stringMember condition "evidence" = evidence
                                 && condition.GetProperty("responseEventSequence").GetInt64() = operation.Sequence -> ()
            | _ -> return! Error(ReviewAccessDenied "Approval was not the original satisfied Operator condition.")
            let! review, decisionSnapshot = verifiedSharedDecision binding snapshot approval
            // Resolve the historical assignment at the decision, not an
            // arbitrary later attempt which happens to have the same owner.
            do! verifyApprovedTask binding decisionSnapshot review approval
            return review, operation.Sequence
        with
        | :? JsonException | :? InvalidOperationException | :? System.Collections.Generic.KeyNotFoundException
        | :? ArgumentException | :? FormatException ->
            return! Error(InvalidBinding "Malformed canonical Operator binding evidence.")
    }

    // Every new integration is authorized by an Accepted, pinned review, not
    // by the contributor's mutable branch. Old branch-only submissions remain
    // readable and completable, but cannot be created after this migration.
    let submitIntegrationWithReview binding operationId taskId actorId submissionId
                                    sourceRef submittedCommit locator targetRef =
        let reviewId, approvalId, namespaceId, requestedReviewRef =
            match locator with
            | TaskBound id -> id, None, taskId, None
            | SharedAccepted(id, approval, reviewRef) -> id, Some approval, sharedNamespace, Some reviewRef
        if not (realIntegrationTaskId taskId) || not (validAgent actorId)
           || submissionId = Guid.Empty || not (validBranch targetRef)
           || targetRef = "refs/heads/jarvis-orchestration"
           || targetRef.StartsWith("refs/heads/jarvis-review/", StringComparison.Ordinal)
           || targetRef.StartsWith("refs/heads/jarvis-integration/", StringComparison.Ordinal)
           || (not (String.IsNullOrWhiteSpace sourceRef) && not (validBranch sourceRef))
           || (not (String.IsNullOrWhiteSpace submittedCommit)
               && not (LedgerStore.validSourceCommitOid submittedCommit))
           || reviewId = Guid.Empty || approvalId = Some Guid.Empty
           || (approvalId.IsSome
               && (String.IsNullOrWhiteSpace sourceRef || String.IsNullOrWhiteSpace submittedCommit
                   || (requestedReviewRef |> Option.exists String.IsNullOrWhiteSpace))) then
            Error(InvalidBinding "Integration requires a valid Accepted review ID and a normal target branch.")
        else
            // Normalize D and N UUID spellings before creating the ledger intent.
            let reviewId = reviewId.ToString("N")
            let action =
                match approvalId with
                | None ->
                    // Preserve the historic task-bound operation hash exactly.
                    intent operationId actorId "SubmitIntegration"
                        {| taskId = taskId; submissionId = submissionId
                           sourceRef = sourceRef; submittedCommit = submittedCommit
                           reviewId = reviewId; targetRef = targetRef |}
                | Some approval ->
                    intent operationId actorId "SubmitIntegration"
                        {| taskId = taskId; submissionId = submissionId
                           sourceRef = sourceRef; submittedCommit = submittedCommit
                           reviewId = reviewId; targetRef = targetRef
                           reviewNamespace = sharedNamespace; reviewRef = requestedReviewRef.Value
                           sharedBindingOperationId = approval.ToString("D") |}
            match LegacyDirectWriterFence.findAppliedForLegacyWrite binding action with
            | Error failure -> Error failure
            | Ok(Some applied) -> Ok applied
            | Ok None -> result {
                let! snapshot = LedgerStore.fetch binding
                let! task, _ = required<ReviewTask> binding snapshot (taskPath taskId)
                if task.TaskId <> taskId then
                    return! Error(LedgerIntegrityError "Integration task identity mismatch.")
                if approvalId.IsSome then
                    if task.Lifecycle = "Cancelled" then
                        return! Error(InvalidBinding "Cancelled task cannot receive shared integration.")
                    let! _ = required<ReviewAgent> binding snapshot (agentPath actorId)
                    let! existing, _ = LedgerStore.readAt binding snapshot (integrationPath taskId submissionId)
                    if existing.IsSome then
                        return! Error(InvalidBinding "Integration submission ID already exists.")
                let path = branchReviewPath namespaceId reviewId
                let! review =
                    match approvalId with
                    | None -> required<BranchReview> binding snapshot path |> Result.map fst
                    | Some approval -> verifiedSharedApproval binding snapshot taskId reviewId approval |> Result.map fst
                if review.TaskId <> namespaceId || review.ReviewId <> reviewId
                   || review.Status <> "Accepted" || review.DecisionSequence <= 0L
                   || String.IsNullOrWhiteSpace review.ReviewerId then
                    return! Error(InvalidBinding "Integration requires an Accepted canonical review.")
                let expectedReviewRef = reviewRef namespaceId reviewId review.SourceVersion
                if review.SourceVersion < 1
                   || review.ReviewRef <> expectedReviewRef
                   || (requestedReviewRef |> Option.exists ((<>) expectedReviewRef)) then
                    return! Error(LedgerIntegrityError "Accepted review has an invalid managed source ref.")
                if not (LedgerStore.validSourceCommitOid review.ReviewedHeadOid) then
                    return! Error(InvalidBinding "Historical review has no canonical pinned SHA; resubmit it for independent review.")
                let pinned = review.ReviewedHeadOid
                if (not (String.IsNullOrWhiteSpace submittedCommit) && submittedCommit <> pinned)
                   || (not (String.IsNullOrWhiteSpace sourceRef)
                       && not (String.IsNullOrWhiteSpace review.TaskRef)
                       && sourceRef <> review.TaskRef) then
                    return! Error(InvalidBinding "Submitted source does not match the accepted review snapshot.")
                if not (String.IsNullOrWhiteSpace review.TargetRef)
                   && review.TargetRef <> targetRef then
                    return! Error(InvalidBinding "Integration target differs from the accepted review target.")
                // This fetch proves that the exact reviewed commit is available
                // from the authoritative remote, even with no source branch here.
                do! LedgerStore.fetchReviewRef binding review.ReviewRef pinned
                let! targetHead = LedgerStore.readRemoteTarget binding targetRef
                // Independently advancing the target does not invalidate Accepted source.
                let managedRef = $"refs/heads/jarvis-integration/{submissionId:N}"
                do! LegacyDirectWriterFence.preflightSourcePublication binding
                do! LedgerStore.publishIntegrationRefForAction binding action managedRef pinned
                do! LedgerStore.fetchIntegrationRef binding managedRef pinned
                return! LegacyDirectWriterFence.transact binding action (fun current -> result {
                    let! currentTask, _ = required<ReviewTask> binding current (taskPath taskId)
                    let! _ = required<ReviewAgent> binding current (agentPath actorId)
                    if currentTask.TaskId <> taskId then
                        return! Error(LedgerIntegrityError "Integration task identity mismatch.")
                    let! latestReview =
                        match approvalId with
                        | None -> required<BranchReview> binding current path |> Result.map fst
                        | Some approval ->
                            if currentTask.Lifecycle = "Cancelled" then
                                Error(InvalidBinding "Cancelled task cannot receive shared integration.")
                            else verifiedSharedApproval binding current taskId reviewId approval |> Result.map fst
                    if latestReview.Status <> "Accepted"
                       || latestReview.Version <> review.Version
                       || latestReview.ReviewRef <> review.ReviewRef
                       || latestReview.ReviewedHeadOid <> pinned then
                        return! Error(StaleEntityVersion "Accepted review changed during integration publication.")
                    let recordPath = integrationPath taskId submissionId
                    let! previous = read<IntegrationSubmission> binding current recordPath
                    if previous.IsSome then
                        return! Error(StaleEntityVersion "Integration submission ID already exists.")
                    // Independent immutable submissions cannot replace an older
                    // pending slice, even when their descriptive TaskRef matches.
                    let record =
                        { SubmissionId = submissionId.ToString("N")
                          TaskId = taskId; ActorId = actorId
                          SourceRef = if String.IsNullOrWhiteSpace review.TaskRef then "" else review.TaskRef
                          SubmittedCommit = pinned; ReviewId = reviewId
                          ReviewRef = review.ReviewRef; ReviewVersion = review.SourceVersion
                          ReviewedHeadOid = pinned; IntegrationRef = managedRef
                          TargetHeadAtSubmission = targetHead; TargetRef = targetRef
                          Status = "Pending"; CreatedSequence = current.Header.NextSequence
                          IntegratedCommit = null; CompletedAtUtc = null
                          PublicationPreviousHead = null; PublicationObservedHead = null
                          PublicationOperationId = null; PublicationMechanism = null
                          SharedBinding =
                            match approvalId with
                            | None -> None
                            | Some approval ->
                                Some { ApprovalOperationId = approval.ToString("D")
                                       SubmissionOperationId = operationId.ToString("D") } }
                    return [ change recordPath None record ]
                })
            }
    // Existing task-bound transport and operation identity stay unchanged.
    let submitIntegration binding operationId taskId actorId submissionId
                          sourceRef submittedCommit (reviewId: string) targetRef =
        match Guid.TryParse reviewId with
        | true, id ->
            submitIntegrationWithReview binding operationId taskId actorId submissionId
                sourceRef submittedCommit (TaskBound id) targetRef
        | _ -> Error(InvalidBinding "Integration requires a valid Accepted review ID.")

    let private sharedRefPrefix = "refs/heads/jarvis-review/_reviews/"

    let private sharedReceipt (submission: IntegrationSubmission) =
        submission.SharedBinding.IsSome
        || (not (isNull submission.ReviewRef)
            && submission.ReviewRef.StartsWith(sharedRefPrefix, StringComparison.Ordinal))

    let private asPending (submission: IntegrationSubmission) =
        { submission with Status = "Pending"; IntegratedCommit = null; CompletedAtUtc = null
                          PublicationPreviousHead = null; PublicationObservedHead = null
                          PublicationOperationId = null; PublicationMechanism = null }

    // Always recover the original SubmitIntegration operation for managed
    // records: clearing the locator and rewriting the review ref must not
    // downgrade a shared binding into an unrelated historical task-bound one.
    let private verifySharedIntegrationAt binding snapshot taskId (submissionId: Guid)
                                          (submission: IntegrationSubmission) = result {
        if not (realIntegrationTaskId taskId) || submission.TaskId <> taskId
           || submission.SubmissionId <> submissionId.ToString("N") then
            return! Error(InvalidBinding "Integration receipt does not name a real canonical task.")
        let path = integrationPath taskId submissionId
        let! originalOperation = LedgerStore.findIntegrationSubmissionOperationForPathAt binding snapshot path
        match originalOperation with
        | None when sharedReceipt submission ->
            return! Error(InvalidBinding "Shared integration lacks its original submission operation.")
        | None -> return None
        | Some operation ->
            let! payload = operationPayload operation
            let originalShared = payload.TryGetProperty("sharedBindingOperationId") |> fst
            if not originalShared && not (sharedReceipt submission) then
                return None
            elif not originalShared || submission.SharedBinding.IsNone then
                return! Error(InvalidBinding "Shared integration locator differs from the original submission.")
            else
                let! submissionOperationId = exactGuid "D" submission.SharedBinding.Value.SubmissionOperationId
                let! approvalId = exactGuid "D" submission.SharedBinding.Value.ApprovalOperationId
                if operation.OperationId <> submissionOperationId.ToString("D")
                   || operation.Sequence <> submission.CreatedSequence
                   || operation.Actor <> submission.ActorId then
                    return! Error(InvalidBinding "Shared integration submission operation identity mismatch.")
                let! original = LedgerStore.verifiedIntegrationOperationSnapshotAt binding snapshot operation
                let! pending = getIntegrationSubmissionAt binding original taskId submissionId
                if pending.Status <> "Pending" || pending.SharedBinding.IsNone
                   || pending <> asPending submission then
                    return! Error(InvalidBinding "Shared integration receipt differs from its original immutable submission.")
                try
                    let requestedSource = stringMember payload "sourceRef"
                    let requestedCommit = stringMember payload "submittedCommit"
                    do! LedgerStore.verifyIntegrationOperationIntent binding operation
                            (intent submissionOperationId submission.ActorId "SubmitIntegration"
                                {| taskId = taskId; submissionId = submissionId
                                   sourceRef = requestedSource; submittedCommit = requestedCommit
                                   reviewId = submission.ReviewId; targetRef = submission.TargetRef
                                   reviewNamespace = sharedNamespace; reviewRef = submission.ReviewRef
                                   sharedBindingOperationId = approvalId.ToString("D") |})
                    let! review, approvalSequence =
                        verifiedSharedApproval binding snapshot taskId submission.ReviewId approvalId
                    if approvalSequence >= operation.Sequence
                       || submission.ReviewRef <> review.ReviewRef
                       || submission.ReviewVersion <> review.SourceVersion
                       || submission.ReviewedHeadOid <> review.ReviewedHeadOid
                       || submission.SubmittedCommit <> review.ReviewedHeadOid
                       || submission.SourceRef <> review.TaskRef || submission.TargetRef <> review.TargetRef
                       || submission.IntegrationRef <> $"refs/heads/jarvis-integration/{submissionId:N}"
                       || requestedSource <> review.TaskRef
                       || requestedCommit <> review.ReviewedHeadOid then
                        return! Error(InvalidBinding "Shared integration source or approval changed.")
                    return Some review
                with
                | :? InvalidOperationException | :? System.Collections.Generic.KeyNotFoundException ->
                    return! Error(InvalidBinding "Malformed shared integration submission operation.")
    }

    // The WorkComposition consumer and completion share the same locator
    // resolution. No fallback from a failed shared proof to task-bound review.
    let getIntegrationReviewAt binding snapshot taskId submissionId submission = result {
        let! shared = verifySharedIntegrationAt binding snapshot taskId submissionId submission
        match shared with
        | Some review -> return review
        | None ->
            let! reviewId = exactGuid "N" submission.ReviewId
            let! review, _ = required<BranchReview> binding snapshot (branchReviewPath taskId submission.ReviewId)
            if review.TaskId <> taskId || review.ReviewId <> reviewId.ToString("N") then
                return! Error(LedgerIntegrityError "Canonical integration review identity mismatch.")
            return review
    }

    let completeIntegration binding operationId taskId actorId submissionId integratedCommit =
        if not (realIntegrationTaskId taskId) || not (validAgent actorId)
           || submissionId = Guid.Empty then
            Error(InvalidBinding "Invalid integration completion.")
        else
            let action = intent operationId actorId "CompleteIntegration"
                           {| taskId = taskId; submissionId = submissionId
                              integratedCommit = integratedCommit |}
            match LegacyDirectWriterFence.findAppliedForLegacyWrite binding action with
            | Error failure -> Error failure
            | Ok(Some applied) -> Ok applied
            | Ok None -> result {
                let! snapshot = LedgerStore.fetch binding
                let path = integrationPath taskId submissionId
                let! currentRecord, currentOid = required<IntegrationSubmission> binding snapshot path
                if currentRecord.Status = "IncludedInAggregate" then
                    return! Error(StaleEntityVersion "Integration submission is already attributed to an aggregate.")
                if currentRecord.Status <> "Pending" then
                    return! Error(StaleEntityVersion "Integration submission is already completed.")
                let! attribution, _ = LedgerStore.readAt binding snapshot (attributionPath taskId submissionId)
                if attribution.IsSome then
                    return! Error(LedgerIntegrityError "Pending submission conflicts with an aggregate attribution.")
                let! shared = verifySharedIntegrationAt binding snapshot taskId submissionId currentRecord
                // Legacy branch-only entries keep the original read-only
                // receipt semantics. New managed entries must NEVER accept an
                // external push merely because present ancestry looks sound:
                // a force-rewritten racing target can satisfy that predicate.
                let! publication =
                    if String.IsNullOrWhiteSpace currentRecord.IntegrationRef then
                        LedgerStore.verifyIntegrationOnRemote binding currentRecord.TargetRef
                            currentRecord.SubmittedCommit integratedCommit
                        |> Result.map (fun () -> None)
                    else result {
                        let expectedRef = $"refs/heads/jarvis-integration/{submissionId:N}"
                        if currentRecord.IntegrationRef <> expectedRef
                           || currentRecord.ReviewedHeadOid <> currentRecord.SubmittedCommit
                           || not (LedgerStore.validSourceCommitOid currentRecord.TargetHeadAtSubmission) then
                            return! Error(LedgerIntegrityError "Managed integration provenance is inconsistent.")
                        do! LedgerStore.fetchReviewRef binding currentRecord.ReviewRef currentRecord.SubmittedCommit
                        do! LedgerStore.fetchIntegrationRef binding currentRecord.IntegrationRef currentRecord.SubmittedCommit
                        if integratedCommit = currentRecord.SubmittedCommit then
                            do! LedgerStore.verifySourceAncestry binding
                                    currentRecord.TargetHeadAtSubmission currentRecord.SubmittedCommit
                        else
                            do! LedgerStore.verifyMechanicalIntegrationMerge binding
                                    currentRecord.TargetHeadAtSubmission currentRecord.SubmittedCommit integratedCommit
                        // The bounded publisher itself observes the present
                        // remote head, performs an ordinary non-force push and
                        // verifies the advertised result. No caller-supplied
                        // push flag or unsigned receipt is trusted.
                        let! receipt = LedgerStore.publishIntegrationTarget binding operationId
                                           currentRecord.TargetRef currentRecord.TargetHeadAtSubmission
                                           currentRecord.SubmittedCommit integratedCommit
                        do! LedgerStore.verifyIntegrationOnRemote binding currentRecord.TargetRef
                                currentRecord.SubmittedCommit integratedCommit
                        return Some receipt
                    }
                return! LegacyDirectWriterFence.transact binding action (fun current -> result {
                    let! _ = required<ReviewAgent> binding current (agentPath actorId)
                    let! existing, oid = required<IntegrationSubmission> binding current path
                    if existing.Status = "IncludedInAggregate" then
                        return! Error(StaleEntityVersion "Integration submission is already attributed to an aggregate.")
                    if existing.Status <> "Pending" then
                        return! Error(StaleEntityVersion "Integration submission is already completed.")
                    let! attribution, _ = LedgerStore.readAt binding current (attributionPath taskId submissionId)
                    if attribution.IsSome then
                        return! Error(LedgerIntegrityError "Pending submission conflicts with an aggregate attribution.")
                    if oid <> currentOid then
                        return! Error(StaleEntityVersion "Integration receipt changed during publication.")
                    if shared.IsSome then
                        let! _ = verifySharedIntegrationAt binding current taskId submissionId existing
                        ()
                    return [ change path (Some oid)
                                { existing with Status = "Integrated"
                                                IntegratedCommit = integratedCommit
                                                CompletedAtUtc = DateTimeOffset.UtcNow.ToString("O")
                                                PublicationPreviousHead =
                                                    publication |> Option.map (fun p -> p.PreviousHead) |> Option.defaultValue null
                                                PublicationObservedHead =
                                                    publication |> Option.map (fun p -> p.ObservedHead) |> Option.defaultValue null
                                                PublicationOperationId =
                                                    publication |> Option.map (fun p -> p.OperationId) |> Option.defaultValue null
                                                PublicationMechanism =
                                                    publication |> Option.map (fun p -> p.Mechanism) |> Option.defaultValue null } ]
                })
            }


    let aggregateInclusionEvidenceText (evidence: Common.AggregateInclusionEvidence) =
        json evidence

    // No permissive fallback or implicit default fields in the typed evidence
    // envelope. The original reviewer supplies the exact canonical locator.
    let private parseAggregateEvidence (body: string) =
        let invalid () = Error(InvalidBinding "Invalid exact aggregate inclusion evidence.")
        try
            let exactObject (element: JsonElement) (names: string list) =
                element.ValueKind = JsonValueKind.Object
                && (element.EnumerateObject() |> Seq.map (fun p -> p.Name) |> Seq.sort |> Seq.toList)
                    = List.sort names
            let review (element: JsonElement) =
                exactObject element [ "taskId"; "reviewId"; "decisionOperationId" ]
            // The canonical ledger entity is capped at 64 KiB; oversized
            // evidence cannot fit that envelope and need not be parsed.
            use doc =
                if isNull body || Encoding.UTF8.GetByteCount body > 64 * 1024 then
                    raise (ArgumentException "Invalid aggregate evidence size.")
                JsonDocument.Parse(body)
            let root = doc.RootElement
            if not (exactObject root [ "aggregateSubmissionId"; "aggregateCompletionOperationId"
                                       "aggregateReview"; "constituentReview"; "aggregateTree"; "corrections" ])
               || not (review (root.GetProperty("aggregateReview")))
               || not (review (root.GetProperty("constituentReview"))) then invalid ()
            else
                let corrections = root.GetProperty("corrections")
                if corrections.ValueKind <> JsonValueKind.Array
                   || not (corrections.EnumerateArray() |> Seq.forall (fun e ->
                       exactObject e [ "path"; "review" ] && review (e.GetProperty("review")))) then invalid ()
                else
                    let evidence = JsonSerializer.Deserialize<Common.AggregateInclusionEvidence>(body, options)
                    if isNull (box evidence) || isNull evidence.Corrections then invalid ()
                    else Ok evidence
        with _ -> invalid ()

    let private verifyIndependentDecisionAt binding snapshot (proof: Common.AggregateReviewEvidence) = result {
        if not (validTaskId proof.TaskId) || proof.ReviewId = Guid.Empty
           || proof.DecisionOperationId = Guid.Empty then
            return! Error(InvalidBinding "Invalid exact review evidence locator.")
        let id = proof.ReviewId.ToString("N")
        let path = branchReviewPath proof.TaskId id
        let! review, _ = required<BranchReview> binding snapshot path
        let! decision, original = operationOrigin binding snapshot proof.DecisionOperationId
        let! accepted, _ = required<BranchReview> binding original path
        if review.TaskId <> proof.TaskId || review.ReviewId <> id
           || review.Status <> "Accepted" || review.SourceVersion < 1
           || review.DecisionSequence <= review.CreatedSequence
           || review.ReviewRef <> reviewRef proof.TaskId id review.SourceVersion
           || not (validAgent review.OwnerAgentId && validAgent review.ReviewerId)
           || review.OwnerAgentId = review.ReviewerId
           || not (validBranch review.TaskRef && validBranch review.TargetRef)
           || not (operationWrote decision path)
           || decision.Sequence <> review.DecisionSequence
           || not (sameAcceptedReview accepted review)
           || accepted.Version < 3 || accepted.Acknowledged then
            return! Error(InvalidBinding "Review does not match its original independent Accepted decision.")
        do! LedgerStore.verifyIntegrationOperationIntent binding decision
                (intent proof.DecisionOperationId review.ReviewerId "DecideBranchReview"
                    {| taskId = proof.TaskId; reviewId = id; reviewerId = review.ReviewerId
                       expectedVersion = accepted.Version - 1; status = "Accepted" |})
        do! LedgerStore.fetchReviewRef binding review.ReviewRef review.ReviewedHeadOid
        do! LedgerStore.verifySourceAncestry binding review.BaseCommit review.ReviewedHeadOid
        return review
    }

    // Task-bound managed receipts must have their original exact Submit
    // operation; shared/unbound or rewritten locators cannot be attributed.
    let private verifyOriginalTaskBoundSubmission binding snapshot taskId (id: Guid)
                     (record: IntegrationSubmission) (review: BranchReview) = result {
        if not (realIntegrationTaskId taskId) || sharedReceipt record
           || record.TaskId <> taskId || record.SubmissionId <> id.ToString("N")
           || review.TaskId <> taskId || record.ReviewId <> review.ReviewId
           || record.ReviewRef <> review.ReviewRef
           || record.ReviewVersion <> review.SourceVersion
           || record.ReviewedHeadOid <> review.ReviewedHeadOid
           || record.SubmittedCommit <> review.ReviewedHeadOid
           || record.SourceRef <> review.TaskRef || record.TargetRef <> review.TargetRef
           || record.IntegrationRef <> $"refs/heads/jarvis-integration/{id:N}"
           || not (LedgerStore.validSourceCommitOid record.TargetHeadAtSubmission)
           || record.CreatedSequence <= review.DecisionSequence then
            return! Error(InvalidBinding "Aggregate inclusion requires an exact task-bound managed submission.")
        let path = integrationPath taskId id
        let! maybeOperation = LedgerStore.findIntegrationSubmissionOperationForPathAt binding snapshot path
        let! operation =
            match maybeOperation with
            | Some op when op.Sequence = record.CreatedSequence && op.Actor = record.ActorId -> Ok op
            | _ -> Error(InvalidBinding "Missing original task-bound integration submission operation.")
        let! operationId = exactGuid "D" operation.OperationId
        let! original = LedgerStore.verifiedIntegrationOperationSnapshotAt binding snapshot operation
        let! pending = getIntegrationSubmissionAt binding original taskId id
        let! originalReview, _ =
            required<BranchReview> binding original (branchReviewPath taskId review.ReviewId)
        if pending <> asPending record || not (sameAcceptedReview originalReview review) then
            return! Error(InvalidBinding "Submission differs from its original immutable Pending record or Accepted review.")
        let! payload = operationPayload operation
        try
            let sourceRef = stringMember payload "sourceRef"
            let submittedCommit = stringMember payload "submittedCommit"
            if (not (String.IsNullOrWhiteSpace sourceRef) && sourceRef <> record.SourceRef)
               || (not (String.IsNullOrWhiteSpace submittedCommit) && submittedCommit <> record.SubmittedCommit) then
                return! Error(InvalidBinding "Original submission request differs from pinned source.")
            do! LedgerStore.verifyIntegrationOperationIntent binding operation
                    (intent operationId record.ActorId "SubmitIntegration"
                        {| taskId = taskId; submissionId = id; sourceRef = sourceRef
                           submittedCommit = submittedCommit; reviewId = record.ReviewId
                           targetRef = record.TargetRef |})
        with
        | :? InvalidOperationException | :? System.Collections.Generic.KeyNotFoundException ->
            return! Error(InvalidBinding "Malformed original task-bound integration request.")
        do! LedgerStore.fetchIntegrationRef binding record.IntegrationRef record.SubmittedCommit
        return ()
    }

    // Read-only publication verifier shared with composition acceptance.
    let internal requireManagedCompletionAt (binding: LedgerBinding) (snapshot: LedgerSnapshot)
                                           taskId (submissionId: Guid) (submission: IntegrationSubmission) = result {
        let path = $"tasks/{taskId}/integrations/{submissionId:N}.json"
        let! completion = LedgerStore.findCompletionOperationForPathAt binding snapshot path
        if completion.Sequence <= submission.CreatedSequence
           || String.IsNullOrWhiteSpace completion.Actor then
            return! Error(InvalidBinding
                "Composition integration lacks a valid canonical completion sequence and actor.")
        let! originalHead = LedgerStore.getOperationOriginAt binding snapshot completion
        let! originalSnapshot = LedgerStore.verifiedHistoricalSnapshot binding originalHead
        let! originalReceipt =
            getIntegrationSubmissionAt binding originalSnapshot taskId submissionId
        if originalReceipt <> submission then
            return! Error(InvalidBinding
                "Composition integration receipt differs from its original immutable completion.")
        do! LedgerStore.verifyCompletionOperationRequest binding completion
                taskId submissionId originalReceipt.IntegratedCommit
        do! LedgerStore.fetchReviewRef binding submission.ReviewRef submission.SubmittedCommit
        do! LedgerStore.fetchIntegrationRef binding submission.IntegrationRef submission.SubmittedCommit

        let legacyDirect =
            originalReceipt.IntegratedCommit = originalReceipt.SubmittedCommit
            && String.IsNullOrWhiteSpace originalReceipt.PublicationOperationId
            && String.IsNullOrWhiteSpace originalReceipt.PublicationMechanism
            && String.IsNullOrWhiteSpace originalReceipt.PublicationPreviousHead
            && String.IsNullOrWhiteSpace originalReceipt.PublicationObservedHead
        if legacyDirect then
            // A blank original v1 receipt is not evidence of pre-v1 origin:
            // generic canonical transactions can manufacture it *today*.
            // No independently verified pre-v1 rollout anchor or historical
            // operation allowlist is available. Fail closed for managed
            // composition acceptance. Legacy branch-only integration records
            // remain covered by ReviewWorkflow's separate compatibility path.
            return! Error(InvalidBinding
                "Managed legacy direct completion has no trusted pre-v1 origin proof.")
        else
            let! _ =
                match Guid.TryParseExact(submission.PublicationOperationId, "D") with
                | true, id when id <> Guid.Empty
                                && submission.PublicationMechanism = "VerifiedNonForceGitPush/v1"
                                && completion.OperationId = id.ToString("D") -> Ok id
                | _ -> Error(InvalidBinding
                    "Managed integration has no original verified non-force publisher receipt.")
            if not (LedgerStore.validSourceCommitOid submission.PublicationPreviousHead)
               || not (LedgerStore.validSourceCommitOid submission.PublicationObservedHead)
               || submission.PublicationPreviousHead = submission.IntegratedCommit then
                return! Error(InvalidBinding "Invalid original integration publication heads.")
            if submission.IntegratedCommit = submission.SubmittedCommit then
                let! remoteHead = LedgerStore.readRemoteTarget binding submission.TargetRef
                do! LedgerStore.verifySourceAncestry binding
                        submission.TargetHeadAtSubmission submission.PublicationPreviousHead
                do! LedgerStore.verifySourceAncestry binding
                        submission.PublicationPreviousHead submission.IntegratedCommit
                do! LedgerStore.verifySourceAncestry binding
                        submission.IntegratedCommit submission.PublicationObservedHead
                do! LedgerStore.verifySourceAncestry binding
                        submission.PublicationObservedHead remoteHead
            else
                do! LedgerStore.verifyPublishedMechanicalIntegration binding
                        submission.TargetRef submission.TargetHeadAtSubmission
                        submission.SubmittedCommit submission.IntegratedCommit
                        submission.PublicationPreviousHead submission.PublicationObservedHead
            // Under the Operator-approved trusted-writer model, the original
            // authorized CompleteIntegration workflow is our publisher. Its
            // immutable canonical operation/request, v1 receipt, independent
            // reviewed source and actual Git facts were checked above. This
            // read-only verifier never publishes or replays an uncertain effect.
            //
            // A privileged actor that can arbitrarily write the canonical
            // ledger AND target ref could fabricate matching history; no
            // in-process receipt proves execution against that actor. The
            // separate audit fixture documents that OUT-OF-MODEL capability.
            return ()
    }



    // Source inclusion is a distinct disposition, never a new publication or
    // operational acceptance receipt. The original aggregate reviewer is the
    // independent attesting actor. Old readers reject the non-Pending and
    // non-Integrated status; they cannot dispatch it or release a graph edge.
    let recordAggregateInclusion binding operationId taskId actorId (submissionId: Guid) evidenceJson =
        if not (realIntegrationTaskId taskId) || not (validAgent actorId)
           || submissionId = Guid.Empty then
            Error(InvalidBinding "Invalid aggregate inclusion identity.")
        else result {
            let! evidence = parseAggregateEvidence evidenceJson
            let aggregateProof = evidence.AggregateReview
            let constituentProof = evidence.ConstituentReview
            if isNull (box aggregateProof) || isNull (box constituentProof)
               || not (realIntegrationTaskId aggregateProof.TaskId)
               || constituentProof.TaskId <> taskId
               || evidence.AggregateSubmissionId = Guid.Empty
               || evidence.AggregateCompletionOperationId = Guid.Empty
               || (aggregateProof.TaskId = taskId && evidence.AggregateSubmissionId = submissionId)
               || not (LedgerStore.validSourceCommitOid evidence.AggregateTree) then
                return! Error(InvalidBinding "Aggregate and constituent must be distinct exact task-bound evidence.")
            let action = intent operationId actorId "RecordAggregateInclusion"
                            {| taskId = taskId; submissionId = submissionId; evidence = evidenceJson |}
            return! LegacyDirectWriterFence.transact binding action (fun snapshot -> result {
                let! _ = required<ReviewAgent> binding snapshot (agentPath actorId)
                let! task, _ = required<ReviewTask> binding snapshot (taskPath taskId)
                let! aggregateTask, _ = required<ReviewTask> binding snapshot (taskPath aggregateProof.TaskId)
                if task.TaskId <> taskId || aggregateTask.TaskId <> aggregateProof.TaskId
                   || task.Lifecycle = "Cancelled" || aggregateTask.Lifecycle = "Cancelled" then
                    return! Error(InvalidBinding "Aggregate inclusion requires existing non-cancelled canonical tasks.")
                let path = integrationPath taskId submissionId
                let! pending, oid = required<IntegrationSubmission> binding snapshot path
                if pending.Status <> "Pending" || pending <> asPending pending then
                    return! Error(StaleEntityVersion "Constituent must still be the original Pending submission.")
                let receiptPath = attributionPath taskId submissionId
                let! prior, _ = LedgerStore.readAt binding snapshot receiptPath
                if prior.IsSome then
                    return! Error(StaleEntityVersion "Constituent inclusion is already recorded.")
                let! aggregate =
                    getIntegrationSubmissionAt binding snapshot aggregateProof.TaskId evidence.AggregateSubmissionId
                if aggregate.Status <> "Integrated"
                   || aggregate.PublicationOperationId <> evidence.AggregateCompletionOperationId.ToString("D")
                   || aggregate.TargetRef <> pending.TargetRef
                   || aggregate.SubmittedCommit = pending.SubmittedCommit then
                    return! Error(InvalidBinding "Aggregate is not a distinct published managed source on the same target.")
                let! aggregateReview = verifyIndependentDecisionAt binding snapshot aggregateProof
                let! constituentReview = verifyIndependentDecisionAt binding snapshot constituentProof
                if actorId <> aggregateReview.ReviewerId || actorId = constituentReview.OwnerAgentId
                   || constituentReview.DecisionSequence >= aggregateReview.DecisionSequence
                   || pending.CreatedSequence >= aggregateReview.DecisionSequence then
                    return! Error(InvalidBinding "Inclusion must be explicitly attested by the independent aggregate reviewer.")
                do! verifyOriginalTaskBoundSubmission binding snapshot taskId submissionId pending constituentReview
                do! verifyOriginalTaskBoundSubmission binding snapshot aggregateProof.TaskId
                        evidence.AggregateSubmissionId aggregate aggregateReview
                do! requireManagedCompletionAt binding snapshot aggregateProof.TaskId evidence.AggregateSubmissionId aggregate
                do! LedgerStore.verifySourceAncestry binding pending.SubmittedCommit aggregate.SubmittedCommit
                let! _, sourceDelta =
                    LedgerStore.reviewedTreeDelta binding constituentReview.BaseCommit pending.SubmittedCommit
                let! finalTree, aggregateDelta =
                    LedgerStore.reviewedTreeDelta binding constituentReview.BaseCommit aggregate.SubmittedCommit
                if sourceDelta.IsEmpty || finalTree <> evidence.AggregateTree then
                    return! Error(InvalidBinding "Missing constituent delta or incorrect independently reviewed final tree.")
                let! _, publishedDelta =
                    LedgerStore.reviewedTreeDelta binding constituentReview.BaseCommit aggregate.IntegratedCommit
                let! remoteHead = LedgerStore.readRemoteTarget binding aggregate.TargetRef
                do! LedgerStore.verifySourceAncestry binding aggregate.PublicationObservedHead remoteHead
                let! _, currentDelta =
                    LedgerStore.reviewedTreeDelta binding constituentReview.BaseCommit remoteHead
                let mutable corrections = Map.empty
                let mutable correctionDeltas = Map.empty
                for correction in evidence.Corrections do
                    if isNull (box correction) || isNull (box correction.Review)
                       || String.IsNullOrEmpty correction.Path
                       || not (Map.containsKey correction.Path sourceDelta)
                       || Map.containsKey correction.Path corrections then
                        return! Error(InvalidBinding "Ambiguous or unrelated reviewed path correction.")
                    let key = correction.Review.TaskId, correction.Review.ReviewId, correction.Review.DecisionOperationId
                    let! delta =
                        match Map.tryFind key correctionDeltas with
                        | Some delta -> Ok delta
                        | None -> result {
                            let! reviewed = verifyIndependentDecisionAt binding snapshot correction.Review
                            if reviewed.TargetRef <> aggregate.TargetRef
                               || reviewed.DecisionSequence <= constituentReview.DecisionSequence
                               || reviewed.DecisionSequence >= aggregateReview.DecisionSequence
                               || reviewed.ReviewedHeadOid = aggregate.SubmittedCommit then
                                return! Error(InvalidBinding "Path correction is not a distinct later Accepted review.")
                            do! LedgerStore.verifySourceAncestry binding reviewed.ReviewedHeadOid aggregate.SubmittedCommit
                            let! _, delta = LedgerStore.reviewedTreeDelta binding reviewed.BaseCommit reviewed.ReviewedHeadOid
                            return delta
                        }
                    correctionDeltas <- Map.add key delta correctionDeltas
                    match Map.tryFind correction.Path delta with
                    | Some value -> corrections <- Map.add correction.Path value corrections
                    | None -> return! Error(InvalidBinding "Correction review did not change its asserted path.")
                for KeyValue(path, original) in sourceDelta do
                    let final = Map.tryFind path aggregateDelta
                    let expected =
                        match Map.tryFind path corrections with
                        | Some corrected when corrected <> original -> Some corrected
                        | Some _ -> None
                        | None -> Some original
                    if final.IsNone || final <> expected
                       || Map.tryFind path publishedDelta <> final
                       || Map.tryFind path currentDelta <> final then
                        return! Error(InvalidBinding "Constituent path was reverted, omitted, changed without exact Accepted correction, or is no longer on target.")
                let attribution: AggregateInclusionRecord =
                    { TaskId = taskId; SubmissionId = submissionId.ToString("N")
                      ProjectId = binding.ProjectId.ToString("D"); LedgerId = binding.LedgerId.ToString("D")
                      Epoch = binding.Epoch; OperationId = operationId.ToString("D"); ReviewerId = actorId
                      AggregateTaskId = aggregateProof.TaskId
                      AggregateSubmissionId = evidence.AggregateSubmissionId.ToString("N")
                      AggregateCommit = aggregate.SubmittedCommit; AggregateTree = finalTree
                      PublishedCommit = aggregate.IntegratedCommit; ObservedTargetHead = remoteHead
                      ConstituentCommit = pending.SubmittedCommit; TargetRef = pending.TargetRef
                      Evidence = evidence }
                return [ change path (Some oid) { pending with Status = "IncludedInAggregate" }
                         change receiptPath None attribution ]
            })
        }


    // A historical disposition is not a current operational acceptance. Verify
    // the exact immutable attribution operation and original record, without
    // reconstructing an effect or claiming that all later target edits retain it.
    let getAggregateInclusionAt binding snapshot taskId submissionId = result {
        let! submission = getIntegrationSubmissionAt binding snapshot taskId submissionId
        let path = attributionPath taskId submissionId
        let! entry = read<AggregateInclusionRecord> binding snapshot path
        match submission.Status, entry with
        | "IncludedInAggregate", Some (record, _) ->
            let! operationId = exactGuid "D" record.OperationId
            let! operation, original = operationOrigin binding snapshot operationId
            let! originalRecord, _ = required<AggregateInclusionRecord> binding original path
            let! originalSubmission = getIntegrationSubmissionAt binding original taskId submissionId
            let! payload = operationPayload operation
            try
                let evidenceJson = stringMember payload "evidence"
                let! evidence = parseAggregateEvidence evidenceJson
                if isNull (box evidence.AggregateReview) || isNull (box evidence.ConstituentReview)
                   || originalRecord <> record || originalSubmission <> submission
                   || not (operationWrote operation path)
                   || not (operationWrote operation (integrationPath taskId submissionId))
                   || record.TaskId <> taskId || record.SubmissionId <> submissionId.ToString("N")
                   || record.ProjectId <> binding.ProjectId.ToString("D")
                   || record.LedgerId <> binding.LedgerId.ToString("D") || record.Epoch <> binding.Epoch
                   || record.ConstituentCommit <> submission.SubmittedCommit
                   || record.TargetRef <> submission.TargetRef || record.Evidence <> evidence
                   || record.AggregateTaskId <> evidence.AggregateReview.TaskId
                   || record.AggregateSubmissionId <> evidence.AggregateSubmissionId.ToString("N")
                   || record.AggregateTree <> evidence.AggregateTree
                   || asPending submission <> { submission with Status = "Pending" } then
                    return! Error(LedgerIntegrityError "Aggregate attribution differs from its immutable disposition.")
                do! LedgerStore.verifyIntegrationOperationIntent binding operation
                        (intent operationId record.ReviewerId "RecordAggregateInclusion"
                            {| taskId = taskId; submissionId = submissionId; evidence = evidenceJson |})
                return Some record
            with
            | :? InvalidOperationException | :? System.Collections.Generic.KeyNotFoundException ->
                return! Error(LedgerIntegrityError "Malformed aggregate inclusion operation.")
        | "IncludedInAggregate", None ->
            return! Error(LedgerIntegrityError "Included constituent is missing its canonical attribution.")
        | _, Some _ ->
            return! Error(LedgerIntegrityError "Attribution exists without its included constituent disposition.")
        | _, None -> return None
    }

    let getIntegrationSubmissions binding taskId =
        if not (validTaskId taskId) then Error(InvalidBinding "Invalid task ID.")
        else result {
            let! snapshot = LedgerStore.fetch binding
            let! _ = required<ReviewTask> binding snapshot (taskPath taskId)
            let! submissions = integrationSubmissionsAt binding snapshot taskId
            let included = submissions |> List.filter (fun submission -> submission.Status = "IncludedInAggregate")
            let! paths = LedgerStore.listAt binding snapshot $"records/integration-attributions/{taskId}"
            let expectedPaths =
                included |> List.map (fun item -> attributionPath taskId (Guid.ParseExact(item.SubmissionId, "N")))
                |> Set.ofList
            if Set.ofList paths <> expectedPaths then
                return! Error(LedgerIntegrityError "Canonical integration dispositions and attribution paths differ.")
            let mutable attributions = []
            for submission in included do
                let! id = exactGuid "N" submission.SubmissionId
                let! attribution = getAggregateInclusionAt binding snapshot taskId id
                attributions <- (attribution |> Option.toList) @ attributions
            return { SnapshotCommit = snapshot.Commit
                     Latest = submissions |> List.tryLast
                     Submissions = submissions
                     AggregateInclusions = List.rev attributions }
        }

    let private emptyNotificationPreferences agentId =
        { AgentId = agentId; Version = 0; MuteAll = false
          MuteAutoReply = ""; BlockedSenders = [] }

    let private notificationPreferencesAt binding snapshot agentId = result {
        let! current = read<AgentNotificationPreferences> binding snapshot
                           (notificationPreferencesPath agentId)
        match current with
        | None -> return emptyNotificationPreferences agentId, None
        | Some(preferences, oid) when preferences.AgentId = agentId
                                    && validNotificationPreferences preferences ->
            return preferences, Some oid
        | Some _ ->
            return! Error(LedgerIntegrityError "Invalid recipient notification preferences.")
    }

    let getNotificationPreferences binding agentId =
        if not (validAgent agentId) then Error(InvalidBinding "Invalid recipient agent.")
        else result {
            let! snapshot = LedgerStore.fetch binding
            let! _ = required<ReviewAgent> binding snapshot (agentPath agentId)
            let! preferences, _ = notificationPreferencesAt binding snapshot agentId
            return { SnapshotCommit = snapshot.Commit; Preferences = preferences }
        }

    // Source project "" denotes the destination project itself, never an
    // unverified wildcard. Checkout aliases are not separate project identities.
    let private localOrSourceProject localProject sourceProject =
        match Common.ProjectSelector.identity localProject with
        | None -> None
        | Some local when sourceProject = "" -> Some ""
        | Some local ->
            Common.ProjectSelector.identity sourceProject
            |> Option.map (fun source -> if source = local then "" else source)

    let setNotificationSenderBlock binding operationId actorId recipientId localProject
                                   senderProject senderId blocked autoReply =
        let source = localOrSourceProject localProject senderProject
        if actorId <> recipientId then
            Error(ReviewAccessDenied "Only the recipient may change notification preferences.")
        elif not (validAgent recipientId && validAgent senderId)
             || not (validSenderProject localProject && validSenderProject senderProject)
             || source.IsNone
             || not (validAutoReply autoReply)
             || (not blocked && autoReply <> "") then
            Error(InvalidBinding "Invalid sender block or autoreply.")
        else
            let source = Option.get source
            LegacyDirectWriterFence.transact binding
                (intent operationId actorId "SetNotificationSenderBlock"
                    {| recipientId = recipientId; senderProject = source; senderId = senderId
                       blocked = blocked; autoReply = autoReply |})
                (fun snapshot -> result {
                    let! _ = required<ReviewAgent> binding snapshot (agentPath recipientId)
                    let! preferences, oid = notificationPreferencesAt binding snapshot recipientId
                    let remaining =
                        preferences.BlockedSenders
                        |> List.filter (fun item ->
                            item.FromProjectName <> source || item.FromAgentId <> senderId)
                    let senders =
                        (if blocked then
                            { FromProjectName = source; FromAgentId = senderId
                              AutoReply = autoReply } :: remaining
                         else remaining)
                        |> List.sortBy (fun item -> item.FromProjectName, item.FromAgentId)
                    let updated =
                        { preferences with Version = preferences.Version + 1
                                           BlockedSenders = senders }
                    if not (validNotificationPreferences updated) then
                        return! Error(InvalidBinding "Recipient has reached the 32-sender block limit.")
                    return [ change (notificationPreferencesPath recipientId) oid updated ]
                })

    let setNotificationMute binding operationId actorId recipientId muted autoReply =
        if actorId <> recipientId then
            Error(ReviewAccessDenied "Only the recipient may change notification preferences.")
        elif not (validAgent recipientId) || not (validAutoReply autoReply)
             || (not muted && autoReply <> "") then
            Error(InvalidBinding "Invalid notification mute or autoreply.")
        else
            LegacyDirectWriterFence.transact binding
                (intent operationId actorId "SetNotificationMute"
                    {| recipientId = recipientId; muted = muted; autoReply = autoReply |})
                (fun snapshot -> result {
                    let! _ = required<ReviewAgent> binding snapshot (agentPath recipientId)
                    let! preferences, oid = notificationPreferencesAt binding snapshot recipientId
                    let updated =
                        { preferences with Version = preferences.Version + 1
                                           MuteAll = muted; MuteAutoReply = autoReply }
                    return [ change (notificationPreferencesPath recipientId) oid updated ]
                })

    // The sender receives only this typed outcome; other preferences stay private.
    let notificationDelivery binding (write: LedgerWrite) =
        let operationId = Guid.Parse write.Operation.OperationId
        if write.Operation.Type <> "NotifyAgent" then
            Error(InvalidBinding "Not a notification operation.")
        elif write.Operation.Paths |> Array.contains (suppressionPath operationId) then
            result {
                let! snapshot = LedgerStore.fetch binding
                let! suppressed, _ =
                    required<NotificationSuppression> binding snapshot (suppressionPath operationId)
                if suppressed.NotificationId <> operationId.ToString("N")
                   || not (validAutoReply suppressed.AutoReply) then
                    return! Error(LedgerIntegrityError "Notification suppression receipt is corrupt.")
                return { Status = "Blocked"; NotificationId = null; AutoReply = suppressed.AutoReply }
            }
        elif write.Operation.Paths |> Array.contains (notificationPath operationId) then
            Ok { Status = "Delivered"; NotificationId = operationId.ToString("N"); AutoReply = "" }
        else Error(LedgerIntegrityError "Notification operation has no valid decision.")

    let notifyAgent binding operationId fromAgentId fromProjectName toAgentId taskId message =
        let source =
            if fromProjectName = "" then Some ""
            else Common.ProjectSelector.identity fromProjectName
        let crossProject = source <> Some ""
        if not (validAgent fromAgentId) || not (validAgent toAgentId)
           || source.IsNone || not (validSenderProject fromProjectName)
           || not (validText 4096 message)
           || (not (String.IsNullOrWhiteSpace taskId) && not (validTaskId taskId)) then
            Error(InvalidBinding "Invalid notification sender, recipient, task or message.")
        else
            let fromProjectName = Option.get source
            // Operation ID is also the stable notification ID: retries cannot duplicate messages.
            LegacyDirectWriterFence.transact binding
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
                    let! preferences, _ = notificationPreferencesAt binding snapshot toAgentId
                    let senderBlock =
                        preferences.BlockedSenders
                        |> List.tryFind (fun item ->
                            item.FromProjectName = (if crossProject then fromProjectName else "")
                            && item.FromAgentId = fromAgentId)
                    let suppressionReply =
                        match senderBlock with
                        | Some blocked -> Some blocked.AutoReply
                        | None when preferences.MuteAll -> Some preferences.MuteAutoReply
                        | None -> None
                    match suppressionReply with
                    | Some reply ->
                        return [ change (suppressionPath operationId) None
                                    { NotificationId = operationId.ToString("N")
                                      FromAgentId = fromAgentId
                                      FromProjectName = if crossProject then fromProjectName else ""
                                      ToAgentId = toAgentId; AutoReply = reply
                                      CreatedAtUtc = DateTimeOffset.UtcNow.ToString("O") } ]
                    | None ->
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

    // Small, durable, project-scoped alert queue. Unlike discretionary inbox
    // notifications, these instructions bypass recipient mute preferences and
    // are consumed by the next response assembled for that exact agent.
    // Git CAS permits one consumer across connected clients; delivery is
    // at-most-once after the consume commit, not an acknowledgement protocol.
    let private responseAlertsAt binding snapshot agentId = result {
        let! stored = read<ResponseAlertQueue> binding snapshot (responseAlertsPath agentId)
        match stored with
        | None ->
            return { AgentId = agentId; Version = 0; Pending = [] }, None
        | Some(queue, oid) ->
            let valid =
                queue.AgentId = agentId
                && queue.Version > 0
                && not (isNull (box queue.Pending))
                && queue.Pending.Length <= 4
                && (queue.Pending |> List.forall (fun alert ->
                    let mutable id = Guid.Empty
                    Guid.TryParseExact(alert.AlertId, "N", &id)
                    && id <> Guid.Empty
                    && validText 1536 alert.Message
                    && not (String.IsNullOrWhiteSpace alert.IssuedBy)
                    && not (String.IsNullOrWhiteSpace alert.QueuedAtUtc)))
                && (queue.Pending |> List.map (fun alert -> alert.AlertId) |> List.distinct |> List.length) = queue.Pending.Length
            if not valid then
                return! Error(LedgerIntegrityError "Invalid response alert queue identity or content.")
            return queue, Some oid
    }

    let getResponseAlerts binding agentId =
        if not (validAgent agentId) then Error(InvalidBinding "Invalid alert target.")
        else
            LedgerStore.fetch binding
            |> Result.bind (fun snapshot ->
                responseAlertsAt binding snapshot agentId
                |> Result.map fst)

    let queueResponseAlert binding operationId actorId targetAgentId message =
        if not (validAgent targetAgentId) || not (validText 1536 message) then
            Error(InvalidBinding "Invalid alert target or message (maximum 1536 UTF-8 bytes).")
        else
            LegacyDirectWriterFence.transact binding
                (intent operationId actorId "QueueResponseAlert"
                    {| targetAgentId = targetAgentId; message = message |})
                (fun snapshot -> result {
                    let! _ = required<ReviewAgent> binding snapshot (agentPath targetAgentId)
                    let! queue, oid = responseAlertsAt binding snapshot targetAgentId
                    if queue.Pending.Length >= 4 then
                        return! Error(StaleEntityVersion "The target already has four undelivered alerts; wait or clear one.")
                    let alert =
                        { AlertId = operationId.ToString("N")
                          IssuedBy = actorId
                          Message = message
                          QueuedAtUtc = DateTimeOffset.UtcNow.ToString("O") }
                    return [ change (responseAlertsPath targetAgentId) oid
                                { queue with Version = queue.Version + 1
                                             Pending = queue.Pending @ [alert] } ]
                })

    let clearResponseAlert binding operationId actorId targetAgentId alertId =
        if not (validAgent targetAgentId) || alertId = Guid.Empty then
            Error(InvalidBinding "Invalid alert target or alert ID.")
        else
            LegacyDirectWriterFence.transact binding
                (intent operationId actorId "ClearResponseAlert"
                    {| targetAgentId = targetAgentId; alertId = alertId.ToString("N") |})
                (fun snapshot -> result {
                    let! queue, oid = responseAlertsAt binding snapshot targetAgentId
                    if not (queue.Pending |> List.exists (fun alert -> alert.AlertId = alertId.ToString("N"))) then
                        return! Error(StaleEntityVersion "That alert is no longer pending.")
                    return [ change (responseAlertsPath targetAgentId) oid
                                { queue with Version = queue.Version + 1
                                             Pending = queue.Pending |> List.filter (fun alert ->
                                                 alert.AlertId <> alertId.ToString("N")) } ]
                })

    let takeResponseAlertsAt binding snapshot agentId =
        if not (validAgent agentId) then Error(InvalidBinding "Invalid alert target.")
        else result {
            let! queued, _ = responseAlertsAt binding snapshot agentId
            if queued.Pending.IsEmpty then
                return []
            else
                // Commit consumption before returning any alert text. If the
                // response is lost afterward, the one-shot alert is lost too.
                // On a failed CAS, do not deliver stale or replaced messages.
                let! _ =
                    LegacyDirectWriterFence.transact binding
                        (intent (Guid.NewGuid()) agentId "DeliverResponseAlerts"
                            {| agentId = agentId; version = queued.Version |})
                        (fun latest -> result {
                            let! current, oid = responseAlertsAt binding latest agentId
                            if current.Version <> queued.Version || current.Pending <> queued.Pending then
                                return! Error(StaleEntityVersion "Response alert queue changed before delivery.")
                            return [ change (responseAlertsPath agentId) oid
                                        { current with Version = current.Version + 1; Pending = [] } ]
                        })
                return queued.Pending
        }

    // Retain the standalone API for callers without a verified snapshot.
    let takeResponseAlerts binding agentId =
        if not (validAgent agentId) then Error(InvalidBinding "Invalid alert target.")
        else
            LedgerStore.fetch binding
            |> Result.bind (fun snapshot -> takeResponseAlertsAt binding snapshot agentId)

    let acknowledgeNotification binding operationId agentId notificationId =
        if not (validAgent agentId) || notificationId = Guid.Empty then
            Error(InvalidBinding "Invalid notification acknowledgement.")
        else
            LegacyDirectWriterFence.transact binding
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
            LegacyDirectWriterFence.transact binding
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
                              WorkingTurnStartedAtUtc = null; ActiveSessionId = null
                              AssociatedSessionIds = [] }
                        return [ change (agentPath agentId) None agent ]
                    })

    let registerAgent binding operationId agentId =
        registerAgentAs binding legacyAdministrativeActor operationId agentId

    // Taskless execution is explicit and independent of roles and attempts.
    // The agent record and durable operation arbitrate concurrent devices.
    let startTasklessWork binding operationId agentId expectedAgentVersion activity =
        if not (validAgent agentId) || expectedAgentVersion < 1 || not (validText 2048 activity) then
            Error(InvalidBinding "Starting taskless work requires a registered agent, exact version and activity.")
        else
            LegacyDirectWriterFence.transact binding
                (intent operationId agentId "StartTasklessWork"
                    {| agentId = agentId; expectedAgentVersion = expectedAgentVersion; activity = activity |})
                (fun snapshot -> result {
                    let! agent, agentOid = required<ReviewAgent> binding snapshot (agentPath agentId)
                    if agent.Version <> expectedAgentVersion
                       || not (isNull (box agent.CurrentAssignment))
                       || agent.Execution <> "Stopped"
                       || not (List.contains agent.State [ "Parked"; "NeedsWork" ]) then
                        return! Error(StaleEntityVersion "StartTasklessWork requires an idle unassigned agent at the exact version.")
                    let updated = enterState "Working" (resetSessions agent)
                    return [ change (agentPath agentId) (Some agentOid)
                                 { updated with Version = agent.Version + 1
                                                StateReason = activity
                                                StateSequence = snapshot.Header.NextSequence } ]
                })

    let yieldTasklessWork binding operationId agentId expectedAgentVersion summary =
        if not (validAgent agentId) || expectedAgentVersion < 1 || not (validText 2048 summary) then
            Error(InvalidBinding "Yielding taskless work requires an exact agent version and summary.")
        else
            LegacyDirectWriterFence.transact binding
                (intent operationId agentId "YieldTasklessWork"
                    {| agentId = agentId; expectedAgentVersion = expectedAgentVersion; summary = summary |})
                (fun snapshot -> result {
                    let! agent, agentOid = required<ReviewAgent> binding snapshot (agentPath agentId)
                    if agent.Version <> expectedAgentVersion
                       || not (isNull (box agent.CurrentAssignment))
                       || agent.Execution <> "Working" then
                        return! Error(StaleEntityVersion "YieldTasklessWork requires this agent's active taskless session at the exact version.")
                    let updated = enterState "Parked" (resetSessions agent)
                    return [ change (agentPath agentId) (Some agentOid)
                                 { updated with Version = agent.Version + 1
                                                StateReason = summary
                                                StateSequence = snapshot.Header.NextSequence } ]
                })

    // Explicit authenticated administrative recovery is distinct from an
    // agent's self-yield. It never releases a task or an independent review.
    let recoverTasklessWorkAs binding actor operationId agentId expectedAgentVersion reason =
        if not (validAgent agentId) || expectedAgentVersion < 1 || not (validText 2048 reason)
           || String.IsNullOrWhiteSpace actor then
            Error(InvalidBinding "Recovery requires an authenticated actor, an exact agent version and a reason.")
        else
            LegacyDirectWriterFence.transact binding
                (intent operationId actor "RecoverTasklessWork"
                    {| agentId = agentId; expectedAgentVersion = expectedAgentVersion
                       reason = reason |})
                (fun snapshot -> result {
                    let! agent, agentOid = required<ReviewAgent> binding snapshot (agentPath agentId)
                    if agent.Version <> expectedAgentVersion
                       || not (isNull (box agent.CurrentAssignment))
                       || agent.Execution <> "Working" then
                        return! Error(StaleEntityVersion "Recovery requires an active, unassigned agent at the exact version.")
                    let updated = enterState "Parked" (resetSessions agent)
                    return [ change (agentPath agentId) (Some agentOid)
                                 { updated with Version = agent.Version + 1
                                                StateReason = reason
                                                StateSequence = snapshot.Header.NextSequence } ]
                })

    let assignTaskAs binding actor operationId taskId attemptId ownerId =
        if not (validTaskId taskId) || attemptId = Guid.Empty
           || not (validAgent ownerId) then
            Error(InvalidBinding "Invalid task assignment.")
        else
            let attemptIdText = attemptId.ToString("N")
            LegacyDirectWriterFence.transact binding
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
            LegacyDirectWriterFence.transact binding
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
            LegacyDirectWriterFence.transact binding
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
            LegacyDirectWriterFence.transact binding
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
            LegacyDirectWriterFence.transact binding
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

    let resumeAgent binding operationId taskId ownerId expectedAttemptVersion expectedAgentVersion generation =
        resumeAgentWithSession binding operationId taskId ownerId expectedAttemptVersion expectedAgentVersion generation ""

    let completeAgentWorkWithSession binding operationId taskId ownerId expectedAttemptVersion expectedAgentVersion
                          generation summary sessionId =
        if not (validTaskId taskId) || not (validAgent ownerId)
           || expectedAttemptVersion < 0 || expectedAgentVersion < 0 || generation < 0
           || not (validText 8192 summary) then
            Error(InvalidBinding "Completion requires a summary and exact assignment versions.")
        else
            LegacyDirectWriterFence.transact binding
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
                        let released =
                            enterState "Completed" agent |> fun updated ->
                                { updated with Version = agent.Version + 1
                                               StateReason = summary; StateSequence = snapshot.Header.NextSequence }
                        let! nextAgent, wakeups =
                            reconcileEligibleOnRelease binding snapshot operationId ownerId released
                        return
                            [ change (checkpointPath taskId attempt.AttemptId snapshot.Header.NextSequence) None checkpoint
                              change (attemptPath taskId attempt.AttemptId) (Some attemptOid)
                                { attempt with Version = attempt.Version + 1; Phase = "Finished" }
                              change (agentPath ownerId) (Some agentOid) nextAgent ] @ wakeups

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
            LegacyDirectWriterFence.transact binding
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
            LegacyDirectWriterFence.transact binding
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
            LegacyDirectWriterFence.transact binding
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
                        let released =
                            { (enterState "NeedsWork" agent) with
                                Version = agent.Version + 1
                                StateReason = reason
                                StateSequence = snapshot.Header.NextSequence }
                        let! nextAgent, wakeups =
                            reconcileEligibleOnRelease binding snapshot operationId actor released
                        return [ change (agentPath agentId) (Some agentOid) nextAgent ] @ wakeups
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
            LegacyDirectWriterFence.transact binding
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
            LegacyDirectWriterFence.transact binding
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
            LegacyDirectWriterFence.transact binding
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
            LegacyDirectWriterFence.transact binding
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
            LegacyDirectWriterFence.transact binding
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

    let recoverAgentAssignmentWithTaskVersionAs binding actor operationId taskId ownerId expectedTaskVersion expectedAttemptVersion
                               expectedAgentVersion generation reason =
        if not (validTaskId taskId) || not (validAgent ownerId)
           || expectedTaskVersion < 0 || expectedAttemptVersion < 1 || expectedAgentVersion < 1 || generation < 1
           || not (validText 2048 reason) then
            Error(InvalidBinding "Recovery requires exact versions and an explicit reason.")
        else
            let recoveryIntent =
                if expectedTaskVersion = 0 then
                    intent operationId actor "RecoverAgentAssignment"
                        {| taskId = taskId; ownerId = ownerId
                           expectedAttemptVersion = expectedAttemptVersion
                           expectedAgentVersion = expectedAgentVersion
                           generation = generation; reason = reason |}
                else
                    intent operationId actor "RecoverAgentAssignment"
                        {| taskId = taskId; ownerId = ownerId; expectedTaskVersion = expectedTaskVersion
                           expectedAttemptVersion = expectedAttemptVersion
                           expectedAgentVersion = expectedAgentVersion
                           generation = generation; reason = reason |}
            LegacyDirectWriterFence.transact binding
                recoveryIntent
                (fun snapshot ->
                    result {
                        let! task, _, attempt, attemptOid = getAttempt binding snapshot taskId
                        let! agent, agentOid = required<ReviewAgent> binding snapshot (agentPath ownerId)
                        if task.Lifecycle <> "InProgress" || attempt.OwnerAgentId <> ownerId
                           || (expectedTaskVersion > 0 && task.Version <> expectedTaskVersion)
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

    let recoverAgentAssignmentAs binding actor operationId taskId ownerId expectedAttemptVersion expectedAgentVersion generation reason =
        recoverAgentAssignmentWithTaskVersionAs binding actor operationId taskId ownerId 0
            expectedAttemptVersion expectedAgentVersion generation reason

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
            LegacyDirectWriterFence.transact binding
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
                    if task.Version <> expectedTaskVersion
                       || task.Lifecycle <> "InProgress"
                       || attempt.AttemptId <> attemptId
                       || attempt.OwnerAgentId <> targetAgentId
                       || attempt.Version <> expectedAttemptVersion
                       || attempt.AssignmentGeneration <> generation
                       || attempt.Phase <> "Started"
                       || agent.Version <> expectedAgentVersion
                       || agent.State <> "Working"
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
    let private clearAdministrativeFocus binding snapshot (attempt: ReviewAttempt) expectedAgentVersion reason operationId actor =
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
                let released =
                    enterState "Completed" agent |> fun updated ->
                        { updated with Version = agent.Version + 1
                                       StateReason = reason; StateSequence = snapshot.Header.NextSequence }
                let! nextAgent, wakeups =
                    reconcileEligibleOnRelease binding snapshot operationId actor released
                return [ change (agentPath attempt.OwnerAgentId) (Some agentOid) nextAgent ] @ wakeups
        }

    let changeAttemptAdministrationAs binding actor operationId taskId action expectedTaskVersion expectedAttemptVersion
                                    expectedAgentVersion generation reason =
        if not (validTaskId taskId) || expectedTaskVersion < 1 || expectedAttemptVersion < 1
           || expectedAgentVersion < 1 || generation < 1 || not (validText 2048 reason)
           || not (List.contains action [ "ParkAttempt"; "BlockAttempt"; "ResumeAttempt"; "AbandonAttempt"; "CancelTask" ]) then
            Error(InvalidBinding "Invalid administrative task/attempt transition.")
        else
            LegacyDirectWriterFence.transact binding
                (intent operationId actor action
                    {| taskId = taskId; expectedTaskVersion = expectedTaskVersion
                       expectedAttemptVersion = expectedAttemptVersion
                       expectedAgentVersion = expectedAgentVersion
                       generation = generation; reason = reason |})
                (fun snapshot -> result {
                    let! task, taskOid, attempt, attemptOid = getAttempt binding snapshot taskId
                    if action = "ResumeAttempt" && attempt.Phase = "Blocked" then
                        let! wait = read<AttemptChallengeWait> binding snapshot (challengeWaitPath taskId attempt.AttemptId)
                        match wait with
                        | Some (record, _) when List.contains record.Status [ "Waiting"; "Eligible" ] ->
                            return! Error(StaleEntityVersion
                                "Challenge-blocked attempt must be requeued via RequeueChallengeBlockedAttempt after verifying every dependency.")
                        | _ -> ()
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
                        else clearAdministrativeFocus binding snapshot attempt expectedAgentVersion reason operationId actor
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
            LegacyDirectWriterFence.transact binding
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
            LegacyDirectWriterFence.transact binding
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
                        clearAdministrativeFocus binding snapshot attempt expectedAgentVersion reason operationId actor
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
            LegacyDirectWriterFence.transact binding
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
            LegacyDirectWriterFence.transact binding
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

    /// Read-only activation eligibility at one canonical snapshot. A retained
    /// assignment may have Execution=Stopped and therefore derived State=Ready
    /// even after its task is explicitly Closed. That is a completion candidate,
    /// not permission to restart the task.
    let inspectAutomaticActivation binding agentId =
        result {
            let! snapshot = LedgerStore.fetch binding
            let! agent = getAgentWorkingTurnAt binding snapshot agentId
            match box agent.CurrentAssignment with
            | null -> return ActivationIneligible "Agent has no current assignment."
            | _ when agent.State <> "Ready" ->
                return ActivationIneligible $"Agent is {agent.State}, not Ready."
            | _ ->
                let assignment = agent.CurrentAssignment
                let! task, attempt = getTaskAt binding snapshot assignment.TaskId
                if task.Lifecycle <> "InProgress" then
                    return ActivationIneligible $"Task {task.TaskId} is {task.Lifecycle}; no automatic start is permitted."
                elif task.AttemptId <> assignment.AttemptId then
                    return ActivationIneligible $"Task {task.TaskId} has a different current attempt."
                else
                    match attempt with
                    | Some current when current.OwnerAgentId = agentId
                                        && current.AttemptId = assignment.AttemptId
                                        && (current.Phase = "Queued" || current.Phase = "Started") ->
                        return ActivationEligible task.TaskId
                    | Some current ->
                        return ActivationIneligible $"Attempt {current.AttemptId} is {current.Phase} or belongs to another agent."
                    | None ->
                        return ActivationIneligible $"Task {task.TaskId} has no current attempt."
        }


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
                let! _ = verifyOwnerWaitIndexesAt binding snapshot agentId
                let key = agentKey agentId
                let! records = LedgerStore.readAgentInboxRecordsForAgentAt binding snapshot key
                let! agent = requiredFromRecords<ReviewAgent> records (agentPath agentId)
                let configPath = "records/agent-instructions/" + key + ".json"
                let! instructionLayers =
                    if records.ContainsKey configPath then
                        // Existing configs (including malformed ones) remain
                        // decoded and verified by the canonical resolver.
                        RoleInstructions.resolveAtKnownAgent binding snapshot agentId
                    else
                        // The same pinned tree listing proves the config absent.
                        Ok ({ SnapshotCommit = snapshot.Commit
                              RoleIds = []; AgentConfigVersion = 0
                              Precedence = [ "global"; "workspace"; "project/directory"
                                             "role (attachment order)"; "agent"; "assignment" ]
                              Documents = [] } : InstructionResolution)
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
                        | "Working", None ->
                            // Explicit taskless execution owns no implementation attempt.
                            isNull (box agent.CurrentAssignment) && String.IsNullOrWhiteSpace agent.ActiveAttemptId
                        | _ -> false
                    if not focusValid then
                        return! Error(LedgerIntegrityError "Agent state/focused attempt mismatch.")
                    let runnable = owned |> List.filter (fun entry ->
                        matchesAssignment entry.Task.TaskId entry.Attempt agent
                        && List.contains entry.Attempt.Phase [ "Queued"; "Started" ]
                        && agent.Execution <> "Working")
                    let parked = owned |> List.filter (fun entry -> entry.Attempt.Phase = "Deferred")
                    let blocked = owned |> List.filter (fun entry -> entry.Attempt.Phase = "Blocked")
                    let mutable blockedWaits = []
                    for entry in blocked do
                        let! waiting = read<AttemptChallengeWait> binding snapshot
                                          (challengeWaitPath entry.Task.TaskId entry.Attempt.AttemptId)
                        match waiting with
                        | None -> ()
                        | Some (record, _) when record.Status = "Requeued" ->
                            // Historical wait record: a later administrative
                            // block may be unrelated to this completed fence.
                            ()
                        | Some (record, _) ->
                            if record.TaskId <> entry.Task.TaskId
                               || record.AttemptId <> entry.Attempt.AttemptId
                               || record.OwnerAgentId <> agentId
                               || not (List.contains record.Status [ "Waiting"; "Eligible" ]) then
                                return! Error(LedgerIntegrityError "Blocked challenge dependency record disagrees with its attempt.")
                            // The canonical blocked attempt is an independent
                            // completeness witness: an entirely omitted owner
                            // index directory must not look like an empty queue.
                            if not (List.isEmpty record.ChallengeIds) then
                                let ownerIndex =
                                    waitIndexPath (ownerWaitIndexPrefix agentId)
                                        entry.Task.TaskId entry.Attempt.AttemptId
                                        entry.Attempt.AssignmentGeneration
                                let! _ = verifiedWaitIndexAt binding snapshot ownerIndex None (Some agentId)
                                ()
                            blockedWaits <- record :: blockedWaits
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
                          BlockedChallengeWaits = List.rev blockedWaits
                          ChallengeSubscribed = false
                          ChallengeAssigned = []
                          ChallengeTriage = []
                          ChallengeRecent = []
                          ChallengeDependencies = []
                          ChallengeAssignedOmitted = 0
                          ChallengeTriageOmitted = 0
                          ChallengeRecentOmitted = 0
                          ChallengeDependenciesOmitted = 0
                          PendingReviews = pending
                          AvailableReviews = available
                          UnacknowledgedFeedback = feedback |> List.sortBy (fun notice -> notice.DecisionSequence)
                          PendingNotifications = notifications
                          IntegrationSubmissions = integrationRecords |> List.sortBy (fun item -> item.CreatedSequence)
                          InstructionLayers = instructionLayers
                          SlotAvailable = isNull (box agent.CurrentAssignment) && agent.Execution <> "Working"
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
            match LegacyDirectWriterFence.findAppliedForLegacyWrite binding action with
            | Error failure -> Error failure
            | Ok(Some applied) -> Ok applied
            | Ok None -> result {
                let! sourceHead =
                    if String.IsNullOrWhiteSpace sourceHead then LedgerStore.committedWorktreeHead binding sourceRef
                    else Ok sourceHead
                let! snapshot = LedgerStore.fetch binding
                let! baseOid =
                    if String.IsNullOrWhiteSpace targetRef then Ok sourceHead
                    else LedgerStore.reviewMergeBase binding targetRef sourceHead
                do! LedgerStore.verifySourceObject binding sourceHead
                let! existing = read<BranchReview> binding snapshot (branchReviewPath taskId id)
                if existing.IsSome then
                    return! Error(StaleEntityVersion "Review ID already exists.")
                do! LegacyDirectWriterFence.preflightSourcePublication binding
                do! LedgerStore.publishReviewRefForAction binding action refName sourceHead
                let writeResult = LegacyDirectWriterFence.transact binding action (fun current -> result {
                    let! existing = read<BranchReview> binding current (branchReviewPath taskId id)
                    if existing.IsSome then
                        return! Error(StaleEntityVersion "Review ID already exists.")
                    let review =
                        { ReviewId = id; TaskId = taskId; AttemptId = null
                          OwnerAgentId = requesterId; Kind = (if isNull purpose then "" else purpose)
                          TaskRef = sourceRef; ReviewRef = refName; SourceVersion = 1; ReviewedHeadOid = sourceHead; BaseCommit = baseOid
                          TargetRef = targetRef; Status = "Unclaimed"; ReviewerId = null
                          Version = 1; CreatedSequence = current.Header.NextSequence
                          DecisionSequence = 0L; Acknowledged = false }
                    return [ change (branchReviewPath taskId id) None review
                             { Path = reviewDocumentPath taskId id "summary.md"
                               ExpectedBlobOid = None; Content = summary } ]
                })
                // Never delete a ref after a ledger race: another submitter
                // may have accepted the identical immutable object.
                return! writeResult
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
            match LegacyDirectWriterFence.findAppliedForLegacyWrite binding action with
            | Error failure -> Error failure
            | Ok(Some applied) -> Ok applied
            | Ok None -> result {
                let! sourceHead =
                    if String.IsNullOrWhiteSpace sourceHead then LedgerStore.committedWorktreeHead binding sourceRef
                    else Ok sourceHead
                let! snapshot = LedgerStore.fetch binding
                let! existing, _ = required<BranchReview> binding snapshot (branchReviewPath taskId id)
                if existing.Version <> expectedVersion || existing.Status <> "Unclaimed" then
                    return! Error(ReviewAlreadyClaimed "Review is no longer unclaimed.")
                let! baseOid =
                    if String.IsNullOrWhiteSpace existing.TargetRef then Ok sourceHead
                    else LedgerStore.reviewMergeBase binding existing.TargetRef sourceHead
                do! LedgerStore.verifySourceObject binding sourceHead
                do! LegacyDirectWriterFence.preflightSourcePublication binding
                do! LedgerStore.publishReviewRefForAction binding action refName sourceHead
                let writeResult =
                    LegacyDirectWriterFence.transact binding action (fun current -> result {
                        let! review, reviewOid =
                            required<BranchReview> binding current (branchReviewPath taskId id)
                        if review.Version <> expectedVersion || review.Status <> "Unclaimed" then
                            return! Error(ReviewAlreadyClaimed "Review was claimed or replaced.")
                        let! _, summaryOid =
                            LedgerStore.readAt binding current (reviewDocumentPath taskId id "summary.md")
                        return [ change (branchReviewPath taskId id) (Some reviewOid)
                                   { review with Version = review.Version + 1
                                                 ReviewRef = refName; SourceVersion = expectedVersion + 1; ReviewedHeadOid = sourceHead
                                                 BaseCommit = baseOid; TaskRef = if String.IsNullOrWhiteSpace sourceRef then review.TaskRef else sourceRef }
                                 { Path = reviewDocumentPath taskId id "summary.md"
                                   ExpectedBlobOid = summaryOid; Content = summary } ]
                    })
                // Orphans are retained for audited, delayed CAS cleanup.
                // Immediate deletion can erase a concurrent winner's ref.
                return! writeResult
            }

    let claimBranchReview binding operationId taskId reviewId reviewerId expectedVersion =
        if not (validTaskId taskId) || reviewId = Guid.Empty || not (validAgent reviewerId)
           || expectedVersion < 1 then Error(InvalidBinding "Invalid review claim.")
        else
            let id = reviewId.ToString("N")
            LegacyDirectWriterFence.transact binding
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
            LegacyDirectWriterFence.transact binding
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
            LegacyDirectWriterFence.transact binding
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
            LegacyDirectWriterFence.transact binding
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
            LegacyDirectWriterFence.transact binding
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
            LegacyDirectWriterFence.transact binding
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
            let! records = LedgerStore.readTaskOverviewPrimaryRecordsAt binding snapshot
            let mutable taskItems = []
            let mutable activeAttempts = []
            let mutable reviews = []
            for KeyValue(path, _) in records do
                if path.EndsWith("/task.json", StringComparison.Ordinal) then
                    let! task = requiredFromRecords<ReviewTask> records path
                    taskItems <- task :: taskItems
                    if not (String.IsNullOrWhiteSpace task.AttemptId) then
                        activeAttempts <- attemptPath task.TaskId task.AttemptId :: activeAttempts
                elif path.EndsWith("/review.json", StringComparison.Ordinal) then
                    let! review = requiredFromRecords<BranchReview> records path
                    reviews <- review :: reviews
            // One further pinned batch for just the linked current attempts:
            // historical attempts, specs, events and integration receipts are
            // never read by this overview. Missing or malformed links still fail.
            let! attempts =
                LedgerStore.readTaskOverviewAttemptRecordsAt binding snapshot activeAttempts
            let mutable tasks = []
            for task in taskItems do
                let! attempt =
                    if String.IsNullOrWhiteSpace task.AttemptId then Ok None
                    else requiredFromRecords<ReviewAttempt> attempts
                             (attemptPath task.TaskId task.AttemptId)
                         |> Result.map Some
                tasks <- {| Task = task; Attempt = attempt |} :: tasks
            return {| SnapshotCommit = snapshot.Commit
                      Tasks = tasks |> List.sortBy (fun item -> item.Task.TaskId)
                      Reviews = reviews |> List.sortBy (fun item -> item.CreatedSequence, item.ReviewId) |}
        }
