# P1 — Workless Task message can smuggle a WorkId, claim unrelated work, and strand the primary launch

**Independent reviewer:** Reviewer 1  
**Review:** `36a90a18735e40eda086c879f78f6f05`  
**Exact frozen SHA:** `4f3b47d85dfe26cbd5a48da37e98f49e4cdfc6bf`  
**Severity:** P1 — project work-claim integrity, passive operator-message semantics, and terminal/account-token liveness.  
**Source:** `Server/WorkQueueContracts.fs:569-585, 900-950, 955-997, 781-845`.  
**Architecture:** `docs/specs/work-queues-and-ephemeral-executions.md` §11 lines 367-375 (Task is passive attention with no standalone work stage; ancillary Role messages may bind only their own message-stage generation; launch terminal must safely reconcile handler leases).

## Independently identified reachable path

1. `postMessage` matches `(Subject.Task _, None)` for the supplied `roleWork` parameter at lines 573-575 but **does not constrain `message.WorkId`**. It adds the original user-supplied `SubjectMessage` unchanged. Thus a Task message T addressed to `task-Plan`, with `WorkId = Some "Victim"`, is accepted, even though a Task message is required to have no work generation.
2. Let a Registered Lead launch L hold a valid, ungated primary claim to Plan with `SubjectId="task-Plan"`. Independently publish `Victim` as an Available implementation stage, `Kind=Implementation`, `QueueId="implementation"`. No other unclaimed launch credits are required.
3. `claimAncillaryMessage(p.Version, freshOperationId, "L", "T", p)` passes the new `hasLivePrimaryActivation` guard and Task subject equality. It chooses the **`Some workId` Role branch solely because of the untrusted field**, and at lines 928-949 checks only `orderAvailable`, `ready`, and matching of other unclaimed launches. It never requires `message.Subject=Role`, `work.Kind=RoleMessage T`, or compatible queue for L. With zero other credits, `matchCredits` succeeds on the empty set. This marks implementation Victim `Claimed L` and Task T `ClaimedAncillary L` despite no legitimate authority to claim Victim.
4. The resulting state cannot be acknowledged normally: `completeAncillaryMessage` requires `work.Kind=RoleMessage T` at lines 975-992 and rejects the forged claim. If L attempts to finish Plan, `finishClaim -> releaseAncillaryOnTerminal` requires the same Role-message kind at lines 795-823 and rejects the entire project terminal transition with `InvalidState "unreconciled ancillary Role-message claim"`. Neither Plan completion nor independent account-token release is available through that normal path.

The issue is not an assertion that an external operator can inject the WorkId field into deployed production. It is a **fail-open exported Phase A contract transition** that accepts an invalid Task message record and then uses its field as a cross-queue work-claim capability. The current patch's new passive Task coverage tests all construct `WorkId=None`; 71/71 focused and 206/206 full Server tests therefore do not exercise this negative input.

## Required correction and regression

- Make Task/Role attention shape a trusted typed distinction or reject invalid original records on publication: Task must have `WorkId=None`, Pending initial state, canonical generation; Role publication must bind the exact one corresponding RoleMessage work item and subject/queue.
- Independently fail closed at **ancillary claim admission**: the `Some workId` path must require `Subject.Role`, `WorkKind.RoleMessage messageId`, exact work/message/role/queue association, and compatibility before changing any stage/handler. Do not rely solely on validation at post or acknowledgement.
- Add a deterministic fixture: publish Plan and unrelated Available Implementation Victim; admit/claim Lead L on Plan; post Task T with `WorkId=Some "Victim"`, then assert rejection at post and at a separately constructed pre-existing malformed message snapshot, with Victim still Available, Task unclaimed, Plan terminable and global token releasable.
- Cover an invalid Role message WorkId/work-kind/queue mismatch, distinct incompatible actor role, competing credit, stale CAS, and exact operation-id replay. Verify no orphaned `ClaimedAncillary` handler or account token.

**Disposition: blocking P1.** This frozen SHA must not be integrated. Correct in a new exact source commit and submit a new independent frozen review. This is pure contract analysis, not proof of a deployed scheduler bug.
