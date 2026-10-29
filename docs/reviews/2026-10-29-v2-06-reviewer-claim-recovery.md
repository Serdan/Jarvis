# V2-06 reviewer claim recovery and orphan source-ref retention

## Invariants and implementation

The **review revision** is immutable and remains pinned after a reviewer
claims it. Reviewer occupancy is separately tracked by the mutable request
`ClaimantId`/version; the ledger request stays `Claimed` when occupancy
is released. A released review is `Claimed` with a null claimant, explicitly
awaiting authorized reassignment. Ordinary `ClaimReview` cannot reclaim or
unlock owner replacement; the recovered reviewer is assigned through
`ReassignReviewer` with exact request version and a reason. Releasing occupancy
is ledger-only and works even when the pinned source ref is missing; new
claims and acceptance still require verifying that exact ref. The current
claimant or the orchestrator may release/reassign; the orchestrator alone
may reassign a released claim. The owner cannot self-review, unfreeze,
withdraw a claimed revision, or change its source snapshot. There are no
activity-based or disconnect-triggered reviewer transitions.

Claim, release, reassignment, withdrawal and decision append immutable
`reviews/<id>/events/<sequence>.json` records with the operation UUID, actor, previous
and new claimants, current review version, exact revision/ref/head/spec
and reason. Verdicts retain distinct `ReviewDecisionEvidence` records
for reviewer-reported inspection, tests, and the final decision. Historical
reviewer-handoff operation IDs remain compatible with the previous request
payload; no invented test or inspection evidence is retroactively created.

The V2 protocol exposes `V2ReleaseReviewClaim`,
`V2ReassignReviewWithReason`, `V2ReviewClaimHistory`, and
`V2AwaitingReviewReassignment`. These are explicitly versioned and do not
consume or release an implementation agent's Working slot.

## Orphan review references

A review ref may be pushed successfully before its ledger Submit/Update
loses a race or times out. `V2InspectOrphanReviewRefs` lists task-scoped
remote refs absent from every immutable ledger review revision. Existing
ledger revisions are protected **even if superseded or withdrawn**, so
claimed/accepted/pending-integration review refs are never treated as
unindexed orphans.

Cleanup is intentionally a **two-step, explicit operation**:

1. `V2ReserveOrphanReviewRef` verifies the unindexed source ref and exact
   SHA, records a permanent immutable ledger reservation/tombstone under
   `tasks/<task>/orphan-cleanups/<request>/r<revision>.json`, with actor,
   reason and ledger sequence. A simultaneous review publication can win
   the ledger transaction first; in that case reservation fails. If the
   reservation wins first, delayed Submit/Update ledger planners reject
   that review ref. The ref is not deleted by this command.
2. `V2DeleteReservedOrphanReviewRef` verifies the canonical reservation,
   pinned expected SHA, and continued absence of any ledger revision
   before attempting exactly one Git delete under
   `--force-with-lease=<ref>:<sha>`. It rechecks the remote afterward,
   returning Deleted or AlreadyAbsent only for a verified missing ref.
   A moved ref, changed OID, ledger conflict or ambiguous Git result
   is rejected rather than unconditionally deleting another commit.

The permanent reservation is the durable cleanup authority.
`V2InspectOrphanReviewRefs` continues to show reserved records after
deletion with `RemoteState=Absent`, preserves the expected SHA and
reservation sequence, and distinguishes a retargeted remote ref from
a still-reserved matching ref. A full listing gathers protected refs once
per verified ledger snapshot rather than re-reading every revision for
each source ref. Remote absence is a separately observed fact, not
falsely recorded as an atomic ledger/source transaction; deletion may
be retried after an uncertain transport outcome. No broad wildcard deletion, source force-rewrite,
worktree modification, or implicit garbage collection is supported.
Unindexed refs are intentionally retained until a caller explicitly
chooses and reserves one.

## Security and limits

The source/ledger remote trust anchor and existing V2 approval gate remain
mandatory. Logical reviewer labels and the literal orchestrator actor
are **not cryptographic authorization**; actor authorization is a separate
V2-11 security requirement. The MCP deletion tool is explicitly marked
destructive, with Git-write authorization. Git source-ref delete rules
may reject the action; no branch-protection circumvention is attempted.

No production ledger was initialized, imported, pushed, deployed or migrated.
All integration/cleanup tests use disposable Git repositories.

## Regression scope

Disposable source/ledger Git fixtures verify competing reviewers,
release/withdrawal/reassignment, idempotent release, stale decisions,
frozen revision preservation, immutable event history, cleanup lease,
reservation replay, rejection of a late submission after a cleanup
reservation, missing deleted ref on retry, retention of a claimed
revision, and a retargeted orphan refusing deletion. The existing
claim-versus-replacement race and source ref integrity tests remain in the
full client regression suite.

## Final verification and known limits

- `dotnet test Client.Tests/Client.Tests.fsproj --no-restore`:
  **340/340 passed** on the final TRX-captured run. The prior quiet run
  reported 339/340 without a failing test name; it was not reproducible
  on the immediate complete rerun. The focused V2-06 tests passed.
- `dotnet test Server.Tests/Server.Tests.fsproj --no-restore`:
  **62/62 passed**, including MCP tool scope and destructive metadata.
- End-to-end opt-in client API fixture exercises release, reassignment,
  immutable claim history, orphan inspection/reservation/deletion, and
  post-deletion tombstone projection.
- `git diff --check` passes. Protocol **3.32** is experimental; no
  production server/client was deployed or restarted.

The task establishes ledger-serialized orphan cleanup but does not
assert end-to-end atomicity across source refs and the canonical ledger.
Source remote protection and backend permissions can prevent deletion.
V2-11 still owns actor authentication, authorization, privacy and
hardening of the operator-only recovery surface.
