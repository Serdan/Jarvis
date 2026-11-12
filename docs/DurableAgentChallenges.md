# Durable agent challenges (protocol 3.65)

Challenges coordinate agents through the existing **verified Git orchestration ledger**, not through browser tabs, hooks, a server-side scheduler, or a workflow-expression language. The first completion policy is **ALL**; the first completion action is **Notify**.

## Objects and ownership

A challenge has a stable 32-character lowercase UUID (N format), an issuing agent, a title, optional task context, a version, one to 32 ordered conditions, state, creation/update timestamps, and the fixed policies `All` and `Notify`. Each condition has a deterministic ID, explicit registered owner agent ID, description, state, evidence, reason, and update timestamp. States are:

| Challenge | Conditions | Meaning |
| --- | --- | --- |
| Pending | At least one Pending, none Blocked | Still awaiting results |
| Blocked | At least one Blocked | Needs an issuer decision; **not complete** |
| Satisfied | Every condition Satisfied | Terminal; exactly one completion notification |
| Cancelled | Explicit issuer cancellation | Terminal; never satisfies dependent work |

Only the named condition owner can resolve that condition as **Satisfied** (with evidence) or **Blocked** (with a reason and optional evidence). A blocked condition produces a normal issuer notification, retaining full reason/evidence in the ledger. The issuer alone may reopen a blocked condition or cancel an unfinished challenge. Reopening resets that condition to Pending; its prior outcome remains in the immutable event history. Already Satisfied and Cancelled challenges are immutable.

The issuing agent and condition owners may read the challenge, including its full evidence and history. Other agents cannot. `list_challenges` returns compact issued/owned challenge summaries (status, condition counts and caller-owned unresolved condition IDs), not full evidence. It has `limit` (1–50, default 50), optional `afterChallengeId`, and optional `expectedSnapshotCommit` for consistency across pages. Read the response `NextCursor` and `SnapshotCommit` from the previous page; if the ledger moves, restart paging instead of combining different snapshots. `get_challenge` returns one snapshot and its full immutable event sequence. These reads do **not** implicitly start a Ready agent.

## Public MCP tools

All tools require `projectName` and the requesting `agentId`. The authenticated OAuth user and the client's bound project provide the security boundary; agent labels do not independently authenticate a caller. Reads require `workspace:read`; writes require `git:write` and normal client permission checks.

| Tool | Required domain arguments | Other inputs |
| --- | --- | --- |
| `create_challenge` | `title`, `conditions: [{ownerAgentId, description}, ...]` | Optional `taskId`, `operationId` |
| `list_challenges` | — | — |
| `get_challenge` | `challengeId` | — |
| `resolve_challenge_condition` | `challengeId`, `conditionId`, `expectedVersion`, `outcome` (`Satisfied`/`Blocked`), `evidence` | `reason` required for Blocked |
| `reopen_challenge_condition` | `challengeId`, `conditionId`, `expectedVersion`, `reason` | — |
| `cancel_challenge` | `challengeId`, `expectedVersion`, `reason` | — |
| `block_on_challenges` | `taskId`, `attemptId`, `reason`, `challengeIds`, `expectedTaskVersion`, `expectedAttemptVersion`, `expectedAgentVersion`, `assignmentGeneration` | `challengeIds` may be empty, at most 16 |
| `requeue_challenge_blocked_attempt` | `taskId`, `attemptId`, `reason`, all three expected versions and `assignmentGeneration` | — |

Writes accept an optional caller-stable `operationId` UUID, generated and returned when omitted. A new challenge's ID is that operation UUID in N format. Its condition IDs derive deterministically from that UUID and their zero-based positions, so an accepted create replay returns the same IDs. Challenge mutations compare the **exact challenge version**, while attempt operations compare task, attempt, agent versions, assignment generation and owner simultaneously. If an operation times out or its response is lost, retry **the same operation ID and the same arguments**. Do not generate a new ID for an uncertain write.

The client owns the canonical pending-operation journal, and the ledger owns the idempotent transaction record. A different payload with an already used operation ID is not a harmless retry. All writes fail closed for unavailable or mismatched ledger bindings.

## Ledger records and notification semantics

All records are on the existing orphan Git ledger branch, independent of source branches:

- `challenges/<challenge-id>/challenge.json` — typed schema-v2 state and version.
- `challenges/<challenge-id>/events/<ledger-sequence>.json` — immutable timestamped create, resolve, reopen and cancel events.
- `notifications/<operation-id>.json` — existing normal notification format, atomically committed with the resolving transition (never a second network write).
- `tasks/<task-id>/attempts/<attempt-id>/challenge-wait.json` — bounded, versioned attempt dependency record, including reason and challenge IDs.
- Existing task, attempt, agent, assignment-generation, and lifecycle audit records are updated **together** when blocking/requeueing an attempt.

The ledger transaction is a conditional Git commit with expected blob OIDs, published by an atomic ref update. Competing clients retry against the latest verified snapshot; stale entity versions fail rather than silently overwrite each other. Reusing a committed operation ID returns the prior receipt without re-executing a notification. An issuer gets **one** completion notification when the last condition becomes Satisfied, not one per contributing condition.

Notifications contain compact per-condition result/evidence snippets, the challenge ID and an explicit `get_challenge` reference. They are limited to the canonical 4 KiB notification budget; **full evidence is never discarded** from the challenge record or immutable history. Large fields and Unicode are truncated only in the notification preview.

## Blocked attempts and explicit requeue

A **Blocked challenge** is distinct from a **Blocked task attempt**. The latter uses the existing attempt phase `Blocked`, not a new agent execution state. `block_on_challenges` is owner-authorized, requires a currently assigned Queued/Started attempt, and atomically:

1. Records the wait reason and zero/one/multiple challenge IDs, validating that linked challenges exist, are not cancelled, and do not refer to a different task.
2. Moves the attempt to `Blocked` while preserving its owner, assignment generation, review and integration history.
3. Releases the owner's active assignment and Working execution; the unassigned disposition becomes `NeedsWork`.
4. Appends an immutable lifecycle event. The inbox shows `Blocked` alongside `BlockedChallengeWaits`.

An empty dependency list is an **explicit manual block** with a reason. It does not imply any synthetic challenge. Taskless agents retain their existing unassigned dispositions; there is no fabricated Waiting state or new scheduler.

When challenges reach Satisfied, Jarvis **only notifies** the issuer. It does not reassign, resume, or launch the blocked owner. After every linked challenge is Satisfied, the owner may call `requeue_challenge_blocked_attempt` with fresh expected versions and an explicit reason. That atomic transaction marks the wait Requeued, increments the assignment generation, issues new assignment instructions and transitions the agent to **Ready, never Working**. A separate ordinary agent-attributed tool call can later begin Working. Partial satisfaction, a Cancelled/Blocked linked challenge, lost ownership, stale versions or an unrelated assignment reject requeue. The generic administrative ResumeAttempt route cannot bypass an active challenge wait.

## Limits and operational constraints

One challenge has 1–32 conditions. Its title and each description are bounded to 2048 UTF-8 bytes; individual evidence to 8192 bytes and reasons to 4096 bytes. Blocked attempts accept up to 16 distinct challenge references and require a reason. The canonical notification format remains bounded to 4096 UTF-8 bytes. Challenge listing is bounded to 4096 challenges per project.

Protocol **3.65** adds the typed `ChallengeRead` and `ChallengeWrite` wire commands with eight public MCP aliases. A client and server must be updated together, and their public tool catalogs refreshed. A separate in-flight P1 auto-promote branch uses protocol 3.64; integration must reconcile the two implementations before release. **Do not deploy without explicit operator approval.**

The real-ledger tests use two independent Git clones and cover independent condition owners, partial and final satisfaction, exactly-once notification, explicit issuer block/reopen/cancel, stale versions, two-writer concurrency, blocked-attempt dependency gating, bypass rejection, and an explicit Ready requeue.