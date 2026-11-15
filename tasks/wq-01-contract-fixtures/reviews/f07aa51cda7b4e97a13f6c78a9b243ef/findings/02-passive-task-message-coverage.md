# P2 — Passive Task-message intake and acknowledgement bypass verified message-index coverage

**Severity:** P2 — missing fail-closed precondition for operator-attention consistency.  
**Frozen source:** `4b8d76eeccf02b2dd12d0291281401447bc6d700`  
**Locations:** `Server/WorkQueueContracts.fs:299-307` (`completeCoverage`), `:884-907` (`claimAncillaryMessage`), `:934-955` (`completeAncillaryMessage`).  
**Accepted architecture:** §5 requires verified queue/message source-high-water/index coverage before acting; §11 (lines 367-375) requires CAS-bound exclusive Task/Role handler responsibility, and that operator-message indexes observe the same verified coverage discipline as work queues.

## Observation

The Role-stage-backed path of `claimAncillaryMessage` calls `orderAvailable`, which first calls `completeCoverage`; an incomplete `MessageCovered`/queue/source/graph projection therefore rejects Role-message work intake.

The **workless passive Task-message path** (`message.WorkId = None`) instead directly adds the message ID to `launch.AncillaryMessages` and sets `ClaimedAncillary`, without `completeCoverage` or any target-subject producer/high-water verification. The corresponding `completeAncillaryMessage` `None -> Ok(markHandled current)` branch does not call `projectDependenciesChanged` or `completeCoverage` either. Thus a Task message may be claimed **and marked Handled** in a project snapshot whose `MessageCovered = false`, `QueueCovered = false` or `SourceCovered = false`, although the same snapshot must reject `orderAvailable` and Role-message dispatch.

A concrete pure-contract state is an active Registered L whose primary Plan has `SubjectId = task-T`, an existing Pending `Subject.Task task-T` message M with `WorkId = None`, and a project snapshot with `MessageCovered = false`. Supplying the latest `Project.Version` and fresh operation IDs allows `claimAncillaryMessage` and then `completeAncillaryMessage` to succeed. This is a static invariant counterexample; the new 57 passing focused cases do not test this missing-coverage branch.

## Impact and correction

Incomplete operator-message coverage cannot establish that the current subject queue/handler projection is authoritative. Accepting a claim and acknowledgement while the index is known incomplete violates the fail-closed policy and can omit other pending subject messages from the handler's observed queue/cursor. It is especially easy to miss because Task attention is intentionally *passive* and does not use the work-stage ordering function.

At claim and acknowledgement, enforce an explicit verified canonical message/subject cursor/high-water precondition under the same project CAS. The pure Phase A fixture may use `completeCoverage` conservatively; a later implementation may check a narrower authoritative subject-specific coverage witness if it is equally fail-closed. Preserve valid passive Task-message behavior when complete, with no new Scheduler demand.

**Negative tests:** start from a valid active Plan and Pending Task message; turn `MessageCovered`, `QueueCovered` and `SourceCovered` false independently, assert that both claim and completion reject without changing state or handler generation, then restore verified coverage and show one legitimate ack/replay. Race a producer high-water change against a stale two-client intake. Confirm Role-stage-backed path remains fenced and there is no accidental Task-message launch.

**Disposition:** Required Phase A hardening; no claim of an already-deployed runtime issue.
