# Durable agent challenges (protocol 3.65)

Challenges coordinate agents through the existing **verified Git orchestration ledger**. They do not need browser tabs, hooks, a server-side scheduler, a workflow-expression language, or notification delivery. The initial completion policy is **ALL**; there is **no completion action**.

## Challenge state and ownership

A challenge has a stable 32-character lowercase UUID (N format), an issuing agent, title, optional task context, version, 1–32 ordered conditions, state, timestamps and completion policy `All`. Each condition has a deterministic ID, an optional registered assignee, optional suggested assignee, description, state, evidence, reason and update timestamp. A registered project agent can explicitly subscribe to unassigned challenge dispatch without any name-based role privilege. The Lead role should instruct its holders to subscribe and promptly triage these conditions.

| Challenge state | Condition states | Meaning |
| --- | --- | --- |
| Pending | At least one Pending, none Blocked | Awaiting condition results |
| Blocked | At least one Blocked | Explicit issuer decision required |
| Satisfied | Every condition Satisfied | Terminal; canonical results and evidence retained |
| Cancelled | Explicit issuer cancellation | Terminal; cannot satisfy dependent work |

Only the named condition owner may resolve a Pending condition as **Satisfied** (with evidence) or **Blocked** (with a reason and optional evidence). A Blocked condition makes the aggregate challenge Blocked, with reason/evidence available to the issuer through the canonical challenge record and audit events. Only the issuer may reopen a Blocked condition or cancel an unfinished challenge. Reopening makes that condition Pending; prior outcome/reason/evidence remain in immutable event history. Satisfied and Cancelled challenges cannot be mutated.

**No challenge transition generates a notification.** This includes creation, partial/full satisfaction, newly Blocked conditions, reopening, cancellation, and blocked-task dependency transitions. There are no writes to `notifications/` or calls to `NotifyAgent` in the challenge workflow. Notification mute/block settings, delivery, or acknowledgement are not preconditions for challenge updates. Ordinary discretionary agent notifications are an unrelated capability and may be used separately for exceptional coordination.

## Public MCP tools

All methods require `projectName` and `agentId`. The authenticated OAuth user and the client's verified project binding are the security boundary; the source actor label must also match the independently attributed tool-call actor. Reads require `workspace:read`, writes `git:write` plus local permission confirmation.

| Tool | Required domain arguments | Optional inputs |
| --- | --- | --- |
| `create_challenge` | `title`, `conditions: [{ownerAgentId, suggestedAgentId, description}, ...]` | `taskId`, `operationId`; owner may be empty |
| `set_challenge_subscription` | `subscribed` (bool) | `operationId` |
| `assign_challenge_condition` | `challengeId`, `conditionId`, `expectedVersion`, `newOwnerAgentId` | `operationId` |
| `split_challenge_condition` | `challengeId`, `conditionId`, `expectedVersion`, `parts: [{ownerAgentId, suggestedAgentId, description}, ...]` (2–32) | `operationId` |
| `list_challenges` | — | `limit`, `afterChallengeId`, `expectedSnapshotCommit` |
| `get_challenge` | `challengeId` | `eventLimit`, `afterEventSequence`, `expectedSnapshotCommit` |
| `resolve_challenge_condition` | `challengeId`, `conditionId`, `expectedVersion`, `outcome` (`Satisfied`/`Blocked`), `evidence` | `reason` (required if Blocked), `operationId` |
| `reopen_challenge_condition` | `challengeId`, `conditionId`, `expectedVersion`, `reason` | `operationId` |
| `cancel_challenge` | `challengeId`, `expectedVersion`, `reason` | `operationId` |
| `block_on_challenges` | `taskId`, `attemptId`, `reason`, `challengeIds`, `expectedTaskVersion`, `expectedAttemptVersion`, `expectedAgentVersion`, `assignmentGeneration` | `challengeIds` may be empty (manual wait); `operationId` |
| `requeue_challenge_blocked_attempt` | `taskId`, `attemptId`, `reason`, task/attempt/agent expected versions, `assignmentGeneration` | `operationId` |

The issuer and condition owners can read a challenge's full current evidence and history. Subscribed agents can view unassigned pending conditions for triage; unrelated unsubscribed agents cannot. A subscribed dispatcher may CAS-assign an **unassigned Pending** condition to a registered agent, while the issuer may directly assign or reassign. Version races fail closed and every assignment has an immutable audit event. Subscription does not grant condition resolution rights: only the actual assignee resolves. Neither assignment nor subscription reassigns a canonical task.

The self-`agent_inbox` includes snapshot-consistent, bounded challenge sections: owned Pending conditions, subscribed unassigned triage conditions, recent issuer outcomes, and conditions attached to its blocked attempt. Each section has stable IDs, short previews, and omitted counts; `list_challenges` is **not** a mandatory extra polling step. `list_challenges` gives paginated summaries of visible challenges (`limit` 1–50); `get_challenge` provides full current evidence and a bounded event page (`eventLimit` 1–16). Use `NextCursor`/`NextEventSequence` and `SnapshotCommit` for consistent paging, restarting after a snapshot change. Challenge reads remain observational and do not activate Ready agents.

Writes use an optional caller-stable operation UUID. A new challenge ID is the create operation UUID in N format; condition IDs derive deterministically from it and their positions. Splitting one Pending condition into two or more parts retains its original ID for the first part and derives all additional IDs from the split operation UUID; the old description and evidence remain in immutable history. The issuer or an explicitly subscribed dispatcher of unassigned work may split, without changing canonical task ownership. A retry with the **same operation ID and exact arguments** returns the original durable receipt without executing another transition. Reusing an operation ID with different arguments is rejected. Mutations compare the exact challenge version; task/attempt transitions compare task, attempt, agent versions and assignment generation. Competing clients must reread stale versions.

## Ledger records

Challenge records and events live on the existing verified orphan Git ledger branch, independent of source branches:

- `challenges/<challenge-id>/challenge.json`: typed versioned challenge state, conditions, current results and evidence.
- `challenges/<challenge-id>/events/<ledger-sequence>.json`: immutable create/resolve/reopen/cancel events, action, actor, evidence, reason and time.
- `tasks/<task-id>/attempts/<attempt-id>/challenge-wait.json`: versioned attempt dependency, reason, owner, linked challenge IDs, wait state and timestamps.
- Existing task, attempt, agent, assignment and lifecycle audit records are updated **atomically** when an attempt blocks, its final linked challenge automatically releases it, or an eligible wait reconciles on owner-slot release.

Canonical state plus immutable events and the operation journal provide exactly-once durable mutation semantics despite duplicate requests, retries and multiple Git clients. An accepted transition and its event are one Git ledger transaction with expected blob OIDs. **No notification entity is part of this transaction**; recipient notification settings never affect CAS, challenge completion, or evidence persistence.

The canonical pending-operation journal protects uncertain requests and exact operation-ID replay. Every mutation fails closed for unavailable, untrusted or mismatched ledger bindings.

## Explicit challenge dependencies and automatic requeue

A **Blocked challenge** differs from a **Blocked task attempt**. The latter uses the existing attempt phase `Blocked`, not a new execution state. `block_on_challenges`, authorized by the active attempt owner, atomically validates up to 16 distinct challenge IDs and the current assignment, pins the reason/dependencies, moves the attempt to `Blocked`, releases the agent from Working to an unassigned disposition `NeedsWork`, and preserves its task owner, generation and review/integration history. Linking a challenge requires issuer/condition-owner visibility. An empty dependency list is an explicit **manual block**. The inbox exposes `Blocked` and `BlockedChallengeWaits` with reason and links. No notification is generated.

Challenges are **non-blocking by default**. Only an attempt explicitly linked with `block_on_challenges` waits. Upon final ALL satisfaction, the same canonical Git transaction also rechecks each linked wait's entire dependency set. Eligible blocked attempts with an idle `NeedsWork` owner atomically become `Queued` and assigned `Ready` (never `Working`): assignment generation, challenge wait status, and lifecycle event are persisted together without a notification. A later agent tool call activates its Ready slot normally. Partial, Blocked, Cancelled, or stale dependencies cannot release attempts.

If the owner is occupied or Parked, the wait becomes durably `Eligible` and does not change that owner's current assignment. Canonical owner-slot release and NeedsWork transition paths reconcile eligibility, revalidate the task, attempt, and every linked challenge, and requeue at most one pending attempt per available slot. Additional eligible waits remain durable until subsequent releases. The generic administrative ResumeAttempt API cannot bypass `Waiting` or `Eligible` links. The explicit `requeue_challenge_blocked_attempt` path remains available for manual zero-dependency blocks; it does not replace automatic challenge wakeups. No scheduler or session launcher is involved.

## Limits, testing and rollout

A challenge has 1–32 conditions; titles/descriptions up to 2048 UTF-8 bytes each, evidence 8192 bytes per condition and reason 4096 bytes. Challenge summaries cap title previews at 160 UTF-8 bytes. Lists and history are paginated; **full evidence is retained unchanged** in canonical records/events.

Independent two-clone Git tests cover condition owners, partial/final satisfaction, concurrent CAS and retries, authorization, Blocked/reopen/cancel and history, maximum-size evidence, paginated reads, zero challenge notification side effects, automatic Ready requeue, multiple dependencies, busy-owner durable eligibility, and administrative bypass rejection.

Protocol **3.65** introduces typed `ChallengeRead` and `ChallengeWrite` wire commands and eleven MCP methods. Coordinate client/server release and refresh the public tool catalog. Integrate with current .NET 11 main and the auto-job protocol changes, and rerun .NET 11 Release tests after resolving source conflicts. **Do not deploy without explicit operator approval.**
