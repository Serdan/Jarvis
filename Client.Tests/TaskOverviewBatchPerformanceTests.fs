module Client.Tests.TaskOverviewBatchPerformanceTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Client.LedgerStore
open Client.ReviewWorkflow
open NUnit.Framework

// Synthetic Git ledger fixtures only. These intentionally use ordinary fixture
// commits to create large immutable snapshots without hundreds of production
// ledger transactions; no live ledger or target ref is modified.
[<TestFixture>]
type TaskOverviewBatchPerformanceTests() =
    let jsonOptions = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

    let entityJson schema item =
        let value = JsonSerializer.Serialize(item, jsonOptions)
        sprintf "{\"schemaVersion\":%d,%s" schema (value.Substring(1))

    let git directory args =
        use proc = new Process()
        proc.StartInfo.FileName <- "git"
        proc.StartInfo.WorkingDirectory <- directory
        proc.StartInfo.RedirectStandardOutput <- true
        proc.StartInfo.RedirectStandardError <- true
        args |> List.iter proc.StartInfo.ArgumentList.Add
        if not (proc.Start()) then failwith "Could not start Git fixture."
        let output = proc.StandardOutput.ReadToEnd()
        let errors = proc.StandardError.ReadToEnd()
        proc.WaitForExit()
        if proc.ExitCode <> 0 then
            failwithf "Git fixture command failed (%A): %s" args errors
        output.Trim()

    let ok = function
        | Ok value -> value
        | Error error -> failwithf "Unexpected Git ledger error: %A" error

    let taskId i = sprintf "overview-%04d" i
    let attemptId (i: int) = i.ToString("x32")
    let reviewId (i: int) = (100000 + i).ToString("x32")
    let taskPath i = sprintf "tasks/%s/task.json" (taskId i)
    let attemptPath i = sprintf "tasks/%s/attempts/%s.json" (taskId i) (attemptId i)
    let reviewPath i owner =
        sprintf "tasks/%s/reviews/%s/review.json" (taskId owner) (reviewId i)

    let taskContent i title currentAttempt =
        let value: ReviewTask =
            { TaskId = taskId i; Title = title; Version = 1
              Lifecycle = "InProgress"; SpecVersion = 1
              AttemptId = currentAttempt }
        entityJson 2 value

    let attemptContent i =
        let value: ReviewAttempt =
            { TaskId = taskId i; AttemptId = attemptId i; Version = 1
              OwnerAgentId = "PerfFixture"; Phase = "Started"
              AssignmentGeneration = 1; Priority = 0; IssuedSequence = int64 i
              SpecBlobOid = null; BaseCommit = null; TargetRef = null
              SourceRepositoryId = null; SourceRemote = null
              SourcePushRemote = null; SourceRef = null }
        entityJson 3 value

    let reviewContent i owner =
        let value: BranchReview =
            { ReviewId = reviewId i; TaskId = taskId owner
              AttemptId = attemptId owner; OwnerAgentId = "PerfFixture"
              Kind = "Implementation"; TaskRef = null; ReviewRef = null
              SourceVersion = 1; ReviewedHeadOid = null; BaseCommit = null
              TargetRef = null; Status = if i % 2 = 0 then "Accepted" else "Unclaimed"
              ReviewerId = null; Version = 1; CreatedSequence = int64 i
              DecisionSequence = 0L; Acknowledged = false }
        entityJson 3 value

    let put (root: string) (relative: string) (content: string) =
        let path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))
        Directory.CreateDirectory(Path.GetDirectoryName(path)) |> ignore
        File.WriteAllText(path, content)

    let commitAndPublish checkout =
        git checkout [ "add"; "-A" ] |> ignore
        git checkout [ "commit"; "-m"; "Synthetic task overview fixture" ] |> ignore
        git checkout [ "push"; "origin"; "HEAD:refs/heads/jarvis-orchestration" ] |> ignore

    let withFixture taskCount reviewCount action =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-task-overview-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let bare = Path.Combine(root, "remote.git")
        let owner = Path.Combine(root, "owner")
        let reader = Path.Combine(root, "reader")
        let ledgerCheckout = Path.Combine(root, "ledger")
        try
            git root [ "init"; "--bare"; bare ] |> ignore
            git root [ "clone"; bare; owner ] |> ignore
            git owner [ "config"; "user.name"; "Overview Fixture" ] |> ignore
            git owner [ "config"; "user.email"; "overview@example.test" ] |> ignore
            put owner "source.txt" "unrelated source branch"
            git owner [ "add"; "source.txt" ] |> ignore
            git owner [ "commit"; "-m"; "source base" ] |> ignore
            git owner [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
            git root [ "clone"; bare; reader ] |> ignore
            let identity = Guid.NewGuid()
            let ledgerId = Guid.NewGuid()
            let binding path : LedgerBinding =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = bare; ApprovedPushUrl = bare
                  ProjectId = identity; LedgerId = ledgerId; Epoch = 1 }
            let first, second = binding owner, binding reader
            LedgerStore.initialize first true |> ok |> ignore
            git owner [ "fetch"; "origin"; "refs/heads/jarvis-orchestration:refs/remotes/origin/jarvis-orchestration" ] |> ignore
            git owner [ "worktree"; "add"; "-b"; "overview-ledger-fixture"; ledgerCheckout
                        "refs/remotes/origin/jarvis-orchestration" ] |> ignore
            git ledgerCheckout [ "config"; "user.name"; "Overview Fixture" ] |> ignore
            git ledgerCheckout [ "config"; "user.email"; "overview@example.test" ] |> ignore
            for i in 1 .. taskCount do
                put ledgerCheckout (taskPath i) (taskContent i (sprintf "Task %04d" i) (attemptId i))
                put ledgerCheckout (attemptPath i) (attemptContent i)
                // These must not be materialized by the overview reader.
                put ledgerCheckout (sprintf "tasks/%s/spec.html" (taskId i)) "<article>Unrelated spec</article>"
                put ledgerCheckout
                    (sprintf "tasks/%s/attempts/%s/events/1.json" (taskId i) (attemptId i))
                    "{\"schemaVersion\":2,\"unrelated\":true}"
            for i in 1 .. reviewCount do
                let ownerIndex = 1 + ((i - 1) % taskCount)
                put ledgerCheckout (reviewPath i ownerIndex) (reviewContent i ownerIndex)
                put ledgerCheckout
                    (sprintf "tasks/%s/reviews/%s/summary.md" (taskId ownerIndex) (reviewId i))
                    "# Unrelated review history"
            // A malformed unrelated historical integration must not be decoded.
            put ledgerCheckout
                (sprintf "tasks/%s/integrations/%s.json" (taskId 1) ((900000).ToString("x32")))
                "{}"
            commitAndPublish ledgerCheckout
            action ledgerCheckout second
        finally
            if Directory.Exists ledgerCheckout then
                try git owner [ "worktree"; "remove"; "--force"; ledgerCheckout ] |> ignore
                with _ -> ()
            for checkout in [ owner; reader ] do
                let config = Client.OrchestrationApi.LedgerBindingConfig.pathForRoot checkout
                let pending = Path.Combine(Path.GetDirectoryName config, "pending", Path.GetFileNameWithoutExtension config)
                if Directory.Exists pending then Directory.Delete(pending, true)
            if Directory.Exists root then Directory.Delete(root, true)

    let readMeasured binding label =
        let before = GC.GetAllocatedBytesForCurrentThread()
        let clock = Stopwatch.StartNew()
        let result, metrics =
            LedgerStore.measureGitReads (fun () -> ReviewWorkflow.listProjectTasks binding)
        clock.Stop()
        let allocated = GC.GetAllocatedBytesForCurrentThread() - before
        TestContext.Progress.WriteLine(
            sprintf "%s: %.2f ms, %d allocated bytes, Git processes=%d fetches=%d individual=%d batches=%d output=%d"
                label clock.Elapsed.TotalMilliseconds allocated
                metrics.ProcessStarts metrics.RemoteFetches
                metrics.IndividualBlobReads metrics.BatchBlobReads metrics.OutputBytes)
        ok result, metrics

    let bounded (metrics: LedgerStore.GitReadMetrics) =
        // A trusted warm fetch adds ancestry verification; the remaining
        // constant work is two scoped tree listings and at most two batches.
        Assert.That(metrics.ProcessStarts, Is.LessThanOrEqualTo(14), "TaskOverview must not start per-entity Git processes.")
        Assert.That(metrics.RemoteFetches, Is.LessThanOrEqualTo(1), "Exactly one head fetch is enough.")
        Assert.That(metrics.BatchBlobReads, Is.LessThanOrEqualTo(2), "Task/review and current-attempt batch only.")
        Assert.That(metrics.IndividualBlobReads, Is.LessThanOrEqualTo(2), "Only canonical header may use a single-blob read.")

    // Preserve the original source-pinned per-entity reader as a test oracle.
    // It is deliberately outside the optimized reader's metrics scope.
    let legacyOverview (binding: LedgerBinding) =
        let snapshot = LedgerStore.fetch binding |> ok
        let paths = LedgerStore.listAt binding snapshot "tasks" |> ok
        let contentAt path =
            let content, oid = LedgerStore.readAt binding snapshot path |> ok
            match content, oid with
            | Some value, Some _ -> value
            | _ -> failwithf "Missing oracle entity %s" path
        let mutable tasks = []
        let mutable reviews = []
        for path in paths do
            if path.EndsWith("/task.json", StringComparison.Ordinal) then
                let task = JsonSerializer.Deserialize<ReviewTask>(contentAt path, jsonOptions)
                let attempt =
                    if String.IsNullOrWhiteSpace task.AttemptId then None
                    else Some (JsonSerializer.Deserialize<ReviewAttempt>(
                        contentAt (sprintf "tasks/%s/attempts/%s.json" task.TaskId task.AttemptId), jsonOptions))
                tasks <- {| Task = task; Attempt = attempt |} :: tasks
            elif path.EndsWith("/review.json", StringComparison.Ordinal) then
                reviews <- JsonSerializer.Deserialize<BranchReview>(contentAt path, jsonOptions) :: reviews
        {| SnapshotCommit = snapshot.Commit
           Tasks = tasks |> List.sortBy (fun item -> item.Task.TaskId)
           Reviews = reviews |> List.sortBy (fun item -> item.CreatedSequence, item.ReviewId) |}

    [<Test>]
    member _.LargeColdWarmAndAdvancedHeadStayBoundedAndEquivalent() =
        withFixture 281 530 (fun checkout reader ->
            let cold, coldMetrics = readMeasured reader "cold 281 tasks/281 attempts/530 reviews"
            Assert.That(cold.Tasks.Length, Is.EqualTo(281))
            Assert.That(cold.Reviews.Length, Is.EqualTo(530))
            Assert.That(cold.Tasks |> List.forall (fun item -> item.Attempt.IsSome), Is.True)
            bounded coldMetrics
            let warm, warmMetrics = readMeasured reader "warm 281 tasks/281 attempts/530 reviews"
            Assert.That(warm, Is.EqualTo(cold))
            bounded warmMetrics

            put checkout (taskPath 1) (taskContent 1 "Changed after the first read" (attemptId 1))
            put checkout (taskPath 282) (taskContent 282 "Newly created task" (attemptId 282))
            put checkout (attemptPath 282) (attemptContent 282)
            put checkout (reviewPath 531 282) (reviewContent 531 282)
            commitAndPublish checkout
            let advanced, advancedMetrics = readMeasured reader "advanced remote head"
            Assert.That(advanced.SnapshotCommit, Is.Not.EqualTo(cold.SnapshotCommit))
            Assert.That(advanced.Tasks.Length, Is.EqualTo(282))
            Assert.That(advanced.Reviews.Length, Is.EqualTo(531))
            Assert.That(advanced.Tasks.Head.Task.Title, Is.EqualTo("Changed after the first read"))
            bounded advancedMetrics)

    [<Test>]
    member _.BatchedOverviewExactlyMatchesOriginalPinnedReadModel() =
        withFixture 32 70 (fun _ reader ->
            let optimized = ReviewWorkflow.listProjectTasks reader |> ok
            let reference = legacyOverview reader
            Assert.That(optimized.SnapshotCommit, Is.EqualTo(reference.SnapshotCommit))
            Assert.That(JsonSerializer.Serialize(optimized.Tasks, jsonOptions),
                        Is.EqualTo(JsonSerializer.Serialize(reference.Tasks, jsonOptions)))
            Assert.That(JsonSerializer.Serialize(optimized.Reviews, jsonOptions),
                        Is.EqualTo(JsonSerializer.Serialize(reference.Reviews, jsonOptions))))

    [<Test>]
    member _.MissingLinkedAttemptAndMalformedSelectedTaskFailClosed() =
        withFixture 3 3 (fun checkout reader ->
            let original, _ = readMeasured reader "small clean fixture"
            Assert.That(original.Tasks.Length, Is.EqualTo(3))
            File.Delete(Path.Combine(checkout, attemptPath 2))
            commitAndPublish checkout
            match ReviewWorkflow.listProjectTasks reader with
            | Error(StaleEntityVersion message) ->
                Assert.That(message, Does.Contain(attemptPath 2))
            | result -> Assert.Fail(sprintf "Missing linked attempt was accepted: %A" result)
            put checkout (attemptPath 2) (attemptContent 2)
            put checkout (taskPath 1) "{\"schemaVersion\":2,\"taskId\":\"overview-0001\"}"
            commitAndPublish checkout
            match ReviewWorkflow.listProjectTasks reader with
            | Error(LedgerIntegrityError message) ->
                Assert.That(message, Does.Contain(taskPath 1))
            | result -> Assert.Fail(sprintf "Malformed canonical task was accepted: %A" result))

    [<TestCase("100755")>]
    [<TestCase("120000")>]
    [<TestCase("160000")>]
    member _.SelectedNonRegularGitModesFailClosed(mode: string) =
        withFixture 2 2 (fun checkout reader ->
            let relative = taskPath 1
            let objectId =
                if mode = "160000" then git checkout [ "rev-parse"; "HEAD" ]
                else git checkout [ "hash-object"; "-w"; "--"; relative ]
            git checkout [ "update-index"; "--add"; "--cacheinfo"; sprintf "%s,%s,%s" mode objectId relative ] |> ignore
            git checkout [ "commit"; "-m"; "Adversarial selected object mode" ] |> ignore
            git checkout [ "push"; "origin"; "HEAD:refs/heads/jarvis-orchestration" ] |> ignore
            match ReviewWorkflow.listProjectTasks reader with
            | Error(LedgerIntegrityError _) -> ()
            | result -> Assert.Fail(sprintf "Selected %s object mode was accepted: %A" mode result))

    [<Test>]
    member _.InvalidCurrentAttemptIdFailsClosedInsteadOfSkipping() =
        withFixture 2 2 (fun checkout reader ->
            put checkout (taskPath 1) (taskContent 1 "Unsafe attempt locator" "../invalid")
            commitAndPublish checkout
            match ReviewWorkflow.listProjectTasks reader with
            | Error(InvalidBinding _) -> ()
            | result -> Assert.Fail(sprintf "Invalid linked attempt locator was accepted: %A" result))

    [<Test>]
    member _.LargeMixedHistoryUsesActualAuthenticatedBridgeAndPinnedPages() =
        withFixture 1500 2000 (fun checkout reader ->
            // Most records are history. Preserve explicit current blockers,
            // null attempts, and unresolved review work for a closed task.
            let taskAt i =
                JsonSerializer.Deserialize<ReviewTask>(File.ReadAllText(Path.Combine(checkout, taskPath i)), jsonOptions)
            let attemptAt i =
                JsonSerializer.Deserialize<ReviewAttempt>(File.ReadAllText(Path.Combine(checkout, attemptPath i)), jsonOptions)
            let reviewAt i owner =
                JsonSerializer.Deserialize<BranchReview>(File.ReadAllText(Path.Combine(checkout, reviewPath i owner)), jsonOptions)
            for i in 1 .. 1500 do
                let t, a = taskAt i, attemptAt i
                let lifecycle = if i <= 4 then "InProgress" else "Closed"
                let phase = if i = 1 then "Blocked" elif i = 2 then "Deferred" elif i <= 4 then "Started" else "Finished"
                put checkout (taskPath i) (entityJson 2 { t with Lifecycle = lifecycle })
                put checkout (attemptPath i) (entityJson 3 { a with Phase = phase })
            put checkout (taskPath 4) (entityJson 2 { taskAt 4 with Lifecycle = "Open"; AttemptId = null })
            for i in 1 .. 2000 do
                let owner = 1 + ((i - 1) % 1500)
                let r = reviewAt i owner
                let status = if i = 5 then "Unclaimed" elif i = 6 then "Claimed" else "Accepted"
                put checkout (reviewPath i owner)
                    (entityJson 3 { r with Status = status; Acknowledged = i <> 3 && i <> 5 && i <> 6
                                           ReviewerId = if i = 6 then "Reviewer" else null })
            commitAndPublish checkout

            let before = LedgerStore.fetch reader |> ok
            let root = Path.GetDirectoryName reader.RepositoryPath
            let rt = Client.Runtime(root, Client.ConsoleTui.ConsoleTui(), Client.PartialTrust)
            let bind = Client.OrchestrationApi.LedgerBindingConfig.bindExisting "reader" rt
            bind reader.ProjectId reader.LedgerId reader.Epoch reader.RemoteName
                reader.ApprovedFetchUrl reader.ApprovedPushUrl before.Commit true
            |> function Ok _ -> () | Error e -> failwithf "Binding failed: %A" e
            let config = Client.OrchestrationApi.LedgerBindingConfig.pathForRoot reader.RepositoryPath
            try
                let command request =
                    Common.ReviewReadCommand
                        { ProjectName = "reader"; Action = "ListProjectTasks"; TaskId = ""
                          RequestId = ""; ReviewerId = ""; DocumentPath = ""; TaskOverview = request }
                let invoke request =
                    let payload = JsonSerializer.Serialize(command request)
                    Client.SignalR.Client.executeAuthenticatedCommandWithChallengeWake
                        rt payload "PerfFixture" None false (fun _ _ -> ())
                    |> Async.AwaitTask |> Async.RunSynchronously
                let parse (response: Common.AgentCommandResponse) =
                    Assert.That(response.Error.IsNone, Is.True, sprintf "%A" response.Error)
                    Assert.That(response.Result.IsSome, Is.True)
                    JsonSerializer.Deserialize<Client.TaskOverviewProjection.Page>(response.Result.Value)
                let unbounded = ReviewWorkflow.listProjectTasks reader |> ok
                let oldPayload = JsonSerializer.Serialize unbounded
                Assert.That(System.Text.Encoding.UTF8.GetByteCount oldPayload,
                            Is.GreaterThan(Common.AgentProtocol.maxResponseBytes))
                let dispatched: Client.SignalR.Client.DispatchedResult =
                    { Payload = oldPayload; ChangedAgentIds = [] }
                match (Client.SignalR.Client.toAgentResponse (command None) [] [] None (Ok dispatched)).Error with
                | Some(Common.OutputTruncated _) -> ()
                | e -> Assert.Fail(sprintf "Legacy overview bypassed actual bridge cap: %A" e)

                let first = invoke None |> parse
                Assert.That(first.SnapshotCommit, Is.EqualTo(before.Commit))
                Assert.That(first.TotalTasks, Is.EqualTo(1500))
                Assert.That(first.TotalReviews, Is.EqualTo(2000))
                Assert.That(first.HasMore, Is.False)
                Assert.That(first.Tasks |> List.map (fun x -> x.Task.TaskId), Is.EqualTo(box [ for i in 1 .. 6 -> taskId i ]))
                Assert.That(first.Reviews |> List.map (fun x -> x.ReviewId), Is.EqualTo(box [ for i in [3;5;6] -> reviewId i ]))
                Assert.That(first.Reviews |> List.forall (fun x -> x.Detail.IsNone), Is.True)
                Assert.That((LedgerStore.fetch reader |> ok).Commit, Is.EqualTo(before.Commit))

                let request = { Common.TaskOverviewRequest.defaultValue with Scope = "history"; PageSize = 100 }
                let historyFirst = invoke (Some request) |> parse
                Assert.That(historyFirst.HasMore, Is.True)
                Assert.That(historyFirst.MatchingTasks, Is.EqualTo(1500))
                Assert.That(historyFirst.MatchingReviews, Is.EqualTo(2000))

                let reject request =
                    let response = invoke (Some request)
                    Assert.That(response.Error.IsSome, Is.True)
                    Assert.That(response.Result.IsNone, Is.True)
                reject { request with Cursor = historyFirst.NextCursor; Assignee = "different" }
                reject { request with Cursor = historyFirst.NextCursor + "!" }
                reject { request with Scope = "unknown" }
                reject { request with PageSize = 101 }
                reject { request with SinceSequence = -2L }

                // A later canonical commit must not shift a pinned continuation.
                put checkout (taskPath 1) (entityJson 2 { taskAt 1 with Title = "Later title" })
                commitAndPublish checkout
                let fresh = invoke None |> parse
                Assert.That(fresh.SnapshotCommit, Is.Not.EqualTo(before.Commit))
                Assert.That(fresh.Tasks.Head.Task.Title, Is.EqualTo("Later title"))

                let rec enumerate (page: Client.TaskOverviewProjection.Page) tasks reviews =
                    Assert.That(page.SnapshotCommit, Is.EqualTo(before.Commit))
                    let tasks = List.rev page.Tasks @ tasks
                    let reviews = List.rev page.Reviews @ reviews
                    if page.HasMore then
                        Assert.That(page.NextCursor, Is.Not.Null.And.Not.Empty)
                        enumerate (invoke (Some { request with Cursor = page.NextCursor }) |> parse) tasks reviews
                    else List.rev tasks, List.rev reviews
                let tasks, reviews = enumerate historyFirst [] []
                Assert.That(tasks |> List.map (fun t -> t.Task, t.Attempt),
                            Is.EqualTo(box (unbounded.Tasks |> List.map (fun t -> t.Task, t.Attempt))))
                Assert.That(reviews |> List.map (fun r -> r.Detail.Value), Is.EqualTo(box unbounded.Reviews))
            finally
                if File.Exists config then File.Delete config)
