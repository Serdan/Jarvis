# P1 — Safe source requeue accepts unrelated WIP proof and drops continuation provenance

**Frozen SHA:** `1b886591ae7e87158fc0debe2bf5f01858d17b7d`  
**Source:** `Server/WorkQueueContracts.fs:63-76, 78-95, 887-921`; tests `Server.Tests/WorkQueueContractTests.fs:381-396`.  
**Architecture:** §7/§9 source-safe activation handoff requires exact repository/ref/SHA provenance for the successor.

**Reproduction:** Create claimed implementation A with `SourceLineage=Some {RepositoryId="repo-A"; ...}`; gate A via a new hard dependency. Call `resolveDependencyGate` with `RemoteSnapshot("unrelated-repo", "refs/heads/other", "unrelated-sha", true)`. `isSafeHandoff` checks only non-empty values and a boolean; it never compares the proof to A.SourceLineage. Resolution succeeds. More generally, the safe RemoteSnapshot is not written into WorkStage.SourceLineage.ReachableRef/ContinuationSha, so after the old launch terminalizes, a successor's contract state has no persisted accepted handoff identity and may resume at the obsolete source baseline.

**Impact:** The reference transition can certify an unrelated or untracked WIP snapshot as a safe source handoff, then publish the work Available without carrying the tested continuation source. The docs correctly disclaim real Git verification as later-phase work, but a pure *typed source-lineage contract* still needs the expected identity and resulting continuation stored.

**Required:** Compare proof identity with stage's pinned repository/task/activation lineage, reject missing or mismatched durable source receipts for mutable work, and atomically pin the accepted remote ref/SHA into successor lineage or retain RecoveryBlocked until known. Continue to require real remote reachability/job reconciliation in the later persistence implementation. Add wrong-repo/ref/SHA and exact-continuation-after-requeue tests.
