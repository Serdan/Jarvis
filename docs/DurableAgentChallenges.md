# Durable agent challenges (protocol 3.65)

Challenges coordinate agents through the existing **verified Git orchestration ledger**. They do not need browser tabs, hooks, a server-side scheduler, a workflow-expression language, or notification delivery. The initial completion policy is **ALL**; there is **no completion action**.

## Challenge state and ownership

A challenge has a stable 32-character lowercase UUID (N format), an issuing agent, title, optional task context, version, 1–32 ordered conditions, state, timestamps and completion policy `All`. Each condition has a deterministic ID, a registered owner agent ID, description, state, evidence, reason and update timestamp.

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
| `create_challenge` | `title`, `conditions: [{ownerAgentId, description}, ...]` | `taskId`, `operationId` |
| `list_challenges` | — | `limit`, `afterChallengeId`, `expectedSnapshotCommit` |
| `get_challenge` | `challengeId` | `eventLimit`, `afterEventSequence`, `expectedSnapshotCommit` |
| `resolve_challenge_condition` | `challengeId`, `conditionId`, `expectedVersion`, `outcome` (`Satisfied`/`Blocked`), `evidence` | `reason` (required if Blocked), `operationId` |
| `reopen_challenge_condition` | `challengeId`, `conditionId`, `expectedVersion`, `reason` | `operationId` |
| `cancel_challenge` | `challengeId`, `expectedVersion`, `reason` | `operationId` |
| `block_on_challenges` | `taskId`, `attemptId`, `reason`, `challengeIds`, `expectedTaskVersion`, `expectedAttemptVersion`, `expectedAgentVersion`, `assignmentGeneration` | `challengeIds` may be empty (manual wait); `operationId` |
| `requeue_challenge_blocked_attempt` | `taskId`, `attemptId`, `reason`, task/attempt/agent expected versions, `assignmentGeneration` | `operationId` |

The issuer and condition owners can read a challenge's full current evidence and history. Unrelated agents cannot. `list_challenges` gives compact summaries of issued/owned challenges, condition counts, state, and the caller's outstanding condition IDs, **without copying large evidence payloads**. It is paginated with `limit` 1–50 (default 50); use response `NextCursor` and `SnapshotCommit` for subsequent pages. `get_challenge` gives current full condition state and evidence plus a bounded audit-event page of 1–16 events (default 16); use response `NextEventSequence` and `SnapshotCommit` to page through immutable history. Cursor zero ends the event stream. If the ledger snapshot changes, restart pagination. These challenge reads are **observational**: they do not implicitly activate a Ready agent.

Writes use an optional caller-stable operation UUID. A new challenge ID is the create operation UUID in N format; condition IDs derive deterministically from it and their positions. A retry with the **same operation ID and exact arguments** returns the original durable receipt without executing another transition. Reusing an operation ID with different arguments is rejected. Mutations compare the exact challenge version; task/attempt transitions compare task, attempt, agent versions and assignment generation. Competing clients must reread stale versions.

## Ledger records

Challenge records and events live on the existing verified orphan Git ledger branch, independent of source branches:

- `challenges/<challenge-id>/challenge.json`: typed versioned challenge state, conditions, current results and evidence.
- `challenges/<challenge-id>/events/<ledger-sequence>.json`: immutable create/resolve/reopen/cancel events, action, actor, evidence, reason and time.
- `tasks/<task-id>/attempts/<attempt-id>/challenge-wait.json`: versioned attempt dependency, reason, owner, linked challenge IDs, wait state and timestamps.
- Existing task, attempt, agent, assignment and lifecycle audit records are updated **atomically** when the attempt blocks or explicitly requeues.

Canonical state plus immutable events and the operation journal provide exactly-once durable mutation semantics despite duplicate requests, retries and multiple Git clients. An accepted transition and its event are one Git ledger transaction with expected blob OIDs. **No notification entity is part of this transaction**; recipient notification settings never affect CAS, challenge completion, or evidence persistence.

The canonical pending-operation journal protects uncertain requests and exact operation-ID replay. Every mutation fails closed for unavailable, untrusted or mismatched ledger bindings.

## Blocked attempts and explicit requeue

A **Blocked challenge** differs from a **Blocked task attempt**. The latter uses the existing attempt phase `Blocked`, not a new execution state. `block_on_challenges`, authorized by the active attempt owner, atomically validates up to 16 distinct challenge IDs and the current assignment, pins the reason/dependencies, moves the attempt to `Blocked`, releases the agent from Working to an unassigned disposition `NeedsWork`, and preserves its task owner, generation and review/integration history. Linking a challenge requires issuer/condition-owner visibility. An empty dependency list is an explicit **manual block**. The inbox exposes `Blocked` and `BlockedChallengeWaits` with reason and links. No notification is generated.

Condition satisfaction changes only the challenge record and event; blocked attempts are never automatically requeued. After **every** linked challenge reaches Satisfied, the same attempt owner may call `requeue_challenge_blocked_attempt` with exact versions and a reason. Requeue atomically marks the wait Requeued, increments generation, creates a new assignment issue, and puts the owner **Ready, never Working**. An ordinary later agent activity starts Working. Partial satisfaction, cancelled/blocked dependency, stale identity, mismatched task or busy owner reject requeue. The generic administrative ResumeAttempt API cannot bypass an active challenge wait. Taskless agents retain their existing unassigned state; no fabricated Waiting state or scheduler is introduced.

## Limits, testing and rollout

A challenge has 1–32 conditions; titles/descriptions up to 2048 UTF-8 bytes each, evidence 8192 bytes per condition and reason 4096 bytes. Challenge summaries cap title previews at 160 UTF-8 bytes. Lists and history are paginated; **full evidence is retained unchanged** in canonical records/events.

Two independent Git-clone tests cover independent condition owners, partial/final satisfaction, concurrent condition races, operation-ID retries, authorization, Blocked/reopen/cancel and audit history, maximum-size evidence, paginated reads, zero notification creation on every challenge transition, blocked-attempt dependencies, administrative bypass rejection and explicit Ready requeue.

Protocol **3.65** adds typed `ChallengeRead` and `ChallengeWrite` wire commands and eight MCP methods. Coordinate client/server release and refresh the public tool catalog. The source branch initially targets .NET 10; integrate carefully with current .NET 11 main and already-integrated auto-job protocol changes, and rerun .NET 11 Release tests after conflicts are resolved. **Do not deploy without explicit operator approval.**
