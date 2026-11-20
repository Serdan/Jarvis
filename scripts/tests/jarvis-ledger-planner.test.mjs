import test from "node:test";
import assert from "node:assert/strict";
import { planLedger } from "../jarvis-ledger-planner.mjs";

const NOW = "2026-11-20T14:00:00.000Z";
const ACCOUNT_HEAD = "a".repeat(40);
const BASE_HEAD = "b".repeat(40);
const REVIEW_HEAD = "c".repeat(40);

function attempt(overrides = {}) {
  return {
    attemptId: "attempt-1",
    phase: "Ready",
    version: 2,
    assignmentGeneration: 4,
    ownerAgentId: "Worker 1",
    priorityRank: 1,
    ...overrides
  };
}

function task(overrides = {}) {
  return {
    taskId: "task-1",
    lifecycle: "InProgress",
    currentAttemptId: "attempt-1",
    attempts: [attempt()],
    ...overrides
  };
}

function agent(overrides = {}) {
  return {
    agentId: "Worker 1",
    verified: true,
    state: "Ready",
    execution: "Stopped",
    currentAssignment: {
      taskId: "task-1",
      attemptId: "attempt-1",
      assignmentGeneration: 4
    },
    unassignedDisposition: null,
    ...overrides
  };
}

function sourceLease(overrides = {}) {
  return {
    taskId: "task-1",
    attemptId: "attempt-1",
    assignmentGeneration: 4,
    ownerAgentId: "Worker 1",
    status: "Valid",
    repositoryId: "jarvis-repository",
    sourceRef: "refs/heads/feature/task-1",
    baseCommit: BASE_HEAD,
    targetRef: "refs/heads/main",
    ...overrides
  };
}

function review(overrides = {}) {
  return {
    reviewId: "review-1",
    version: 2,
    status: "Pending",
    frozen: true,
    claim: null,
    reviewRef: "refs/heads/jarvis-review/task-1/review-1/r2",
    sourceRef: "refs/heads/feature/task-1",
    targetRef: "refs/heads/main",
    reviewedHeadOid: REVIEW_HEAD,
    createdSequence: 12,
    ...overrides
  };
}

function project(name = "Jarvis", overrides = {}) {
  const capacity = {
    complete: true,
    observedAtUtc: NOW,
    configuredLimits: { worker: 5, reviewer: 5 },
    sessions: [],
    reservations: [],
    ...overrides.capacity
  };
  return {
    projectName: name,
    source: {
      commit: ACCOUNT_HEAD,
      ref: "refs/heads/main",
      observedAtUtc: NOW
    },
    completeness: {
      tasks: true, agents: true, sourceLeases: true, reviews: true,
      challenges: true, submissions: true, capacity: true
    },
    capacity,
    tasks: overrides.tasks ?? [task()],
    agents: overrides.agents ?? [agent()],
    sourceLeases: overrides.sourceLeases ?? [sourceLease()],
    reviews: overrides.reviews ?? [],
    challenges: overrides.challenges ?? [],
    submissions: overrides.submissions ?? []
  };
}

function snapshot(projects = [project()], accountOverrides = {}) {
  return {
    schemaVersion: 1,
    capturedAtUtc: NOW,
    source: {
      commit: ACCOUNT_HEAD,
      ref: "refs/heads/account-ledger",
      observedAtUtc: NOW
    },
    completeness: { account: true, projects: true, accountCapacity: true },
    accountCapacity: {
      complete: true,
      observedAtUtc: NOW,
      configuredLimits: { worker: 10, reviewer: 10 },
      sessions: projects.flatMap(item => item.capacity.sessions),
      reservations: projects.flatMap(item => item.capacity.reservations),
      ...accountOverrides
    },
    projects
  };
}

function workerKeyFor(projectName = "Jarvis") {
  const result = planLedger(snapshot([project(projectName)]), NOW);
  return result.workerCandidates[0].idempotencyKey;
}

function challengeProject(status, dependencyStatuses, ownerOverrides = {}, attemptOverrides = {}) {
  const currentAttempt = attempt({ phase: "Blocked", ...attemptOverrides });
  const currentTask = task({ attempts: [currentAttempt] });
  const sessionStatus = ownerOverrides.execution === "Working" ? "Active" :
    ownerOverrides.execution === "Unknown" || ownerOverrides.state === "Unknown" ? "Unknown" : null;
  const sessions = sessionStatus ? [{
    sessionId: "challenge-owner-session", projectName: "Jarvis", kind: "worker", status: sessionStatus, agentId: "Worker 1"
  }] : [];
  const currentOwner = agent({
    state: "NeedsWork",
    execution: "Stopped",
    currentAssignment: null,
    unassignedDisposition: "NeedsWork",
    ...ownerOverrides
  });
  return project("Jarvis", {
    tasks: [currentTask],
    agents: [currentOwner],
    capacity: { sessions },
    challenges: [{
      waitId: "wait-1",
      taskId: "task-1",
      attemptId: "attempt-1",
      assignmentGeneration: 4,
      ownerAgentId: "Worker 1",
      status,
      ownerReleaseProvenance: status === "Eligible" ? { operationId: "release-op-1", ledgerSequence: 9001, actor: "Operator", atUtc: NOW } : null,
      dependencies: dependencyStatuses.map((dependencyStatus, index) => ({
        dependencyId: "dependency-" + (index + 1),
        status: dependencyStatus
      }))
    }]
  });
}

function hasHold(result, reason) {
  return result.holds.some(item => item.reason === reason);
}

test("identical snapshots produce identical candidates and stable idempotency keys without mutation", () => {
  const input = snapshot([project("Jarvis", { reviews: [review()] })]);
  const before = JSON.stringify(input);
  const first = planLedger(input, NOW);
  const second = planLedger(input, NOW);

  assert.deepEqual(second, first);
  assert.equal(first.workerCandidates.length, 1);
  assert.equal(first.reviewCandidates.length, 1);
  assert.match(first.workerCandidates[0].idempotencyKey, /^worker:[0-9a-f]{64}$/);
  assert.match(first.reviewCandidates[0].idempotencyKey, /^review:[0-9a-f]{64}$/);
  assert.equal("launchId" in first.workerCandidates[0], false);
  assert.equal(JSON.stringify(input), before);
});

test("worker candidates require an existing Ready assignment and reviewer candidates preserve exact frozen identity", () => {
  const result = planLedger(snapshot([project("Jarvis", { reviews: [review()] })]), NOW);

  assert.deepEqual(result.workerCandidates.map(x => [
    x.projectName, x.taskId, x.attemptId, x.assignmentGeneration, x.ownerAgentId
  ]), [["Jarvis", "task-1", "attempt-1", 4, "Worker 1"]]);
  assert.deepEqual(result.reviewCandidates.map(x => [
    x.projectName, x.reviewId, x.reviewVersion, x.reviewedHeadOid, x.reviewRef
  ]), [["Jarvis", "review-1", 2, REVIEW_HEAD, "refs/heads/jarvis-review/task-1/review-1/r2"]]);
  assert.equal("agentId" in result.reviewCandidates[0], false);
  assert.deepEqual(result.requeueIntents, []);
});

test("account-wide sessions in another project consume the shared worker limit", () => {
  const remoteSessions = [1, 2, 3].map(index => ({
    sessionId: "wayfold-session-" + index,
    projectName: "Wayfold",
    kind: "worker",
    status: "Active"
  }));
  const jarvis = project("Jarvis");
  const wayfold = project("Wayfold", {
    tasks: [], agents: [], sourceLeases: [],
    capacity: { configuredLimits: { worker: 5, reviewer: 5 }, sessions: remoteSessions }
  });
  const result = planLedger(snapshot([jarvis, wayfold], {
    configuredLimits: { worker: 3, reviewer: 10 }
  }), NOW);

  assert.equal(result.workerCandidates.length, 0);
  assert.ok(hasHold(result, "AccountCapacityFull"));
});

test("project capacity is enforced independently of remaining account capacity", () => {
  const localSessions = [{
    sessionId: "jarvis-active-1",
    projectName: "Jarvis",
    kind: "worker",
    status: "Active"
  }];
  const jarvis = project("Jarvis", {
    capacity: {
      configuredLimits: { worker: 1, reviewer: 5 },
      sessions: localSessions
    }
  });
  const result = planLedger(snapshot([jarvis], {
    configuredLimits: { worker: 10, reviewer: 10 }
  }), NOW);

  assert.equal(result.workerCandidates.length, 0);
  assert.ok(hasHold(result, "ProjectCapacityFull"));
});

test("Unknown sessions count against capacity and Uncertain reservations count as in-flight", () => {
  const remote = project("Wayfold", {
    tasks: [], agents: [], sourceLeases: [],
    capacity: {
      configuredLimits: { worker: 5, reviewer: 5 },
      sessions: [{
        sessionId: "unknown-session",
        projectName: "Wayfold",
        kind: "worker",
        status: "Unknown"
      }]
    }
  });
  const unknownResult = planLedger(snapshot([project("Jarvis"), remote], {
    configuredLimits: { worker: 1, reviewer: 10 }
  }), NOW);
  assert.ok(hasHold(unknownResult, "AccountCapacityFull"));

  const uncertain = project("Wayfold", {
    tasks: [], agents: [], sourceLeases: [],
    capacity: {
      configuredLimits: { worker: 5, reviewer: 5 },
      reservations: [{
        reservationId: "uncertain-launch",
        projectName: "Wayfold",
        kind: "worker",
        status: "Uncertain",
        idempotencyKey: "worker:uncertain-other-project"
      }]
    }
  });
  const uncertainResult = planLedger(snapshot([project("Jarvis"), uncertain], {
    configuredLimits: { worker: 1, reviewer: 10 }
  }), NOW);
  assert.ok(hasHold(uncertainResult, "AccountCapacityFull"));
});

test("stale or incomplete account snapshots fail closed with explicit holds", () => {
  const stale = planLedger(snapshot(), "2026-11-20T14:04:00.000Z");
  assert.equal(stale.workerCandidates.length, 0);
  assert.ok(hasHold(stale, "SnapshotStale"));

  const incomplete = planLedger(snapshot([project()], { complete: false }), NOW);
  assert.equal(incomplete.workerCandidates.length, 0);
  assert.ok(hasHold(incomplete, "AccountCapacityIncomplete"));
});

test("missing project capacity and account/project capacity contradictions hold that project", () => {
  const missing = project("Jarvis", {
    capacity: { complete: false }
  });
  const missingResult = planLedger(snapshot([missing]), NOW);
  assert.equal(missingResult.workerCandidates.length, 0);
  assert.ok(hasHold(missingResult, "ProjectCapacityIncomplete"));

  const contradictory = project("Jarvis", {
    capacity: { sessions: [{
      sessionId: "not-in-account-snapshot",
      projectName: "Jarvis",
      kind: "worker",
      status: "Active"
    }] }
  });
  const contradictionResult = planLedger(snapshot([contradictory], { sessions: [] }), NOW);
  assert.equal(contradictionResult.workerCandidates.length, 0);
  assert.ok(hasHold(contradictionResult, "CapacityContradiction"));
});

test("source lease conflicts and missing leases prevent worker candidates", () => {
  const conflict = project("Jarvis", {
    sourceLeases: [sourceLease({ status: "Conflict" })]
  });
  const conflictResult = planLedger(snapshot([conflict]), NOW);
  assert.equal(conflictResult.workerCandidates.length, 0);
  assert.ok(hasHold(conflictResult, "SourceLeaseConflict"));

  const missing = project("Jarvis", { sourceLeases: [] });
  const missingResult = planLedger(snapshot([missing]), NOW);
  assert.equal(missingResult.workerCandidates.length, 0);
  assert.ok(hasHold(missingResult, "SourceLeaseMissing"));
});

test("terminal tasks and stale attempt generations are held", () => {
  const terminal = project("Jarvis", {
    tasks: [task({ lifecycle: "Closed" })]
  });
  const terminalResult = planLedger(snapshot([terminal]), NOW);
  assert.equal(terminalResult.workerCandidates.length, 0);
  assert.ok(hasHold(terminalResult, "TerminalTask"));

  const staleGeneration = project("Jarvis", {
    tasks: [task({ currentAttemptId: "older-attempt" })]
  });
  const staleResult = planLedger(snapshot([staleGeneration]), NOW);
  assert.equal(staleResult.workerCandidates.length, 0);
  assert.ok(hasHold(staleResult, "StaleAttemptGeneration"));
});

test("only exact unclaimed frozen reviews become reviewer candidates", () => {
  const p = project("Jarvis", {
    reviews: [
      review(),
      review({ reviewId: "claimed", claim: { agentId: "Reviewer 1" } }),
      review({ reviewId: "mutable", frozen: false }),
      review({ reviewId: "missing-claim", claim: undefined })
    ]
  });
  const result = planLedger(snapshot([p]), NOW);

  assert.deepEqual(result.reviewCandidates.map(x => x.reviewId), ["review-1"]);
  assert.ok(hasHold(result, "ReviewAlreadyClaimed"));
  assert.ok(hasHold(result, "ReviewNotFrozen"));
  assert.ok(hasHold(result, "ReviewClaimStateMissing"));
});

test("pending and partially satisfied Challenge waits are explicit projection states", () => {
  const pending = planLedger(snapshot([
    challengeProject("Pending", ["Pending"])
  ]), NOW);
  assert.equal(pending.challengeStates[0].state, "Pending");
  assert.equal(pending.challengeStates[0].holdReason, "ChallengePending");
  assert.ok(hasHold(pending, "ChallengePending"));

  const partial = planLedger(snapshot([
    challengeProject("Pending", ["Satisfied", "Pending"])
  ]), NOW);
  assert.equal(partial.challengeStates[0].state, "PartiallySatisfied");
  assert.equal(partial.challengeStates[0].satisfiedDependencies, 1);
  assert.ok(hasHold(partial, "ChallengePartiallySatisfied"));
});

test("Eligible Challenge waits distinguish busy and parked owners", () => {
  const busy = planLedger(snapshot([
    challengeProject("Eligible", ["Satisfied"], {
      state: "Working",
      execution: "Working",
      currentAssignment: { taskId: "other-task", attemptId: "other-attempt", assignmentGeneration: 1 },
      unassignedDisposition: null
    })
  ]), NOW);
  assert.equal(busy.challengeStates[0].state, "EligibleOwnerBusy");
  assert.ok(hasHold(busy, "ChallengeOwnerBusy"));
  assert.deepEqual(busy.challengeStates[0].ownerReleaseProvenance, { operationId: "release-op-1", ledgerSequence: 9001, actor: "Operator", atUtc: NOW });

  const parked = planLedger(snapshot([
    challengeProject("Eligible", ["Satisfied"], {
      state: "Parked",
      execution: "Stopped",
      currentAssignment: null,
      unassignedDisposition: "Parked"
    })
  ]), NOW);
  assert.equal(parked.challengeStates[0].state, "EligibleOwnerParked");
  assert.ok(hasHold(parked, "ChallengeOwnerParked"));
});

test("Eligible Challenge with an already-free NeedsWork owner is an invariant violation", () => {
  const result = planLedger(snapshot([
    challengeProject("Eligible", ["Satisfied"])
  ]), NOW);

  assert.equal(result.challengeStates[0].state, "InvariantViolation");
  assert.equal(result.challengeStates[0].holdReason,
    "ChallengeReconciliationInvariantViolation");
  assert.ok(hasHold(result, "ChallengeReconciliationInvariantViolation"));
  assert.deepEqual(result.requeueIntents, []);
});

test("canonical Challenge requeue is projected once as Ready work, without a planner requeue intent", () => {
  const before = planLedger(snapshot([
    challengeProject("Pending", ["Pending"])
  ]), NOW);
  assert.equal(before.workerCandidates.length, 0);
  assert.equal(before.challengeStates[0].state, "Pending");

  const afterProject = project("Jarvis", {
    tasks: [task()],
    agents: [agent()],
    challenges: [{
      waitId: "wait-1",
      taskId: "task-1",
      attemptId: "attempt-1",
      assignmentGeneration: 4,
      ownerAgentId: "Worker 1",
      status: "Requeued",
      ownerReleaseProvenance: { operationId: "release-op-2", ledgerSequence: 9187, actor: "Operator", atUtc: NOW },
      requeueProvenance: { operationId: "challenge-requeue-1", ledgerSequence: 9188, actor: "ChallengeWorkflow", atUtc: NOW },
      dependencies: [{ dependencyId: "dependency-1", status: "Satisfied" }]
    }]
  });
  const after = planLedger(snapshot([afterProject]), NOW);

  assert.equal(after.challengeStates[0].state, "Requeued");
  assert.deepEqual(after.challengeStates[0].requeueProvenance, { operationId: "challenge-requeue-1", ledgerSequence: 9188, actor: "ChallengeWorkflow", atUtc: NOW });
  assert.deepEqual(after.challengeStates[0].ownerReleaseProvenance, { operationId: "release-op-2", ledgerSequence: 9187, actor: "Operator", atUtc: NOW });
  assert.equal(after.challengeStates[0].holdReason, null);
  assert.equal(after.workerCandidates.length, 1);
  assert.equal(after.workerCandidates[0].attemptId, "attempt-1");
  assert.deepEqual(after.requeueIntents, []);
});

test("Requeued Challenges retain audit provenance after the owner starts work", () => {
  const started = project("Jarvis", {
    tasks: [task({ attempts: [attempt({ phase: "Started" })] })],
    agents: [agent({ state: "Working", execution: "Working" })],
    capacity: { sessions: [{
      sessionId: "worker-1-started",
      projectName: "Jarvis",
      kind: "worker",
      status: "Active",
      agentId: "Worker 1"
    }] },
    challenges: [{
      waitId: "wait-1",
      taskId: "task-1",
      attemptId: "attempt-1",
      assignmentGeneration: 4,
      ownerAgentId: "Worker 1",
      status: "Requeued",
      requeueProvenance: { operationId: "challenge-requeue-2", ledgerSequence: 9189, actor: "ChallengeWorkflow", atUtc: NOW },
      dependencies: [{ dependencyId: "dependency-1", status: "Satisfied" }]
    }]
  });
  const result = planLedger(snapshot([started]), NOW);
  assert.equal(result.challengeStates[0].state, "RequeuedWorking");
  assert.equal(result.challengeStates[0].holdReason, null);
  assert.equal(result.workerCandidates.length, 0);

  const missingAudit = JSON.parse(JSON.stringify(started));
  delete missingAudit.challenges[0].requeueProvenance;
  const invalid = planLedger(snapshot([missingAudit]), NOW);
  assert.equal(invalid.challengeStates[0].state, "InvariantViolation");
  assert.ok(hasHold(invalid, "ChallengeReconciliationInvariantViolation"));
});

test("Ready worker owners must be stopped, verified and bound to the exact assignment generation", () => {
  const busy = project("Jarvis", {
    agents: [agent({ state: "Working", execution: "Working" })],
    capacity: { sessions: [{
      sessionId: "worker-1-active",
      projectName: "Jarvis",
      kind: "worker",
      status: "Active",
      agentId: "Worker 1"
    }] }
  });
  const busyResult = planLedger(snapshot([busy]), NOW);
  assert.equal(busyResult.workerCandidates.length, 0);
  assert.ok(hasHold(busyResult, "OwnerBusy"));

  const activeButNotAttributed = project("Jarvis", {
    capacity: { sessions: [{
      sessionId: "owner-active",
      projectName: "Jarvis",
      kind: "worker",
      status: "Active",
      agentId: "Worker 1"
    }] }
  });
  const activeResult = planLedger(snapshot([activeButNotAttributed]), NOW);
  assert.equal(activeResult.workerCandidates.length, 0);
  assert.ok(hasHold(activeResult, "OwnerBusy"));

  const missingActivity = project("Jarvis", {
    agents: [agent({ state: "Working", execution: "Working" })]
  });
  const missingActivityResult = planLedger(snapshot([missingActivity]), NOW);
  assert.equal(missingActivityResult.workerCandidates.length, 0);
  assert.ok(hasHold(missingActivityResult, "CapacityContradiction"));

  const parked = project("Jarvis", {
    agents: [agent({
      state: "Parked", execution: "Stopped", currentAssignment: null,
      unassignedDisposition: "Parked"
    })]
  });
  const parkedResult = planLedger(snapshot([parked]), NOW);
  assert.equal(parkedResult.workerCandidates.length, 0);
  assert.ok(hasHold(parkedResult, "OwnerParked"));

  const mismatched = project("Jarvis", {
    agents: [agent({ currentAssignment: {
      taskId: "task-1", attemptId: "attempt-1", assignmentGeneration: 3
    } })]
  });
  const mismatchResult = planLedger(snapshot([mismatched]), NOW);
  assert.equal(mismatchResult.workerCandidates.length, 0);
  assert.ok(hasHold(mismatchResult, "OwnerAssignmentMismatch"));
});

test("duplicate and uncertain reservations suppress repeated candidate identities", () => {
  const key = workerKeyFor();
  const reserved = project("Jarvis", {
    capacity: {
      reservations: [{
        reservationId: "prior-reservation",
        projectName: "Jarvis",
        kind: "worker",
        status: "Reserved",
        idempotencyKey: key,
        agentId: "Worker 1"
      }]
    }
  });
  const reservedResult = planLedger(snapshot([reserved]), NOW);
  assert.equal(reservedResult.workerCandidates.length, 0);
  assert.ok(hasHold(reservedResult, "DuplicateReservation"));

  const uncertain = project("Jarvis", {
    capacity: {
      reservations: [{
        reservationId: "uncertain-reservation",
        projectName: "Jarvis",
        kind: "worker",
        status: "Uncertain",
        idempotencyKey: key,
        agentId: "Worker 1"
      }]
    }
  });
  const uncertainResult = planLedger(snapshot([uncertain]), NOW);
  assert.equal(uncertainResult.workerCandidates.length, 0);
  assert.ok(hasHold(uncertainResult, "UncertainSubmission"));
});

test("pending and uncertain submissions block re-launch of the same attempt generation", () => {
  const pending = project("Jarvis", {
    submissions: [{
      submissionId: "submission-pending",
      taskId: "task-1",
      attemptId: "attempt-1",
      assignmentGeneration: 4,
      status: "Pending"
    }]
  });
  const pendingResult = planLedger(snapshot([pending]), NOW);
  assert.equal(pendingResult.workerCandidates.length, 0);
  assert.ok(hasHold(pendingResult, "PendingSubmission"));

  const uncertain = project("Jarvis", {
    submissions: [{
      submissionId: "submission-uncertain",
      taskId: "task-1",
      attemptId: "attempt-1",
      assignmentGeneration: 4,
      status: "Uncertain"
    }]
  });
  const uncertainResult = planLedger(snapshot([uncertain]), NOW);
  assert.equal(uncertainResult.workerCandidates.length, 0);
  assert.ok(hasHold(uncertainResult, "UncertainSubmission"));
});

test("unclassified capacity occupants fail closed rather than being treated as idle", () => {
  const unknown = project("Jarvis", {
    capacity: {
      sessions: [{
        sessionId: "unclassified",
        projectName: "Jarvis",
        kind: "other",
        status: "Unknown"
      }]
    }
  });
  const result = planLedger(snapshot([unknown]), NOW);

  assert.equal(result.workerCandidates.length, 0);
  assert.ok(hasHold(result, "UnknownCapacityKind"));
});

test("candidate allocation is stable and never exceeds either configured capacity", () => {
  const first = project("Jarvis", {
    tasks: [
      task({ taskId: "task-b", currentAttemptId: "attempt-b", attempts: [
        attempt({ attemptId: "attempt-b", ownerAgentId: "Worker 2", priorityRank: 20 })
      ] }),
      task({ taskId: "task-a", currentAttemptId: "attempt-a", attempts: [
        attempt({ attemptId: "attempt-a", ownerAgentId: "Worker 1", priorityRank: 10 })
      ] })
    ],
    agents: [
      agent({ agentId: "Worker 1", currentAssignment: {
        taskId: "task-a", attemptId: "attempt-a", assignmentGeneration: 4
      } }),
      agent({ agentId: "Worker 2", currentAssignment: {
        taskId: "task-b", attemptId: "attempt-b", assignmentGeneration: 4
      } })
    ],
    sourceLeases: [
      sourceLease({ taskId: "task-a", attemptId: "attempt-a", ownerAgentId: "Worker 1" }),
      sourceLease({ taskId: "task-b", attemptId: "attempt-b", ownerAgentId: "Worker 2" })
    ],
    capacity: { configuredLimits: { worker: 1, reviewer: 5 } }
  });
  const result = planLedger(snapshot([first], {
    configuredLimits: { worker: 2, reviewer: 5 }
  }), NOW);

  assert.deepEqual(result.workerCandidates.map(x => x.taskId), ["task-a"]);
  assert.ok(hasHold(result, "ProjectCapacityFull"));
});


test("fully satisfied dependencies cannot remain Pending after canonical Challenge reconciliation", () => {
  const result = planLedger(snapshot([
    challengeProject("Pending", ["Satisfied"])
  ]), NOW);

  assert.equal(result.challengeStates[0].state, "InvariantViolation");
  assert.equal(result.challengeStates[0].holdReason,
    "ChallengeReconciliationInvariantViolation");
  assert.ok(hasHold(result, "ChallengeReconciliationInvariantViolation"));
  assert.deepEqual(result.requeueIntents, []);
});

test("reviewer candidates respect account-wide and project-level reviewer capacity", () => {
  const jarvis = project("Jarvis", { reviews: [review()] });
  const wayfold = project("Wayfold", {
    tasks: [], agents: [], sourceLeases: [], reviews: [],
    capacity: {
      configuredLimits: { worker: 5, reviewer: 5 },
      sessions: [{
        sessionId: "wayfold-reviewer-session",
        projectName: "Wayfold",
        kind: "reviewer",
        status: "Active"
      }]
    }
  });
  const accountLimited = planLedger(snapshot([jarvis, wayfold], {
    configuredLimits: { worker: 10, reviewer: 1 }
  }), NOW);
  assert.equal(accountLimited.reviewCandidates.length, 0);
  assert.ok(hasHold(accountLimited, "AccountCapacityFull"));

  const projectLimited = project("Jarvis", {
    reviews: [review()],
    capacity: { configuredLimits: { worker: 5, reviewer: 0 } }
  });
  const projectLimitedResult = planLedger(snapshot([projectLimited], {
    configuredLimits: { worker: 10, reviewer: 10 }
  }), NOW);
  assert.equal(projectLimitedResult.reviewCandidates.length, 0);
  assert.ok(hasHold(projectLimitedResult, "ProjectCapacityFull"));
});

test("a stale project snapshot suppresses only that project", () => {
  const stale = project("Stale");
  stale.source.observedAtUtc = "2026-11-20T13:55:00.000Z";
  const fresh = project("Fresh");

  const result = planLedger(snapshot([stale, fresh]), NOW);

  assert.deepEqual(result.workerCandidates.map(candidate => candidate.projectName), ["Fresh"]);
  assert.ok(result.holds.some(hold =>
    hold.projectName === "Stale" && hold.reason === "ProjectSnapshotStaleOrUnbound"));
});

test("hold ordering is deterministic when same-attempt submissions arrive in a different order", () => {
  const makeProject = submissionIds => project("Jarvis", {
    submissions: submissionIds.map(submissionId => ({
      submissionId,
      taskId: "task-1",
      attemptId: "attempt-1",
      assignmentGeneration: 4,
      status: "Pending"
    }))
  });

  const forward = planLedger(snapshot([makeProject(["submission-a", "submission-b"])]), NOW);
  const reversed = planLedger(snapshot([makeProject(["submission-b", "submission-a"])]), NOW);

  assert.deepEqual(reversed, forward);
  assert.deepEqual(forward.holds.map(hold => hold.submissionId), ["submission-a", "submission-b"]);
});
