# WQ-03 source-host activation prerequisites — unresolved, intentionally fail closed

**Classification:** P1 **activation blockers / external dependencies**, not a rejection of the inert combined source. Applies to exact review `21add89f26fc3297160cf1d54f4b19464c20259d`.

1. **Host-side immutable WIP policy and retention absent.** A matching `ls-remote refs/heads/jarvis-wip/**` only proves the observed tip at that moment. The local bare-remote test deliberately force-overwrites and deletes a WIP ref. Production must independently attest create-only generation-scoped refs, rejected updates and deletion for the active credential *and all bypass roles*, and retention long enough for pending stages/recovery. The currently accepted implementation correctly denies `YieldSource` rather than declaring the remote protected.

2. **Authoritative original-effect journal and terminal reconciliation absent.** Before source mutation is dispatched, the trusted host needs complete durable original-operation IDs bound to work, launch, claim generation, repository, worktree and remote ref, covering processes/jobs, push/merge and retry. An unknown push outcome or previously dispatched process remains unsafe until its *original* operation and remote effect are settled; clean source status, a self-reported checkpoint and transient negative ancestry are insufficient. `verifySourceExternalEffectsAndRefProtection` unconditionally errors, so the absence of this bridge cannot falsely release the claim. `MarkSourceRecoveryBlocked` is a conservative durable hold, not host proof.

3. **Successful source-handoff/successor end-to-end acceptance not yet exercised.** With the host guards absent, safe positive `YieldSource` is intentionally disabled. A future separately reviewed replacement must prove original journal reconciliation, proof-backed WIP persistence, old-session late writes rejected, new session fresh UUID and generation, clean pinned successor checkout before actionable CAS, and token/account capacity correctness. Include two-client negative races and real host policy evidence. Do not turn off the fail-closed guard to make fixture positives pass.

4. **Operator-controlled activation only.** The current merge may be integrated *as inert source code*, not switched into live OrchestrationApi or Scheduler, migrated, deployed, or treated as a completed production WQ-03 release. Require explicit Lead/operator gate and independent frozen review after protection/effect bridge implementation.

**Source pointers:** `Client/Modules/WorkQueueGitLedger.fs:326-449, 885-922`; `Server/WorkQueueContracts.fs:570-596, 758-805, 1458-1552`; `Client.Tests/WorkQueueGitLedgerTests.fs:1030-1180`; `docs/specs/wq03-source-host-safety-dependency.md`.

**Verdict for this finding:** Open / blocking **activation**. No new source defect found within the restricted inert integration scope.
