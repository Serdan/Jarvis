# Independent review — replaceable, claimable review requests (RV4)

**Date:** 2026-10-28
**Reviewed revision:** 5d15810, branch feature/orchestration-v2-ledger-store
**Files:** docs/specs/orchestration-v2-architecture.md and docs/OrchestrationV2CommitReview.md
**Type:** Design-only review; no implementation changes.

## Disposition

**Design accepted in principle, not yet implementation-ready.**

Keep the single canonical Git ledger, independent source review refs, claims as a freezing boundary, versioned review history, and the property that submission does not stop the implementation agent. The revised spec resolves the previous architectural concern of blocking agents during reviews.

**Operating constraint:** exactly one implementation agent owns a task at a time. Jarvis does not need to support competing implementation agents independently developing or submitting the same task. The reviewer is a different actor and may claim a request while its owner updates it. Repeated calls after timeouts/resets remain possible, but should use the existing operation-ID deduplication mechanism.

Under this constraint, the sole critical finding is **approval coverage** (RV4-03). Source-ref publication safety and one-active-request tracking remain modest defensive invariants, not reasons to build multi-owner scheduling. The other findings concern actual review handoff, feedback delivery and lifecycle correctness.

## P2 — RV4-01: Keep review refs immutable under retries, not hypothetical competing task owners

**Evidence:** Architecture §§3–4 define refs such as `jarvis-review/315/<request>/r2`, published before the ledger update.

With a **single task owner**, two independent agents submitting different candidates as r2 is out of scope. The former P0 finding incorrectly assumed parallel implementation agents. Duplicate or late operations can still occur from retries, lost responses or a restarted client; we only need to ensure publication does not rewrite an existing ref accidentally.

**Simpler remedy:** Keep the existing deterministic `r1` / `r2` naming. Publish **create-only**: if the ref does not exist, create it; if it already points to the exact expected SHA, reuse it for the same logical operation; if it points elsewhere, return a conflict/integrity error and do not update it (even if Git permits a fast-forward). The canonical ledger's operation UUID remains the idempotency authority. No candidate UUID suffixes, parallel-submitter protocol, extra locks or agent-coordination layer.

**Tests:** retry identical source publication after lost reply; existing ref with different SHA is never modified; owner update racing reviewer claim leaves the existing claimed ref frozen.

## P1 — RV4-02: A single active review pointer makes retry and lifecycle semantics explicit

**Evidence:** The spec promises one actionable review per task, but independent review request records do not identify the current request on the task/attempt.

This is **not** a multi-agent assignment race. The sole owner may nevertheless retry a call after uncertainty, start a fresh review after an earlier decision, or interact with task cancellation/finalization. A task-level pointer makes current-review selection deterministic without scanning older review history.

**Simpler remedy:** Put `activeReviewRequestId` in the existing attempt (or task) record. Submit sets it when empty; update/claim preserve it; a terminal review decision clears it in the same ledger mutation. Require the current pointer to match for reviewer decisions. Operation-ID deduplication handles duplicate deliveries. Reject a second actionable request until the prior request resolves; historical reviews remain separate.

**Tests:** same owner retries Submit with the same operation ID, attempts another Submit before resolution, then submits a new request after resolution; cancel/finalize clears or explicitly settles the active review. No tests for competing implementation agents on one task are required.

## P0 — RV4-03: Review scope and finalization do not prove cumulative approval coverage

**Evidence:** Architecture §4 allows arbitrary revised base/head, later submissions after a terminal decision, and FinalizeImplementation selecting one accepted review (approximately lines 230, 235, 251–263); §7 integrates the selected approved revision (around lines 373–376). There is no invariant specifying how accepted/rejected prior ranges participate in the final approved change set.

Example: first review covers task base A through commit C, and the reviewer requests changes. The agent continues to E, and a new review uses C as base, reviewing only D–E. Accepting D–E does not approve A–C, even though integrating E pulls A–C in. With multiple previously accepted incremental reviews, selecting only the last accepted revision can likewise omit the explicit chain.

**Remedy:** Prefer a simple default: all new review requests for a task use its canonical original task base or the latest **verified-integrated** base and review the **cumulative final tree delta**, including corrections to previously rejected work. Restrict base overrides to an explicit, validated, auditable operation. If incremental review is eventually offered, record an approval dependency chain and prove that the union covers the exact proposed integration tree without gaps or unapproved predecessors. FinalizeImplementation must verify an accepted coverage proof for the selected head, not merely the existence of one accepted review.

**Required tests:** rejected A..C followed by later C..E review; two accepted chained ranges; accepted C while work branch advances E; attempt to finalize on a partial approval; rebased/merged head relative to the target base; integration must never silently include an unapproved ancestor patch.

## P1 — RV4-04: Claimed-review release has no self-service reviewer-queue continuation

**Evidence:** Architecture §4 says release without immediate reassignment leaves status Claimed, claimantId=null and AwaitingReassignment, and only ReassignReviewer may choose a replacement (around line 263).

This preserves the freeze, but an agent who voluntarily releases a claim cannot make the review available to a replacement reviewer without orchestrator intervention. That introduces a potential bottleneck even when the original reviewer has explicitly relinquished it.

**Remedy:** Separate **revisionFrozen** from **claimant occupancy**. An authorized release may place the request in a FrozenAwaitingReviewer queue, visible to eligible reviewers for claiming the exact same immutable revision. The original owner **still cannot replace** it after its first claim. Reassignment and administrative override remain available without treating reviewer disappearance as auto-release. If a deliberately centralized reassignment policy is chosen instead, state that limitation as an explicit choice and provide an unattended handoff workflow.

**Required tests:** reviewer releases claim, another reviewer reclaims exact same revision without orchestrator; rejected owner update while claimant null; simultaneous reclaim; old reviewer cannot decide after release; no auto-release on disconnection.

## P1 — RV4-05: Source integrity must be checked at claim, not only at acceptance

**Evidence:** Architecture §4 ClaimReview preconditions check ledger request status/version and reviewer eligibility (line 231). The spec requires a remote ref verification when reviewers fetch or accept (around line 265), but it does not unambiguously require remote source availability **before** claiming.

If review refs were deleted, rewritten or unreachable after submission, a reviewer can claim a non-reviewable snapshot and lock its owner out of replacement.

**Remedy:** Before committing ClaimReview, validate fetchability and the pinned remote ref's exact OID in the designated source repository, using a bounded read-only check. If the ref is missing or source remote unavailable, fail the claim without locking the request and surface SourceReviewRefUnavailable/RemoteUnavailable. A claim made while the source later disappears must have an explicit blocked-review recovery path that maintains the frozen revision and audit history.

**Required tests:** source ref vanishes before claim; remote offline at claim; malformed or rewritten ref; ref disappears after claim; blocked-review recovery and resumed review of the original SHA.

## P1 — RV4-06: Review feedback can be invisible to an already-Working owner

**Evidence:** Architecture §4 RequestChanges says it will provide durable feedback without changing a Working assignment generation (around lines 212, 233 and 245). GetAgentInbox describes pending review but does not define a distinct projection of **unacknowledged terminal review decisions** (around line 211).

Without a new assignment or an explicit notification cursor, the Working owner may continue its session or resume after reset without discovering that a reviewer has requested changes. Durable Git history is insufficient as a notification contract.

**Remedy:** Provide a read-only inbox field such as unacknowledgedReviewFeedback with review/request/revision IDs, decision sequence, feedback, and action required. Add an explicit acknowledgment operation or a monotonic acknowledgedDecisionSequence stored in agent/attempt state (never automatically consume on read). A reviewer decision must be discoverable in an agent's next GetAgentInbox even if the attempt remains Working. Do not interrupt its current work automatically.

**Required tests:** RequestChanges during Working, owner goes offline and resumes, two review decisions in succession, inbox repeated until acknowledgment, independent checkpointing does not mark feedback read.

## P1 — RV4-07: Finalize/cancel/close must settle active and claimed reviews atomically

**Evidence:** Architecture §4 FinalizeImplementation requires an accepted revision and "no unreviewed required work remains" (around line 235). CancelTask, CloseTask and Park/Block coexist with independently tracked reviews (lines 236–242); no explicit transition matrix defines what happens to the activeReviewRequestId when task state changes.

An agent could have accepted review A and active claimed review B on the same task, then finalize using A. Without a stronger rule, B remains a reviewer responsibility for a task already Implemented. Cancel or Close may similarly leave an actionable review behind.

**Remedy:** Finalization must require no actionable unclaimed/claimed review for the task **or** atomically resolve it with an explicit audited outcome, with reviewer consent for a claimed request. Cancel/Abandon/Close require symmetric review cleanup/withdrawal policies and an exact state-transition matrix. A pending or claimed review is never silently discarded by changing the task lifecycle. PlanIntegration must reference accepted/finalized coverage only.

**Required tests:** accepted A followed by pending B and attempted finalization, cancel while Claimed, close with a pending unclaimed review, acceptance racing finalization, task replay after cancellation and no ghost reviewer queue entries.

## P2 — RV4-08: Metadata-only updates need not create more source refs

**Evidence:** Architecture §4 allows replacing only summary/evidence and says each revision is published to a new source ref (around lines 253–257).

A metadata-only revision has unchanged base/head and source objects. Repushing another branch for the same code is unnecessary work and produces more refs to retain and clean up.

**Remedy:** Keep every revision metadata record immutable, but allow later revisions with identical pinned base/head to **reuse the previously published immutable source ref** after verifying its OID. Only source changes require a new source ref. Still use the same ledger CAS/version/claim rules.

**Required test:** metadata-only update does not push another source ref but creates a new ledger revision; stale reviewer claim sees the new metadata version; claimed request cannot be edited.

## Consolidated design changes

1. Keep deterministic per-revision source ref names with create-only/idempotent publication; do not introduce per-agent candidate coordination.
2. Track the one active review request with a simple pointer in the task or attempt record; use it for lifecycle and retry consistency.
3. Review cumulative change sets by default; finalization must prove approval coverage of the exact integration target.
4. Keep revision freeze independent of reviewer occupancy; reviewers can explicitly release and hand off the frozen request.
5. Validate pinned remote source at claim and provide recoverable blocked-review state.
6. Surface reviewer decisions as explicitly acknowledged owner-inbox items.
7. Close all actionable review obligations as part of task finalization/cancellation.
8. Reuse an immutable source ref for metadata-only revisions.

No database, new authoritative task ref, server scheduler, or GitHub PR dependency is needed.

## Release gate

**Do not wire the new review MCP tools or replace the prototype until RV4-03 (cumulative approval scope) has a normative contract.** RV4-02 needs a simple active-review pointer, and RV4-01 needs create-only source publication. Resolve RV4-04 through RV4-07 during implementation because they affect actual reviewer handoff and owner notifications. RV4-08 is optional. Focus race tests on **one owner updating versus one reviewer claiming**, duplicate operation delivery, and reviewer-versus-reviewer claims; **parallel implementation agents on the same task are explicitly out of scope**.

The current V2 review prototype is knowingly based on the old exclusive ReadyForReview model; its passing tests are not evidence that the newly proposed workflow is implemented.
