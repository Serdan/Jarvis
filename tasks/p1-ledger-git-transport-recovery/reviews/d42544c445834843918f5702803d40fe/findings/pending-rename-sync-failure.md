# P1 — Directory-sync failure is swallowed after pending-intent rename

**Frozen review:** `d42544c445834843918f5702803d40fe`; exact source `2a74c33d4a9759528161aa85fc17c8a63decb671`.

**Invariant:** A public Task/Review write must durably retain the caller's original UUID and command before its first remote fetch. A local durability failure must fail closed, not allow canonical Git I/O as though a crash-recoverable intent had been secured.

**Source evidence:** `Client/Modules/OrchestrationApi.fs:839-855` now writes a temporary pending record with `FileOptions.WriteThrough`, flushes it, then invokes `durableArchiveMove temp file directory`. That helper at `:611-620` calls `File.Move` followed by `syncArchiveDirectory folder` on Unix; `:596-609` throws `IOException` when opening or fsyncing that directory fails. However, the new caller catches **any** `IOException` from `durableArchiveMove` whenever `File.Exists file` and ignores it, then reads the record and returns success. This condition is satisfied after a successful rename followed by a **failed fsync**, not only by a collision.

**Causal counterexample:** On the first public `TaskApi.write` or `ReviewApi.write` for UUID U, the temporary file is written and flushed, its rename to `U.json` succeeds, but the directory fsync returns an I/O error (e.g. EIO). The catch sees `U.json` on disk and suppresses the durability failure. `readFile` succeeds, so `executeLedgerWrite` calls `verifyRemote` and can reach Git transaction/publication. At that point the new directory entry is **not confirmed durable**, and a subsequent system crash may lose the local record for U, despite the operation having proceeded. The exact bug is that post-rename sync errors are treated as harmless 'destination already exists' races.

**Evidence boundary:** This is a source-proven fail-open path, not a claim that the independent Release tests reproduced a filesystem crash. The existing added fault tests inject DNS/network failures, not a directory-sync error. All 10 selected .NET11 Release tests passed.

**Narrow correction:** Distinguish destination-already-existed collisions *before the rename* from errors after the rename. Do not suppress a sync failure merely because the final file exists. Propagate/fail closed (or re-sync the directory successfully) before any remote Git read or write; retain the U+command file for recovery where available. Add a deterministic fault seam/test for a successful rename followed by directory-sync failure, asserting that no remote fetch/push occurs and the original U+hash remains eligible for recovery.

**Verdict impact:** **P1 blocker, RequestChanges.** This concerns the specific newly claimed durability guarantee and can be fixed without broad redesign or touching WQ-02 core.
