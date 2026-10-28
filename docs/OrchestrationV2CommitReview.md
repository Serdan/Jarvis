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


## Experimental task setup and canonical agent inbox (protocol 3.21)

The same opt-in project binding now exposes:

- V2CreateTask atomically creates tasks/<id>/task.json and canonical tasks/<id>/spec.html in one remote ledger transaction. HTML must be nonempty and no more than 128 KiB. The caller's stable operation UUID makes retries safe.
- V2ReviseTaskSpec edits the title and HTML at an expected task version only while the task is Open and unassigned. Assigned-task changes require an explicit future assignment generation.
- V2RegisterAgent, V2AssignTask, and V2BeginAttempt register stable agent labels, create the sole implementation attempt, and acquire the one Working slot. The attempt pins the canonical spec blob OID and records issuance sequence and priority.
- V2BindReviewBase pins a verified source commit and target ref against the exact Working attempt and generation, preserving one cumulative review base.
- V2AcknowledgeReviewFeedback acknowledges a specific review decision sequence without changing Working state.
- V2AgentInbox returns a single snapshot projection containing working, runnableQueue, pendingReviews, availableReviews, parked, blocked, acceptedPendingIntegration, slotAvailable, unassigned, the agent record and ledger commit. It cross-checks the Working slot with the authoritative agent record; runnable queue sorting is deterministic (priority descending, issued sequence ascending, task ID, attempt ID).
- V2ReviewTask now returns the canonical spec HTML and exact blob OID alongside the task/attempt state.

V2TaskRead and V2TaskWrite are separate typed wire commands. Human-facing task and inbox MCP aliases require workspace-read or Git-write permissions respectively. All reads and writes still require an explicit local V2 binding and never fall back to V1.

The new operations were exercised on a disposable bare Git remote from two clones, including an opt-in client-adapter path from task creation to review submission. No production ledger initialization, migration or deployment was performed.

**Still missing before cutover:** versioned assignment instructions and receipts, durable local pending-intent journal, explicit park/block/reissue/cancel transitions, source integration evidence, full review checkpoint evidence and V1-to-V2 migration. Protocol 3.21 is an additive experimental capability, not the planned breaking protocol 4.0 deployment.


## Explicit agent lifecycle (experimental protocol 3.22)

V2 agents now have one authoritative state, independent of task phase and
review status. A registered agent begins Parked. The orchestrator assigns a
task to a Parked or Completed agent, atomically changing it to Assigned,
binding its focused attempt and retaining its task-spec OID. BeginAttempt
changes Assigned to Working and acquires the single execution slot.

V2YieldAgent is an explicit Working to Ready transition. It records an
immutable task checkpoint (summary, next action, generation, ledger sequence),
changes the attempt to Paused and releases the execution slot. The operator
may prompt a Ready agent to continue; V2ResumeAgent checks both the agent
version and attempt version/generation, then restores Working on the same
assignment. No inactivity heuristic or browser-session timeout changes state.

V2CompleteAgentWork is an explicit Working/Ready to Completed transition
with a final checkpoint. It changes the attempt to Finished, releases the
slot and signals that the orchestrator can allocate fresh work. Review
requests and source integration may remain outstanding. V2ParkAgent is a
deliberate orchestrator action from Completed/Parked to Parked, with a
reason; it refuses to steal an Assigned, Ready or Working agent.

V2AgentInbox includes the focused assignment, the five-state agent record,
operatorActionRequired (Assigned/Ready), and
needsOrchestratorAssignment (Completed), alongside the independently
projected task/review responsibilities. Task integration is a separate
operation: a reviewer may finalize an accepted Finished attempt after its
agent has already started a different task without modifying the agent's
current Working slot. Review acceptance itself never changes agent state.

Each mutation uses a caller-stable operation UUID and required expected
versions; task+agent+checkpoint changes are one canonical ledger transaction.
These transitions were verified with disposable two-clone Git fixtures and
the opt-in V2 command adapter. This remains isolated from V1 and has not
been deployed or migrated.


## Assignment reissuance and deliberate recovery (experimental protocol 3.23)

Review decisions now write distinct canonical notification records under each review request. The agent inbox exposes unacknowledgedFeedback across all owned tasks; acknowledging one exact decision sequence does not hide another. The notification record and its acknowledgment are Git-ledger state, not ephemeral UI state. A retry using the same operation UUID remains idempotent.

When changes are requested while the owner is Working or Ready on the same attempt, that agent keeps its focused assignment and receives a notification. If the attempt is Finished, the reviewer decision requeues **that same attempt** at assignmentGeneration + 1, preserving its owner, source base and pinned specification. It does not change the agent state or preempt work on another task.

V2ReissueQueuedAttempt is an explicit orchestrator selection for a queued generation. It requires exact attempt and agent versions, the current generation, an idle agent (Completed or Parked), and a reason. The agent becomes Assigned and can be started by the operator. Reissue never creates a second owner or overwrites a newer assignment.

V2RecoverAgentAssignment is an explicit orchestrator-controlled restart of an Assigned, Ready or Working agent. It writes an immutable Recovery checkpoint with the reason, advances the same attempt's assignment generation, releases the old Working slot and changes the agent to Assigned. This is not an inactivity detector or automatic response to a harness stall. Old-generation commands fail, while source and review records remain intact.

Both commands are opt-in V2 Git-write MCP tools with normal stable operation UUIDs and local permissions. Migration, live V1 and production server state are untouched.
