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

    [<TestCase(0, 0)>]
    [<TestCase(220, 220)>]
    [<TestCase(2200, 2200)>]
    member _.ReviewerQueueAndSelectedReadsHaveSourcePinnedBoundedGoldenFixtures(taskCount: int, reviewCount: int) =
        withFixture taskCount reviewCount (fun checkout reader ->
            let sampleQueue () =
                let response, _, metric =
                    PerformanceRegression.sample
                        (fun () -> ReviewWorkflow.branchReviewerQueue reader "PerfFixture")
                        (fun result ->
                            let snapshot, reviews = ok result
                            JsonSerializer.Serialize(
                                {| snapshotCommit = snapshot; reviews = reviews |}, jsonOptions))
                let snapshot, reviews = ok response
                Assert.That(metric.GitProcesses, Is.LessThanOrEqualTo(12),
                            "ReviewerInbox may not start one Git child per review.")
                Assert.That(metric.RemoteFetches, Is.LessThanOrEqualTo(1))
                Assert.That(metric.BatchBlobReads, Is.LessThanOrEqualTo(1))
                Assert.That(metric.SingleBlobReads, Is.LessThanOrEqualTo(1))
                Assert.That(metric.GitStdoutBytes,
                            Is.LessThanOrEqualTo(int64 metric.ResultBytes * 32L + 65536L),
                            "ReviewerInbox Git stdout must stay bounded by its response.")
                snapshot, reviews, metric

            let originalHead, originalReviews, cold = sampleQueue()
            Assert.That(originalReviews.Length, Is.EqualTo(reviewCount / 2))
            let warmHead, warmReviews, warm = sampleQueue()
            Assert.That(warmHead, Is.EqualTo(originalHead))
            Assert.That((warmReviews = originalReviews), Is.True)

            if taskCount > 0 then
                let (result, metrics) =
                    LedgerStore.measureGitReads
                        (fun () -> ReviewWorkflow.getTaskWithSpec reader (taskId 1))
                let _, task, attempt, spec, specOid = ok result
                Assert.That(task.TaskId, Is.EqualTo(taskId 1))
                Assert.That(attempt.IsSome, Is.True)
                Assert.That(spec, Does.Contain("Unrelated spec"))
                Assert.That(String.IsNullOrWhiteSpace specOid, Is.False)
                Assert.That(metrics.ProcessStarts, Is.LessThanOrEqualTo(16),
                            "GetTaskWithSpec must have a fixed Git budget independent of task population.")
                Assert.That(metrics.RemoteFetches, Is.LessThanOrEqualTo(1))
                // ReviewDetails pinned entity path and integrity are exercised
                // without inspecting live Git review refs or history.
                let pinned = LedgerStore.fetch reader |> ok
                let review = ReviewWorkflow.getBranchReviewAt reader pinned
                                 (taskId 1) (Guid.ParseExact(reviewId 1, "N"))
                Assert.That((ok review).ReviewId, Is.EqualTo(reviewId 1))

            // An unrelated canonical head advance must not reuse stale
            // membership or introduce per-record Git subprocesses.
            if taskCount > 0 then
                put checkout (taskPath 1) (taskContent 1 "Advanced title" (attemptId 1))
            else
                put checkout "synthetic-advance.txt" "unrelated snapshot advance"
            commitAndPublish checkout
            let advancedHead, advancedReviews, advanced = sampleQueue()
            Assert.That(advancedHead, Is.Not.EqualTo(originalHead))
            Assert.That((advancedReviews = originalReviews), Is.True)
            let warmSamples =
                [ for _ in 1 .. 30 do
                    let _, reviews, metric = sampleQueue()
                    Assert.That((reviews = advancedReviews), Is.True)
                    metric ]
            // The SHA refers to the actual compiled source checkout; the
            // fixture snapshot is separately emitted as a stable golden pin.
            let sourceCheckout = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."))
            let sourceSha = git sourceCheckout [ "rev-parse"; "HEAD" ]
            let report =
                PerformanceRegression.report sourceSha "reviewer-queue"
                    taskCount cold warm advanced warmSamples
            TestContext.Progress.WriteLine(
                JsonSerializer.Serialize(
                    {| sourceSha = sourceSha
                       fixtureHeadBefore = originalHead
                       fixtureHeadAfter = advancedHead
                       reportPath = Path.GetFileName(report)
                       taskCount = taskCount
                       reviewCount = reviewCount |}, jsonOptions)))
