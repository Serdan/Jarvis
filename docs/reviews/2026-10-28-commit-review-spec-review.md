# Design review — Orchestration V2 commit-review workflow

**Date:** 2026-10-28
**Reviewed:** `docs/specs/orchestration-v2-architecture.md`, `docs/OrchestrationV2CommitReview.md`, and prototype `Client/Modules/ReviewWorkflow.fs` at `a75d175`.
**Disposition:** Canonical Git-ledger architecture accepted. **Do not treat the commit-review protocol as production-ready** until the P0 issues below are specified and tested. This is a design review; it does not authorize a V1 cutover.

## Assessment

The design correctly separates implementation ownership, review readiness, reviewer decisions, source integration, remote push, and task closure. Atomic ledger updates and reviewer queue projections are appropriate for a stateless server. The prototype's two-clone review tests prove useful primitives, but not the complete semantics of reviewing a patch set or making that patch set available to another agent.

The required correction is **not** a server database, scheduler, per-task ledger ref, or GitHub pull-request dependency. Keep one canonical orphan ledger ref and use source Git as the code transport. Clarify the boundary between those two Git histories.

## Must resolve before native review tools or migration

### RV3-01 — P0: The reviewable change set is not defined

**Evidence:** Architecture §4 “Commit submission and reviewer decisions” (approx. lines 208–212), §6 source binding, §7 integration; `ReviewSubmission` stores only `CommitOid` and `SourceRef` (ReviewWorkflow.fs lines 39–53).

A commit OID names an object, not the complete **intended diff** for the task. An agent can have multiple commits, merge another branch, rebase, or submit the repository's existing HEAD without changing anything. “The reviewer approves this SHA” is insufficient to establish which changes they approved.

**Required contract:** A review round pins `sourceRepositoryId`, `sourceRemote`, `sourceRef`, `baseCommitOid`, `headCommitOid`, `targetRef`, `assignmentGeneration`, `specBlobOid`, and a stable `reviewId`. Its default reviewable delta is the final tree diff `base..head` (with explicit ancestry and merge-base rules), not just the final commit's single-parent patch. Record the reviewed scope/changed files and make the base visible in the reviewer UI. Define whether a zero-diff result is acceptable only for explicitly no-code tasks. Reject an unrelated or previously integrated HEAD as a code submission without explicit justification.

**Tests:** three-commit task, unrelated source commit, source branch with merge commits, no-diff submission, rebased submission, and two attempts whose heads are in the same repository.

### RV3-02 — P0: Submission does not guarantee that a reviewer can retrieve the code

**Evidence:** Architecture §4 explicitly says the source need not be pushed. `SubmitForReview` only verifies the **local** source ref; review on another clone can fail with SourceUnavailable and requires a manual push/fetch (OrchestrationV2CommitReview.md §§3–6).

A review can become ReadyForReview while no reviewer can access its commit. A source ref that later moves or is deleted may also make accepted evidence uninspectable even though the ledger history survives.

**Required contract:** Define an **idempotent source-publication phase** before a remote review becomes ReadyForReview. Prefer a durable review ref on the approved source remote (or an explicitly equivalent immutable/reachable source ref) that a reviewer can fetch and verify resolves to the pinned SHA. A same-device shared-object exception is acceptable only when the reviewer is explicitly bound to that reachable local repository; otherwise mark AwaitingSourcePublication, not ReadyForReview. Record source remote, ref, published OID, and verification result. Define visibility/permissions, retention until integration/closure, cleanup, and crash recovery if source publication succeeded but the ledger operation failed. Never push to main implicitly.

**Tests:** separate devices/clones; submitted-but-not-published commit; branch deletion/rewrite during review; source publication succeeds but ledger response is lost; cleanup without losing accepted source evidence.

### RV3-03 — P0: Source integration is not strictly constrained to the reviewed patch set

**Evidence:** Architecture §7 PlanIntegration/RecordIntegration supports integration of a source branch/ref, with ancestry or patch-equivalence evidence, but §4 does not state how a reviewed head limits later source branch movement.

The reviewer may approve commit H, after which the agent pushes H2 with additional unreviewed changes. An integration action that merges the moving branch or merely checks that H is an ancestor can incorporate H2. Ancestry is proof that H is included, not proof that *only* its approved change set was integrated.

**Required contract:** `AcceptAttempt` pins an accepted `reviewId/base/head` plus the approved change-set identity. `PlanIntegration` may integrate the exact approved head or a newly verified equivalent patch; never use a current branch tip by default. `RecordIntegration` verifies the intended source changes against the accepted base/head, the actual target diff and target remote OID. A materially expanded or changed patch requires new review. Explicitly distinguish unrelated target changes already present from unreviewed source changes.

**Tests:** branch advances after acceptance; downstream unreviewed commit; rebase/cherry-pick exact equivalence; patch-equivalence false positive; target head advances concurrently.

## Important specification gaps

### RV3-04 — P1: Reviewer routing and recovery are underspecified

**Evidence:** ReviewerId exists in the attempt; the review prototype requires it at AssignTask. The advertised protocol has no GetReviewerQueue, GetReview, ClaimReview, ReassignReviewer or equivalent, despite an implementation reviewerQueue helper.

Decide whether the reviewer is selected on assignment, submission, or via an explicit reviewer claim. Add an audited, optimistic-concurrency **reassign/defer/reclaim** path when a reviewer is unavailable. One reviewer can own multiple pending reviews; reviewer responsibility must be separately projected from work the same agent implements. A reviewer going offline should not strand a task indefinitely or silently make it “free.” Do not infer liveness from ledger state.

**Tests:** reviewer changes after submission, two reviewers race to claim, reviewer parked/blocked, reviewer queue and agent inbox show consistent responsibility.

### RV3-05 — P1: Returned work lacks a complete immutable instruction generation

**Evidence:** Architecture §3 says generations contain instructions, source base, pinned spec and issued sequence; §4 RequestChanges says “new Queued generation.” Prototype `decide` increments only `AssignmentGeneration` and writes a feedback string (ReviewWorkflow.fs lines 272–285).

Define a returned generation record including reviewer feedback, source review ID, next requested action, pinned task spec, base/head, issued sequence, and acknowledgement/receipt rules. The agent must be able to obtain all of this from GetAgentInbox/GetAttempt and resume on its own, even if it is Working on another task at the time. No silent preemption; stale generations must remain invalid.

**Tests:** reviewer returns Task A while owner Works on B; fresh session reads complete instructions; old generation cannot checkpoint/resubmit; duplicate reviewer decision does not issue another generation.

### RV3-06 — P1: Submission checkpoint/evidence prerequisites conflict with the prototype

**Evidence:** Architecture §4 transition table requires a valid checkpoint before SubmitForReview; prototype tests submit successfully without CheckpointAttempt and expose only a free-text summary.

Choose one normative behavior. **Recommended:** SubmitForReview includes/persists its own final implementation checkpoint atomically (summary, changed source identifiers, test evidence, known limitations, next action), instead of making the agent issue a separate administrative checkpoint just before submission. Keep a separate CheckpointAttempt for progress during Working. Specify maximum evidence size, whether evidence is mandatory, and whether a returned assignment keeps previous evidence.

**Tests:** submit directly after implementation with final evidence, missing source verification, replay after lost response, accepted review retains exact evidence, later revisions do not overwrite prior submissions.

### RV3-07 — P1: Park, block, cancel and supersede must update the reviewer queue consistently

**Evidence:** Architecture §4 allows ParkAttempt/BlockAttempt on ReadyForReview, but the review prototype's reviewerQueue filters only Pending review records, independent of attempt phase (ReviewWorkflow.fs lines 313–330). No explicit withdraw/replace-submission workflow exists.

A parked review can remain incorrectly visible as pending. Define the complete review-state matrix: Pending, Deferred/Blocked, Withdrawn/Superseded, ChangesRequested, Accepted, Cancelled. Reviewer inbox membership should be consistent with attempt phase and reviewer assignment in the *same* ledger snapshot. Provide an agent-initiated Withdraw/Supersede submission while awaiting review (when not already accepted), with a new review ID, correct slot semantics, and immutable audit history.

**Tests:** park a pending review, block on missing source, cancel task while review pending, withdraw and submit a newer SHA, stale accept after supersession, resume reviewer-target work.

### RV3-08 — P1: Review schema and public API need a normative definition

**Evidence:** Architecture §3 normative JSON describes task/attempt/agent, but not review; the ledger layout omits `tasks/<id>/reviews/<id>.json`. Architecture §8 lists SubmitForReview/RequestChanges/AcceptAttempt but no dedicated review reads; implementation uses reviewPath, GetReview, and reviewerQueue. The architecture's example agent filename `Agent%203.json` is not the prototype's base64url-encoded agent ID.

Specify one canonical path encoding across platforms and migration. Add review JSON schema, immutable submitted fields, mutable decision fields, versioning, review-round uniqueness, reviewer/inbox DTOs, and failure codes (`ReviewUnavailable`, `ReviewAccessDenied`, `SourceNotPublished`, etc. as appropriate). Include the read and selection APIs, not only write methods. Align code and specification before command/DTO wiring.

**Tests:** schema round-trip, cross-device path encoding, history of old review rounds, case-fold collisions, malformed records, and exact-version decision conflicts.

### RV3-09 — P2: Acceptance evidence should be attached to the reviewed object

**Evidence:** Architecture §4 says reviewer may Accept; prototype Accept checks local Git object existence, then changes status to Implemented (ReviewWorkflow.fs lines 267–283).

Object presence is not proof that the reviewer inspected the right patch set or evaluated task criteria. The API should record an explicit reviewer verdict with pinned review/base/head IDs, observations, verification commands/results (if provided), unresolved findings, and decision sequence. Do not pretend the tool can prove actual human/agent attention; this is audit evidence, not cryptographic review attestation. Keep implementation acceptance distinct from integration evidence.

**Tests:** acceptance references immutable review scope and evidence; stale approval cannot attach to a new review round; previous decision remains discoverable.

## Recommended normative flow

1. An implementation agent owns one Working attempt and develops code on an ordinary source branch.
2. SubmitReview prepares **base/head/target plus final implementation evidence** and verifies a durable, reviewer-accessible source ref; failure is explicit and recoverable.
3. One canonical ledger commit pins the review scope, records the final checkpoint, transitions the attempt to ReadyForReview and releases its execution slot.
4. The reviewer inbox exposes the pending pinned change set. Reviewers can accept, request changes, defer, or explicitly hand off responsibility.
5. Accept stores a reviewer decision for **that exact change set**. RequestChanges issues a full new generation with feedback and no automatic preemption. Withdraw/Supersede invalidates the pending round but preserves history.
6. Integration plans target only the accepted head/verified equivalent patch, confirm the remote target, publish durable integration evidence, and close/retire separately.

## Completion gate and disposition

The ledger architecture remains approved, and the existing two-clone prototype tests demonstrate atomic state transitions and optimistic concurrency. They do not establish correctness of the **source review contract**. Require RV3-01/02/03 before publishing review MCP commands. Require RV3-04 through RV3-08 before allowing production review handoffs or migrating live task state. RV3-09 can be completed with the reviewer UI/API work, but should not be omitted from the eventual schema.

No database, server-side scheduler, GitHub PR dependency, new authoritative task refs, or V1 compatibility path is required to address these findings.
