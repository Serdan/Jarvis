# Activation blockers — WQ-03 host protection and authoritative source effects

**Classification:** **P1 operational/dependency blocker**, not a defect permitting unsafe source requeue in frozen `2d3a5b881436826063ece164a4f86c6f654b84fa`. The reviewed adapter **intentionally cannot complete `YieldSource`**. Reviewer accepts only this fail-closed isolated foundation; full WQ-03 acceptance remains open.

## 1. Host-enforced WIP immutability and retention — REQUIRED

- The effective source Git host must enforce create-only, no-overwrite/no-force and no-delete for all generation-scoped `refs/heads/jarvis-wip/**` refs, including nested namespace matching, actual credentials and bypass roles. Retain the ref/object for the entire unfinished lineage and any outstanding claims/checkpoints; evidence must cover GC/pruning.
- Independently record and verify the effective policy/ruleset version and authorization/bypass scope. Neither `git ls-remote` nor a successful non-force creation is sufficient.
- A host-test fixture using the same effective auth must prove that overwrite/deletion attempts are denied, then that two independent clones can still fetch the exact continuation SHA. Do **not** destructively probe production refs.
- Until this is available, the unconditional `verifySourceExternalEffectsAndRefProtection` rejection at `Client/Modules/WorkQueueGitLedger.fs:391-393` must remain. The current disposable bare remote permits force update and deletion in `SourceYieldFailsClosedWithoutAuthoritativeJobJournalAndProtectedWipRetention`; it is deliberately not a protected-host proof.

## 2. Original mutating-job and push journal — REQUIRED

- A trusted host/agent bridge must durably journal **all** source-mutating processes, Git pushes, merges, retries and their original operation IDs **before** dispatch, bound to project, repository identity, approved remote, work ID, launch UUID, claim generation, isolated worktree ID, source ref and effect type.
- Terminal effect evidence must be authoritative. A process missing from a journal is not known terminal; a timeout, client crash, stale status or `NotFoundAtVerifiedHead` push result is not evidence that a delayed original push cannot land. Unknown events retain the original UUID/recovery data, worktree/claim/token fences, and durable RecoveryBlocked status.
- After failure of the source handoff preflight, a returned `InvalidBinding "RecoveryBlocked..."` currently **does not itself persist** `GateState.SourceRecoveryBlocked`. The claim and token remain held, which prevents requeue, but the production bridge must durably record the blocked reason using an audited transition, or explicitly fail out without releasing authority. Do not treat a returned error string or voluntary evidence text as the canonical blocked-state record.
- Add crash/restart and two-clone adversarial tests with a clean HEAD but a still-running mutating process, an uncertain original push and a late-arriving old process/push; no successor may receive actionable source while reconciliation is incomplete.

## 3. Successful handoff path — NOT YET TESTED OR AUTHORIZED

- Only after 1 and 2 are implemented can a **new separately frozen review** verify the positive public sequence: generation-scoped immutable WIP remote push; original operation-ID retry on lost ledger reply; verified `YieldSource` receipt; old launch/token terminal, new FIFO; successor `ClaimNext` from a clean **linked detached generation worktree** at exact immutable WIP SHA; new InitializeAgent UUID/session; original old-session/project CAS and physical effects rejected.
- Current successor tests invoke `verifySourceClaimCheckout` directly with a constructed post-handoff work stage because the full positive public handoff is correctly unreachable. This is sufficient for reviewing the *guard*, not for claiming production end-to-end acceptance.
- Review and operator authorize the host bridge and subsequent cutover independently. Continue to prohibit scheduler activation, migration, deployment or release-token automation until these prerequisites pass.

**Current frozen review:** Accepted only as an **inert safe foundation**, with this P1 host dependency expressly unresolved. No Github Actions, deployment, main push, or Codex were used.