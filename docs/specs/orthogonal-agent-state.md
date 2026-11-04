# Orthogonal agent assignment and execution state

**Status:** Implemented in source with protocol 3.46; not yet deployed. Existing live v2 ledgers require an explicit conversion.

**Scope:** Jarvis Git-backed task/agent ledger, public task commands, inbox/TUI projections, and one-time conversion of existing ledger records.

**Related architecture:** [orchestration-architecture.md](orchestration-architecture.md), especially section 3 and its working-session rules.

## 1. Decision

Replace the exclusive agent lifecycle `Assigned | Ready | Working | Completed | Parked` with three independent facts:

1. **Current assignment:** an optional reference to a task/attempt. Ownership and attempt progress remain recorded on the task/attempt.
2. **Execution:** `Stopped | Working`. This is *logical agent execution*, not a connection or process liveness indicator.
3. **Unassigned disposition:** `NeedsWork | Parked`, meaningful only when the current assignment is absent. `Parked` means the orchestrator explicitly found no work for that agent (or the agent has just been registered), not simply that the agent is inactive.

`Ready`, `Working`, `Needs work` and `Parked` are **derived display statuses**, not competing stored states. There is no stored `Assigned`, `Ready`, `Completed` or `Idle` status.

The task's lifecycle, the attempt's progress, and all reviews remain independent. The design simplifies the existing implementation; it does not introduce another scheduler, state machine framework, approval workflow, database, or global execution lock.

## 2. Canonical records and authority

### Agent record (conceptual shape)

```text
Agent {
    agentId, version,
    currentAssignment: { taskId, attemptId } | null,
    execution: Stopped | Working,
    unassignedDisposition: NeedsWork | Parked | null,
    reason, sequence,
    executionEnteredAtUtc,
    workingTurnStartedAtUtc?,
    activeSessionId?,
    associatedSessionIds[]
}
```

The shape is conceptual, not a requirement for exact .NET property names.

- `currentAssignment` is the **single current assignment slot**. It points to the authoritative task/attempt; it does not duplicate the task spec, owner, phase, issue, or checkpoint. An attempt retains its `ownerAgentId` even after it is finished, blocked, deferred, abandoned or superseded.
- `execution` records whether that agent is logically in a Working turn. `Stopped` does not mean the process has exited, the ChatGPT session is unavailable, or the agent cannot receive review feedback.
- `unassignedDisposition` is **null if and only if** `currentAssignment` is present. A present disposition always implies `execution = Stopped`.
- A currently assigned agent can be Stopped/Ready or Working; it cannot be unassigned/Parked and Working simultaneously.
- An agent's current assignment identifies both the task **and** the attempt. Do not rely on a bare attempt ID being globally unique across unrelated tasks.
- Remove the legacy redundant `state`, `focusAttemptId` and `activeAttemptId` fields in the canonical converted representation. A current Working attempt is resolved using `currentAssignment`, not a second active pointer.
- Keep the existing version checks, explicit reason/sequence audit, provider-independent session association, and Working-turn timestamps. An assignment change/reissue resets current-generation session associations as appropriate, without deleting historical associations from Git history.

### Attempt progress

Store **implementation progress**, not whether the agent is currently executing. Target phases:

| Phase | Meaning |
| --- | --- |
| `Queued` | Work is assigned or ready to be reassigned but has not started in the current generation |
| `Started` | Work has begun; may be running or yielded/stopped |
| `Deferred` | Orchestrator explicitly postponed the attempt (`ParkAttempt`) |
| `Blocked` | Orchestrator explicitly recorded a blocking condition |
| `Finished` | Agent completed its responsibility |
| `Abandoned` | Attempt intentionally retired |
| `Superseded` | Another attempt replaced this attempt |

`Working` and `Paused` disappear as attempt phases: execution owns that distinction. `Acknowledged` disappears as a phase: the existing immutable assignment acknowledgment receipt owns that fact. No review verdict is an implementation phase; retire historical `Accepted` as a current-phase value.

A `Started` attempt remains `Started` across Working ↔ Stopped transitions. A checkpoint does not change its phase or stop execution. `Deferred` and `Blocked` preserve the existing attempt, owner, history, checkpoints and source context.

### Ownership versus current assignment

- A task's current attempt and its recorded owner remain authoritative about **who owns that work**. An agent's current assignment points to that attempt only while it occupies the agent's execution/attention slot.
- Finishing, deferring or blocking an attempt can free the slot without erasing its owner or task history. An agent may therefore own historical/deferred work while currently executing another task.
- At most one current assignment per agent; at most one current implementation attempt/owner per task. No concurrent implementation of one task.
- A current agent assignment must point to the task's current attempt with matching owner. The converse is **not** required: a task may retain a blocked, deferred or finished attempt with no current agent assignment.
- If a tool observes genuinely inconsistent current pointers, it reports the mismatch rather than silently reassigning someone. Do not add distributed locks or elaborate ownership proofs.

## 3. Display and operator projections

| Current assignment | Execution | Disposition | Derived agent display |
| --- | --- | --- | --- |
| None | Stopped | NeedsWork | **Needs work** |
| None | Stopped | Parked | **Parked** |
| Actionable attempt | Stopped | null | **Ready** |
| Started attempt | Working | null | **Working** |

An **actionable** attempt here means the current assignment is `Queued` or `Started`, with an assignment generation that can be started/resumed. A normal blocked/deferred attempt does **not** remain in the agent's current slot.

`Blocked` and `Deferred` are **attempt/task badges or queue entries**, not primary agent statuses. A blocked attempt should still be visible to the orchestrator; the agent becomes `Needs work` after its slot is released, unless later explicitly parked.

Inbox projections derive, rather than store:

- `OperatorActionRequired`: current assignment present, execution Stopped, attempt actionable (display Ready).
- `NeedsOrchestratorAssignment`: unassigned disposition NeedsWork.
- `CurrentAssignment`: nullable task/attempt reference; no ambiguity between historical owner and current slot.
- `CanAcceptAssignment`: current assignment is absent. Do not use `SlotAvailable` to mean merely "not Working", because a Ready agent is Stopped **but already assigned**.
- `Unassigned`, if kept for clients: simply `currentAssignment = null`. Unrelated available reviews, pending feedback or historical task ownership must not change it.
- Pending/available reviews and unacknowledged feedback remain **separate inbox sections**; they do not prevent task assignment or alter display status.

Existing public commands may keep their names, but returned fields and descriptive text must reflect the new meaning. Prefer removing redundant projections over adding alias fields indefinitely. The TUI should show the derived status, current task where present, and independent blocked/deferred work and review notifications.

## 4. Transitions

All mutations remain explicit, Git-ledger-backed, idempotent by operation ID and checked against appropriate versions/generations. The table describes observable results, not new mandatory API names.

| Operation | Agent assignment/execution/disposition | Attempt effect | Task/review effect |
| --- | --- | --- | --- |
| `RegisterAgent` | None / Stopped / Parked | None | None |
| `AssignTask` | Assignment set / Stopped / null → **Ready** | Create Queued attempt/issue | Task InProgress as today |
| `AcknowledgeAssignment` | Unchanged (Ready) | Write acknowledgment receipt, phase stays Queued | None |
| `ResumeAgent` on newly assigned Ready agent | Keep assignment / Working / null | Queued → Started | Atomic assignment acknowledgement |
| `YieldAgent` | Keep assignment / Stopped / null → **Ready** | Remains Started; add yield checkpoint | None |
| `ResumeAgent` on stopped agent | Keep assignment / Working / null | Remains Started | None |
| `ResumeAgent` after reported stall | Remains Working | Remains Started | Explicit recovery event and new timed segment only |
| `CheckpointAttempt` | Unchanged | Record progress checkpoint; remains Started | None |
| `CompleteAgentWork` | None / Stopped / NeedsWork | Started → Finished, completion checkpoint | Does **not** close task or decide review |
| `ParkAgent` | None / Stopped / Parked | None | Explicit no-work disposition; no preemption |
| `ParkAttempt` | Release assignment → None / Stopped / NeedsWork if that attempt occupied it | → Deferred | Task remains InProgress; reviews untouched |
| `BlockAttempt` | Release assignment → None / Stopped / NeedsWork if that attempt occupied it | → Blocked | Task remains InProgress; reviews untouched |
| `ResumeAttempt` | Assign to chosen/recorded owner when their slot is free → Ready | Deferred/Blocked → Queued, issue next generation | Task remains InProgress; reviews untouched |
| `AbandonAttempt` | Release matching current assignment → NeedsWork | → Abandoned | InProgress task → Open; Closed task stays Closed |
| `CancelTask` (assigned attempt) | Release matching assignment → NeedsWork | → Abandoned | Task → Cancelled; reviews untouched |
| `TransferOwner` | Release prior matching slot; assign new owner → Ready | Prior attempt Superseded; successor Queued | Change task attempt, not reviews |
| `CloseTask` | **No change** | **No change** | Task → Closed, independently |
| `ReopenTask` | No implicit assignment | Preserve history; no attempt resurrection | Reopen only when the previous attempt is resolved |

Clarifications:

- A newly assigned agent is **Ready immediately**. Its first `ResumeAgent` starts the Queued attempt and atomically records assignment acknowledgement. Later `ResumeAgent` calls resume a Started Ready attempt or explicitly recover a stalled Working session. `BeginAttempt` is retired from the public MCP surface; its historical internal ledger operation remains readable.
- Completion is a terminal **attempt event**. The agent becomes Needs work, not a persistent Completed state. The orchestrator can assign a new task or explicitly ParkAgent.
- `ParkAgent` is only for an unassigned agent. `ParkAttempt` is different: it defers a task attempt and frees its owner to take other work. Their names need not change just because the underlying model is simpler.
- An explicitly Parked, unassigned agent **can be assigned**. Assignment is an explicit orchestrator decision that supersedes the earlier no-work disposition; no intermediate "unpark" operation is necessary.
- `ResumeAttempt` is an explicit reassignment of the deferred/blocked work, not an automatic resumption when a tool call arrives. If the recorded owner is occupied by another assignment, reject with a useful busy result; the orchestrator may transfer ownership or wait.
- `ReissueQueuedAttempt` must be able to allocate an unoccupied slot for a Queued attempt without manufacturing an additional attempt. Reissuing instructions/adopting specs for an already Ready assignment does not create a new agent state.
- `RecoverAgentAssignment` remains explicit and resets the same assignment to Stopped/Ready with a new generation and preserved checkpoint/history; it never auto-interrupts an unrelated task.
- Completion or abandonment **after task closure** remains supported. Closed task state is not silently reopened or revoked by agent operations.
- Do not allow a generic review, Git, file or job action to start/stop execution or to acquire/release an assignment.
- A currently assigned and stopped agent cannot receive another assignment without explicit completion, abandonment, defer/block or transfer of the existing assignment.

## 5. Working turns, sessions, and statistics

The existing soft-stop guidance at 25 minutes and hard-stop guidance at 30 minutes is unchanged. A normal Working turn must yield or complete before ending. A harness stall can leave logical execution Working until an explicit recovery; disconnected devices do not automatically change execution.

- Entering Working sets the Working-interval start and the current timed segment start. Yield/complete/recovery transitions update only the relevant timestamps. Checkpoints and unrelated review actions do neither.
- Explicit stall recovery while still Working retains the logical Working interval start, creates an event, and restarts the timed segment. A changed provider session ID by itself never implies a stall.
- Retain provider-supplied opaque IDs, ordered associations, and the existing warning to superseded sessions. Assignment/generation changes reset *current* associations; historical associations remain recoverable from Git.
- Distinguish time in **Working execution** from time **assigned-but-stopped (Ready)** and **unassigned NeedsWork/Parked**. Derive these from timestamped state/assignment/disposition transitions and Git history rather than persisting duration counters.
- A finished attempt is counted as a completion event, not as days spent in a synthetic "Completed" agent state. All metrics remain wall-clock observations, not productive CPU-time claims.
- Keep only timestamps necessary for the current interval/segment and reconstruct earlier boundaries from existing event/Git history. Do not add a second stored "display state" or counters.

## 6. Migration of existing Git-ledger data

This changes canonical JSON shape; it cannot be deployed by simply changing enum validation. Perform a **one-time, explicit, checked conversion of the bound Git orphan ledger**, preserving its parent history. No old/new dual-write mode, V1 fallback, migration daemon, or permanent legacy tool aliases.

### Conversion mapping

| Existing agent `state` | New agent representation |
| --- | --- |
| Assigned | Current focus assignment, Stopped, disposition null |
| Ready | Current focus assignment, Stopped, disposition null |
| Working | Current active/focused assignment, Working, disposition null |
| Completed | No current assignment, Stopped, NeedsWork |
| Parked | No current assignment, Stopped, Parked |

| Existing attempt `phase` | New phase |
| --- | --- |
| Queued | Queued |
| Acknowledged | Queued (the existing receipt preserves acknowledgment) |
| Working | Started |
| Paused | Started |
| Parked | Deferred |
| Blocked | Blocked |
| Finished | Finished |
| Abandoned | Abandoned |
| Superseded | Superseded |
| Accepted (obsolete) | Inspect historical case; retain original event/history and do not invent an unfinished/finished outcome blindly |

Important legacy cases:

- Existing Completed agents can retain a *historical* focus on a Finished attempt or a Queued follow-up. Neither is a current agent assignment after conversion; queued follow-up work remains reachable for explicit reissue.
- Existing blocked/parked attempts may retain an owner without occupying that agent's current slot.
- An old Ready/Working attempt must resolve to exactly one task, attempt and owner; ambiguous or contradictory live records require an explicit diagnostic and operator decision, not an arbitrary guess.
- Preserve immutable assignment issues, acknowledgment receipts, checkpoints, review snapshots/findings, agent session history, operation receipts and audit events. Do not rewrite source branches or reviews.
- Convert affected agent and attempt entities to a **new explicit record schema version**, with atomic ledger commit(s) using existing Git transaction semantics. Keep historical Git commits readable by normal Git inspection; the new authoritative head uses one coherent schema. If conversion fails, do not partially activate the new shape.
- Implement and ship a compatible client/server protocol together. Explicitly inspect the live ledger and select/approve the conversion before switching normal writes to the new record shape. Existing task data must not disappear merely because the client upgraded.

## 7. Acceptance criteria

1. A freshly registered agent displays Parked, then displays Ready immediately after assignment; no persisted Assigned state is created.
2. A Ready agent can start Working, yield Ready, resume Working, and complete to Needs work. The attempt is Started throughout start/yield/resume and Finished only on completion.
3. A Needs-work agent can be explicitly parked; a Parked agent can be assigned directly; no automatic parking on completion.
4. Blocking or deferring an attempt preserves it and its owner/checkpoints while freeing the agent to work on another task. Resuming the old attempt is explicit and rejects a busy owner.
5. No unrelated review or feedback notification changes agent assignment, execution or the Unassigned/NeedsWork inbox result.
6. Task closure neither releases an assignment nor changes execution. The assignment can subsequently complete or be abandoned, leaving the task Closed.
7. Acknowledgment receipts remain authoritative after the Acknowledged attempt phase is removed. New and existing assignment generations are handled consistently.
8. Reissue, explicit recovery, transfer, cancellation, closed-task abandonment and delayed feedback retain their intended behavior without resurrecting obsolete states.
9. Stalled Working recovery preserves ownership and timestamps correctly; ordinary resume begins a new Working interval; session identity remains informative, not an authorization gate.
10. The TUI, inbox and MCP descriptions use derived statuses and do not expose contradictory versions of the same state.
11. Conversion tests use disposable Git remotes with representative legacy records, including Completed+Queued and deferred work; verify immutable history and review documents survive, and ambiguous data is reported rather than silently guessed.
12. Full Client.Tests and Server.Tests pass in Release. Validate a two-client round-trip against a disposable shared ledger before any live conversion.

## 8. Out of scope

No automatic task scheduling, forced work preemption, special "reviewer assignment" state, new review gate, background task monitoring, database, distributed locking, retrospective import of every historic task, or generalized migration framework.

**Deployment boundary:** Implementation and disposable two-clone conversion tests do not authorize migrating a live ledger or deploying binaries. The one-time `MigrateOrthogonalAgentState` command requires the exact previously inspected `LedgerHealth` head. Explicitly approve and perform production cutover separately.
