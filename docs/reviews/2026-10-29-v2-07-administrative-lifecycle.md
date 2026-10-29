# V2-07 administrative lifecycle implementation review

## Decision and authority model

Administrative transitions are **explicit, caller-operation-ID and expected-version
guarded**, and committed to the same canonical orphan Git ledger as assignments,
review requests and execution slots. No agent state derives from inactivity. No
server-side database or state was added. The source-controlled backlog is not the
runtime task ledger.

An administrative `ParkAttempt` or `BlockAttempt` changes an eligible
Queued/Acknowledged/Working/Paused attempt to Parked or Blocked. When the old
attempt is still focused, the old agent becomes Completed **with no focused
attempt and no Working slot**; the agent does not automatically become Parked.
An independently applied `ParkAgent` still controls agent-level parking.
When an agent has already moved to another task, an unrelated newer focus
or Working slot is never overwritten.

`ResumeAttempt` requires the current Parked/Blocked phase, exact task,
attempt and agent versions and generation, and explicit reason. It records
a new immutable assignment generation with reason `AdministrativeResume`,
moves the attempt to Queued, and does not automatically acquire an agent
slot. The orchestrator must select it with ReissueQueuedAttempt; BeginAttempt
then starts the new generation. Old issued generations cannot be replayed.

`CancelTask`, `AbandonAttempt` and `TransferOwner` **atomically**
withdraw an active Unclaimed or Claimed review, clear the attempt's active
review pointer, append an immutable review claim/withdrawal event, release
the old focused slot if still owned, and append a task lifecycle event.
No claimed review is silently released for owner revision replacement.
Previously submitted review snapshots and decisions remain immutable.
Abandonment returns the task to Open and clears its current attempt;
cancellation marks it Cancelled with historical attempt retained.
An Open task can be cancelled through explicit CancelOpenTask.

`TransferOwner` supersedes the current primary attempt and creates a
distinct new attempt ID with a fresh assignment generation, pinned task
spec and new registered owner/reviewer. The previous owner/review/attempt
remains historically addressable, including its source refs.
**Source repository/base/ref identity is not inherited** by the new owner;
they must explicitly bind their own approved source. Transfers reject
already accepted/integrating attempts and busy new owners. The old
Working slot is released only when it really belongs to that attempt.

`ReopenTask` is explicit and audited for Closed/Cancelled tasks. It
returns the task to Open with no active attempt, retaining former attempts,
source reviews, integration evidence, non-source approvals and lifecycle
records. A later assignment creates a new attempt; no old generation
becomes current again.

## No-source closure

Documentation or other no-source tasks are not declared Integrated.
CloseNonSourceTask requires **Finished** implementation, a registered
approver different from the implementation owner, meaningful acceptance
criteria and explicit evidence, exact task and attempt versions, and no
source binding, source review, accepted patch or integration action.
The operation persists a separate immutable `NonSourceClosureApproval`
with approver/owner, pinned spec blob, criteria, evidence, operation UUID
and `NoSourceIntegration=true`, alongside task Closed and an immutable
lifecycle event. `V2GetNonSourceApproval` retrieves a specific approval
by immutable sequence.

Existing source integration/closure semantics are untouched: a source
task must still have verified actual integration; no new command
pretends a review approval equals a Git merge or push.

## Immutable audit and reader

Each admin transition appends
`tasks/<task>/lifecycle/<sequence>.json` with operation UUID, action,
actor, reason, previous/new task status, attempt phase and owner,
assignment generation, review request disposition and optional evidence.
`V2TaskLifecycleHistory` reads verified canonical snapshot history;
`GetTask` and `GetAgentInbox` project the new states. Agent Completed
with no focus is now explicitly valid only when its Working slot is empty.
The task reviewer inbox and unacknowledged feedback remain independently
derived from stored records, including when an old task is cancelled
while its owner is Working elsewhere.

All administrative MCP actions use the existing V2 task read/write wire
commands and local explicit V2 binding. The experimental protocol is
**3.33**, because V2TaskWriteCommand adds ExpectedTaskVersion. All
mutations require Git-write scope and the existing local approval gate.

## Security and boundaries

The operator actor is a logical `Orchestrator` label and the no-source
approver is a registered agent label. These are **not cryptographic
authorization**; subject-bound operator/reviewer identity and privacy
controls remain V2-11 requirements. There is no automatic staleness
recovery, new server scheduler or database. No production deployment,
push, live ledger initialization or V1 migration has been performed.

## Verification

Disposable Git remote tests cover: Assigned/Acknowledged/Ready/Working
attempt park/block; independent agent Parked; release and reissue of
generation; wrong/stale versions; claimed review withdrawal on cancellation
and owner transfer; late reviewer feedback after a newer Working assignment;
explicit Open cancellation and reopening; abandoned task reuse; no-source
owner self-approval rejection, empty-evidence rejection, immutable approval
lookup; and typed client protocol operations. MCP catalog and permission
metadata are tested by the server suite.

Final suite results are recorded in the backlog when verification completes.
