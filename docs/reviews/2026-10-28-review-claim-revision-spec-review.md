# Independent review — replaceable, claimable review requests (RV4)

**Date:** 2026-10-28
**Reviewed revision:** 5d15810, branch feature/orchestration-v2-ledger-store
**Files:** docs/specs/orchestration-v2-architecture.md and docs/OrchestrationV2CommitReview.md
**Type:** Design-only review; no implementation changes.

## Disposition

**Design accepted in principle, not yet implementation-ready.**

Keep the single canonical Git ledger, independent source review refs, claims as a freezing boundary, versioned review history, and the property that submission does not stop the implementation agent. The revised spec resolves the previous architectural concern of blocking agents during reviews.

However, three P0 correctness holes remain in publication concurrency, task-level uniqueness, and approval coverage. Resolve those before replacing the old review prototype. The other findings specify handoff, validation, notification and lifecycle behavior.

## P0 — RV4-01: Competing updates can reuse and advance the same source review ref

**Evidence:** Architecture §§3–4, especially the revision naming rule at approximately line 253 and the publish-before-ledger rule at lines 255–261; review contract "Workflow" steps 1–5.

The published source ref is determined by task ID, request ID and revision number: jarvis-review/315/<request>/r2. Two clients holding expected version 1 can both prepare revision 2 with different commits. Both publish their candidate to the **same** ref *before* taking the ledger's optimistic concurrency decision. A regular Git push can fast-forward an existing branch, so a losing candidate can update the ref intended to be immutable, even if its ledger update is rejected. The canonical ledger serializes its own state but cannot serialize the earlier, separate source push. The test for Claim versus Update does not cover concurrent Update versus Update.

**Remedy:** Allocate a unique, immutable candidate ref per **logical publication operation**, for example refs/heads/jarvis-review/<task>/<request>/r2-<operationUUID> (or stable collision-resistant candidate ID). Publish using create-only semantics (must be absent or already resolve to the same intended SHA), with approved source-host ref protection when available. Never update an existing review ref to a different OID, even if Git would permit a fast-forward. A losing candidate remains an orphan publication that is never returned in review queues and is cleaned up separately. The ledger CAS chooses the one authoritative candidate. Retries with the same UUID inspect the same ref/OID.

**Required tests:** two independent UpdateReviewRequest operations with the same expected version and different heads; a fast-forward-related pair of heads; lost source-push reply and retried same operation; no existing ref ever changes OID; loser cannot overwrite winner's published revision.

## P0 — RV4-02: One actionable review request per task lacks an atomic uniqueness constraint

**Evidence:** Architecture §3 describes independent tasks/<task>/reviews/<request>/request.json documents (around lines 112–118, 140–166). §4 promises "One request is actionable per task at a time" around line 257, but SubmitForReview has no specified task-level active-review pointer or equivalent atomic conflict validation.

Two concurrent submissions can choose **different request UUIDs**, write different review paths, and both succeed after a non-FF replay unless the transaction explicitly checks a shared invariant. Expected version of their distinct request records does not create a conflict.

**Remedy:** Add a task- or attempt-level activeReviewRequestId field (or an equivalent single-slot task-local review index). Submit and terminal resolution update this slot in the **same ledger commit** as the review record. Claim and Update verify that it still refers to their request; finalization checks it. Use expected versions of this shared slot on all transitions, including replay on a new ledger head. A history of terminal reviews remains separate.

**Required tests:** concurrent SubmitForReview with distinct request UUIDs for the same task; submit racing cancel/close/finalize; stale Submit replay after another review became active; no two actionable requests in any confirmed snapshot.

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

1. Use unique publication identities per candidate revision and create-only source-ref semantics.
2. Introduce a shared active-review slot per task/attempt and enforce it in every relevant ledger mutation.
3. Review cumulative change sets by default; finalization must prove approval coverage of the exact integration target.
4. Keep revision freeze independent of reviewer occupancy; reviewers can explicitly release and hand off the frozen request.
5. Validate pinned remote source at claim and provide recoverable blocked-review state.
6. Surface reviewer decisions as explicitly acknowledged owner-inbox items.
7. Close all actionable review obligations as part of task finalization/cancellation.
8. Reuse an immutable source ref for metadata-only revisions.

No database, new authoritative task ref, server scheduler, or GitHub PR dependency is needed.

## Release gate

**Do not wire the new review MCP tools or replace the prototype until RV4-01, RV4-02 and RV4-03 have concrete normative state and concurrency contracts.** Resolve RV4-04 through RV4-07 in the same design iteration because they affect operational correctness. RV4-08 is an optimization and may be deferred. Expand the acceptance tests beyond claim-vs-update to cover **two competing updates** and **two competing initial submissions**.

The current V2 review prototype is knowingly based on the old exclusive ReadyForReview model; its passing tests are not evidence that the newly proposed workflow is implemented.
