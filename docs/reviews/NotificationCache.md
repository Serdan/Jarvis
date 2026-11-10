# Agent dashboard notification and Git blob cache limits

## Scope and design

The previous `AgentDashboard.parsedNotifications` was a process-global `ConcurrentDictionary` **keyed by the entire notification JSON string**, retaining one string key and deserialized object for every historical notification version read over the client lifetime. While a project snapshot already caches its fully derived rows for an unchanged Git head, every new head can encounter the same thousands of immutable notifications. The adjacent `LedgerStore.dashboardBlobCache` similarly retained the *raw* content of every historical notification/task/review Git OID indefinitely. Fixing the parsed cache alone would not bound historical JSON retention.

The client now uses two independent, thread-safe bounded FIFO caches:

| Content | Key and value | Max entries | Max estimated retained payload |
|---|---|---:|---:|
| Git snapshot read blobs | Immutable Git blob OID → UTF-16 JSON content | 16,384 | 32 MiB |
| Parsed dashboard notifications | Immutable Git blob OID → `Result<AgentNotification,string>` | 16,384 | 16 MiB |

Each insertion accounts for **2 bytes per OID character, 2 per JSON character, plus 128 bytes of object overhead**. The parsed cache uses the JSON length as a conservative proxy for its decoded fields. This is an **estimated payload ceiling**, not an exact CLR heap byte measurement; dictionaries, nodes and object headers introduce bounded additional overhead. Entries whose estimated size exceeds a cache's entire budget are **never retained**. The FIFO removes the oldest inserted keys until *both* entry and byte bounds are satisfied. A duplicate OID cannot replace immutable content. No LRU framework, timeout service, server-side cache, additional persistence, ledger schema or client wire change is involved.

The `AgentDashboard.cachedProjects` cache continues to retain the *latest derived rows for each project identity* and reuse them while its fetched commit stays unchanged. The new bounds concern historical Git and notification objects across arbitrarily many versions of those commits; the head cache does not retain old heads for the same project. This change does not attempt to limit the number of simultaneously configured project identities.

## Correctness when projects, heads, or cache entries change

- `LedgerStore.readViewRecordsAt` obtains one Git tree listing for an explicitly fetched/pinned canonical commit, then resolves OIDs either from the bounded raw cache or from a verified Git `cat-file --batch` read. Each request holds its own local dictionaries of **all needed cached and fetched values**, so concurrent eviction by another project's refresh cannot make an in-progress snapshot incomplete. The returned `Map` is built from those local values, never by looking up globally evictable entries again.
- The public `readDashboardRecordsAt` and `readAgentInboxRecordsAt` still return `Map<string,string>`; only the dashboard's internal reader includes `struct(oid,content)`. Existing canonical path filtering, blob validation, error behavior and pinned-commit semantics remain intact.
- Immutable parsed notifications are reused across changed heads by stable OID instead of full JSON string key. After eviction or a new OID following acknowledgement, the current snapshot is parsed again. Malformed canonical JSON fails closed; a corrected new OID can recover, and a previously pinned older head remains independently readable.
- The dashboard reduces all unacknowledged notifications **in one pass** into a per-agent count and the newest notification determined by the existing lexicographic `CreatedAtUtc, NotificationId` ordering. The previous implementation scanned and sorted the entire unread list separately for every agent. The output record types and human-readable `FromAgent [#TaskId]: Message` format are unchanged; agents without unread messages receive count zero and no latest notification.
- The global caches are keyed by immutable Git content OID, not project names, paths, agents or arbitrary user-provided JSON. Two independent project views of the same Git ledger can reuse exactly the same immutable blob safely without double-counting notifications in either derived snapshot. Each project's current-head cache is still scoped by project and canonical root.
- Snapshot processing is bounded by the **current ledger's size** in temporary memory: the caps control *retained global cache state*, not the unavoidable temporary working set needed to process an unusually large Git tree. Large historical ledgers exceeding the raw cache limit may require Git to re-read evicted blobs on every changed-head refresh. This is a deliberate memory/throughput tradeoff.

## Measurements

Reproducible local Release test: `Client.Tests/AgentDashboardCacheTests.fs`, test `measure_dashboard_notification_refresh_latency_and_allocations`. It builds a **single identical fixture of 12,000 notifications targeting 48 agents**, with varying timestamps and acknowledgement states. The old algorithm is reproduced in the test using full-JSON-keyed `ConcurrentDictionary` and per-agent filtering/sorting, and the new implementation consumes those same records. Each sample forces full GC before capturing `GC.GetTotalAllocatedBytes(true)` and `Stopwatch` duration.

| Derivation only, Release | Previous algorithm | Bounded algorithm |
|---|---:|---:|
| Cold elapsed | 38.38 ms | 16.85 ms |
| Cold allocations | 13,099,144 B | 9,879,704 B |
| Warm elapsed | 14.00 ms | 4.54 ms |
| Warm allocations | 2,804,808 B | 1,422,856 B |
| Full JSON strings retained as cache keys by the test's old algorithm | 12,000 | 0 |

Representative single run on 2026-11-08, Linux/.NET 10 with an 11.0-preview SDK. Other runs varied (cold 42.37 vs 20.98 ms; warm 16.59 vs 5.23 ms) but showed the same direction. The numbers **exclude Git fetch and batch-read I/O, UI repaint, and project discovery**; they describe only notification deserialization and row-summary derivation. Full canonical Git dashboard warm refresh in the integration fixture was ~63 ms, including its ordinary network/ledger checks, but this alone is not a controlled before/after end-to-end measurement. No production throughput or global memory-usage claim is made.

The larger eviction fixture consists of 10 changing heads over 12,000 historic notifications, with the first head requiring **20,133,430 estimated parsed-cache bytes**, deliberately exceeding 16 MiB. After many heads, a run retained 9,998 entries and 16,777,000 estimated bytes, under both caps. Re-reading the evicted oldest head reproduced its **exact** per-agent counts and latest-message summaries. The raw Git cache is separately asserted against its 32 MiB/16,384-entry bound.

## Verification

- `AgentDashboardCacheTests` (9 tests): timestamp+ID tie-breaking, acknowledge/new-OID invalidation, cross-project content isolation, malformed-content fail-closed, FIFO deterministic eviction and pinned-value survival, oversize/byte limits, invalid configuration, 8,000 parallel insertions, 10 changed heads/12,000 notifications, historical-head revisit after genuine eviction, and the comparative allocation/latency fixture.
- `ReviewWorkflowTests.AgentsDashboardReadsCanonicalReadyInsteadOfSourceBranchTaskFiles`: real Git clones bound as two project views, unchanged-head reuse, two new unread notifications, acknowledgement of the newest with changed OID, and correct counts/summaries in *both* projects on the next head.
- `ReviewWorkflowTests.DashboardBatchMatchesPinnedLedgerReadsAndTracksNewBlobs`: public original `Map<string,string>` API agrees with independent pinned reads across new Git heads, and older snapshots stay reproducible.
- Full Client and Server Release suites are required on the frozen source **and the no-ff merge commit** before publication.
