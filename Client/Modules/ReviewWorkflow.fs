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
      CurrentReviewId: string
      SubmittedCommit: string
      SourceRef: string
      ReviewFeedback: string }

[<CLIMutable>]
type ReviewSubmission =
    { ReviewId: string
      TaskId: string
      AttemptId: string
      Version: int
      OwnerAgentId: string
      ReviewerId: string
      AssignmentGeneration: int
      CommitOid: string
      SourceRef: string
      Summary: string
      SubmittedSequence: int64
      Status: string
      DecisionSequence: int64
      Feedback: string }

type ReviewVerdict =
    | Accept
    | RequestChanges

module ReviewWorkflow =
    let private options = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)
    let private json value = JsonSerializer.Serialize(value, options)
    let private idPattern = Regex("^[A-Za-z0-9_-]{1,80}$", RegexOptions.Compiled)

    let private taskPath (taskId: string) = $"tasks/{taskId}/task.json"
    let private attemptPath (taskId: string) (attemptId: string) = $"tasks/{taskId}/attempts/{attemptId}.json"
    let private reviewPath (taskId: string) (reviewId: string) = $"tasks/{taskId}/reviews/{reviewId}.json"

    let private agentPath (agentId: string) =
        let safe = Convert.ToBase64String(Encoding.UTF8.GetBytes(agentId)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        $"agents/{safe}.json"

    let private validTaskId taskId = not (isNull taskId) && idPattern.IsMatch taskId
    let private validAgent agent = not (String.IsNullOrWhiteSpace agent) && Encoding.UTF8.GetByteCount(agent) <= 120
    let private validText limit (value: string) = not (String.IsNullOrWhiteSpace value) && Encoding.UTF8.GetByteCount(value) <= limit

    let private newIntent operationId actor kind request =
        { OperationId = operationId
          Actor = actor
          Kind = kind
          RequestJson = json request }

    let private change path oid value =
        { Path = path
          ExpectedBlobOid = oid
          Content = json value }

    let private load<'t> binding snapshot path : Result<('t * string) option, LedgerFailure> =
        result {
            let! content, oid = LedgerStore.readAt binding snapshot path
            match content, oid with
            | None, None -> return None
            | Some text, Some blob ->
                try
                    let value = JsonSerializer.Deserialize<'t>(text, options)
                    if isNull (box value) then
                        return! Error(LedgerIntegrityError $"Invalid null record: {path}")
                    else
                        return Some(value, blob)
                with error ->
                    return! Error(LedgerIntegrityError $"Invalid JSON at {path}: {error.Message}")
            | _ -> return! Error(LedgerIntegrityError $"Inconsistent ledger record: {path}")
        }

    let private required<'t> binding snapshot path : Result<'t * string, LedgerFailure> =
        result {
            let! item = load<'t> binding snapshot path
            match item with
            | Some value -> return value
            | None -> return! Error(StaleEntityVersion $"Missing ledger record: {path}")
        }

    let createTask binding operationId taskId title =
        if not (validTaskId taskId) || not (validText 512 title) then
            Error(InvalidBinding "Task ID or title is invalid.")
        else
            let path = taskPath taskId
            let intent = newIntent operationId "Orchestrator" "CreateTask"
                            {| taskId = taskId; title = title |}
            LedgerStore.transact binding intent (fun snapshot ->
                result {
                    let! existing = load<ReviewTask> binding snapshot path
                    if existing.IsSome then
                        return! Error(StaleEntityVersion "Task already exists.")
                    let task =
                        { TaskId = taskId; Title = title; Version = 1
                          Lifecycle = "Open"; AttemptId = null }
                    return [ change path None task ]
                })

    let registerAgent binding operationId agentId =
        if not (validAgent agentId) then
            Error(InvalidBinding "Invalid agent identity.")
        else
            let path = agentPath agentId
            let intent = newIntent operationId "Orchestrator" "RegisterAgent" {| agentId = agentId |}
            LedgerStore.transact binding intent (fun snapshot ->
                result {
                    let! existing = load<ReviewAgent> binding snapshot path
                    if existing.IsSome then
                        return! Error(StaleEntityVersion "Agent is already registered.")
                    let agent = { AgentId = agentId; Version = 1; ActiveAttemptId = null }
                    return [ change path None agent ]
                })

    // The single primary attempt is distinct from an agent's execution slot.
    let assignTask binding operationId taskId attemptId ownerId reviewerId =
        if not (validTaskId taskId) || attemptId = Guid.Empty
           || not (validAgent ownerId) || not (validAgent reviewerId) || ownerId = reviewerId then
            Error(InvalidBinding "Task, attempt, owner or independent reviewer is invalid.")
        else
            let path = taskPath taskId
            let attempt = attemptPath taskId (attemptId.ToString("N"))
            let intent = newIntent operationId "Orchestrator" "AssignTask"
                            {| taskId = taskId; attemptId = attemptId; ownerId = ownerId; reviewerId = reviewerId |}
            LedgerStore.transact binding intent (fun snapshot ->
                result {
                    let! task, taskOid = required<ReviewTask> binding snapshot path
                    let! _ = required<ReviewAgent> binding snapshot (agentPath ownerId)
                    let! _ = required<ReviewAgent> binding snapshot (agentPath reviewerId)
                    let! existing = load<ReviewAttempt> binding snapshot attempt
                    if task.Lifecycle <> "Open" || not (isNull task.AttemptId) || existing.IsSome then
                        return! Error(StaleEntityVersion "Task is not open for a new primary attempt.")
                    let nextTask = { task with Version = task.Version + 1; Lifecycle = "InProgress"; AttemptId = attemptId.ToString("N") }
                    let assigned =
                        { TaskId = taskId; AttemptId = attemptId.ToString("N"); Version = 1
                          OwnerAgentId = ownerId; ReviewerId = reviewerId; Phase = "Queued"
                          AssignmentGeneration = 1; CurrentReviewId = null
                          SubmittedCommit = null; SourceRef = null; ReviewFeedback = null }
                    return [ change path (Some taskOid) nextTask
                             change attempt None assigned ]
                })

    let beginAttempt binding operationId taskId agentId expectedVersion generation =
        if not (validTaskId taskId) || not (validAgent agentId) || expectedVersion < 1 || generation < 1 then
            Error(InvalidBinding "Invalid begin request.")
        else
            let task = taskPath taskId
            let agent = agentPath agentId
            let intent = newIntent operationId agentId "BeginAttempt"
                            {| taskId = taskId; agentId = agentId; expectedVersion = expectedVersion; generation = generation |}
            LedgerStore.transact binding intent (fun snapshot ->
                result {
                    let! taskValue, _ = required<ReviewTask> binding snapshot task
                    let attempt = attemptPath taskId taskValue.AttemptId
                    let! current, attemptOid = required<ReviewAttempt> binding snapshot attempt
                    let! worker, agentOid = required<ReviewAgent> binding snapshot agent
                    if taskValue.Lifecycle <> "InProgress" || current.OwnerAgentId <> agentId
                       || current.Version <> expectedVersion || current.AssignmentGeneration <> generation
                       || current.Phase <> "Queued" then
                        return! Error(StaleEntityVersion "Stale or unavailable assignment generation.")
                    if not (isNull worker.ActiveAttemptId) then
                        return! Error(AgentBusy "Agent already has a Working attempt.")
                    return [ change attempt (Some attemptOid) { current with Version = current.Version + 1; Phase = "Working" }
                             change agent (Some agentOid) { worker with Version = worker.Version + 1; ActiveAttemptId = current.AttemptId } ]
                })

    // A submitted SHA is immutable review input; no branch tip is implicitly accepted.
    let submitCommit binding operationId taskId agentId expectedVersion generation reviewId sourceRef commit summary =
        if not (validTaskId taskId) || not (validAgent agentId)
           || expectedVersion < 1 || generation < 1 || reviewId = Guid.Empty
           || not (validText 4096 summary) then
            Error(InvalidBinding "Invalid commit-review submission.")
        else
            let reviewIdText = reviewId.ToString("N")
            let intent = newIntent operationId agentId "SubmitForReview"
                            {| taskId = taskId; agentId = agentId; expectedVersion = expectedVersion
                               generation = generation; reviewId = reviewId; sourceRef = sourceRef
                               commit = commit; summary = summary |}
            LedgerStore.transact binding intent (fun snapshot ->
                result {
                    let! task, _ = required<ReviewTask> binding snapshot (taskPath taskId)
                    let attempt = attemptPath taskId task.AttemptId
                    let review = reviewPath taskId reviewIdText
                    let! current, attemptOid = required<ReviewAttempt> binding snapshot attempt
                    let! worker, agentOid = required<ReviewAgent> binding snapshot (agentPath agentId)
                    let! oldReview = load<ReviewSubmission> binding snapshot review
                    if task.Lifecycle <> "InProgress" || current.Phase <> "Working"
                       || current.OwnerAgentId <> agentId || current.Version <> expectedVersion
                       || current.AssignmentGeneration <> generation
                       || worker.ActiveAttemptId <> current.AttemptId || oldReview.IsSome then
                        return! Error(StaleEntityVersion "Submission no longer matches the Working assignment.")
                    do! LedgerStore.verifySourceCommit binding sourceRef commit
                    let submission =
                        { ReviewId = reviewIdText; TaskId = taskId; AttemptId = current.AttemptId; Version = 1
                          OwnerAgentId = agentId; ReviewerId = current.ReviewerId
                          AssignmentGeneration = generation; CommitOid = commit; SourceRef = sourceRef
                          Summary = summary; SubmittedSequence = snapshot.Header.NextSequence
                          Status = "Pending"; DecisionSequence = 0L; Feedback = null }
                    let nextAttempt =
                        { current with Version = current.Version + 1; Phase = "ReadyForReview"
                                       CurrentReviewId = reviewIdText; SubmittedCommit = commit; SourceRef = sourceRef }
                    return [ change review None submission
                             change attempt (Some attemptOid) nextAttempt
                             change (agentPath agentId) (Some agentOid)
                                 { worker with Version = worker.Version + 1; ActiveAttemptId = null } ]
                })

    // Only the assigned reviewer can decide; changes return a new queued generation
    // without pre-empting whatever the implementation agent is currently doing.
    let decide binding operationId taskId reviewId reviewerId expectedReviewVersion verdict feedback =
        if not (validTaskId taskId) || reviewId = Guid.Empty || not (validAgent reviewerId)
           || expectedReviewVersion < 1
           || (verdict = RequestChanges && not (validText 4096 feedback))
           || (not (isNull feedback) && Encoding.UTF8.GetByteCount(feedback) > 4096) then
            Error(InvalidBinding "Invalid review decision or missing changes-requested feedback.")
        else
            let reviewPathValue = reviewPath taskId (reviewId.ToString("N"))
            let decision = match verdict with | Accept -> "Accepted" | RequestChanges -> "ChangesRequested"
            let intent = newIntent operationId reviewerId "ReviewDecision"
                            {| taskId = taskId; reviewId = reviewId; reviewerId = reviewerId
                               expectedReviewVersion = expectedReviewVersion; decision = decision; feedback = feedback |}
            LedgerStore.transact binding intent (fun snapshot ->
                result {
                    let! currentReview, reviewOid = required<ReviewSubmission> binding snapshot reviewPathValue
                    let! task, taskOid = required<ReviewTask> binding snapshot (taskPath taskId)
                    let attempt = attemptPath taskId currentReview.AttemptId
                    let! current, attemptOid = required<ReviewAttempt> binding snapshot attempt
                    if currentReview.ReviewerId <> reviewerId || currentReview.OwnerAgentId = reviewerId then
                        return! Error(ReviewAccessDenied "Only the independently assigned reviewer may decide.")
                    if currentReview.Version <> expectedReviewVersion || currentReview.Status <> "Pending"
                       || task.Lifecycle <> "InProgress" || current.Phase <> "ReadyForReview"
                       || current.AttemptId <> currentReview.AttemptId
                       || current.CurrentReviewId <> currentReview.ReviewId
                       || current.AssignmentGeneration <> currentReview.AssignmentGeneration
                       || current.SubmittedCommit <> currentReview.CommitOid then
                        return! Error(StaleEntityVersion "Review is stale, unauthorized or already decided.")
                    if verdict = Accept then
                        do! LedgerStore.verifySourceObject binding currentReview.CommitOid
                    let reviewed =
                        { currentReview with Version = currentReview.Version + 1; Status = decision
                                             DecisionSequence = snapshot.Header.NextSequence; Feedback = feedback }
                    let nextAttempt =
                        { current with Version = current.Version + 1
                                       Phase = if verdict = Accept then "Accepted" else "Queued"
                                       AssignmentGeneration = if verdict = Accept then current.AssignmentGeneration else current.AssignmentGeneration + 1
                                       CurrentReviewId = if verdict = Accept then current.CurrentReviewId else null
                                       ReviewFeedback = feedback }
                    let changes =
                        [ change reviewPathValue (Some reviewOid) reviewed
                          change attempt (Some attemptOid) nextAttempt ]
                    if verdict = Accept then
                        let accepted = { task with Version = task.Version + 1; Lifecycle = "Implemented" }
                        return changes @ [ change (taskPath taskId) (Some taskOid) accepted ]
                    else
                        return changes
                })

    let getTask binding taskId =
        if not (validTaskId taskId) then
            Error(InvalidBinding "Invalid task ID.")
        else
            result {
                let! snapshot = LedgerStore.fetch binding
                let! task, _ = required<ReviewTask> binding snapshot (taskPath taskId)
                if String.IsNullOrWhiteSpace task.AttemptId then
                    return snapshot, task, None
                else
                    let! attempt, _ = required<ReviewAttempt> binding snapshot (attemptPath taskId task.AttemptId)
                    return snapshot, task, Some attempt
            }

    let getReview binding taskId reviewId =
        if not (validTaskId taskId) || reviewId = Guid.Empty then
            Error(InvalidBinding "Invalid review lookup.")
        else
            result {
                let! snapshot = LedgerStore.fetch binding
                let! review, _ = required<ReviewSubmission> binding snapshot (reviewPath taskId (reviewId.ToString("N")))
                return snapshot, review
            }

    // Reviewer inbox is a projection of durable review records, never a second queue.
    let reviewerQueue binding reviewerId =
        if not (validAgent reviewerId) then
            Error(InvalidBinding "Invalid reviewer identity.")
        else
            result {
                let! snapshot = LedgerStore.fetch binding
                let! paths = LedgerStore.listAt binding snapshot "tasks"
                let candidates = paths |> List.filter (fun path -> path.Contains("/reviews/"))
                let mutable failed = None
                let mutable pending = []
                for path in candidates do
                    if failed.IsNone then
                        match required<ReviewSubmission> binding snapshot path with
                        | Error error -> failed <- Some error
                        | Ok(review, _) when review.ReviewerId = reviewerId && review.Status = "Pending" ->
                            pending <- review :: pending
                        | Ok _ -> ()
                match failed with
                | Some error -> return! Error error
                | None ->
                    return snapshot, (pending |> List.sortBy (fun review -> review.SubmittedSequence, review.TaskId, review.ReviewId))
            }
