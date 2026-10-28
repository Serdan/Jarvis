# Orchestration V2: independent review requests and immutable revisions

**Status:** Normative V2 design (2026-10-28). The existing `Client/Modules/ReviewWorkflow.fs` implementation remains an **earlier prototype**: it still uses ReadyForReview as an attempt phase, releases the Working slot at submission, lacks ClaimReview/UpdateReviewRequest, and is **not compliant** with this revised contract. Do not expose the prototype through MCP or migrate live V1 tasks until the implementation is aligned.

The authority is the single orphan Git ref `refs/heads/jarvis-orchestration`. The server remains stateless. Review source refs live in the project's ordinary source Git repository; they are **not** alternative task ledgers.

## Model

An implementation agent is allowed to continue Working and checkpointing after submitting a review. Review requests have independent status; review submission does **not** stop work or release the agent's slot.

A logical review request has a stable UUID, task/attempt owner, mutable current revision/version pointer and one of:

- `Unclaimed`: visible in reviewer intake. The owner can update or replace the request.
- `Claimed`: one reviewer has claimed **one exact revision**. Its base/head, metadata, published ref and evidence are frozen.
- `Accepted`, `ChangesRequested`, `Withdrawn`: terminal decision/outcome, retained with the whole review history. Reassignment of a Claimed request retains the frozen revision.

A request can have revisions r1, r2, r3, etc. Each revision is immutable, independently published as `refs/heads/jarvis-review/<taskId>/<requestId>/r<n>`, and pins source repository/remote, source and target refs, base SHA, head SHA, spec, summary, final implementation evidence and publication verification. **Do not modify or force-update earlier revision refs.** The reviewer evaluates the final tree diff between base and head (with explicit ancestry), not only the last commit's patch.

At most one unclaimed-or-claimed request is actionable per task. Previous revisions remain auditable, and claimed work is not silently superseded.

## Workflow

1. The agent commits code to the working branch. When it wants review, Jarvis verifies/publishes a **new immutable source review ref** on the approved source remote and verifies that the remote advertises the pinned SHA. This step never pushes main.
2. `SubmitForReview` writes a ledger request with revision r1 and final checkpoint; its status is `Unclaimed`. The agent remains Working.
3. Until claimed, `UpdateReviewRequest` may replace r1 with r2, including a newer commit, base, summary, test evidence, or target. It uses the same logical request ID and an expected request version/current revision. It publishes r2 as its **own** Git ref and atomically points the ledger request to r2, marking r1 superseded.
4. A reviewer sees the latest Unclaimed revision in `GetReviewerQueue` and calls `ClaimReview` with the exact expected version and revision. Claim atomically records claimant and the frozen r2 reference, with review audit evidence.
5. **The claim/replacement race is resolved by the ledger's ordinary fast-forward CAS.** If replacement commits first, the stale claim fails; the reviewer reloads the current revision. If claim commits first, the owner's replacement fails with `ReviewAlreadyClaimed`. Neither writer silently overwrites the other.
6. Once Claimed, the owner cannot unilaterally alter or replace the review. Agent development can continue; later commits belong to another review. `ReleaseReviewClaim`/`ReassignReviewer` may change the reviewer **without unfreezing** that revision. Any explicit withdrawal after claim needs reviewer/orchestrator authorization.
7. The reviewer decides on the pinned revision, giving feedback and evidence. `AcceptReview` approves only that immutable change; it does **not** stop Working, mark task Implemented, integrate, or push later changes. `RequestChanges` creates durable feedback: it updates a stopped owner's next Queued assignment, but does not interrupt a Working owner.
8. An explicit `FinalizeImplementation` selects the accepted review as task completion basis and releases the slot when appropriate. Separate PlanIntegration/RecordIntegration must integrate the exact accepted patch (or verified equivalent), never the moving work branch head. CloseTask and source review-branch cleanup remain separate verified actions.

## Recovery and retention

Publishing the review ref and changing the ledger are two distinct Git operations. A source review ref may exist without an authoritative ledger request/revision (e.g. when Claim wins the race after a candidate r2 ref was pushed). Such a ref is an **orphan publication**, not a live review; only a separate checked cleanup operation may remove it.

An ambiguous source push or ledger push is resolved by reading the exact source review ref, the canonical ledger and its idempotency operation record, using the **same operationId** on retries. Never force-move old refs to “repair” uncertainty. Do not delete a review ref still named by a claimed, accepted or pending-integration revision. A reviewer must verify that the remote ref still resolves to the pinned head, or fail closed with a source-integrity error.

All request updates, claims, decisions, slot transitions and evidence changes use one ledger commit per logical operation with optimistic versions and immutable operation UUIDs. Revision history is retained in ledger Git history and separate revision/decision records.

## Acceptance coverage

- Two agents' independent source clones; an agent posts r1, updates to r2, reviewer sees only r2, and r1 remains inspectable.
- Claim and update racing on the same expected request version: exactly one wins; no review of a silently replaced SHA.
- Two reviewers racing ClaimReview: exactly one claimant.
- An agent keeps Working while a review is Unclaimed/Claimed/Accepted, with no implicit slot release.
- A reviewer accepts pinned r2 after the working branch advanced to r3; r3 remains unreviewed and cannot be integrated as part of r2.
- Lost response after publishing r2 ref, ledger commit rejected, cross-device fetch, moved/deleted ref, and safe retention/cleanup of orphan refs.
- Reviewer reassignment retains the frozen revision; stale decisions and owner updates cannot bypass the claim.

**Implementation gap:** The previously passing prototype tests cover a different handoff model. Their success must not be interpreted as acceptance of this revised model. Add tests above and retire the old ReadyForReview/slot-release contract before any V2 task API cutover.
