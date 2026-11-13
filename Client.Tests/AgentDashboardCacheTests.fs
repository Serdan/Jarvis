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
       ProjectLabel = project
       ProjectAliases = [project]
       Agent = name
       State = state
       StateEnteredAt = None
       StateReason = None
       Focus = None
       RoleIds = []
       Activity = None
       ReviewContext = None
       PendingNotifications = unread
       LatestNotification = None
       ActivityKeys = [project + "::" + name] } : AgentView.AgentRow)

let private available project identity head agents reviews : AgentDashboard.ProjectResult =
    { Project = project
      Aliases = [project]
      Result = Ok { Identity = identity; Head = head; Agents = agents; PendingReviews = reviews } }

let private unavailable project reason : AgentDashboard.ProjectResult =
    { Project = project; Aliases = [project]; Result = Error (AgentDashboard.OtherDashboardFailure reason) }

[<Test>]
let projectDashboardDeduplicatesSharedLedgersButNotSameNamedDistinctLedgers () =
    let shared = "project-a:ledger-11111111:1"
    let different = "project-b:ledger-22222222:1"
    let results =
        [ available "Wayfold" shared "head-shared" [agentRow "Wayfold" "A" "Working" 3] 2
          available "Wayfold copy" shared "head-shared" [agentRow "Wayfold copy" "A" "Working" 3] 2
          available "Wayfold" different "head-different" [agentRow "Wayfold" "B" "Ready" 1] 1
          available "Empty" "project-c:ledger-33333333:1" "head-empty" [] 0
          unavailable "Offline" "Failed to fetch canonical ledger" ]
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

[<Test>]
let canonicalAgentKeysAndActivityAttributionDistinguishSameNamedLedgers () =
    let first = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:11111111111111111111111111111111:1"
    let second = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb:22222222222222222222222222222222:1"
    let data =
        AgentDashboard.snapshotFromResults
            [ available "Wayfold" first "head-first" [agentRow "Wayfold" "Lead" "Working" 1] 0
              available "Wayfold" second "head-second" [agentRow "Wayfold" "Lead" "Parked" 0] 0 ]
    let keys = data.Agents |> List.map _.Key
    Assert.That(keys.Length, Is.EqualTo(2))
    Assert.That(keys |> List.distinct |> List.length, Is.EqualTo(2))
    Assert.That(keys, Does.Contain(first + "::Lead"))
    Assert.That(keys, Does.Contain(second + "::Lead"))
    let seen = System.Collections.Generic.Dictionary<string, DateTimeOffset>()
    seen["Wayfold::Lead"] <- DateTimeOffset.UtcNow
    for row in data.Agents do
        Assert.That(row.ActivityKeys, Is.Empty)
        Assert.That(AgentView.lastSeenAt row seen, Is.EqualTo(None),
                    "A local project label cannot attribute presence across distinct ledgers.")
    Assert.That(AgentView.changedAgents data data, Is.Empty)

[<Test>]
let aliasesOfOneLedgerPreserveUnambiguousLocalActivityAttribution () =
    let identity = "project:shared-ledger:1"
    let data =
        AgentDashboard.snapshotFromResults
            [ available "owner" identity "head-1" [agentRow "owner" "Lead" "Ready" 0] 0
              available "reviewer" identity "head-1" [agentRow "reviewer" "Lead" "Ready" 0] 0 ]
    Assert.That(data.Agents.Length, Is.EqualTo(1))
    let row = data.Agents.Head
    Assert.That(row.Key, Is.EqualTo(identity + "::Lead"))
    Assert.That(row.ActivityKeys, Is.EquivalentTo(["owner::Lead"; "reviewer::Lead"]))
    let now = DateTimeOffset.UtcNow
    let seen = System.Collections.Generic.Dictionary<string, DateTimeOffset>()
    seen["owner::Lead"] <- now.AddMinutes(-5.0)
    seen["reviewer::Lead"] <- now
    Assert.That(AgentView.lastSeenAt row seen, Is.EqualTo(Some now))

[<Test>]
let agentRosterDisambiguatesLongSameNamedProjectsInNarrowColumns () =
    let name = "A Very Long Project Name That Cannot Fit"
    let first = "project:aaaa1111:1"
    let second = "project:bbbb2222:1"
    let data =
        AgentDashboard.snapshotFromResults
            [ available name first "head-1" [agentRow name "Agent 1" "Ready" 0] 0
              available name second "head-2" [agentRow name "Agent 1" "Working" 0] 0 ]
    Assert.That(data.Agents.Length, Is.EqualTo(2))
    let labels = data.Agents |> List.map (AgentView.projectColumnLabel 24)
    Assert.That(labels |> List.distinct |> List.length, Is.EqualTo(2))
    Assert.That(labels.Head, Does.Contain("[aaaa1111]"))
    Assert.That(labels.Tail.Head, Does.Contain("[bbbb2222]"))

[<Test>]
let projectSelectorsMatchAllAliasesWithoutCrossLedgerLeakage () =
    let shared = "project-owner:shared-ledger:1"
    let distinct = "project-other:separate-ledger:1"
    let data =
        AgentDashboard.snapshotFromResults
            [ available "owner" shared "head-shared" [agentRow "owner" "Lead" "Ready" 2] 1
              available "reviewer" shared "head-shared" [agentRow "reviewer" "Lead" "Ready" 2] 1
              available "reviewer" distinct "head-other" [agentRow "reviewer" "Other" "Working" 3] 2 ]
    Assert.That(data.Projects.Length, Is.EqualTo(2))
    let sharedProject = data.Projects |> List.find (fun p -> p.Identity = shared)
    Assert.That(sharedProject.Aliases, Is.EquivalentTo(["owner"; "reviewer"]))
    let selectedProjects filter =
        AgentView.visibleProjects filter data |> List.map _.Identity
    let selectedRows filter =
        AgentView.visibleRows filter data |> List.map _.Key
    Assert.That(selectedProjects "@owner" = [shared], Is.True)
    Assert.That(selectedProjects "@reviewer", Is.EquivalentTo([shared; distinct]))
    Assert.That(selectedProjects "@owner @reviewer" = [shared], Is.True)
    Assert.That(selectedProjects "@other", Is.Empty)
    Assert.That(selectedRows "@owner" = [shared + "::Lead"], Is.True)
    Assert.That(selectedRows "@reviewer", Is.EquivalentTo([shared + "::Lead"; distinct + "::Other"]))
    Assert.That(selectedRows "@owner @reviewer" = [shared + "::Lead"], Is.True)
    Assert.That(selectedRows "@owner Other", Is.Empty)
    let sharedAgent = data.Agents |> List.find (fun row -> row.Key = shared + "::Lead")
    let otherAgent = data.Agents |> List.find (fun row -> row.Key = distinct + "::Other")
    Assert.That(sharedAgent.ProjectAliases, Is.EquivalentTo(["owner"; "reviewer"]))
    Assert.That(sharedAgent.ActivityKeys = ["owner::Lead"], Is.True,
                "An ambiguous alias is a selector, but not a safe local activity identity.")
    Assert.That(otherAgent.ActivityKeys, Is.Empty)

[<Test>]
let differingCanonicalHeadsFailClosedRatherThanSelectingAStaleAlias () =
    let identity = "project:one-ledger:1"
    let ready = available "owner" identity "H1" [agentRow "owner" "Lead" "Ready" 0] 0
    let working = available "reviewer" identity "H2" [agentRow "reviewer" "Lead" "Working" 1] 2
    for pair in [[ready; working]; [working; ready]] do
        let data = AgentDashboard.snapshotFromResults pair
        Assert.That(data.Agents, Is.Empty)
        Assert.That(data.Projects.Length, Is.EqualTo(1))
        let project = data.Projects.Head
        Assert.That(project.Identity, Is.EqualTo(identity))
        Assert.That(project.Aliases, Is.EquivalentTo(["owner"; "reviewer"]))
        Assert.That(project.Counts.IsNone, Is.True,
                    "A fresh dashboard must not publish arbitrary stale counts from a different head.")
        Assert.That(project.Error.Value, Does.Contain("Inconsistent canonical ledger heads"))
        Assert.That(AgentView.visibleProjects "@owner" data |> List.length, Is.EqualTo(1))
        Assert.That(AgentView.visibleProjects "@reviewer" data |> List.length, Is.EqualTo(1))
        Assert.That((AgentView.totalCounts data).Tracked, Is.EqualTo(0))

[<Test>]
let identicalCanonicalHeadWithDifferingUnreadOrStateCannotChooseArbitraryAlias () =
    let identity = "project:one-ledger:1"
    let ready = available "owner" identity "same-head" [agentRow "owner" "Lead" "Ready" 0] 0
    let working = available "reviewer" identity "same-head" [agentRow "reviewer" "Lead" "Working" 1] 0
    let data = AgentDashboard.snapshotFromResults [ready; working]
    Assert.That(data.Agents, Is.Empty)
    Assert.That(data.Projects.Length, Is.EqualTo(1))
    Assert.That(data.Projects.Head.Counts.IsNone, Is.True)
    Assert.That(data.Projects.Head.Error.Value, Does.Contain("Inconsistent canonical ledger contents"))


let private temporary project : AgentDashboard.ProjectResult =
    { Project = project; Aliases = [project]
      Result = Error (AgentDashboard.TemporaryBindingContention "Lock held by another client") }

[<Test>]
let transientDashboardContentionPreservesVerifiedRowsAsStaleThenRecoversFresh () =
    let wayfold = "a:ledger-a:1"
    let jarvis = "b:ledger-b:1"
    let earlier =
        AgentDashboard.snapshotFromResults
            [ available "Wayfold" wayfold "H1" [agentRow "Wayfold" "Worker" "Working" 2] 3
              available "Jarvis" jarvis "J1" [agentRow "Jarvis" "Lead" "Ready" 1] 1 ]
    let contended =
        AgentDashboard.snapshotFromResults
            [ temporary "Wayfold"
              available "Jarvis" jarvis "J2" [agentRow "Jarvis" "Lead" "Ready" 4] 1 ]
    let temporaryProject = contended.Projects |> List.find (fun p -> p.Project = "Wayfold")
    Assert.That(temporaryProject.Status, Is.EqualTo(AgentView.TemporarilyUnavailable))
    let carried = AgentView.preserveTransientProjects (Some earlier) contended
    Assert.That(carried.Projects.Length, Is.EqualTo(2))
    Assert.That(carried.Agents.Length, Is.EqualTo(2))
    let stale = carried.Projects |> List.find (fun p -> p.Project = "Wayfold")
    let fresh = carried.Projects |> List.find (fun p -> p.Project = "Jarvis")
    Assert.That(stale.Identity, Is.EqualTo(wayfold))
    Assert.That(stale.Status, Is.EqualTo(AgentView.Stale))
    Assert.That(stale.Counts.Value.Tracked, Is.EqualTo(1))
    Assert.That(stale.Counts.Value.PendingReviews, Is.EqualTo(3))
    Assert.That(stale.Error.Value, Does.Contain("stale"))
    Assert.That(fresh.Status, Is.EqualTo(AgentView.Fresh))
    Assert.That(fresh.Counts.Value.Unread, Is.EqualTo(4))
    Assert.That((AgentView.totalCounts carried).Unread, Is.EqualTo(6))
    let staleRow = carried.Agents |> List.find (fun row -> row.Key = wayfold + "::Worker")
    Assert.That(staleRow.ProjectLabel, Does.EndWith(" [STALE]"))
    Assert.That(AgentView.projectStatusLine 120 stale.Project stale, Does.Contain("STALE"))
    let twice = AgentView.preserveTransientProjects (Some carried) contended
    Assert.That(twice.Agents.Length, Is.EqualTo(2))
    let repeatedRow = twice.Agents |> List.find (fun row -> row.Key = staleRow.Key)
    Assert.That(repeatedRow.ProjectLabel, Is.EqualTo(staleRow.ProjectLabel),
                "Repeated contention must not accumulate stale suffixes.")
    let recovered =
        AgentDashboard.snapshotFromResults
            [ available "Wayfold" wayfold "H2" [agentRow "Wayfold" "Worker" "Ready" 0] 0
              available "Jarvis" jarvis "J2" [agentRow "Jarvis" "Lead" "Ready" 4] 1 ]
        |> AgentView.preserveTransientProjects (Some twice)
    Assert.That(recovered.Projects |> List.forall (fun project -> project.Status = AgentView.Fresh), Is.True)
    Assert.That(recovered.Agents |> List.exists (fun row -> row.ProjectLabel.Contains("[STALE]")), Is.False)
    Assert.That((AgentView.totalCounts recovered).Unread, Is.EqualTo(4))

[<Test>]
let transientDashboardWithNoVerifiedSnapshotRemainsUnknownNotZero () =
    let unknown = AgentDashboard.snapshotFromResults [temporary "Offline"]
    let initial = AgentView.preserveTransientProjects None unknown
    Assert.That(initial.Agents, Is.Empty)
    Assert.That(initial.Projects.Head.Status, Is.EqualTo(AgentView.TemporarilyUnavailable))
    Assert.That(initial.Projects.Head.Counts.IsNone, Is.True)
    let detail = AgentView.projectStatusLine 120 "Offline" initial.Projects.Head
    Assert.That(detail, Does.Contain("TEMPORARILY UNAVAILABLE"))
    Assert.That(detail, Does.Contain("counts unknown"))
    let resumed = AgentView.preserveTransientProjects (Some initial) unknown
    Assert.That(resumed.Projects.Head.Counts.IsNone, Is.True)

[<Test>]
let permanentDashboardFailureNeverReusesPreviousTrustedRows () =
    let old = AgentDashboard.snapshotFromResults
                  [available "Wayfold" "a:ledger-a:1" "H1" [agentRow "Wayfold" "A" "Ready" 1] 2]
    for reason in ["Malformed binding"; "Access denied"; "Ledger history unavailable"; "Remote unavailable"] do
        let failed = AgentDashboard.snapshotFromResults [unavailable "Wayfold" reason]
        let shown = AgentView.preserveTransientProjects (Some old) failed
        Assert.That(shown.Projects.Head.Status, Is.EqualTo(AgentView.Unavailable))
        Assert.That(shown.Projects.Head.Counts.IsNone, Is.True)
        Assert.That(shown.Projects.Head.Error.Value, Does.Contain(reason))
        Assert.That(shown.Agents, Is.Empty)


[<Test>]
let transientAliasesOfOneVerifiedLedgerDoNotDuplicateProjectTotals () =
    let shared = "project:shared-ledger:1"
    let prior =
        AgentDashboard.snapshotFromResults
            [ available "owner" shared "H1" [agentRow "owner" "Lead" "Ready" 2] 3
              available "reviewer" shared "H1" [agentRow "reviewer" "Lead" "Ready" 2] 3 ]
    Assert.That(prior.Projects.Length, Is.EqualTo(1))
    let current = AgentDashboard.snapshotFromResults [temporary "owner"; temporary "reviewer"]
    let stale = AgentView.preserveTransientProjects (Some prior) current
    Assert.That(stale.Projects.Length, Is.EqualTo(1),
                "Multiple aliases of one verified ledger must produce one stale summary.")
    Assert.That(stale.Agents.Length, Is.EqualTo(1),
                "Canonical stale agents must not duplicate across aliases.")
    Assert.That(stale.Projects.Head.Aliases, Is.EquivalentTo(["owner"; "reviewer"]))
    Assert.That(stale.Projects.Head.Status, Is.EqualTo(AgentView.Stale))
    let counts = AgentView.totalCounts stale
    Assert.That(counts.Tracked, Is.EqualTo(1))
    Assert.That(counts.Unread, Is.EqualTo(2))
    Assert.That(counts.PendingReviews, Is.EqualTo(3))
