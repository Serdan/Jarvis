# AgentInbox canonical snapshot scaling — JRV-2026-11-05

## Source of truth

The remote Git ledger remains authoritative. AgentInbox first calls the existing authenticated/ancestry-checked LedgerStore.fetch, pins the returned immutable commit OID, and reads one Git ls-tree listing from that commit. The same OID is passed to one Git cat-file --batch for previously unseen immutable blob OIDs. It does not consult source checkout task files, use a server cache, or introduce per-agent mutable projection records.

The canonical read model includes agents, task metadata, active attempt records, branch review records, notifications and integration submissions. Large task specs, review prose, checkpoints and historical attempt events are not selected. The existing dashboard keeps its narrower record selection, sharing the same OID-keyed derived content cache. New canonical heads always re-enumerate the tree, so changed/deleted records are never inferred from a previous projection. A cached blob is reused only if the verified snapshot's tree contains its cryptographic OID. There is no second authority, no extra record writer and no cache-dependent correctness decision. Concurrent readers pin their own commit; advancing remote heads cannot mix objects within a response.

All records used in the inbox pass exactly the same canonical schema, required-property, lifecycle/state and deserialization validation function as individual ledger reads. The batched reader also enforces the canonical regular-file Git object mode (100644) before consulting cached content; executable blobs and Gitlinks fail closed as they do in individual reads. The notification FromProjectName legacy fallback is preserved. Task/attempt/review/integration/notification path and identity validation remains in the original inbox logic. Missing linked attempts still fail closed. Agent queues, review/feedback ordering and notification sort order are unchanged.

A derived per-agent index was **not** warranted by the measured result: the existing one-tree/one-batch approach achieves subsecond cold reads and quarter-second warm reads on the representative fixture. Avoiding per-agent mirrored membership eliminates synchronization/invalidation complexity. The existing process-local OID content cache is a performance convenience, not a canonical store; memory growth under very long client lifetimes could be audited separately if measurements justify a bounded eviction policy.

## Reproducible benchmark

Client.Tests.ReviewWorkflowTests.AgentInboxBulkLedgerBenchmarkAndSnapshotCorrectness builds a disposable Git ledger fixture with two registered agents and **220 task records, 220 attempt records, 220 review records, 220 integration submissions and 220 notifications**. It also inserts 220 unrelated 16 KiB task spec documents that the inbox must avoid reading. Those records are committed in one additional canonical Git commit to avoid setup transaction/push time contaminating the measured inbox operation. The test measures cold and repeated fetches, Git subprocess launches, local individual blob reads, cat-file batches, Git output bytes, allocated .NET bytes (GC.GetTotalAllocatedBytes) and wall time. Exact response membership for both agents and cold/warm serialized equality are asserted.

Measurements on the Jarvis Linux client, .NET 10 Release test run, November 8, 2026:

| Metric | Previous cold | Previous repeat | Optimized cold | Optimized repeat |
|---|---:|---:|---:|---:|
| Wall time | 27,256.0 ms | 14,305.2 ms | 495.0 ms | 228.5 ms |
| Git subprocesses | 2,213 | 2,214 | 10 | 10 |
| Remote fetches | 1 | 1 | 1 | 1 |
| Single blob Git reads | 1,102 | 1,102 | 1 | 1 |
| Git cat-file batches | 0 | 0 | 1 | 0 |
| Git stdout bytes | 568,310 | 568,351 | 516,469 | 139,475 |
| Allocated managed bytes | 173,016,832 | 172,547,160 | 14,917,544 | 10,822,040 |
| Serialized inbox bytes | 41,965 | 41,965 | 41,965 | 41,965 |

The unchanged 41,965-byte response is a useful compatibility signal, but baseline and optimized fixture runs use different randomly generated commit IDs; exact cross-run payload identity is not inferred from equal size. The benchmark separately verifies exact queue/notification/review/integration/feedback membership and stable output across repeated reads from the same canonical commit.

The first successful benchmark result demonstrates about 55x cold and 63x repeated elapsed speedup, with roughly 99.5% fewer Git subprocess launches. This is a single machine measurement, not a universal latency guarantee; future ledgers, storage devices and remote hosts may differ.

## Regression evidence and remaining gates

Focused test cases additionally mutate canonical task lifecycle, attempt phase and attempt ownership and require the inbox to fail closed; change canonical task object modes to executable blobs and Gitlinks; verify remote head advancing between inbox calls does not alter old pinned snapshot reads; and check empty inbox state. Existing ReviewWorkflowTests and AgentDashboard tests exercise working/ready/blocked/parked, reviews, notifications and multi-agent behavior.

Post-review Release verification on November 8, 2026: `dotnet test Jarvis.slnx --configuration Release --no-restore` passed **397/397 Client.Tests** and **95/95 Server.Tests**. All six focused AgentInbox tests passed independently. `git diff --check` was clean.

The full-suite gate also exposed an existing `JobManagerTests` test with a fixed five-second `dotnet fsi` startup polling limit. The sibling test already accounts for slow host startup using a measured 20-second deadline; the environment-sanitization test was aligned to that existing test policy and then passed, without changing production job execution.

Lead self-review was Accepted, and the exact source commit was integrated to main by merge commit `9f0f8eb372bd00472f153fad4670059fad4108ca`; the ledger task is Closed. No MCP tool catalog or public protocol change was needed.

The later bounded-process merge preserved the same Git-read instrumentation and canonical pinned batch semantics while replacing the Git runner. Its Linux Release benchmark still observed ten Git launches with 220 historical records (approximately 108 ms cold, 74 ms warm on that run); the subsequent combined full Release suite passed 420/420 Client.Tests and 95/95 Server.Tests. Integration and local tests are not evidence that a running client has been updated.
