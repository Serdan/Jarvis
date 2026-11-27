module ReviewerQueueProjection20261127

open System
open System.Collections
open System.IO
open System.Text
open System.Text.Json
open NUnit.Framework
open Common
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.OrchestrationApi
open AutomationHealthHostedFixture

let withFixture action =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-reviewer-queue-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    let inheritedGit =
        Environment.GetEnvironmentVariables()
        |> Seq.cast<DictionaryEntry>
        |> Seq.map (fun entry -> string entry.Key)
        |> Seq.filter (fun key -> key.StartsWith("GIT_", StringComparison.Ordinal))
        |> Seq.toList
    let original =
        ("XDG_CONFIG_HOME" :: "GIT_CONFIG_NOSYSTEM" :: "GIT_CONFIG_GLOBAL" :: inheritedGit)
        |> List.distinct
        |> List.map (fun key -> key, Environment.GetEnvironmentVariable(key))
    try
        Assert.That(OperatingSystem.IsLinux(), Is.True)
        for key in inheritedGit do Environment.SetEnvironmentVariable(key, null)
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(root, "config"))
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1")
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", Path.Combine(root, "gitconfig"))
        let first, second, _, reader, _ = create root
        action first second reader
    finally
        for key, value in original do Environment.SetEnvironmentVariable(key, value)
        Directory.Delete(root, true)

let reviewer = "Reviewer fixture"
let other = "Other reviewer"
let foreign = "Foreign worker"
let bytes (s: string) = Encoding.UTF8.GetByteCount s
let head binding = (LedgerStore.fetch binding |> success).Commit
let path (r: BranchReview) = $"tasks/{r.TaskId}/reviews/{r.ReviewId}/review.json"
let entity (r: BranchReview) =
    let json = JsonSerializer.Serialize(r, JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase))
    "{\"schemaVersion\":3," + json.Substring(1)
let api reader action taskId requestId subject document =
    let cmd : ReviewReadCommand =
        { ProjectName = "Fixture"; Action = action; TaskId = taskId; RequestId = requestId
          ReviewerId = subject; DocumentPath = document }
    ReviewApi.read cmd reader
let queue reader = api reader "ReviewerQueue" "" "" reviewer ""
let rawAndOuter value =
    let raw = JsonSerializer.Serialize(value: obj)
    let envelope : AgentCommandResponse =
        { Result = Some raw; Error = None; Instructions = []
          StateChangeEvidence = StateChangeEvidence.Confirmed []
          InboxActivation = None; PendingJob = None }
    raw, JsonSerializer.Serialize envelope
let prop name (element: JsonElement) = element.GetProperty(name: string)
let asReviews (root: JsonElement) =
    (prop "reviews" root).EnumerateArray()
    |> Seq.map (fun e -> JsonSerializer.Deserialize<BranchReview>(e.GetRawText()))
    |> Seq.toList
let idOf (r: BranchReview) = r.TaskId, r.ReviewId
let legacyOrder (r: BranchReview) = r.CreatedSequence, r.TaskId, r.ReviewId
let actionable (r: BranchReview) =
    (r.Status = "Claimed" && r.ReviewerId = reviewer && r.OwnerAgentId <> reviewer)
    || (r.Status = "Unclaimed" && r.OwnerAgentId <> reviewer)
let intendedOrder (r: BranchReview) = (if r.Status = "Claimed" then 0 else 1), legacyOrder r
let sample reader =
    let timer = Diagnostics.Stopwatch.StartNew()
    let result, reads = LedgerStore.measureGitReads(fun () -> queue reader)
    timer.Stop()
    let raw, outer = rawAndOuter (success result)
    TestContext.Progress.WriteLine(
        $"QUEUE raw={bytes raw} wrapper={bytes outer} elapsedMs={timer.ElapsedMilliseconds} gitStarts={reads.ProcessStarts} fetches={reads.RemoteFetches} individualBlobReads={reads.IndividualBlobReads} gitStdout={reads.OutputBytes}")
    raw, outer, reads

// Bulk fixture deliberately has synthetic review records, not evidence that
// refs exist or that the records are independently claimable. Actual published
// refs and real claim CAS operations are tested separately below.
let seed binding count =
    let source = git binding.RepositoryPath ["rev-parse"; "HEAD"]
    let rows =
        [ for i in 1 .. count ->
            let task = sprintf "bulk-%04d" (i % 19)
            let id = (Guid(i, 0s, 0s, Array.zeroCreate<byte> 8)).ToString("N")
            let owner, status, claim =
                match i % 10 with
                | 0 | 1 | 2 | 3 -> reviewer, "Unclaimed", null
                | 4 | 5 | 6 -> foreign, "Unclaimed", null
                | 7 -> foreign, "Claimed", reviewer
                | 8 -> foreign, "Claimed", other
                | _ -> foreign, (if i % 20 = 9 then "Accepted" else "Withdrawn"), other
            { ReviewId = id; TaskId = task; AttemptId = null; OwnerAgentId = owner
              Kind = "Independent source review"; TaskRef = "refs/heads/feature/fixture"
              ReviewRef = $"refs/heads/jarvis-review/{task}/{id}/r1"; SourceVersion = 1
              ReviewedHeadOid = source; BaseCommit = source; TargetRef = "refs/heads/main"
              Status = status; ReviewerId = claim
              Version = (if status = "Unclaimed" then 1 else 2)
              CreatedSequence = int64 (i / 6 + 1)
              DecisionSequence = (if status = "Accepted" then 1L else 0L)
              Acknowledged = false } ]
    let intent : LedgerIntent =
        { OperationId = Guid.NewGuid(); Actor = "fixture"; Kind = "Fixture.ReviewQueue"; RequestJson = "{}" }
    LedgerStore.transact binding intent (fun _ ->
        Ok [ for row in rows -> { Path = path row; ExpectedBlobOid = None; Content = entity row } ])
    |> success |> ignore
    rows

[<Test; NonParallelizable>]
let legacy_skew_and_read_only () =
    withFixture (fun first second reader ->
        let rows = seed first 1000
        let before = head second
        let _, beforeTask, beforeAttempt = ReviewWorkflow.getTask second "health-hosted-cas" |> success
        let beforeAgent = ReviewWorkflow.getAgentWorkingTurnAt second (LedgerStore.fetch second |> success) "Worker" |> success
        let raw, _, reads = sample reader
        use doc = JsonDocument.Parse raw
        let actual = asReviews doc.RootElement
        let expected = rows |> List.filter (fun r -> r.Status = "Unclaimed" || (r.Status = "Claimed" && r.ReviewerId = reviewer)) |> List.sortBy legacyOrder
        Assert.That(actual |> List.map idOf, Is.EqualTo(box (expected |> List.map idOf)))
        Assert.That(actual.Length, Is.EqualTo(800))
        Assert.That(actual |> List.filter (fun r -> r.OwnerAgentId = reviewer) |> List.length, Is.EqualTo(400))
        Assert.That(reads.IndividualBlobReads, Is.GreaterThanOrEqualTo(1000L))
        Assert.That((prop "snapshotCommit" doc.RootElement).GetString(), Is.EqualTo(before))
        Assert.That(head first, Is.EqualTo(before))
        let final = LedgerStore.fetch second |> success
        Assert.That(final.Commit, Is.EqualTo(before))
        let afterAgent = ReviewWorkflow.getAgentWorkingTurnAt second final "Worker" |> success
        let afterTask, afterAttempt = ReviewWorkflow.getTaskAt second final "health-hosted-cas" |> success
        Assert.That(afterAgent, Is.EqualTo(beforeAgent))
        Assert.That(afterTask, Is.EqualTo(beforeTask))
        Assert.That(afterAttempt, Is.EqualTo(beforeAttempt)))

let submit binding owner =
    let id = Guid.NewGuid()
    let task = "actual-review-fixture"
    let source = git binding.RepositoryPath ["rev-parse"; "HEAD"]
    ReviewWorkflow.submitBranchReview binding (Guid.NewGuid()) task owner id "" source "Exact fixture evidence" "Independent" ""
    |> success |> ignore
    task, id, source

let assertClaimConflict result =
    match result with
    | Error(ReviewAlreadyClaimed _) -> ()
    | actual -> Assert.Fail(sprintf "Expected stale/non-unclaimed rejection, actual %A" actual)

[<Test; NonParallelizable>]
let real_published_review_evidence_and_claim_race () =
    withFixture (fun first second reader ->
        for actor in [reviewer; other] do ReviewWorkflow.registerAgent first (Guid.NewGuid()) actor |> success |> ignore
        Client.RoleInstructions.RoleInstructions.createRole first "fixture" (Guid.NewGuid()) "Reviewer" "Independent" "Inspect frozen source"
        |> success |> ignore
        Client.RoleInstructions.RoleInstructions.attachRole first "fixture" (Guid.NewGuid()) reviewer "Reviewer" 0 |> success |> ignore
        let task, id, source = submit first foreign
        let ownTask, ownId, _ = submit first reviewer
        let claimedTask, claimedId, _ = submit first foreign
        ReviewWorkflow.claimBranchReview first (Guid.NewGuid()) claimedTask claimedId reviewer 1 |> success |> ignore
        let withdrawnTask, withdrawnId, _ = submit first foreign
        ReviewWorkflow.withdrawBranchReview first (Guid.NewGuid()) withdrawnTask withdrawnId foreign 1 |> success |> ignore
        let decidedTask, decidedId, _ = submit first foreign
        ReviewWorkflow.claimBranchReview first (Guid.NewGuid()) decidedTask decidedId reviewer 1 |> success |> ignore
        ReviewWorkflow.decideBranchReview first (Guid.NewGuid()) decidedTask decidedId reviewer 2 Accept |> success |> ignore
        let before = LedgerStore.fetch second |> success
        let observed, rows = ReviewWorkflow.branchReviewerQueue second reviewer |> success
        Assert.That(observed, Is.EqualTo(before.Commit))
        Assert.That(rows |> List.exists (fun r -> r.ReviewId = ownId.ToString("N")), Is.True, "Legacy includes own submission")
        Assert.That(rows |> List.exists (fun r -> r.ReviewId = claimedId.ToString("N")), Is.True)
        Assert.That(rows |> List.exists (fun r -> r.ReviewId = withdrawnId.ToString("N") || r.ReviewId = decidedId.ToString("N")), Is.False)
        let evidence = ReviewWorkflow.getBranchReview second task id |> success
        Assert.That(evidence.HeadCommit, Is.EqualTo(source))
        Assert.That(evidence.Review.ReviewedHeadOid, Is.EqualTo(source))
        let raw, _ = rawAndOuter (api reader "GetReview" task (id.ToString("N")) "" "" |> success)
        Assert.That(raw, Does.Contain(source))
        let _, docs = ReviewWorkflow.listReviewDocuments second task id |> success
        let _, doc = ReviewWorkflow.getReviewDocument second task id "summary.md" |> success
        Assert.That(doc.Content, Is.EqualTo("Exact fixture evidence"))
        Assert.That(docs |> List.exists (fun d -> d.Path = "summary.md" && d.BlobOid = doc.BlobOid), Is.True)
        let readDoc, _ = rawAndOuter (api reader "ReadDocument" task (id.ToString("N")) "" "summary.md" |> success)
        Assert.That(readDoc, Does.Contain(doc.BlobOid))
        Assert.That((ReviewWorkflow.getBranchReview second task (Guid.NewGuid())).IsError, Is.True)
        Assert.That((ReviewWorkflow.getReviewDocument second task id "../summary.md").IsError, Is.True)
        Assert.That(head first, Is.EqualTo(before.Commit))
        // Two independent readers see version 1. Exactly one claim with that
        // version may win. Other has no attached Reviewer role; legacy writer
        // does not check it, so this is a measured gap, not authorization advice.
        ReviewWorkflow.claimBranchReview first (Guid.NewGuid()) task id other 1 |> success |> ignore
        let afterWinner = head first
        ReviewWorkflow.claimBranchReview second (Guid.NewGuid()) task id reviewer 1 |> assertClaimConflict
        ReviewWorkflow.claimBranchReview second (Guid.NewGuid()) withdrawnTask withdrawnId reviewer 2 |> assertClaimConflict
        ReviewWorkflow.claimBranchReview second (Guid.NewGuid()) decidedTask decidedId reviewer 3 |> assertClaimConflict
        Assert.That(head second, Is.EqualTo(afterWinner))
        let pinned = ReviewWorkflow.getBranchReviewAt second before task id |> success
        Assert.That(pinned.Version, Is.EqualTo(1))
        Assert.That(pinned.Status, Is.EqualTo("Unclaimed"))
        let now = ReviewWorkflow.getBranchReview second task id |> success
        Assert.That(now.Review.Version, Is.EqualTo(2))
        Assert.That(now.Review.ReviewerId, Is.EqualTo(other))
        let _, afterQueue = ReviewWorkflow.branchReviewerQueue second reviewer |> success
        Assert.That(afterQueue |> List.exists (fun r -> r.ReviewId = id.ToString("N")), Is.False)
        // Direct legacy claim on an own review succeeds: capture the exact
        // current predicate. This is not a request to change it in this task.
        ReviewWorkflow.claimBranchReview first (Guid.NewGuid()) ownTask ownId reviewer 1 |> success |> ignore
        let own = ReviewWorkflow.getBranchReview second ownTask ownId |> success
        Assert.That(own.Review.ReviewerId, Is.EqualTo(own.Review.OwnerAgentId))
        let afterOwn = head second
        git first.ApprovedFetchUrl ["update-ref"; "-d"; evidence.Review.ReviewRef] |> ignore
        let missing = ReviewWorkflow.getBranchReview second task id |> success
        Assert.That(missing.HeadCommit, Is.Null)
        Assert.That((LedgerStore.fetchReviewRef second evidence.Review.ReviewRef source).IsError, Is.True)
        Assert.That(head first, Is.EqualTo(afterOwn)))

[<Test; NonParallelizable>]
let corrupt_record_fails_queue_not_silently_omitted () =
    withFixture (fun first second reader ->
        let rows = seed first 10
        let target = rows.Head
        let intent : LedgerIntent =
            { OperationId = Guid.NewGuid(); Actor = "fixture"; Kind = "Fixture.CorruptReview"; RequestJson = "{}" }
        LedgerStore.transact first intent (fun s ->
            let _, oid = LedgerStore.readAt first s (path target) |> success
            Ok [{ Path = path target; ExpectedBlobOid = oid; Content = "{\"schemaVersion\":999}" }])
        |> success |> ignore
        let before = head second
        Assert.That((queue reader).IsError, Is.True)
        Assert.That(head first, Is.EqualTo(before)))

// Explicit RED: contract to replace the tool default, not the legacy internal
// reader. Synthetic expected eligibility is only ownership/status filtering;
// real write authority remains independently rechecked at current CAS.
[<Test; NonParallelizable; Category("ExpectedRed")>]
let compact_actionable_default_contract () =
    withFixture (fun first second reader ->
        let rows = seed first 300
        let before = head second
        let raw, _, reads = sample reader
        use doc = JsonDocument.Parse raw
        let root = doc.RootElement
        let actual = asReviews root
        let expected = rows |> List.filter actionable |> List.sortBy intendedOrder
        let pageLimit = 32
        Assert.That(head first, Is.EqualTo(before))
        Assert.Multiple(fun () ->
            Assert.That(actual |> List.map idOf, Is.EqualTo(box (expected |> List.truncate pageLimit |> List.map idOf)), "Claimed-first bounded actionable records with stable ties")
            let count name =
                match root.TryGetProperty(name: string) with
                | true, value when value.ValueKind = JsonValueKind.Number -> value.GetInt32()
                | _ -> -1
            Assert.That(count "availableCount", Is.EqualTo(expected.Length), "Explicit eligible total")
            Assert.That(count "omittedCount", Is.EqualTo(expected.Length - pageLimit), "Explicit omitted count")
            let cursorPresent =
                match root.TryGetProperty("nextCursor") with
                | true, value when value.ValueKind = JsonValueKind.String -> not (String.IsNullOrWhiteSpace(value.GetString()))
                | _ -> false
            Assert.That(cursorPresent, Is.True, "Snapshot-pinned continuation")
            Assert.That(bytes raw, Is.LessThan(32768), "Bound default raw size")
            Assert.That(reads.IndividualBlobReads, Is.LessThan(32L), "Batch read of review records, not per-review subprocess")))
