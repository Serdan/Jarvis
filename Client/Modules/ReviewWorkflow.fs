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
      ActiveAttemptId: string }

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
      ReviewFeedback: string
      FeedbackSequence: int64
      AcknowledgedFeedbackSequence: int64 }

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
      CreatedSequence: int64 }

[<CLIMutable>]
type InboxTask =
    { Task: ReviewTask
      Attempt: ReviewAttempt }

[<CLIMutable>]
type AgentInbox =
    { SnapshotCommit: string
      Agent: ReviewAgent
      Working: InboxTask option
      RunnableQueue: InboxTask list
      Parked: InboxTask list
      Blocked: InboxTask list
      AcceptedPendingIntegration: InboxTask list
      PendingReviews: ReviewRequest list
      AvailableReviews: ReviewRequest list
      SlotAvailable: bool
      Unassigned: bool }

type ReviewVerdict =
    | Accept
    | RequestChanges

module ReviewWorkflow =
    let private options =
        JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)
    let private json value = JsonSerializer.Serialize(value, options)
    let private idPattern = Regex("^[A-Za-z0-9_-]{1,80}$", RegexOptions.Compiled)
    let private refPattern = Regex("^refs/heads/[A-Za-z0-9][A-Za-z0-9_./-]*$", RegexOptions.Compiled)

    let private taskPath taskId = $"tasks/{taskId}/task.json"
    let private specPath taskId = $"tasks/{taskId}/spec.html"
    let private attemptPath taskId attemptId = $"tasks/{taskId}/attempts/{attemptId}.json"
    let private requestPath taskId requestId = $"tasks/{taskId}/reviews/{requestId}/request.json"
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
    let private validBranch (value: string) =
        not (isNull value) && refPattern.IsMatch value
        && not (value.Contains("..")) && not (value.EndsWith("/"))

    let private intent operationId actor kind payload =
        { OperationId = operationId
          Actor = actor
          Kind = kind
          RequestJson = json payload }

    let private change path blob value =
        { Path = path; ExpectedBlobOid = blob; Content = json value }

    let private read<'t> binding snapshot path : Result<('t * string) option, LedgerFailure> =
        result {
            let! content, blobOid = LedgerStore.readAt binding snapshot path
            match content, blobOid with
            | None, None -> return None
            | Some content, Some blobOid ->
                try
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

    let createTaskWithSpec binding operationId taskId title html =
        if not (validTaskId taskId) || not (validText 512 title)
           || not (validText (128 * 1024) html) then
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
                        let agent = { AgentId = agentId; Version = 1; ActiveAttemptId = null }
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
                        let! _ = required<ReviewAgent> binding snapshot (agentPath ownerId)
                        let! _ = required<ReviewAgent> binding snapshot (agentPath reviewerId)
                        let! specContent, specOid = LedgerStore.readAt binding snapshot (specPath taskId)
                        if specContent.IsNone || specOid.IsNone then
                            return! Error(LedgerIntegrityError "Canonical task spec is missing.")
                        if task.Lifecycle <> "Open" || not (String.IsNullOrWhiteSpace task.AttemptId) then
                            return! Error(StaleEntityVersion "Task already has an implementation owner.")
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
                              ReviewFeedback = null; FeedbackSequence = 0L
                              AcknowledgedFeedbackSequence = 0L }
                        return
                            [ change (taskPath taskId) (Some taskOid)
                                { task with Version = task.Version + 1; Lifecycle = "InProgress"; AttemptId = attemptIdText }
                              change (attemptPath taskId attemptIdText) None attempt ]
                    })

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
                           || attempt.Phase <> "Queued" then
                            return! Error(StaleEntityVersion "Attempt is not available for Begin.")
                        if not (String.IsNullOrWhiteSpace agent.ActiveAttemptId) then
                            return! Error(AgentBusy "Agent is already Working.")
                        return
                            [ change (attemptPath taskId attempt.AttemptId) (Some attemptOid)
                                { attempt with Version = attempt.Version + 1; Phase = "Working" }
                              change (agentPath ownerId) (Some agentOid)
                                { agent with Version = agent.Version + 1; ActiveAttemptId = attempt.AttemptId } ]
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

    let submitReview binding operationId taskId ownerId expectedVersion generation requestId sourceRef headOid summary =
        if not (validTaskId taskId) || not (validAgent ownerId) || requestId = Guid.Empty
           || expectedVersion < 1 || generation < 1 || not (validText 4096 summary) then
            Error(InvalidBinding "Invalid review submission.")
        else
            let requestIdText = requestId.ToString("N")
            let action =
                intent operationId ownerId "SubmitForReview"
                    {| taskId = taskId; ownerId = ownerId; expectedVersion = expectedVersion
                       generation = generation; requestId = requestId; sourceRef = sourceRef
                       headOid = headOid; summary = summary |}
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
                            let revision =
                                { RequestId = requestIdText; Revision = 1; TaskId = taskId
                                  AttemptId = attempt.AttemptId; BaseCommit = attempt.BaseCommit
                                  HeadCommit = headOid; SourceRef = sourceRef; TargetRef = attempt.TargetRef
                                  ReviewRef = published; Summary = summary
                                  CreatedSequence = current.Header.NextSequence }
                            return
                                [ change (requestPath taskId requestIdText) None request
                                  change (revisionPath taskId requestIdText 1) None revision
                                  change (attemptPath taskId attempt.AttemptId) (Some attemptOid)
                                    { attempt with Version = attempt.Version + 1
                                                   ActiveReviewRequestId = requestIdText
                                                   LatestReviewRequestId = requestIdText } ]
                        })
            }

    let updateReview binding operationId taskId ownerId generation requestId expectedRequestVersion
                     sourceRef headOid summary =
        if not (validTaskId taskId) || not (validAgent ownerId) || requestId = Guid.Empty
           || expectedRequestVersion < 1 || generation < 1 || not (validText 4096 summary) then
            Error(InvalidBinding "Invalid review update.")
        else
            let id = requestId.ToString("N")
            let action =
                intent operationId ownerId "UpdateReviewRequest"
                    {| taskId = taskId; ownerId = ownerId; generation = generation
                       requestId = requestId; expectedRequestVersion = expectedRequestVersion
                       sourceRef = sourceRef; headOid = headOid; summary = summary |}
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
                            let revision =
                                { RequestId = id; Revision = nextNumber; TaskId = taskId
                                  AttemptId = liveAttempt.AttemptId; BaseCommit = liveAttempt.BaseCommit
                                  HeadCommit = headOid; SourceRef = sourceRef; TargetRef = liveAttempt.TargetRef
                                  ReviewRef = published; Summary = summary
                                  CreatedSequence = current.Header.NextSequence }
                            return
                                [ change (requestPath taskId id) (Some requestOid)
                                    { request with Version = request.Version + 1; CurrentRevision = nextNumber }
                                  change (revisionPath taskId id nextNumber) None revision ]
                        })
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
                        do! LedgerStore.fetchReviewRef binding revision.ReviewRef revision.HeadCommit
                        do! LedgerStore.verifyReviewDiff binding revision.BaseCommit revision.HeadCommit
                        return [ change (requestPath taskId id) (Some requestOid)
                                    { request with Version = request.Version + 1; Status = "Claimed"
                                                   ReviewerId = reviewerId; ClaimantId = reviewerId
                                                   ClaimedRevision = expectedRevision } ]
                    })

    let decide binding operationId taskId requestId reviewerId expectedVersion verdict feedback =
        if not (validTaskId taskId) || requestId = Guid.Empty || not (validAgent reviewerId)
           || expectedVersion < 1
           || (verdict = RequestChanges && not (validText 4096 feedback))
           || (not (isNull feedback) && Encoding.UTF8.GetByteCount feedback > 4096) then
            Error(InvalidBinding "Invalid review decision.")
        else
            let id = requestId.ToString("N")
            let status = match verdict with | Accept -> "Accepted" | RequestChanges -> "ChangesRequested"
            LedgerStore.transact binding
                (intent operationId reviewerId "ReviewDecision"
                    {| taskId = taskId; requestId = requestId; reviewerId = reviewerId
                       expectedVersion = expectedVersion; status = status; feedback = feedback |})
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
                        let nextAttempt =
                            { attempt with Version = attempt.Version + 1
                                           ActiveReviewRequestId = null
                                           ReviewFeedback = feedback
                                           FeedbackSequence = snapshot.Header.NextSequence
                                           AcceptedReviewRequestId =
                                               if verdict = Accept then id else attempt.AcceptedReviewRequestId
                                           AcceptedReviewRevision =
                                               if verdict = Accept then request.CurrentRevision else attempt.AcceptedReviewRevision
                                           AcceptedHead =
                                               if verdict = Accept then revision.HeadCommit else attempt.AcceptedHead }
                        let review =
                            { request with Version = request.Version + 1; Status = status
                                           DecisionSequence = snapshot.Header.NextSequence; Feedback = feedback }
                        return
                            [ change (requestPath taskId id) (Some requestOid) review
                              change (attemptPath taskId attempt.AttemptId) (Some attemptOid) nextAttempt ]
                    })


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
                        let! agent, agentOid = required<ReviewAgent> binding snapshot (agentPath ownerId)
                        let! request, _ = required<ReviewRequest> binding snapshot (requestPath taskId id)
                        if task.Lifecycle <> "InProgress" || attempt.OwnerAgentId <> ownerId
                           || attempt.Phase <> "Working" || attempt.Version <> expectedAttemptVersion
                           || agent.ActiveAttemptId <> attempt.AttemptId
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
                        return
                            [ change (taskPath taskId) (Some taskOid)
                                { task with Version = task.Version + 1; Lifecycle = "Implemented" }
                              change (attemptPath taskId attempt.AttemptId) (Some attemptOid)
                                { attempt with Version = attempt.Version + 1; Phase = "Accepted" }
                              change (agentPath ownerId) (Some agentOid)
                                { agent with Version = agent.Version + 1; ActiveAttemptId = null } ]
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
                        if attempt.OwnerAgentId <> ownerId || attempt.FeedbackSequence <> decisionSequence
                           || attempt.AcknowledgedFeedbackSequence >= decisionSequence then
                            return! Error(StaleEntityVersion "Feedback is not pending at that sequence.")
                        return
                            [ change (attemptPath taskId attempt.AttemptId) (Some attemptOid)
                                { attempt with Version = attempt.Version + 1
                                               AcknowledgedFeedbackSequence = decisionSequence } ]
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
                let mutable failure = None
                let mutable owned = []
                let mutable pending = []
                let mutable available = []
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
                                           [ "Queued"; "Acknowledged"; "Working"; "Parked"; "Blocked"
                                             "Accepted"; "Superseded"; "Abandoned" ]) then
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
                        entry.Attempt.Phase = "Accepted" && entry.Task.Lifecycle <> "Closed")
                    if working.Length > 1
                       || (match working with
                           | [ item ] -> agent.ActiveAttemptId <> item.Attempt.AttemptId
                           | [] -> not (String.IsNullOrWhiteSpace agent.ActiveAttemptId)
                           | _ -> true) then
                        return! Error(LedgerIntegrityError "Agent slot disagrees with the task's Working attempts.")
                    let active = working |> List.tryHead
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
                          Working = active
                          RunnableQueue = runnable
                          Parked = parked
                          Blocked = blocked
                          AcceptedPendingIntegration = accepted
                          PendingReviews = pending
                          AvailableReviews = available
                          SlotAvailable = active.IsNone
                          Unassigned = owned.IsEmpty && pending.IsEmpty && available.IsEmpty }
            }
