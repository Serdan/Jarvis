# Lead review — AgentInbox canonical snapshot scaling

**Verdict: Accept** (Lead self-review; no separate Jarvis reviewer). Frozen source commit `1d09db9c800f471ed21c39cc008a3423c1e52d27`, baseline `e50c24afceb03d7caf180dee2099144f27ebeb89`.

## Implementation
- One authenticated/ancestry-checked fetch produces an immutable canonical commit; one NUL-safe Git tree walk enumerates task, agent, notification, review and integration records from that commit.
- A single `git cat-file --batch` loads previously unseen immutable blob OIDs, with process-local cache reuse only for OIDs present in the newly fetched tree. No second authoritative index, server cache or writer.
- The inbox preserves its existing common JSON schema/deserialization validation, path and identity checks, state/phase consistency checks, ordering and notification legacy fallback. Large task specs/checkpoints are not loaded.
- Review correction `b30fef4` restores a missing old-reader invariant: canonical record objects must be normal Git blobs (mode 100644). Executable blobs and Gitlinks now fail closed even when their OIDs are cached. A disposable-ledger negative-space test proves both cases.
- Ancillary test-only correction `1d09db9` replaces a pre-existing five-second FSI-startup poll limit with the 20-second measured deadline already used by the adjacent JobManager test.

## Evidence
- Disposable 220-per-category history fixture including 220 unrelated 16 KiB specifications.
- Cold: 27,256 ms -> 495 ms; repeated: 14,305 ms -> 228.5 ms; Git subprocesses: 2,213/2,214 -> 10; individual blob lookups: 1,102 -> 1; cold managed allocation: 173.0 MB -> 14.9 MB.
- Same serialized inbox byte count in the baseline and optimized benchmark; stronger new assertions verify exact Owner and Reviewer membership, single-snapshot repeat equivalence and pinned-head consistency, rather than inferring identity from byte count.
- Focused AgentInbox Release tests: **6/6 passed**; complete `dotnet test Jarvis.slnx --configuration Release --no-restore`: **397/397 Client.Tests**, **95/95 Server.Tests**; `git diff --check` clean.
- Review compared `LedgerStore.readAt/listAt/blobAt` against the optimized `readViewRecordsAt`, checked new getAgentInbox joins/ordering and failure boundaries, and verified the feature branch contains main.

## Residual / future work
- Enumeration and cold decode remain proportional to historical entity count; the measured fixture does not yet warrant a per-agent derived index. Monitor larger real ledgers if polling latency regresses.
- The existing process-local immutable-OID cache is unbounded; audit a bounded cache only if sustained-memory measurements warrant it. Neither issue blocks this improvement.
- These are source and benchmark observations, not evidence of deployment.