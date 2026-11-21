# Phase 1: deterministic Jarvis-ledger planner

## Scope

The new pure module at scripts/jarvis-ledger-planner.mjs projects one fully materialized account/project snapshot into deterministic worker and reviewer candidates plus explicit holds. It does not fetch data, reserve capacity, claim reviews, launch agents, wake Challenges, or write to any ledger.

The module is intentionally separate from scripts/manager-heartbeat.mjs. That existing module plans one project's Manager tick from its own configuration, observation and local launch journal. This new contract is account-wide: it reconciles per-project task/attempt/agent/source-lease/review/Challenge state against cross-project capacity evidence and immutable review identities. Reusing the old local-journal input model would hide that authority boundary. This adds no second runtime, launch journal or dispatch framework; it adds only the pure ledger decision core and tests.

The planner does not use a local last-seen cache, legacy FeedbackStore attributed activity, Telos Work, Work Queue SQLite, or Work Scheduler prototype state as proof that an agent is idle.

## API and freshness

    import { planLedger } from "./jarvis-ledger-planner.mjs";

    const plan = planLedger(snapshot, nowUtc);
    // Optional policy override:
    const strictPlan = planLedger(snapshot, nowUtc, { maxAgeMs: 60_000 });

- nowUtc is an explicit offset-aware ISO-8601 timestamp. There are no clock reads inside the planner.
- The default maximum snapshot age is 180 seconds. The same input snapshot, nowUtc and options always produce the same plan.
- The account source and every project source carry a commit, ref and observation timestamp.
- Top-level completeness must explicitly confirm the account, project set and account capacity. Each project explicitly confirms tasks, agents, source leases, reviews, Challenges, submissions and project capacity.
- Missing, stale or incomplete account/capacity evidence suppresses every candidate. An invalid project snapshot suppresses candidates for that project while allowing other complete projects to be evaluated against the same verified account-wide capacity snapshot.
- Account and project capacity records must agree exactly for each project. An active/unknown capacity record referring to a project absent from the snapshot is a global hold.

## Normalized snapshot contract

The adapter that materializes this contract is deliberately outside Phase 1. It must read canonical Jarvis ledger state and provide a single internally consistent observation.

    {
      schemaVersion: 2,
      capturedAtUtc: "2026-11-20T14:00:00.000Z",
      source: {
        commit: "<40-64 lowercase hex>",
        ref: "refs/heads/account-ledger",
        observedAtUtc: "2026-11-20T14:00:00.000Z"
      },
      completeness: {
        account: true,
        projects: true,
        accountCapacity: true
      },
      accountCapacity: {
        complete: true,
        observedAtUtc: "2026-11-20T14:00:00.000Z",
        configuredLimits: { worker: 8, reviewer: 3 },
        sessions: [{
          sessionId: "session-1",
          projectName: "Jarvis",
          kind: "worker",
          status: "Active", // Active | Unknown | Inactive
          agentId: "Worker 1" // optional when not attributable
        }],
        reservations: [{
          reservationId: "reservation-1",
          projectName: "Jarvis",
          kind: "worker",
          status: "Reserved", // Reserved | Dispatching | Submitted | Uncertain | terminal
          idempotencyKey: "worker:<stable-key>",
          agentId: "Worker 1" // optional
        }]
      },
      projects: [{
        projectName: "Jarvis",
        source: {
          commit: "<40-64 lowercase hex>",
          ref: "refs/heads/main",
          observedAtUtc: "2026-11-20T14:00:00.000Z"
        },
        completeness: {
          tasks: true, agents: true, sourceLeases: true, reviews: true,
          challenges: true, submissions: true, capacity: true
        },
        capacity: {
          complete: true,
          observedAtUtc: "2026-11-20T14:00:00.000Z",
          configuredLimits: { worker: 4, reviewer: 2 },
          sessions: [],       // exact account-capacity subset for Jarvis
          reservations: []    // exact account-capacity subset for Jarvis
        },
        tasks: [{
          taskId: "task-1",
          lifecycle: "InProgress", // Open | InProgress | terminal
          currentAttemptId: "attempt-1",
          attempts: [{
            attemptId: "attempt-1",
            phase: "Ready", // Ready | Blocked | Started | ...
            version: 2,
            assignmentGeneration: 4,
            ownerAgentId: "Worker 1",
            priorityRank: 10 // optional; lower ranks are considered first
          }]
        }],
        agents: [{
          agentId: "Worker 1",
          verified: true,
          state: "Ready",
          execution: "Stopped",
          currentAssignment: {
            taskId: "task-1",
            attemptId: "attempt-1",
            assignmentGeneration: 4
          },
          unassignedDisposition: null
        }],
        sourceLeases: [{
          taskId: "task-1",
          attemptId: "attempt-1",
          assignmentGeneration: 4,
          ownerAgentId: "Worker 1",
          status: "Valid",
          repositoryId: "jarvis-repository",
          sourceRef: "refs/heads/feature/task-1",
          baseCommit: "<40-64 lowercase hex>",
          targetRef: "refs/heads/main"
        }],
        reviews: [{
          reviewId: "review-1",
          version: 2,
          status: "Pending",
          frozen: true,
          claim: null,
          reviewRef: "refs/heads/jarvis-review/task-1/review-1/r2",
          sourceRef: "refs/heads/feature/task-1",
          targetRef: "refs/heads/main",
          reviewedHeadOid: "<40-64 lowercase hex>",
          createdSequence: 9180
        }],
        challenges: [{
          waitId: "wait-1",
          taskId: "task-1",
          attemptId: "attempt-1",
          waitIndexGeneration: 4,
          ownerAgentId: "Worker 1",
          status: "Pending", // Pending | Eligible | Requeued
          dependencies: [{
            dependencyId: "dependency-1",
            status: "Pending" // Pending | Satisfied
          }],
          ownerReleaseProvenance: null,
          requeueProvenance: null
        }],
        submissions: []
      }]
    }

### Canonical-to-normalized mapping

The adapter is responsible for translating canonical ledger entities into this schema; the normalized values below are not literal canonical enum values.

| Canonical source | Normalized planner value |
| --- | --- |
| `AttemptChallengeWait.Status = "Waiting"` | `challenges[].status = "Pending"`; `Eligible` and `Requeued` pass through unchanged |
| The current owner-wait `AttemptChallengeWaitIndex.AssignmentGeneration` entry, cross-checked against each dependency index | `challenges[].waitIndexGeneration`; this is the immutable generation of the block index, not a field on `AttemptChallengeWait` |
| `AttemptChallengeWait.ChallengeIds` joined to canonical Challenge records | `dependencies[]`; `Satisfied` maps to `Satisfied`, valid outstanding status maps to `Pending`, an empty list remains empty and denotes a manual block, and missing/cancelled/malformed links fail closed |
| `ReviewAttempt.Phase = "Queued"` | `attempts[].phase = "Ready"` |
| `BranchReview.Status = "Unclaimed"` | `reviews[].status = "Pending"` |
| `TaskLifecycleEvent.OperationId`, `Sequence`, `ActorId`, `Action`, `PreviousPhase`, `NewPhase`, `PreviousOwnerId`, `NewOwnerId`, `PreviousGeneration`, `NewGeneration` | Corresponding `requeueProvenance` fields; `AttemptChallengeWait.RequeuedAtUtc` supplies `atUtc` |

The `AttemptChallengeWait` record itself has no assignment-generation field. For a dependency-linked wait blocked at generation N, the adapter selects the owner-wait index entry for the current wait and cross-checks the matching entry under every dependency index. For `Waiting`/`Eligible`, that generation must equal the current blocked attempt generation; after requeue, it remains N while the attempt, owner assignment and source lease advance to N+1. The matching lifecycle event must record `PreviousGeneration = N` and `NewGeneration = N+1`; the adapter joins the wait to its attempt by project/task/attempt identity, not by requiring the generations to be equal. Older index entries are historical and ignored; missing, ambiguous or disagreeing current index entries fail closed. For an empty manual wait, there is no current index at its blocked generation even if stale historical locators remain. The planner validates the normalized index generation, lifecycle transition, and current attempt generation independently.

A zero-dependency wait has no reverse-index entry and is represented with `waitIndexGeneration: null`. It is a valid `ManualBlocked` hold while the normalized wait is `Pending` and the attempt is `Blocked`; an empty dependency list is not vacuously satisfied. It remains held until the canonical owner-attributed `RequeueChallengeBlockedAttempt` lifecycle event records `Blocked` to `Queued`, the owner on both sides, and the exact N to N+1 generation transition. For this manual path, `requeueProvenance.actor` must equal the wait owner. The planner never creates the requeue itself.

Account capacity sessions and reservations cover every project. Each project's corresponding arrays must be the exact filtered subset, not a separately sampled approximation. Active and Unknown sessions count against their worker/reviewer limit. Reserved, Dispatching, Submitted and Uncertain reservations also count; terminal reservations do not consume capacity, but their idempotency keys remain visible to suppress a replay of the same identity. Unknown session status/kind, unknown reservation status/kind, missing capacity arrays, missing limits or contradictory capacity views fail closed.

Agent state is also evidence, not an idle heuristic. The adapter must provide verified agent state, execution state, current assignment and unassigned disposition. A Working/Unknown agent must be represented by an active/unknown session or a live reservation; otherwise the project is held for a capacity contradiction. An active/unknown session attributed to an agent prevents that same agent from being considered available even if a stale agent record says Ready.

A source lease is valid only when its identity, owner and assignment generation match the attempt and it carries a repository, distinct source/target refs and a valid base commit. Multiple or conflicting leases are held. A worker candidate additionally requires the task to remain Open/InProgress, the attempt to be the task's current Ready attempt, and the owner's verified Ready/Stopped state plus exact current-assignment identity and generation.

A reviewer candidate is emitted only for a Pending, frozen request with an explicit claim: null, version, review ref, source ref, target ref and frozen reviewed head. The candidate contains the immutable review identity and no reviewer/agent ID. The later shared-queue claim remains a separate authority.

## Deterministic decisions

- Worker keys are SHA-256 identities over [projectName, taskId, attemptId, assignmentGeneration].
- Reviewer keys are SHA-256 identities over [projectName, reviewId, version].
- No random launch ID is minted. Repeated polling of the same snapshot returns the same candidate identity.
- Worker candidates are considered by priorityRank, project, task and attempt. Reviewer candidates are considered by creation sequence, project, review and version. Accepted candidates consume provisional account and project capacity in that deterministic order.
- Holds are ordered by project, scope, identity and reason, with a canonical content tie-breaker so equivalent evidence remains ordered independently of source array order.
- The planner emits no named reviewer assignment and no task/attempt mutation.
- requeueIntents is always empty. The canonical Challenge workflow owns condition satisfaction, owner release, requeue and audit writes.

The planner emits explicit holds including SnapshotStale, SnapshotIncomplete, AccountCapacityMissing, MissingCapacityEvidence, CapacityContradiction, AccountCapacityFull, ProjectCapacityFull, OwnerBusy, OwnerParked, OwnerStateUnknown, OwnerAssignmentMismatch, SourceLeaseMissing, SourceLeaseConflict, TerminalTask, StaleAttemptGeneration, PendingSubmission, UncertainSubmission, DuplicateReservation, ChallengePending, ChallengePartiallySatisfied, ChallengeManualBlock, ChallengeOwnerBusy, ChallengeOwnerParked and ChallengeReconciliationInvariantViolation.

Challenge projection states are Pending, PartiallySatisfied, ManualBlocked, EligibleOwnerBusy, EligibleOwnerParked, Requeued, RequeuedWorking, RequeuedOwnerParked, RequeuedOwnerUnknown and InvariantViolation. A zero-dependency manual wait is a valid ManualBlocked hold, not an invariant violation; it cannot become Eligible from vacuous dependency satisfaction and remains blocked until its owner performs the explicit canonical requeue. An Eligible dependency-linked wait whose owner is already stopped, unassigned and explicitly NeedsWork is an invariant violation: canonical reconciliation should already have requeued it. A Requeued wait requires canonical requeueProvenance (operationId, ledgerSequence, actor, atUtc, action, previousPhase, newPhase, previousOwnerAgentId, newOwnerAgentId, previousGeneration, newGeneration) and a valid generation transition: the immutable wait-index generation equals PreviousGeneration and the current attempt generation equals NewGeneration. Manual requeue provenance must additionally attribute the actor to the wait owner. Owner-release provenance is carried through unchanged. Neither state creates a planner-side requeue or release intent.

## Challenge transition trace

This is a normalized fixture trace; the planner does not execute the workflow transition.

**Before canonical satisfaction/release**

- Task task-1 is InProgress.
- Attempt attempt-1, generation 4, is Blocked.
- Wait wait-1 is Pending; its dependency is Pending and its immutable wait-index generation is 4.
- The owner is stopped, unassigned and NeedsWork.

Planner projection:

    {
      workerCandidates: [],
      challengeStates: [{ state: "Pending", holdReason: "ChallengePending" }],
      requeueIntents: []
    }

**Canonical workflow transition**

The dependency becomes Satisfied. ChallengeWorkflow performs the existing owner-eligibility/release checks and transactionally records the wait as Requeued, the attempt as canonical Queued (normalized Ready), and the exact owner assignment at generation 5. The immutable wait index remains at generation 4. The lifecycle event records PreviousGeneration 4 and NewGeneration 5. The canonical ledger event remains the source of audit provenance; the planner does not repeat the transition.

**After canonical requeue**

- Wait wait-1 is Requeued; dependency is Satisfied and `waitIndexGeneration` remains 4.
- `requeueProvenance` carries the canonical operation, sequence, actor, action, Blocked-to-Queued phases, owner IDs and PreviousGeneration 4 / NewGeneration 5.
- Attempt attempt-1 is Ready, still the task's current attempt, with `assignmentGeneration: 5`.
- Owner Worker 1 is Ready/Stopped, with the exact task/attempt assignment at generation 5.
- Source lease is valid at generation 5 and both capacity views show an available slot.

Planner projection:

    {
      workerCandidates: [{
        kind: "worker",
        projectName: "Jarvis",
        taskId: "task-1",
        attemptId: "attempt-1",
        assignmentGeneration: 5,
        idempotencyKey: "worker:<same stable SHA-256 identity>"
      }],
      challengeStates: [{
        state: "Requeued",
        holdReason: null,
        waitIndexGeneration: 4,
        assignmentGeneration: 5,
        requeueProvenance: {
          operationId: "challenge-requeue-1",
          ledgerSequence: 9188,
          actor: "ChallengeWorkflow",
          atUtc: "2026-11-20T14:00:00.000Z",
          action: "RequeueChallengeBlockedAttempt",
          previousPhase: "Blocked",
          newPhase: "Queued",
          previousOwnerAgentId: "Worker 1",
          newOwnerAgentId: "Worker 1",
          previousGeneration: 4,
          newGeneration: 5
        }
      }],
      requeueIntents: []
    }

The candidate exists because canonical reconciliation already made the attempt Ready. If the owner has already started it, the Challenge is projected as RequeuedWorking and no second worker candidate is emitted.

**Manual zero-dependency hold**

- A canonical wait with an empty `ChallengeIds` list has no reverse-index entry; normalize it with `waitIndexGeneration: null` and status `Pending` while its attempt remains `Blocked`.
- The planner projects `ManualBlocked` / `ChallengeManualBlock`, emits no worker candidate and emits no requeue intent. Empty dependencies do not mean that the wait is satisfied.
- Only the owner's explicit canonical requeue can release it. The lifecycle event must attribute the actor and both owner IDs to that owner and record `PreviousGeneration: N`, `NewGeneration: N+1`; the current attempt and owner assignment must then carry N+1.
- After that verified transition, the wait is `Requeued` with no wait-index generation, and ordinary source-lease, owner and capacity checks still apply before a worker candidate is emitted.

## Phase 2 authority/runtime boundary

Phase 1 stops at a deterministic plan. It does not:

- materialize or refresh the account/project snapshot;
- create or consume reservations;
- launch a worker or reviewer session;
- assign a named reviewer or claim a review request;
- change task, attempt, source-lease or agent state;
- satisfy, release or requeue a Challenge;
- merge, integrate, deploy, migrate, restart or cut over anything.

A future runtime must independently revalidate snapshot freshness and all relevant ledger versions at the point of action, then reserve capacity and dispatch with the same idempotency key under an atomic/serialized authority. It must resolve uncertain submissions without replaying them. Review claiming remains in the shared review queue; reviewer selection must not grant implementation or merge authority. The canonical Challenge workflow remains the sole owner of wake/release/requeue behavior and its audit provenance.

The snapshot adapter, runtime reservation/dispatch, and any live scheduler integration are Phase 2 work and require their own independent review. This Phase 1 module has no live side effects.

## Tests

Run from the Jarvis repository root:

    node --test scripts/tests/jarvis-ledger-planner.test.mjs

Regression run with the existing project-scoped Manager planner:

    node --test scripts/tests/manager-heartbeat.test.mjs scripts/tests/jarvis-ledger-planner.test.mjs
