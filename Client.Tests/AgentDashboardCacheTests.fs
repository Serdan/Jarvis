module AgentDashboardCacheTests

open System
open System.Diagnostics
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Client
open Client.LedgerStore
open Client.ReviewWorkflow
open NUnit.Framework

let private jsonOptions =
    JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

let private notification id agent fromAgent task message created acknowledged =
    ({ NotificationId = id
       ToAgentId = agent
       FromAgentId = fromAgent
       FromProjectName = "Jarvis"
       TaskId = task
       Message = message
       CreatedAtUtc = created
       AcknowledgedAtUtc = acknowledged } : AgentNotification)

let private entry (notice: AgentNotification) =
    let content = JsonSerializer.Serialize(notice, jsonOptions)
    let oid = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes content)).ToLowerInvariant()
    $"notifications/{notice.NotificationId}.json", struct (oid, content)

let private summarized records =
    match AgentDashboard.notificationSummaries records with
    | Ok result -> result
    | Error message -> failwith ("Canonical notification aggregation failed: " + message)

[<Test>]
let unread_summaries_pick_latest_by_timestamp_and_notification_id () =
    let first =
        notification "0001" "Agent A" "Reporter" "task-1" "older"
            "2026-11-08T00:00:00Z" null
    let second =
        notification "0002" "Agent A" "Reviewer" "task-2" "latest tie-break"
            "2026-11-08T01:00:00Z" null
    let tied =
        notification "0003" "Agent A" "Lead" "" "newest tie-break"
            "2026-11-08T01:00:00Z" null
    let acknowledged =
        notification "0004" "Agent A" "Other" "" "acknowledged"
            "2026-11-09T00:00:00Z" "2026-11-09T00:00:01Z"
    let other =
        notification "0005" "Agent B" "Author" "" "for B"
            "2026-11-09T00:00:00Z" null
    let records = [ first; second; tied; acknowledged; other ] |> List.map entry |> Map.ofList
    let totals = summarized records
    Assert.That(totals["Agent A"].Pending, Is.EqualTo(3))
    Assert.That(totals["Agent A"].Latest, Is.EqualTo(Some "Lead: newest tie-break"))
    Assert.That(totals["Agent B"].Pending, Is.EqualTo(1))
    Assert.That(totals["Agent B"].Latest, Is.EqualTo(Some "Author: for B"))
    Assert.That(totals.ContainsKey "Agent C", Is.False)
    Assert.That(summarized records = totals, Is.True)

[<Test>]
let acknowledgement_changes_oid_and_removes_unread_without_cross_project_leakage () =
    let openNotice =
        notification "shared-id" "Agent A" "Reporter" "task" "unread"
            "2026-11-08T01:00:00Z" null
    let original = Map.ofList [ entry openNotice ]
    Assert.That((summarized original |> Map.find "Agent A").Pending, Is.EqualTo(1))
    let acknowledged = { openNotice with AcknowledgedAtUtc = "2026-11-08T02:00:00Z" }
    let changed = Map.ofList [ entry acknowledged ]
    Assert.That(summarized changed |> Map.containsKey "Agent A", Is.False)
    Assert.That((summarized original |> Map.find "Agent A").Pending, Is.EqualTo(1))
    let otherProject =
        Map.ofList [
            entry (notification "shared-id" "Agent A" "Different" "" "other project"
                       "2026-11-08T04:00:00Z" null) ]
    Assert.That((summarized otherProject |> Map.find "Agent A").Latest, Is.EqualTo(Some "Different: other project"))
    Assert.That(summarized changed |> Map.containsKey "Agent A", Is.False)

[<Test>]
let malformed_notification_fails_closed_and_corrected_oid_recovers () =
    let corrupted = Map.ofList [ "notifications/invalid.json", struct ("bad-oid", "{broken-json") ]
    match AgentDashboard.notificationSummaries corrupted with
    | Error message -> Assert.That(message, Does.Contain("Invalid canonical ledger entity"))
    | Ok _ -> Assert.Fail("Malformed historical notification must fail closed.")
    let corrected =
        Map.ofList [
            entry (notification "invalid" "Agent A" "Owner" "" "corrected"
                       "2026-11-08T01:00:00Z" null) ]
    Assert.That((summarized corrected |> Map.find "Agent A").Latest, Is.EqualTo(Some "Owner: corrected"))

[<Test>]
let bounded_fifo_cache_evicts_oldest_and_pinned_values_survive () =
    let cache = BoundedFifoCache<string>(2, 100L)
    cache.Add("one", "first", 40L)
    cache.Add("two", "second", 40L)
    let pinned = cache.TryGet("one")
    Assert.That(pinned.IsSome, Is.True)
    cache.Add("three", "third", 40L)
    Assert.That(cache.TryGet("one").IsNone, Is.True)
    Assert.That(cache.TryGet("two").IsSome, Is.True)
    Assert.That(cache.TryGet("three").IsSome, Is.True)
    Assert.That(pinned.Value, Is.EqualTo("first"))
    Assert.That(cache.Counts = struct (2, 80L), Is.True)
    cache.Add("too-large", String.replicate 200 "X", 200L)
    Assert.That(cache.TryGet("too-large").IsNone, Is.True)
    cache.Add("three", "ignored duplicate", 40L)
    Assert.That(cache.TryGet("three").Value, Is.EqualTo("third"))
    Assert.That(cache.Counts = struct (2, 80L), Is.True)

[<Test>]
let bounded_fifo_cache_enforces_bytes_even_with_entry_room () =
    let cache = BoundedFifoCache<string>(100, 100L)
    cache.Add("first", "value", 61L)
    cache.Add("second", "value", 60L)
    Assert.That(cache.TryGet("first").IsNone, Is.True)
    Assert.That(cache.TryGet("second").IsSome, Is.True)
    Assert.That(cache.Counts = struct (1, 60L), Is.True)

[<Test>]
let parallel_project_refreshes_cannot_exceed_global_cache_bounds () =
    let cache = BoundedFifoCache<string>(64, 2048L)
    Parallel.For(0, 8000, fun i ->
        let key = $"oid-{i:D8}"
        cache.Add(key, $"value-{i}", 64L)
        match cache.TryGet key with
        | ValueSome _ | ValueNone -> ())
    |> ignore
    let struct (entries, bytes) = cache.Counts
    Assert.That(entries, Is.InRange(1, 64))
    Assert.That(bytes, Is.LessThanOrEqualTo(2048L))

[<Test>]
let invalid_fifo_budgets_and_weights_are_rejected () =
    Assert.Throws<ArgumentException>(TestDelegate(fun () ->
        BoundedFifoCache<string>(0, 100L) |> ignore)) |> ignore
    Assert.Throws<ArgumentException>(TestDelegate(fun () ->
        BoundedFifoCache<string>(1, 0L) |> ignore)) |> ignore
    let cache = BoundedFifoCache<string>(2, 100L)
    Assert.Throws<ArgumentException>(TestDelegate(fun () ->
        cache.Add("a", "x", -1L))) |> ignore

[<Test>]
let changing_heads_and_large_notification_history_keep_caches_bounded () =
    let baseline =
        [| for i in 0 .. 11999 ->
            notification (sprintf "%032x" i) ("Agent " + string (i % 48))
                "Reporter" (sprintf "task-%d" i)
                (String.replicate 60 "message-")
                (sprintf "2026-11-08T%02d:%02d:%02dZ" ((i/3600)%24) ((i/60)%60) (i%60))
                (if i % 3 = 0 then "2026-11-08T15:00:00Z" else null) |]
    let makeRecords head =
        baseline
        |> Array.mapi (fun i item ->
            if i % 37 = 0 then entry { item with Message = item.Message + string head }
            else entry item)
        |> Map.ofArray
    let firstHead = makeRecords 0
    let firstSummary = summarized firstHead
    let estimatedCompleteHeadBytes =
        firstHead
        |> Seq.sumBy (fun (KeyValue(_, struct (oid, content))) ->
            2L * int64 (oid.Length + content.Length) + 128L)
    Assert.That(estimatedCompleteHeadBytes, Is.GreaterThan(16L * 1024L * 1024L),
                "Fixture must exceed parsed-cache byte capacity to actually exercise eviction.")
    for head in 0 .. 9 do
        let grouped = makeRecords head |> summarized
        Assert.That(grouped.Count, Is.EqualTo(32),
                    "Sixteen agents have only acknowledged notifications in this fixture.")
        Assert.That(grouped |> Seq.sumBy (fun (KeyValue(_, item)) -> item.Pending),
                    Is.EqualTo(8000))
        let struct (count, bytes) = AgentDashboard.parsedNotificationCacheStats()
        Assert.That(count, Is.LessThanOrEqualTo(16384))
        Assert.That(bytes, Is.LessThanOrEqualTo(16L * 1024L * 1024L))
        let struct (blobCount, blobBytes) = LedgerStore.dashboardBlobCacheStats()
        Assert.That(blobCount, Is.LessThanOrEqualTo(16384))
        Assert.That(blobBytes, Is.LessThanOrEqualTo(32L * 1024L * 1024L))
    let revisited = summarized firstHead
    Assert.That((revisited = firstSummary), Is.True,
                "After older OIDs have been evicted, rereading the historical Git head must reconstruct identical counts and latest messages.")
    let struct (retained, retainedBytes) = AgentDashboard.parsedNotificationCacheStats()
    TestContext.Progress.WriteLine(
        $"AgentDashboardCache 10 heads, old head revisited: retainedEntries={retained}; estimatedRetainedBytes={retainedBytes}; fullHeadBytes={estimatedCompleteHeadBytes}")

[<Test>]
let measure_dashboard_notification_refresh_latency_and_allocations () =
    let records =
        [ for i in 0 .. 11999 ->
            notification ("perf" + sprintf "%028x" i) ("Agent " + string (i % 48))
                "Reporter" (sprintf "task-%d" i)
                ("Repeated instruction with realistic message length " + String.replicate 80 "x")
                (sprintf "2026-11-08T%02d:%02d:%02dZ" ((i/3600)%24) ((i/60)%60) (i%60))
                (if i % 3 = 0 then "2026-11-08T15:00:00Z" else null)
            |> entry ] |> Map.ofList
    // Original production algorithm, reproduced on exactly the same OID
    // snapshot fixture to compare allocations without changing Git I/O.
    let legacyCache =
        System.Collections.Concurrent.ConcurrentDictionary<string, AgentNotification>(StringComparer.Ordinal)
    let legacy () =
        let all =
            records
            |> Map.toList
            |> List.map (fun (_, struct (_, content)) ->
                legacyCache.GetOrAdd(content, fun raw -> JsonSerializer.Deserialize<AgentNotification>(raw, jsonOptions)))
        [ for i in 0 .. 47 do
            let id = "Agent " + string i
            let pending =
                all |> List.filter (fun item ->
                    item.ToAgentId = id && String.IsNullOrWhiteSpace item.AcknowledgedAtUtc)
            if not pending.IsEmpty then
                let newest =
                    pending
                    |> List.sortByDescending (fun item -> item.CreatedAtUtc, item.NotificationId)
                    |> List.head
                let latest =
                    newest.FromAgentId
                    + (if String.IsNullOrWhiteSpace newest.TaskId then "" else " #" + newest.TaskId)
                    + ": " + newest.Message
                yield id, struct (pending.Length, Some latest) ]
        |> Map.ofList
    let current () =
        summarized records |> Map.map (fun _ item -> struct (item.Pending, item.Latest))
    let measure label (run: unit -> Map<string, struct (int * string option)>) =
        GC.Collect()
        GC.WaitForPendingFinalizers()
        GC.Collect()
        let before = GC.GetTotalAllocatedBytes(true)
        let sw = Stopwatch.StartNew()
        let result = run ()
        sw.Stop()
        let allocated = GC.GetTotalAllocatedBytes(true) - before
        TestContext.Progress.WriteLine(
            $"AgentDashboardNotificationCache {label}: elapsedMs={sw.Elapsed.TotalMilliseconds:F2}; allocatedBytes={allocated}; count={result.Count}")
        result
    let legacyCold = measure "legacy-cold" legacy
    let newCold = measure "bounded-cold" current
    let legacyWarm = measure "legacy-warm" legacy
    let newWarm = measure "bounded-warm" current
    Assert.That((legacyCold = legacyWarm), Is.True)
    Assert.That((legacyCold = newCold && newCold = newWarm), Is.True,
                "The optimized parser must produce byte-for-byte equivalent unread metadata.")
    Assert.That(newCold.Count, Is.EqualTo(32))
    TestContext.Progress.WriteLine(
        $"AgentDashboardNotificationCache legacyFullJsonKeysRetained={legacyCache.Count} whereas bounded parsed cache does not retain JSON keys")
    let struct (count, bytes) = AgentDashboard.parsedNotificationCacheStats()
    Assert.That(count, Is.LessThanOrEqualTo(16384))
    Assert.That(bytes, Is.LessThanOrEqualTo(16L * 1024L * 1024L))

let private agentRow project name state unread =
    ({ Key = project + "::" + name
       Project = project
       Agent = name
       State = state
       StateEnteredAt = None
       StateReason = None
       Focus = None
       PendingNotifications = unread
       LatestNotification = None } : AgentView.AgentRow)

[<Test>]
let projectDashboardDeduplicatesSharedLedgersButNotSameNamedDistinctLedgers () =
    let shared = "project-a:ledger-11111111:1"
    let different = "project-b:ledger-22222222:1"
    let results =
        [ "Wayfold", Ok (Some (shared, [agentRow "Wayfold" "A" "Working" 3], 2))
          "Wayfold copy", Ok (Some (shared, [agentRow "Wayfold copy" "A" "Working" 3], 2))
          "Wayfold", Ok (Some (different, [agentRow "Wayfold" "B" "Ready" 1], 1))
          "Empty", Ok (Some ("project-c:ledger-33333333:1", [], 0))
          "Offline", Error "Failed to fetch canonical ledger" ]
    let data = AgentDashboard.snapshotFromResults results
    Assert.That(data.Agents.Length, Is.EqualTo(2))
    Assert.That(data.Projects.Length, Is.EqualTo(4))
    Assert.That(data.Projects |> List.filter (fun p -> p.Project = "Wayfold") |> List.length, Is.EqualTo(2))
    let empty = data.Projects |> List.find (fun p -> p.Project = "Empty")
    let offline = data.Projects |> List.find (fun p -> p.Project = "Offline")
    Assert.That(empty.Counts.Value.Tracked, Is.EqualTo(0))
    Assert.That(offline.Counts.IsNone, Is.True)
    let counts = AgentView.totalCounts data
    Assert.That(counts.Tracked, Is.EqualTo(2))
    Assert.That(counts.Working, Is.EqualTo(1))
    Assert.That(counts.Ready, Is.EqualTo(1))
    Assert.That(counts.Unread, Is.EqualTo(4))
    Assert.That(counts.PendingReviews, Is.EqualTo(3))
