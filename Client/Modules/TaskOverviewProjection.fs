module Client.TaskOverviewProjection

open System
open System.Text.Json
open Client.ReviewWorkflow

/// A pure projection over one verified canonical reader result. No fetch,
/// activation, mutation or cursor authentication occurs here.
type Scope =
    | Pertinent
    | History

type Query =
    { Scope: Scope
      Lifecycle: string option
      Phase: string option
      Assignee: string option
      ReviewStatus: string option
      SinceSequence: int64 option }

let defaultQuery =
    { Scope = Pertinent; Lifecycle = None; Phase = None; Assignee = None
      ReviewStatus = None; SinceSequence = None }

type TaskItem =
    { Task: ReviewTask
      Attempt: ReviewAttempt option }

/// Full branch/ref/OID provenance is opt-in. IDs, versions and review
/// authority are retained in compact mode. Review detail remains targeted.
type ReviewItem =
    { ReviewId: string
      TaskId: string
      AttemptId: string
      OwnerAgentId: string
      Kind: string
      Status: string
      ReviewerId: string
      Version: int
      SourceVersion: int
      CreatedSequence: int64
      DecisionSequence: int64
      Acknowledged: bool
      Detail: BranchReview option }

type Projection =
    { SnapshotCommit: string
      TotalTasks: int
      TotalReviews: int
      Tasks: TaskItem array
      Reviews: ReviewItem array }

type Page =
    { SnapshotCommit: string
      TotalTasks: int
      TotalReviews: int
      MatchingTasks: int
      MatchingReviews: int
      Offset: int
      Tasks: TaskItem list
      Reviews: ReviewItem list
      HasMore: bool
      NextCursor: string }

type PageError =
    | InvalidPageSize
    | InvalidOffset
    | PageEnvelopeTooLarge
    | ItemTooLarge of kind: string * id: string

[<Literal>]
let DefaultPageSize = 50

[<Literal>]
let MaximumPageSize = 100

// Reserve at least half the actual 900 KiB bridge cap for framing,
// escaping and surrounding tool metadata. The composed route must test it.
[<Literal>]
let PageByteBudget = 460800

let private terminalTask (task: ReviewTask) =
    task.Lifecycle = "Closed" || task.Lifecycle = "Cancelled"

let private terminalAttempt (attempt: ReviewAttempt) =
    List.contains attempt.Phase [ "Finished"; "Abandoned"; "Superseded"; "Cancelled" ]

let private matches expected actual =
    expected |> Option.forall ((=) actual)

let private matchesAttempt expected getter (attempt: ReviewAttempt option) =
    expected |> Option.forall (fun value -> attempt |> Option.exists (fun a -> getter a = value))

let private recent minimum sequence =
    minimum |> Option.forall (fun n -> sequence >= n)

let private compact includeDetail (r: BranchReview) : ReviewItem =
    { ReviewId = r.ReviewId; TaskId = r.TaskId; AttemptId = r.AttemptId
      OwnerAgentId = r.OwnerAgentId; Kind = r.Kind; Status = r.Status
      ReviewerId = r.ReviewerId; Version = r.Version; SourceVersion = r.SourceVersion
      CreatedSequence = r.CreatedSequence; DecisionSequence = r.DecisionSequence
      Acknowledged = r.Acknowledged
      Detail = if includeDetail then Some r else None }

/// Explicit filters narrow scope; History is required for terminal history.
/// SinceSequence uses issued sequence for attempts and the maximum of creation
/// and decision sequence for reviews. Tasks without attempts have no such
/// sequence and are excluded only when this filter is explicitly supplied.
/// Unknown state is retained by default rather than classified as terminal.
let project (snapshotCommit: string) (query: Query) (tasks: TaskItem list) (reviews: BranchReview list) =
    let byId = tasks |> Seq.map (fun t -> t.Task.TaskId, t) |> Map.ofSeq
    let currentTaskReview (r: BranchReview) =
        match Map.tryFind r.TaskId byId with
        | None -> true // inconsistent/orphaned evidence must remain visible
        | Some t ->
            not (terminalTask t.Task)
            && (String.IsNullOrWhiteSpace r.AttemptId || r.AttemptId = t.Task.AttemptId)
    let pertinentReview (r: BranchReview) =
        match r.Status with
        | "Withdrawn" -> false
        | "Accepted" | "ChangesRequested" -> not r.Acknowledged && currentTaskReview r
        | _ -> true // includes Unclaimed, Claimed and unknown states
    let relevantReviews =
        reviews |> List.filter (fun r -> query.Scope = History || pertinentReview r)
    let reviewTaskIds = relevantReviews |> Seq.map (fun r -> r.TaskId) |> Set.ofSeq
    let taskScope t =
        query.Scope = History
        || not (terminalTask t.Task)
        || (t.Attempt |> Option.exists (terminalAttempt >> not))
        || Set.contains t.Task.TaskId reviewTaskIds
    let filteredTasks =
        tasks
        |> List.filter (fun t ->
            taskScope t
            && matches query.Lifecycle t.Task.Lifecycle
            && matchesAttempt query.Phase (fun a -> a.Phase) t.Attempt
            && matchesAttempt query.Assignee (fun a -> a.OwnerAgentId) t.Attempt
            && (query.SinceSequence |> Option.forall (fun n ->
                t.Attempt |> Option.exists (fun a -> a.IssuedSequence >= n))))
        |> List.sortBy (fun t -> t.Task.TaskId)
        |> List.toArray
    let filteredReviews =
        relevantReviews
        |> List.filter (fun r ->
            matches query.ReviewStatus r.Status
            && matches query.Assignee r.OwnerAgentId
            && recent query.SinceSequence (max r.CreatedSequence r.DecisionSequence)
            && (query.Lifecycle |> Option.forall (fun lifecycle ->
                Map.tryFind r.TaskId byId |> Option.exists (fun t -> t.Task.Lifecycle = lifecycle)))
            && (query.Phase |> Option.forall (fun phase ->
                Map.tryFind r.TaskId byId |> Option.exists (fun t ->
                    t.Task.AttemptId = r.AttemptId
                    && (t.Attempt |> Option.exists (fun a -> a.Phase = phase))))))
        |> List.sortBy (fun r -> r.CreatedSequence, r.ReviewId)
        |> List.map (compact (query.Scope = History))
        |> List.toArray
    { SnapshotCommit = snapshotCommit
      TotalTasks = tasks.Length; TotalReviews = reviews.Length
      Tasks = filteredTasks; Reviews = filteredReviews }

/// Page selection over the pinned, sorted projection. The caller binds an
/// opaque cursor to the verified snapshot, query, ledger identity and expiry;
/// this function only accepts the decoded offset and emits the actual cursor
/// via the supplied encoder, so its bytes count toward the page budget.
let selectPage (encodeCursor: int -> string) pageSize offset (projection: Projection) =
    let size = if pageSize = 0 then DefaultPageSize else pageSize
    let taskCount = projection.Tasks.Length
    let reviewCount = projection.Reviews.Length
    let total = taskCount + reviewCount
    let page reversedTasks reversedReviews consumed =
        let next = offset + consumed
        let more = next < total
        { SnapshotCommit = projection.SnapshotCommit
          TotalTasks = projection.TotalTasks; TotalReviews = projection.TotalReviews
          MatchingTasks = taskCount; MatchingReviews = reviewCount
          Offset = offset; Tasks = List.rev reversedTasks; Reviews = List.rev reversedReviews
          HasMore = more; NextCursor = if more then encodeCursor next else null }
    let fits (p: Page) = JsonSerializer.SerializeToUtf8Bytes(p).Length <= PageByteBudget
    if size < 1 || size > MaximumPageSize then Error InvalidPageSize
    elif offset < 0 || (offset >= total && offset <> 0) then Error InvalidOffset
    else
        let rec collect tasks reviews count =
            let position = offset + count
            if position >= total || count >= size then Ok (page tasks reviews count)
            else
                let ts, rs, kind, id =
                    if position < taskCount then
                        let t = projection.Tasks[position]
                        t :: tasks, reviews, "task", t.Task.TaskId
                    else
                        let r = projection.Reviews[position - taskCount]
                        tasks, r :: reviews, "review", r.ReviewId
                let candidate = page ts rs (count + 1)
                if fits candidate then collect ts rs (count + 1)
                elif count = 0 then Error (ItemTooLarge(kind, id))
                else Ok (page tasks reviews count)
        if total = 0 then
            let empty = page [] [] 0
            if fits empty then Ok empty else Error PageEnvelopeTooLarge
        else collect [] [] 0
