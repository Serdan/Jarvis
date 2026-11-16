# Reviewer 3 — independent frozen WQ10 account Preparing CAS review

**Snapshot:** `4307675a4b7b81310c57f2a375fab998790ac462` (base `ee50af8b68694e0e5fda1c28a251d07db3363aab`).

**Verdict: RequestChanges.** The implementation adds an exact-head/version WQ07 account-only Preparing CAS, full original-operation receipt, bounded private disposable Git sink, and distinct account/project/scheduler outcome states. It does not write project Prepared, authorize Scheduler dispatch, or initiate production cutover. Existing frozen snapshot tests demonstrate the intended happy path and several negative paths. I independently ran `dotnet test Client.Tests/Client.Tests.fsproj --configuration Release --no-restore --filter FullyQualifiedName~WorkQueueCanonicalMigrationPrepareTests --verbosity minimal` against Worker 8's clean source worktree: **18 passed, 0 failed, 0 skipped** (.NET 11 Release).

The green tests do **not** establish the claimed authorization and recovery contracts. Blocking findings:

1. **P1 — caller-controlled verification keys:** `findings/caller-controlled-trust-anchors.md`. Public entrypoint takes all purported host-pinned signer keys from its own caller, so three self-generated signing keys authorize the fixture write without independent operator/protocol/fence provenance. Lower-level account port verifies shape only.
2. **P1 — original legacy source/journal high-water omitted:** `findings/unverified-original-journal-highwater.md`. The newly writable account path explicitly drops `OriginalJournalsAndSourceHighWaterMissing` even though project-ledger sequence equality cannot establish original source/journal coverage and a digest alone is not proof.
3. **P2 — idempotent replay fails after project progress:** `findings/replay-blocked-by-project-progress.md`. Current mutable project prechecks can block an exact-original receipt recovery after the account CAS already succeeded.

Keep the disposable-only boundary and absence of scheduler/project activation. Fix the trust-anchor and source-provenance acceptance gates; add negative tests for forged/self-provisioned signers and missing old journal high-water. Reconcile original receipts before mutable first-time project guards. Do not treat this review or account Preparing success as authorization for real migration or integration.