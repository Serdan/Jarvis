module TaskOverviewProjectionTests

open System
open System.Text.Json
open Client.ReviewWorkflow
open Client.TaskOverviewProjection

let check name condition = if not condition then failwith name
let require = function Ok x -> x | Error e -> failwithf "%A" e
let task i lifecycle phase : TaskItem =
    let id = sprintf "t%06d" i
    let attemptId = if Option.isSome phase then id + "-attempt" else null
    { Task = { TaskId = id; Title = "task " + id; Version = 3
               Lifecycle = lifecycle; SpecVersion = 2; AttemptId = attemptId }
      Attempt = phase |> Option.map (fun p ->
          { TaskId = id; AttemptId = attemptId; Version = 7
            OwnerAgentId = "Worker 6"; Phase = p; AssignmentGeneration = 2
            Priority = 0; IssuedSequence = int64 i; SpecBlobOid = "spec"
            BaseCommit = "base"; TargetRef = "refs/heads/main"
            SourceRepositoryId = "repo"; SourceRemote = "remote"
            SourcePushRemote = "remote"; SourceRef = "refs/heads/feature" }) }

let review i (t: TaskItem) status acknowledged : BranchReview =
    { ReviewId = sprintf "r%06d" i; TaskId = t.Task.TaskId
      AttemptId = t.Task.AttemptId; OwnerAgentId = "Worker 6"; Kind = "Implementation"
      TaskRef = "refs/heads/task"; ReviewRef = "refs/heads/review"
      SourceVersion = 3; ReviewedHeadOid = "head"; BaseCommit = "base"
      TargetRef = "refs/heads/main"; Status = status; ReviewerId = "Reviewer 1"
      Version = 2; CreatedSequence = int64 (i / 2)
      DecisionSequence = int64 (i + 100); Acknowledged = acknowledged }

let cursor i = sprintf "snapshot-query-offset-%d" i
let page size offset p = selectPage cursor size offset p |> require
let bytes (p: Page) = JsonSerializer.SerializeToUtf8Bytes(p).Length

let enumerate size p =
    let rec loop offset pages =
        let x = page size offset p
        let count = x.Tasks.Length + x.Reviews.Length
        check "snapshot" (x.SnapshotCommit = p.SnapshotCommit)
        check "count bound" (count <= size)
        check "byte bound" (bytes x <= PageByteBudget)
        check "matching totals" (x.MatchingTasks = p.Tasks.Length && x.MatchingReviews = p.Reviews.Length)
        if x.HasMore then
            check "nonempty continuation" (count > 0 && x.NextCursor = cursor (offset + count))
            loop (offset + count) (x :: pages)
        else
            check "terminal cursor" (isNull x.NextCursor)
            List.rev (x :: pages)
    loop 0 []

[<EntryPoint>]
let main _ =
    let active =
        [ task 1 "Open" None
          task 2 "InProgress" (Some "Started")
          task 3 "InProgress" (Some "Blocked")
          task 4 "InProgress" (Some "Deferred")
          task 5 "InProgress" (Some "Queued")
          task 6 "Implemented" (Some "Finished")
          task 7 "Integrated" (Some "Finished")
          task 8 "FutureLifecycle" (Some "FuturePhase")
          task 9 "Closed" (Some "Blocked") ]
    let history =
        [ for i in 10..1509 ->
            let t = task i (if i % 2 = 0 then "Closed" else "Cancelled") (Some "Finished")
            { t with Task = { t.Task with Title = String.replicate 220 "\"<&" } } ]
    let tasks = List.rev (active @ history)
    let oldReviews =
        [ for i in 10..2009 ->
            review i history[(i - 10) % history.Length] "Accepted" false ]
    let linkedTerminal = history.Head
    let relevant =
        [ review 1 active[1] "Unclaimed" false
          review 2 active[2] "Claimed" false
          review 3 active[2] "ChangesRequested" false
          review 4 active[3] "Accepted" true
          review 5 linkedTerminal "Unclaimed" false
          review 6 active[7] "FutureStatus" false
          review 7 active[4] "Withdrawn" false ]
    let reviews = List.rev (relevant @ oldReviews)
    let snapshot = "pinned-head"
    let p = project snapshot defaultQuery tasks reviews
    check "pertinent task IDs" (p.Tasks |> Array.map (fun t -> t.Task.TaskId) =
        (active @ [linkedTerminal] |> List.map (fun t -> t.Task.TaskId) |> List.toArray))
    check "pertinent reviews" (p.Reviews |> Array.map (fun r -> r.ReviewId) =
        (relevant |> List.filter (fun r -> List.contains r.Status ["Unclaimed";"Claimed";"ChangesRequested";"FutureStatus"])
                  |> List.map (fun r -> r.ReviewId) |> List.toArray))
    check "no default provenance" (p.Reviews |> Array.forall (fun r -> Option.isNone r.Detail))
    check "null attempt retained" (Option.isNone p.Tasks[0].Attempt)
    check "overall totals" (p.TotalTasks = tasks.Length && p.TotalReviews = reviews.Length)
    let first = page 0 0 p
    check "default entire frontier" (not first.HasMore && bytes first < 32768)
    let smaller = project snapshot defaultQuery (List.rev (active @ [linkedTerminal])) relevant
    check "history affects counts, not membership" (p.Tasks = smaller.Tasks && p.Reviews = smaller.Reviews)
    let blocked = project snapshot { defaultQuery with Phase = Some "Blocked" } tasks reviews
    check "phase filters" (blocked.Tasks.Length = 2 && blocked.Reviews.Length = 2)
    let claimed = project snapshot { defaultQuery with ReviewStatus = Some "Claimed"; Assignee = Some "Worker 6" } tasks reviews
    check "review status/assignee" (claimed.Reviews.Length = 1)
    let recent = project snapshot { defaultQuery with SinceSequence = Some 105L } tasks reviews
    check "explicit sequence semantics" (recent.Tasks.Length = 0 && recent.Reviews.Length = 2)
    let historical = project snapshot { defaultQuery with Scope = History } tasks reviews
    let legacyBytes = JsonSerializer.SerializeToUtf8Bytes({| SnapshotCommit = snapshot; Tasks = tasks; Reviews = reviews |}).Length
    check "fixture exceeds actual bridge cap" (legacyBytes > 921600)
    let pages = enumerate 100 historical
    let allTasks = pages |> List.collect (fun x -> x.Tasks)
    let allReviews = pages |> List.collect (fun x -> x.Reviews) |> List.map (fun r -> r.Detail.Value)
    check "historical task equivalence" (allTasks = (tasks |> List.sortBy (fun t -> t.Task.TaskId)))
    check "historical review equivalence" (allReviews = (reviews |> List.sortBy (fun r -> r.CreatedSequence, r.ReviewId)))
    check "invalid negative offset" (selectPage cursor 10 -1 p = Error InvalidOffset)
    check "invalid terminal offset" (selectPage cursor 10 (p.Tasks.Length + p.Reviews.Length) p = Error InvalidOffset)
    check "invalid size" (selectPage cursor 101 0 p = Error InvalidPageSize)
    let empty = project snapshot defaultQuery [] []
    check "empty page" ((page 0 0 empty).Tasks.IsEmpty && not (page 0 0 empty).HasMore)
    let one = project snapshot defaultQuery [active.Head] []
    check "exact item boundary" (not (page 1 0 one).HasMore)
    let giant = { active.Head with Task = { active.Head.Task with Title = String.replicate PageByteBudget "<" } }
    check "oversized record explicit" (selectPage cursor 0 0 (project snapshot defaultQuery [giant] [])
        = Error (ItemTooLarge("task", giant.Task.TaskId)))
    let largeRows = [for i in 1..4 -> let t = task i "Open" None in {t with Task = {t.Task with Title = String.replicate 30000 "<"}}]
    let bytePages = enumerate 100 (project snapshot defaultQuery largeRows [])
    check "bytes limit before count" (bytePages.Length > 1)
    printfn "Projection passed: %d tasks, %d reviews, legacy %d bytes, %d history pages, default %d bytes." tasks.Length reviews.Length legacyBytes pages.Length (bytes first)
    0
