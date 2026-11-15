# Independent architecture review — ChangesRequested

**Reviewer:** Reviewer 2
**Frozen review:** `b7fd8a31f67b40f384ead6b569d93724`
**Task:** `work-queues-ephemeral-executions`
**Exact frozen source:** `36155553147705748c90d73a5aaf497b511768ef` on `refs/heads/docs/work-queues-design-corrections`
**File:** `docs/specs/work-queues-and-ephemeral-executions.md` (424 lines, 70,532 bytes), verified against the registered clean worktree at this exact head
**Comparison:** predecessor `e737c2da901ae4073f7ecc066781f428020ffdd1`; original review `d4fbb56e2d4c451eab1ef23c281dc647` with F01–F06
**Scope:** Independent documentation architecture/concurrency/recovery analysis; no implementation, code tests, merge, deployment, schedule registration or frozen-source modification.

## Verdict: ChangesRequested

The corrected design is a major improvement and **substantially remedies F01–F06**. Its central model is coherent: durable work subjects and checkpoint histories, ephemeral one-claim-per-launch executions, task-addressed conversations, Lead-owned prioritization, Manager role dispatch rather than worker assignment, exact frozen review/accepted-SHA integration, a single account-global capacity ledger, and explicit fail-closed recovery. Nevertheless, four still-unresolved protocol boundaries could admit duplicate demand, post-terminal activity or partial migration. They are **blocking for approval of this as an implementation contract**. A fifth finding concerns cap reconfiguration and can be resolved before runtime configuration is exposed.

## Assessment of previous F01–F06

- **F01 — Launch demand**: Addressed in principle by canonical CAS demand credits rather than pre-claim queue counts, project-wide coverage, and two-Manager fixtures (sections 4, 8; tests 20–21). **Residual B03:** out-of-credit-order claims require explicit global rematching of other live credits, including overlapping subscriptions, or duplicate pending launches can recur.
- **F02 — Session/capacity**: The design now distinguishes logical terminal work/launch events from provider-session exit, retains uncertain launches, forbids second claims on an already used launch, and supplies `NoWork` semantics (sections 3, 7–9; tests 5–7, 21–22). The chosen `maxConcurrentAgents` is explicitly a **logical Jarvis-execution** cap, not a physical count of live provider/browser sessions. This distinction must be preserved in UI and operator expectations. **Residuals B01/B02:** taskless Manager/NoWork terminal evidence and cross-ledger fencing/handoff ordering are incomplete.
- **F03 — Source continuity**: Satisfactorily addressed at design level by a pinned repository/base, generation-scoped remotely reachable WIP commit/ref verified *before* yield, isolated successor worktrees, mutating-job fencing and preservation of immutable reviewed SHA (section 9; tests 12, 19). During Phase A, retention should explicitly prevent GC/deletion of still-required WIP reachability; this is implementation detail, not a new blocking finding.
- **F04 — Canonical review/queue transitions**: Addressed by one CAS linking direct review and work claims, explicit verdict-recorded/terminal-pending state, prohibition on post-verdict YieldWork/BlockWork, idempotent canonical verdict producer, and policy-gated exact-SHA integration (section 10; tests 6, 22, 27).
- **F05 — Concurrent source stages**: Addressed by a canonical writer-lineage key, stage generation and one actionable writer per lineage; multiple review findings join the same current correction stage, with stale feedback routed to Lead (sections 4, 10; tests 9, 28).
- **F06 — Migration/index/dependencies**: Claimed reviews, pending intents, mutating jobs and protocol fencing are now explicit cutover refuses; block/satisfaction races and continuous per-domain/per-shard index coverage are covered (sections 5, 11, 14; tests 15, 23, 29). **Residual B04:** a multi-ledger activation epoch/partial conversion gate is still required to uphold the promised all-or-nothing availability.

## Other design checks

- **Operator communication:** Task/subject-addressed append-only Question/Response/Decision events, challenge-backed blocking replies, cursor-based continuation and operator attention indexes are internally consistent. Routine tool telemetry is not a conversation; decisions do not silently rewrite task specs or authorize integration. Tests 24–26 cover cross-session replies and stale-question races.
- **Global semaphore and scheduler:** One account-global ledger covers all roles/projects/devices, including Manager. CAS before browser side effects, uncertainty retention, skip-when-full scheduler, coalesced capacity wake and terminal last-slot handoff are the right primitives. A last-slot handoff must be preceded by an effective **project** fence, not just a global swap (B02). Config shrinking under occupancy requires defined semantics (B05).
- **Frozen review and integration:** The exact source SHA/ref remains authoritative; a review verdict is not a task close, launch termination or merge. Older source writers cannot overwrite newer lineage state. No code review or implementation approval follows from this docs-only verdict.

## Findings

1. **B01 (P1):** `findings/01-taskless-launch-terminal.md` — Manager has no work claim and therefore no ordinary terminal operation/receipt; `NoWork` is described but lacks an explicit launch-level transition. Capacity can leak or be freed without fencing.
2. **B02 (P1):** `findings/02-cross-ledger-handoff-fencing.md` — Global Manager→child token CAS does not itself fence the project Manager launch. Specify project-fence-before-transfer, two-ledger commit ordering, receipts and crash recovery for every partial state.
3. **B03 (P1):** `findings/03-demand-credit-rematching.md` — A worker can claim an item different from its demand-credit generation. Without atomic reassignment/cancellation of other live credits, a subsequent Manager can overlaunch for the remaining work.
4. **B04 (P1):** `findings/04-cross-project-cutover-epoch.md` — Multi-project plus account-ledger migration lacks a durable staged activation/read gate; partial conversion failure cannot otherwise guarantee old-schema authority or safe recovery.
5. **B05 (P2):** `findings/05-capacity-resize.md` — Reducing `maxConcurrentAgents` below currently reserved tokens contradicts the unconditional invariant unless rejected or represented as drain-only state; serialize changes with reservations.

## Re-review acceptance

Require a new exact pinned docs commit, not mutation of this frozen review. It should state: (1) the full taskless launch/Manager terminal/no-work operation and idempotent receipt; (2) prepared-project-fence → verified global handoff → once-only browser submit, including partial crash/uncertain states; (3) invariant and atomic rematching rules for live demand credits under arbitrary priority-based claims and overlapping role subscriptions; (4) a staged multi-ledger migration activation epoch/handshake with read/write gates and recoverable partial commits; and (5) the cap resize policy. Add the negative-space fixtures in each finding alongside existing tests 1–33. No rollout or unattended Manager activation until separate approval.

**Final decision:** **ChangesRequested**. This is a documentation finding, not a request to change frozen source or production state.