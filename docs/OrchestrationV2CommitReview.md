# Orchestration V2 — independent source review workflow

**Status (2026-10-28):** The isolated F# prototype now implements the core single-owner review lifecycle and has disposable-remote tests. It is **not yet connected to MCP/TUI**, and the live V1 task system remains unchanged.

The authoritative orchestration state stays on one remote orphan Git ledger branch. Source review refs belong to the normal project source remote. The Jarvis server does not store review/task state.

## Core contract

Each task has exactly one implementation owner and one current implementation attempt. The attempt pins a **review baseline** once, prior to the first review. Every subsequent review of that task, including after a rejection, covers the *entire cumulative tree delta* from the pinned baseline to its submitted head. Previously rejected changes cannot disappear by using their rejected head as a new review base.

A review request is independent of the implementation attempt's Working state. Submitting it does not release the agent's slot. While it is Unclaimed, the sole owner may replace its revision. Once a registered, non-owner reviewer claims it, the exact revision is frozen. Claim and replacement use expected request versions and the ordinary ledger fast-forward publication race; the loser must refresh. Only one active review request per attempt is allowed.

Every code revision is reachable via a create-only `refs/heads/jarvis-review/<task>/<request>/r<n>` source ref. Publication verifies the actual remote OID and never advances an existing ref, even if normal Git would permit a fast-forward. A ref pushed before a losing ledger mutation is an unauthoritative orphan candidate; it must not enter reviewer queues. The source publication and ledger transaction cannot be atomic across refs. Stable operation UUIDs allow safe replay of accepted ledger changes without publishing another revision.

## Available isolated prototype operations

`CreateTask`, `RegisterAgent`, `AssignTask`, `BeginAttempt`, `BindReviewBase`, `SubmitForReview`, `UpdateReviewRequest`, `ClaimReview`, `WithdrawReview`, `ReassignReviewer`, `ReviewDecision` (Accept/RequestChanges), `FinalizeImplementation`, `AcknowledgeReviewFeedback`, `GetTask`, `GetReview`, `GetReviewerQueue`.

- Submit creates Unclaimed revision 1, sets `activeReviewRequestId` and keeps the owner Working. The review request and current revision are separate Git ledger records.
- Update appends a revision while Unclaimed. The older revision/ref remains unchanged. Retries discover their prior operation first.
- Claim is open to any *registered* reviewer except the task owner. The reviewer claims the exact version/revision and fetches the pinned source ref. Other reviewers cannot decide on it.
- A claimant may hand off its frozen review to another registered reviewer; the owner cannot silently reopen or alter it. An unclaimed request may be withdrawn by its owner; a claimed request may be withdrawn only by its claimant.
- Accept records approval of the exact cumulative source revision; RequestChanges records feedback. Neither transition interrupts an owner already Working. The active review pointer is cleared; earlier review history remains.
- FinalizeImplementation is separate: it requires acceptance of the **latest** review request, no active review, and the matching pinned cumulative source head. Only then does it mark Implemented and release the Working slot. Source integration and task closure remain separate.
- Feedback has a decision sequence and explicit acknowledgment sequence; ordinary task reads do not auto-acknowledge it.

## Coverage

The fixtures run against a disposable bare Git remote and two independent clones. They verify create-only refs, cross-device claim/fetch, replacement before claim, claimed revision immutability, agent-slot retention, claim/update race, owner/reviewer separation, source-ref mismatch rejection, reviewer handoff, withdrawal, operation UUID idempotency, cumulative resubmission after rejection, stale-finalization rejection, and regular ledger concurrency/rewrite handling.

## Explicitly incomplete

This is a **core domain prototype**, not the full protocol-4.0 feature. Do not expose it as production Jarvis task handling yet. Remaining work includes:

- Full spec HTML/assignment-generation/checkpoint and reviewer-evidence schemas (currently source summary and pinned base/head are tracked, not the full final checkpoint).
- Native MCP DTOs, reviewer/owner inbox views and tool permissions.
- Durable client configuration/trust binding, crash-time pending intent storage, checked orphan-ref cleanup and reviewer-release/reclaim behavior.
- Complete park/block/cancel/source integration transitions, migration and global V1 writer cutoff.
- More adversarial tests for remote outages, host ref protection, backup/restore, source rewrites and larger contention.

The older exclusive `ReadyForReview` attempt workflow and its tests have been replaced in this isolated client prototype. The pre-cutover scope above remains intentionally excluded.


## Experimental MCP bridge (protocol 3.20)

The branch includes explicit, **non-V1** MCP methods: V2ReviewTask, V2ReviewDetails,
V2ReviewerInbox, V2SubmitReview, V2ReplaceReview, V2ClaimReview, V2DecideReview,
V2WithdrawReview, V2ReassignReview and V2FinalizeReview. These are human-oriented
aliases for two typed wire commands (V2ReviewRead, V2ReviewWrite). Every mutation
takes a caller-owned **operationId** and the exact expected entity version.

Client routing requires an explicit local binding, stored **outside the repository** at
the path computed by ReviewV2Binding.pathForRoot. On Linux this is typically
$XDG_CONFIG_HOME/Jarvis/ledger-v2/<sha256-of-absolute-checkout-path>.json
(or ~/.config/Jarvis/... when XDG_CONFIG_HOME is not set). The config is created
only by explicit local setup; no tool writes or guesses it automatically. Example:

{
  "schemaVersion": 1,
  "enabled": true,
  "projectRoot": "/absolute/path/to/a/registered/project/checkout",
  "projectId": "existing-ledger-project-UUID",
  "ledgerId": "existing-ledger-UUID",
  "epoch": 1,
  "remoteName": "origin",
  "approvedFetchUrl": "exact-git-remote-fetch-URL",
  "approvedPushUrl": "exact-git-remote-push-URL"
}

These identifiers must be read from an **already approved, existing** V2 ledger;
the MCP bridge does not initialize a new remote ledger or select a source
remote without authorization. Both V2 reads and writes fail closed if the
binding is absent, disabled, malformed or mismatched. The client then performs
its normal remote/identity/ancestry verification before operations.

OAuth requires workspace:read for V2 query methods and git:write for V2
mutations. The Jarvis client applies its existing local Git permission policy.
All V1 task commands retain their original semantics; the bridge never
falls back to worktree-local task files.

This is a development capability, **not a complete V2 cutover**. Fresh projects
still need V2 task/spec creation, agent registration, assignment, baseline
binding and ledger initialization through the approved setup/migration workflow
before the review commands become usable. Native reviewer/owner TUI views,
complete checkpoint evidence schemas and protocol-4.0 migration remain pending.
