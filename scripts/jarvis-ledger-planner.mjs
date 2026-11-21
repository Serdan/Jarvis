import { createHash } from "node:crypto";

const DEFAULT_MAX_AGE_MS = 180_000;
const ACTIVE_TASK_LIFECYCLES = new Set(["Open", "InProgress"]);
const TERMINAL_TASK_LIFECYCLES = new Set(["Closed", "Cancelled", "Completed", "Rejected"]);
const SESSION_STATUSES = new Set(["Active", "Unknown", "Inactive"]);
const LIVE_RESERVATION_STATUSES = new Set(["Reserved", "Dispatching", "Submitted", "Uncertain"]);
const RESERVATION_STATUSES = new Set([
  ...LIVE_RESERVATION_STATUSES, "Observed", "Blocked", "Cleared", "Released", "Failed"
]);
const SUBMISSION_STATUSES = new Set(["Pending", "Uncertain", "Integrated", "Rejected", "Withdrawn"]);
const DEPENDENCY_STATUSES = new Set(["Pending", "Satisfied"]);
const PROJECT_COMPLETENESS_KEYS = [
  "tasks", "agents", "sourceLeases", "reviews", "challenges", "submissions", "capacity"
];

function isRecord(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function isNonEmptyString(value) {
  return typeof value === "string" && value.trim().length > 0;
}

function isSha(value) {
  return typeof value === "string" && /^[0-9a-f]{40,64}$/.test(value);
}

function isRef(value) {
  return typeof value === "string" && /^refs\/heads\/[^\s]+$/.test(value);
}

function parseTime(value) {
  if (typeof value !== "string" || !/(?:Z|[+-]\d\d:\d\d)$/.test(value)) return null;
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : null;
}

function compareText(a, b) {
  const left = String(a ?? "");
  const right = String(b ?? "");
  return left < right ? -1 : left > right ? 1 : 0;
}

function stableValue(value) {
  if (Array.isArray(value)) return `[${value.map(stableValue).join(",")}]`;
  if (isRecord(value)) {
    return `{${Object.keys(value).sort(compareText)
      .map(key => `${JSON.stringify(key)}:${stableValue(value[key])}`).join(",")}}`;
  }
  return JSON.stringify(value);
}

function identityKey(projectName, taskId, attemptId, generation) {
  return JSON.stringify([projectName, taskId, attemptId, generation]);
}

function attemptIdentityKey(projectName, taskId, attemptId) {
  return JSON.stringify([projectName, taskId, attemptId]);
}

function addHold(holds, scope, reason, identity = {}, details = {}) {
  holds.push({ scope, reason, ...identity, ...details });
}

function sortHolds(holds) {
  return holds.sort((a, b) =>
    compareText(a.projectName, b.projectName) ||
    compareText(a.scope, b.scope) ||
    compareText(a.taskId, b.taskId) ||
    compareText(a.attemptId, b.attemptId) ||
    compareText(a.reviewId, b.reviewId) ||
    compareText(a.waitId, b.waitId) ||
    compareText(a.reason, b.reason) ||
    compareText(stableValue(a), stableValue(b)));
}

function deterministicKey(kind, parts) {
  const digest = createHash("sha256").update(JSON.stringify(parts)).digest("hex");
  return kind + ":" + digest;
}

function validLimits(value) {
  return isRecord(value) &&
    Number.isInteger(value.worker) && value.worker >= 0 &&
    Number.isInteger(value.reviewer) && value.reviewer >= 0;
}

function timeEvidenceIsFresh(value, capturedAtMs, nowMs, maxAgeMs) {
  const observedAt = parseTime(value);
  return observedAt !== null &&
    observedAt <= capturedAtMs + 1000 &&
    capturedAtMs - observedAt <= maxAgeMs &&
    nowMs - observedAt <= maxAgeMs;
}

function validateCapacityRecords(capacity, scope, holds, identity = {}) {
  if (!Array.isArray(capacity.sessions) || !Array.isArray(capacity.reservations)) {
    addHold(holds, scope, "MissingCapacityEvidence", identity);
    return;
  }

  const sessionIds = new Set();
  for (const session of capacity.sessions) {
    if (!isRecord(session) || !isNonEmptyString(session.sessionId) ||
        !isNonEmptyString(session.projectName) ||
        (session.agentId != null && !isNonEmptyString(session.agentId))) {
      addHold(holds, scope, "InvalidCapacityEvidence", identity);
      continue;
    }
    if (sessionIds.has(session.sessionId)) {
      addHold(holds, scope, "CapacityContradiction", identity, { identity: session.sessionId });
    }
    sessionIds.add(session.sessionId);
    if (!["worker", "reviewer"].includes(session.kind)) {
      addHold(holds, scope, "UnknownCapacityKind", identity, { identity: session.sessionId });
    }
    if (!SESSION_STATUSES.has(session.status)) {
      addHold(holds, scope, "UnknownCapacityStatus", identity, { identity: session.sessionId });
    }
  }

  const reservationIds = new Set();
  for (const reservation of capacity.reservations) {
    if (!isRecord(reservation) || !isNonEmptyString(reservation.reservationId) ||
        !isNonEmptyString(reservation.projectName) ||
        !isNonEmptyString(reservation.idempotencyKey) ||
        (reservation.agentId != null && !isNonEmptyString(reservation.agentId))) {
      addHold(holds, scope, "InvalidCapacityEvidence", identity);
      continue;
    }
    if (reservationIds.has(reservation.reservationId)) {
      addHold(holds, scope, "CapacityContradiction", identity, {
        identity: reservation.reservationId
      });
    }
    reservationIds.add(reservation.reservationId);
    if (!["worker", "reviewer"].includes(reservation.kind)) {
      addHold(holds, scope, "UnknownCapacityKind", identity, {
        identity: reservation.reservationId
      });
    }
    if (!RESERVATION_STATUSES.has(reservation.status)) {
      addHold(holds, scope, "UnknownReservationStatus", identity, {
        identity: reservation.reservationId
      });
    }
  }
}

function stableRecordArray(records, idField) {
  return [...records].sort((a, b) => compareText(a[idField], b[idField]))
    .map(record => JSON.stringify({
      id: record[idField],
      projectName: record.projectName,
      kind: record.kind,
      status: record.status,
      agentId: record.agentId ?? null,
      idempotencyKey: record.idempotencyKey ?? null
    }));
}

function capacityCounts(capacity) {
  const counts = { worker: 0, reviewer: 0 };
  for (const session of capacity.sessions) {
    if (session.status === "Active" || session.status === "Unknown") counts[session.kind]++;
  }
  for (const reservation of capacity.reservations) {
    if (LIVE_RESERVATION_STATUSES.has(reservation.status)) counts[reservation.kind]++;
  }
  return counts;
}

function matchingReservations(capacity, idempotencyKey, agentId = null) {
  const reservations = Array.isArray(capacity) ? capacity : capacity.reservations;
  return {
    exact: reservations.filter(item => item.idempotencyKey === idempotencyKey),
    owner: agentId === null ? [] : reservations.filter(item =>
      item.agentId === agentId && LIVE_RESERVATION_STATUSES.has(item.status))
  };
}

function validateRoot(snapshot, nowUtc, maxAgeMs, holds) {
  if (!isRecord(snapshot) || snapshot.schemaVersion !== 2 ||
      !isRecord(snapshot.completeness) || !Array.isArray(snapshot.projects)) {
    addHold(holds, "account", "SnapshotIncomplete");
    return null;
  }

  for (const key of ["account", "projects", "accountCapacity"]) {
    if (snapshot.completeness[key] !== true) {
      addHold(holds, "account", "SnapshotIncomplete", {}, { evidence: key });
    }
  }

  const capturedAtMs = parseTime(snapshot.capturedAtUtc);
  const nowMs = parseTime(nowUtc);
  if (capturedAtMs === null || nowMs === null) {
    addHold(holds, "account", "InvalidSnapshotTime");
  } else if (capturedAtMs > nowMs + 1000) {
    addHold(holds, "account", "SnapshotFromFuture");
  } else if (nowMs - capturedAtMs > maxAgeMs) {
    addHold(holds, "account", "SnapshotStale");
  }

  if (!isRecord(snapshot.source) || !isSha(snapshot.source.commit) ||
      !isRef(snapshot.source.ref) ||
      (capturedAtMs !== null && nowMs !== null &&
       !timeEvidenceIsFresh(snapshot.source.observedAtUtc, capturedAtMs, nowMs, maxAgeMs))) {
    addHold(holds, "account", "SnapshotSourceMissingOrStale");
  }

  const capacity = snapshot.accountCapacity;
  if (!isRecord(capacity)) {
    addHold(holds, "account", "AccountCapacityMissing");
    return capturedAtMs;
  }
  if (capacity.complete !== true) addHold(holds, "account", "AccountCapacityIncomplete");
  if (!validLimits(capacity.configuredLimits)) {
    addHold(holds, "account", "MissingCapacityEvidence");
  }
  if (capturedAtMs !== null && nowMs !== null &&
      !timeEvidenceIsFresh(capacity.observedAtUtc, capturedAtMs, nowMs, maxAgeMs)) {
    addHold(holds, "account", "AccountCapacityStale");
  }
  validateCapacityRecords(capacity, "account", holds);
  return capturedAtMs;
}

function validateProject(project, snapshot, capturedAtMs, nowMs, maxAgeMs, holds) {
  const projectName = project?.projectName;
  const identity = { projectName };
  if (!isRecord(project) || !isNonEmptyString(projectName)) {
    addHold(holds, "project", "ProjectSnapshotIncomplete", identity);
    return false;
  }

  if (!isRecord(project.completeness) ||
      PROJECT_COMPLETENESS_KEYS.some(key => project.completeness[key] !== true)) {
    addHold(holds, "project", "ProjectSnapshotIncomplete", identity);
  }

  for (const field of ["tasks", "agents", "sourceLeases", "reviews", "challenges", "submissions"]) {
    if (!Array.isArray(project[field])) {
      addHold(holds, "project", "ProjectSnapshotIncomplete", identity, { evidence: field });
    }
  }

  if (Array.isArray(project.tasks) && project.tasks.some(task =>
    !isRecord(task) || !isNonEmptyString(task.taskId) || !isNonEmptyString(task.lifecycle) ||
    (task.currentAttemptId != null && !isNonEmptyString(task.currentAttemptId)) ||
    !Array.isArray(task.attempts) || task.attempts.some(attempt =>
      !isRecord(attempt) || !isNonEmptyString(attempt.attemptId) ||
      !isNonEmptyString(attempt.phase) || !Number.isInteger(attempt.version) ||
      !Number.isInteger(attempt.assignmentGeneration) || !isNonEmptyString(attempt.ownerAgentId)))) {
    addHold(holds, "project", "ProjectSnapshotIncomplete", identity, { evidence: "taskAttempts" });
  }
  if (Array.isArray(project.agents) && project.agents.some(agent =>
    !isRecord(agent) || !isNonEmptyString(agent.agentId) || agent.verified !== true ||
    !["Ready", "Working", "NeedsWork", "Free", "Blocked", "Parked", "Stopped", "Unknown"].includes(agent.state) ||
    !["Working", "Stopped", "Unknown"].includes(agent.execution) ||
    !Object.hasOwn(agent, "currentAssignment") ||
    !Object.hasOwn(agent, "unassignedDisposition") ||
    (agent.currentAssignment !== null && !isRecord(agent.currentAssignment)))) {
    addHold(holds, "project", "ProjectSnapshotIncomplete", identity, { evidence: "agents" });
  }
  for (const field of ["sourceLeases", "reviews", "challenges", "submissions"]) {
    if (Array.isArray(project[field]) && project[field].some(item => !isRecord(item))) {
      addHold(holds, "project", "ProjectSnapshotIncomplete", identity, { evidence: field });
    }
  }
  if (Array.isArray(project.sourceLeases) && project.sourceLeases.some(item =>
    !isRecord(item) || !isNonEmptyString(item.taskId) || !isNonEmptyString(item.attemptId) ||
    !Number.isInteger(item.assignmentGeneration) || !isNonEmptyString(item.ownerAgentId) ||
    !isNonEmptyString(item.status))) {
    addHold(holds, "project", "ProjectSnapshotIncomplete", identity, { evidence: "sourceLeaseIdentity" });
  }
  if (Array.isArray(project.reviews) && project.reviews.some(item =>
    !isRecord(item) || !isNonEmptyString(item.reviewId) || !Number.isInteger(item.version) ||
    !isNonEmptyString(item.status))) {
    addHold(holds, "project", "ProjectSnapshotIncomplete", identity, { evidence: "reviewIdentity" });
  }
  if (Array.isArray(project.submissions) && project.submissions.some(item =>
    !isRecord(item) || !isNonEmptyString(item.submissionId) || !isNonEmptyString(item.taskId) ||
    !isNonEmptyString(item.attemptId) || !Number.isInteger(item.assignmentGeneration) ||
    !isNonEmptyString(item.status))) {
    addHold(holds, "project", "ProjectSnapshotIncomplete", identity, { evidence: "submissionIdentity" });
  }

  if (!isRecord(project.source) || !isSha(project.source.commit) ||
      !isRef(project.source.ref) ||
      (capturedAtMs !== null && nowMs !== null &&
       !timeEvidenceIsFresh(project.source.observedAtUtc, capturedAtMs, nowMs, maxAgeMs))) {
    addHold(holds, "project", "ProjectSnapshotStaleOrUnbound", identity);
  }

  const capacity = project.capacity;
  if (!isRecord(capacity)) {
    addHold(holds, "project", "ProjectCapacityMissing", identity);
    return false;
  }
  if (capacity.complete !== true) {
    addHold(holds, "project", "ProjectCapacityIncomplete", identity);
  }
  if (!validLimits(capacity.configuredLimits)) {
    addHold(holds, "project", "MissingCapacityEvidence", identity);
  }
  if (capturedAtMs !== null && nowMs !== null &&
      !timeEvidenceIsFresh(capacity.observedAtUtc, capturedAtMs, nowMs, maxAgeMs)) {
    addHold(holds, "project", "ProjectCapacityStale", identity);
  }
  validateCapacityRecords(capacity, "project", holds, identity);

  if (Array.isArray(project.agents) && project.agents.every(isRecord) &&
      Array.isArray(capacity.sessions) && capacity.sessions.every(isRecord) &&
      Array.isArray(capacity.reservations) && capacity.reservations.every(isRecord)) {
    const knownAgents = new Set(project.agents.map(agent => agent.agentId));
    const occupiedAgentIds = new Set([
      ...capacity.sessions
        .filter(session => session.status === "Active" || session.status === "Unknown")
        .map(session => session.agentId),
      ...capacity.reservations
        .filter(reservation => LIVE_RESERVATION_STATUSES.has(reservation.status))
        .map(reservation => reservation.agentId)
    ].filter(isNonEmptyString));
    for (const session of capacity.sessions) {
      if ((session.status === "Active" || session.status === "Unknown") &&
          isNonEmptyString(session.agentId) && !knownAgents.has(session.agentId)) {
        addHold(holds, "project", "CapacityContradiction", identity, {
          agentId: session.agentId
        });
      }
    }
    for (const agent of project.agents) {
      if ((agent.state === "Working" || agent.state === "Unknown" ||
           agent.execution === "Working" || agent.execution === "Unknown") &&
          !occupiedAgentIds.has(agent.agentId)) {
        addHold(holds, "project", "CapacityContradiction", identity, {
          agentId: agent.agentId
        });
      }
    }
  }

  const account = snapshot.accountCapacity;
  if (isRecord(account) && Array.isArray(account.sessions) &&
      Array.isArray(account.reservations) &&
      Array.isArray(capacity.sessions) && capacity.sessions.every(isRecord) &&
      Array.isArray(capacity.reservations) && capacity.reservations.every(isRecord)) {
    const accountSessions = account.sessions.filter(x => x.projectName === projectName);
    const accountReservations = account.reservations.filter(x => x.projectName === projectName);
    if (JSON.stringify(stableRecordArray(accountSessions, "sessionId")) !==
        JSON.stringify(stableRecordArray(capacity.sessions, "sessionId")) ||
        JSON.stringify(stableRecordArray(accountReservations, "reservationId")) !==
        JSON.stringify(stableRecordArray(capacity.reservations, "reservationId"))) {
      addHold(holds, "project", "CapacityContradiction", identity);
    }
  }

  return holds.length === 0;
}

function projectIndexes(project) {
  const taskById = new Map();
  const agentById = new Map();
  const duplicateIds = [];

  for (const task of project.tasks) {
    if (taskById.has(task.taskId)) duplicateIds.push("task");
    else taskById.set(task.taskId, task);
    const attemptIds = new Set();
    for (const attempt of task.attempts) {
      if (attemptIds.has(attempt.attemptId)) duplicateIds.push("attempt");
      attemptIds.add(attempt.attemptId);
    }
  }
  for (const agent of project.agents) {
    if (agentById.has(agent.agentId)) duplicateIds.push("agent");
    else agentById.set(agent.agentId, agent);
  }
  const reviewIds = new Set();
  for (const review of project.reviews) {
    if (reviewIds.has(review.reviewId)) duplicateIds.push("review");
    reviewIds.add(review.reviewId);
  }
  const waitIds = new Set();
  for (const wait of project.challenges) {
    if (waitIds.has(wait.waitId)) duplicateIds.push("challenge");
    waitIds.add(wait.waitId);
  }
  return { taskById, agentById, duplicateIds };
}

function buildChallengeProjection(project, taskById, agentById, holds) {
  const states = [];
  const blockedKeys = new Set();
  const attemptsByKey = new Map();

  for (const task of project.tasks) {
    for (const attempt of task.attempts) {
      attemptsByKey.set(attemptIdentityKey(project.projectName, task.taskId, attempt.attemptId), { task, attempt });
    }
  }

  for (const wait of [...project.challenges].sort((a, b) => compareText(a.waitId, b.waitId))) {
    const identity = {
      projectName: project.projectName,
      taskId: wait.taskId,
      attemptId: wait.attemptId,
      waitId: wait.waitId
    };
    const linked = attemptsByKey.get(attemptIdentityKey(project.projectName, wait.taskId, wait.attemptId));
    const task = taskById.get(wait.taskId);
    const attempt = linked?.attempt;
    const owner = agentById.get(wait.ownerAgentId);
    const dependencies = wait.dependencies;
    const manualBlock = Array.isArray(dependencies) && dependencies.length === 0;
    const assignmentGeneration = Number.isInteger(attempt?.assignmentGeneration)
      ? attempt.assignmentGeneration : null;
    const key = assignmentGeneration !== null
      ? identityKey(project.projectName, wait.taskId, wait.attemptId, assignmentGeneration)
      : Number.isInteger(wait.waitIndexGeneration)
        ? identityKey(project.projectName, wait.taskId, wait.attemptId, wait.waitIndexGeneration)
        : null;
    const requeueProvenance = wait.requeueProvenance ?? null;
    const ownerReleaseProvenance = wait.ownerReleaseProvenance ?? null;
    let state = "InvariantViolation";
    let reason = null;
    let satisfiedCount = 0;

    const invalid = !isNonEmptyString(wait.waitId) ||
      !isNonEmptyString(wait.taskId) || !isNonEmptyString(wait.attemptId) ||
      !Object.hasOwn(wait, "waitIndexGeneration") ||
      !isNonEmptyString(wait.ownerAgentId) ||
      !Array.isArray(dependencies) ||
      (manualBlock ? wait.waitIndexGeneration !== null :
        !Number.isInteger(wait.waitIndexGeneration) || wait.waitIndexGeneration < 1) ||
      !task || !attempt || attempt.ownerAgentId !== wait.ownerAgentId ||
      !Number.isInteger(attempt.assignmentGeneration) || attempt.assignmentGeneration < 1 ||
      !owner || owner.verified !== true;

    if (invalid) {
      reason = "ChallengeReconciliationInvariantViolation";
    } else {
      const dependencyIds = new Set();
      let dependencyInvalid = false;
      for (const dependency of dependencies) {
        if (!isRecord(dependency) || !isNonEmptyString(dependency.dependencyId) ||
            dependencyIds.has(dependency.dependencyId) ||
            !DEPENDENCY_STATUSES.has(dependency.status)) {
          dependencyInvalid = true;
          break;
        }
        dependencyIds.add(dependency.dependencyId);
        if (dependency.status === "Satisfied") satisfiedCount++;
      }

      const allSatisfied = !dependencyInvalid && dependencies.length > 0 &&
        satisfiedCount === dependencies.length;
      const anySatisfied = satisfiedCount > 0;
      const waitGenerationMatches = manualBlock ||
        wait.waitIndexGeneration === attempt.assignmentGeneration;
      const isFreeNeedsWorkOwner = owner.state === "NeedsWork" &&
        owner.execution === "Stopped" && owner.currentAssignment === null &&
        owner.unassignedDisposition === "NeedsWork";
      const isParkedOwner = owner.state === "Parked" ||
        owner.unassignedDisposition === "Parked";
      const isBusyOwner = owner.state === "Working" ||
        owner.execution === "Working" || owner.currentAssignment !== null;
      const validRequeueProvenance = isRecord(requeueProvenance) &&
        isNonEmptyString(requeueProvenance.operationId) &&
        Number.isInteger(requeueProvenance.ledgerSequence) &&
        requeueProvenance.ledgerSequence > 0 &&
        isNonEmptyString(requeueProvenance.actor) && parseTime(requeueProvenance.atUtc) !== null &&
        requeueProvenance.action === "RequeueChallengeBlockedAttempt" &&
        requeueProvenance.previousPhase === "Blocked" &&
        requeueProvenance.newPhase === "Queued" &&
        requeueProvenance.previousOwnerAgentId === wait.ownerAgentId &&
        requeueProvenance.newOwnerAgentId === wait.ownerAgentId &&
        Number.isInteger(requeueProvenance.previousGeneration) &&
        requeueProvenance.previousGeneration > 0 &&
        Number.isInteger(requeueProvenance.newGeneration) &&
        requeueProvenance.newGeneration === requeueProvenance.previousGeneration + 1;
      const requeueGenerationMatches = validRequeueProvenance &&
        (manualBlock ? wait.waitIndexGeneration === null :
          wait.waitIndexGeneration === requeueProvenance.previousGeneration) &&
        attempt.assignmentGeneration === requeueProvenance.newGeneration;

      if (dependencyInvalid ||
          (["Pending", "Eligible"].includes(wait.status) &&
            (attempt.phase !== "Blocked" || !waitGenerationMatches)) ||
          (wait.status === "Pending" && !manualBlock && allSatisfied) ||
          (wait.status === "Eligible" && (manualBlock || !allSatisfied)) ||
          (wait.status === "Requeued" &&
            (!requeueGenerationMatches || (!manualBlock && !allSatisfied)))) {
        reason = "ChallengeReconciliationInvariantViolation";
      } else if (wait.status === "Pending") {
        if (manualBlock) {
          state = "ManualBlocked";
          reason = "ChallengeManualBlock";
        } else {
          state = anySatisfied ? "PartiallySatisfied" : "Pending";
          reason = anySatisfied ? "ChallengePartiallySatisfied" : "ChallengePending";
        }
      } else if (wait.status === "Eligible") {
        if (isParkedOwner) {
          state = "EligibleOwnerParked";
          reason = "ChallengeOwnerParked";
        } else if (isFreeNeedsWorkOwner) {
          reason = "ChallengeReconciliationInvariantViolation";
        } else if (isBusyOwner) {
          state = "EligibleOwnerBusy";
          reason = "ChallengeOwnerBusy";
        } else {
          reason = "ChallengeOwnerUnavailable";
        }
      } else if (wait.status === "Requeued") {
        const assignment = owner.currentAssignment;
        const assignmentMatches = assignment &&
          assignment.taskId === wait.taskId &&
          assignment.attemptId === wait.attemptId &&
          assignment.assignmentGeneration === attempt.assignmentGeneration;
        const ownerAttributedManualRequeue = !manualBlock ||
          requeueProvenance.actor === wait.ownerAgentId;
        if (!validRequeueProvenance || !requeueGenerationMatches || !ownerAttributedManualRequeue ||
            task.currentAttemptId !== attempt.attemptId ||
            !assignmentMatches) {
          reason = "ChallengeReconciliationInvariantViolation";
        } else if (attempt.phase === "Ready" && owner.state === "Ready" &&
            owner.execution === "Stopped" && owner.unassignedDisposition == null) {
          state = "Requeued";
        } else if (["Started", "Working"].includes(attempt.phase) &&
            owner.state === "Working" && owner.execution === "Working") {
          state = "RequeuedWorking";
        } else if (isParkedOwner) {
          state = "RequeuedOwnerParked";
          reason = "ChallengeOwnerParked";
        } else if (owner.state === "Unknown" || owner.execution === "Unknown") {
          state = "RequeuedOwnerUnknown";
          reason = "ChallengeOwnerUnavailable";
        } else {
          reason = "ChallengeReconciliationInvariantViolation";
        }
      } else {
        reason = "ChallengeReconciliationInvariantViolation";
      }
    }

    states.push({
      ...identity,
      waitIndexGeneration: wait.waitIndexGeneration ?? null,
      assignmentGeneration,
      state,
      requeueProvenance: isRecord(requeueProvenance) ? { ...requeueProvenance } : null,
      ownerReleaseProvenance: isRecord(ownerReleaseProvenance) ? { ...ownerReleaseProvenance } : null,
      satisfiedDependencies: satisfiedCount,
      totalDependencies: Array.isArray(dependencies) ? dependencies.length : 0,
      holdReason: reason
    });
    if (reason) {
      if (key !== null) blockedKeys.add(key);
      addHold(holds, "challenge", reason, identity, {
        waitIndexGeneration: wait.waitIndexGeneration ?? null,
        assignmentGeneration,
        state
      });
    }
  }

  return { states, blockedKeys };
}

function attemptBaseHolds(project, task, attempt, context) {
  const holds = [];
  const projectName = project.projectName;
  const identity = {
    projectName, taskId: task.taskId, attemptId: attempt.attemptId
  };
  const key = identityKey(projectName, task.taskId, attempt.attemptId,
    attempt.assignmentGeneration);
  const idempotencyKey = deterministicKey("worker", [
    projectName, task.taskId, attempt.attemptId, attempt.assignmentGeneration
  ]);

  if (TERMINAL_TASK_LIFECYCLES.has(task.lifecycle)) {
    addHold(holds, "attempt", "TerminalTask", identity, { lifecycle: task.lifecycle });
  } else if (!ACTIVE_TASK_LIFECYCLES.has(task.lifecycle)) {
    addHold(holds, "attempt", "UnknownTaskLifecycle", identity, { lifecycle: task.lifecycle });
  }

  if (!Number.isInteger(attempt.assignmentGeneration) ||
      attempt.assignmentGeneration < 1 || !Number.isInteger(attempt.version) ||
      attempt.version < 1 || task.currentAttemptId !== attempt.attemptId) {
    addHold(holds, "attempt", "StaleAttemptGeneration", identity, {
      currentAttemptId: task.currentAttemptId ?? null,
      assignmentGeneration: attempt.assignmentGeneration ?? null
    });
  }

  const owner = context.agentById.get(attempt.ownerAgentId);
  if (!owner) {
    addHold(holds, "attempt", "OwnerMissing", identity, {
      ownerAgentId: attempt.ownerAgentId ?? null
    });
  } else if (owner.verified !== true) {
    addHold(holds, "attempt", "OwnerStateUnverified", identity, { ownerAgentId: owner.agentId });
  } else if (owner.state === "Parked" || owner.unassignedDisposition === "Parked") {
    addHold(holds, "attempt", "OwnerParked", identity, { ownerAgentId: owner.agentId });
  } else if (context.sessions.some(session =>
    session.agentId === owner.agentId && session.status === "Unknown")) {
    addHold(holds, "attempt", "OwnerStateUnknown", identity, { ownerAgentId: owner.agentId });
  } else if (context.sessions.some(session =>
    session.agentId === owner.agentId && session.status === "Active")) {
    addHold(holds, "attempt", "OwnerBusy", identity, { ownerAgentId: owner.agentId });
  } else if (owner.state !== "Ready" || owner.execution !== "Stopped") {
    addHold(holds, "attempt",
      owner.state === "Unknown" || owner.execution === "Unknown" ? "OwnerStateUnknown" : "OwnerBusy",
      identity, { ownerAgentId: owner.agentId, state: owner.state, execution: owner.execution });
  } else {
    const assignment = owner.currentAssignment;
    if (!assignment || assignment.taskId !== task.taskId ||
        assignment.attemptId !== attempt.attemptId ||
        assignment.assignmentGeneration !== attempt.assignmentGeneration) {
      addHold(holds, "attempt", "OwnerAssignmentMismatch", identity, {
        ownerAgentId: owner.agentId
      });
    } else if (owner.unassignedDisposition != null) {
      addHold(holds, "attempt", "OwnerAssignmentContradiction", identity, {
        ownerAgentId: owner.agentId,
        unassignedDisposition: owner.unassignedDisposition
      });
    }
  }

  const leases = context.leasesByKey.get(key) ?? [];
  if (leases.length !== 1) {
    addHold(holds, "attempt", leases.length === 0 ? "SourceLeaseMissing" : "SourceLeaseConflict",
      identity, { leaseCount: leases.length });
  } else {
    const lease = leases[0];
    if (lease.status !== "Valid" ||
        lease.ownerAgentId !== attempt.ownerAgentId ||
        lease.assignmentGeneration !== attempt.assignmentGeneration ||
        !isNonEmptyString(lease.repositoryId) ||
        !isRef(lease.sourceRef) || !isSha(lease.baseCommit) ||
        !isRef(lease.targetRef) || lease.sourceRef === lease.targetRef) {
      addHold(holds, "attempt", "SourceLeaseConflict", identity, {
        leaseStatus: lease.status ?? null
      });
    }
  }

  for (const submission of context.submissionsByKey.get(key) ?? []) {
    if (submission.status === "Uncertain" || submission.status === "Pending") {
      addHold(holds, "attempt",
        submission.status === "Uncertain" ? "UncertainSubmission" : "PendingSubmission",
        identity, { submissionId: submission.submissionId });
    } else if (!SUBMISSION_STATUSES.has(submission.status)) {
      addHold(holds, "attempt", "UnknownSubmissionStatus", identity, {
        submissionId: submission.submissionId
      });
    }
  }

  const existing = matchingReservations(context.reservations, idempotencyKey,
    owner?.agentId ?? null);
  if (existing.exact.length > 0) {
    const uncertain = existing.exact.some(x => x.status === "Uncertain");
    addHold(holds, "attempt", uncertain ? "UncertainSubmission" : "DuplicateReservation",
      identity, { reservationCount: existing.exact.length });
  } else if (existing.owner.length > 0) {
    addHold(holds, "attempt", "OwnerReserved", identity, {
      ownerAgentId: owner?.agentId ?? null
    });
  }

  return { holds, idempotencyKey };
}

function reviewBaseHolds(project, review, reservations) {
  const holds = [];
  const identity = { projectName: project.projectName, reviewId: review.reviewId };

  if (!isNonEmptyString(review.reviewId) ||
      !Number.isInteger(review.version) || review.version < 1) {
    addHold(holds, "review", "ReviewIdentityIncomplete", identity);
  }
  if (review.status === "Claimed" || review.claim != null) {
    addHold(holds, "review", "ReviewAlreadyClaimed", identity);
  } else if (review.status === "Uncertain") {
    addHold(holds, "review", "UncertainSubmission", identity);
  } else if (review.status !== "Pending" &&
      !["Accepted", "ChangesRequested", "Rejected", "Withdrawn"].includes(review.status)) {
    addHold(holds, "review", "UnknownReviewStatus", identity, {
      status: review.status ?? null
    });
  }

  if (review.status === "Pending" && review.claim === undefined) {
    addHold(holds, "review", "ReviewClaimStateMissing", identity);
  }
  if (review.status === "Pending" && review.frozen !== true) {
    addHold(holds, "review", "ReviewNotFrozen", identity);
  }
  if (review.status === "Pending" &&
      (!isSha(review.reviewedHeadOid) || !isRef(review.reviewRef) ||
       !isRef(review.sourceRef) || !isRef(review.targetRef))) {
    addHold(holds, "review", "ReviewSnapshotIncomplete", identity);
  }

  const idempotencyKey = deterministicKey("review", [
    project.projectName, review.reviewId, review.version
  ]);
  const existing = matchingReservations(reservations, idempotencyKey);
  if (existing.exact.length > 0) {
    const uncertain = existing.exact.some(x => x.status === "Uncertain");
    addHold(holds, "review", uncertain ? "UncertainSubmission" : "DuplicateReservation",
      identity, { reservationCount: existing.exact.length });
  }
  return { holds, idempotencyKey };
}

function finalize(plan) {
  plan.challengeStates.sort((a, b) =>
    compareText(a.projectName, b.projectName) || compareText(a.waitId, b.waitId));
  sortHolds(plan.holds);
  return plan;
}

/**
 * Pure Phase-1 projection from one complete account/project ledger snapshot.
 * It never writes reservations, launches agents, claims reviews, or wakes Challenges.
 */
export function planLedger(snapshot, nowUtc, options = {}) {
  const maxAgeMs = options.maxAgeMs ?? DEFAULT_MAX_AGE_MS;
  if (!Number.isInteger(maxAgeMs) || maxAgeMs < 1) {
    throw new TypeError("maxAgeMs must be a positive integer");
  }

  const plan = {
    schemaVersion: 2,
    source: isRecord(snapshot?.source) ? { ...snapshot.source } : null,
    capturedAtUtc: snapshot?.capturedAtUtc ?? null,
    workerCandidates: [],
    reviewCandidates: [],
    challengeStates: [],
    requeueIntents: [],
    holds: []
  };

  const globalHolds = [];
  const nowMs = parseTime(nowUtc);
  const capturedAtMs = validateRoot(snapshot, nowUtc, maxAgeMs, globalHolds);
  if (globalHolds.length > 0) {
    plan.holds.push(...globalHolds);
    return finalize(plan);
  }

  const projectNames = new Set();
  for (const project of snapshot.projects) {
    if (!isRecord(project) || !isNonEmptyString(project.projectName)) continue;
    if (projectNames.has(project.projectName)) {
      addHold(globalHolds, "account", "DuplicateProjectIdentity", {
        projectName: project.projectName
      });
    }
    projectNames.add(project.projectName);
  }

  const occupiedProjects = new Set([
    ...snapshot.accountCapacity.sessions
      .filter(x => x.status === "Active" || x.status === "Unknown")
      .map(x => x.projectName),
    ...snapshot.accountCapacity.reservations
      .filter(x => LIVE_RESERVATION_STATUSES.has(x.status))
      .map(x => x.projectName)
  ]);
  for (const projectName of occupiedProjects) {
    if (!projectNames.has(projectName)) {
      addHold(globalHolds, "account", "CapacityProjectMissing", { projectName });
    }
  }
  if (globalHolds.length > 0) {
    plan.holds.push(...globalHolds);
    return finalize(plan);
  }

  const validProjects = [];
  for (const project of snapshot.projects) {
    const holds = [];
    const valid = validateProject(project, snapshot, capturedAtMs, nowMs, maxAgeMs, holds);
    if (!valid) {
      plan.holds.push(...holds);
      continue;
    }
    const { taskById, agentById, duplicateIds } = projectIndexes(project);
    if (duplicateIds.length > 0) {
      addHold(holds, "project", "DuplicateProjectEntityIdentity", {
        projectName: project.projectName
      }, { entities: [...duplicateIds].sort(compareText) });
      plan.holds.push(...holds);
      continue;
    }
    validProjects.push({ project, taskById, agentById });
  }

  const accountUsed = capacityCounts(snapshot.accountCapacity);
  const projectUsed = new Map();
  for (const { project } of validProjects) {
    projectUsed.set(project.projectName, capacityCounts(project.capacity));
  }

  const workerRaw = [];
  const reviewRaw = [];

  for (const { project, taskById, agentById } of validProjects) {
    const projectName = project.projectName;
    const challengeHolds = [];
    const challenge = buildChallengeProjection(project, taskById, agentById, challengeHolds);
    plan.challengeStates.push(...challenge.states);
    plan.holds.push(...challengeHolds);

    const leasesByKey = new Map();
    for (const lease of project.sourceLeases) {
      const key = identityKey(projectName, lease.taskId, lease.attemptId,
        lease.assignmentGeneration);
      const values = leasesByKey.get(key) ?? [];
      values.push(lease);
      leasesByKey.set(key, values);
    }
    const submissionsByKey = new Map();
    for (const submission of project.submissions) {
      const key = identityKey(projectName, submission.taskId, submission.attemptId,
        submission.assignmentGeneration);
      const values = submissionsByKey.get(key) ?? [];
      values.push(submission);
      submissionsByKey.set(key, values);
    }

    for (const task of [...project.tasks].sort((a, b) => compareText(a.taskId, b.taskId))) {
      for (const attempt of [...task.attempts].sort((a, b) =>
        compareText(a.attemptId, b.attemptId))) {
        if (attempt.phase === "Blocked") {
          const key = identityKey(projectName, task.taskId, attempt.attemptId,
            attempt.assignmentGeneration);
          if (!challenge.blockedKeys.has(key)) {
            addHold(plan.holds, "attempt", "AttemptBlocked", {
              projectName, taskId: task.taskId, attemptId: attempt.attemptId
            });
          }
          continue;
        }
        if (attempt.phase !== "Ready") continue;

        const evaluated = attemptBaseHolds(project, task, attempt, {
          agentById, leasesByKey, submissionsByKey, sessions: project.capacity.sessions,
          reservations: project.capacity.reservations
        });
        const key = identityKey(projectName, task.taskId, attempt.attemptId,
          attempt.assignmentGeneration);
        if (evaluated.holds.length > 0 || challenge.blockedKeys.has(key)) {
          plan.holds.push(...evaluated.holds);
          continue;
        }

        workerRaw.push({
          kind: "worker",
          projectName,
          taskId: task.taskId,
          attemptId: attempt.attemptId,
          assignmentGeneration: attempt.assignmentGeneration,
          attemptVersion: attempt.version,
          priorityRank: Number.isInteger(attempt.priorityRank) ? attempt.priorityRank : 100000,
          ownerAgentId: attempt.ownerAgentId,
          sourceLease: { ...leasesByKey.get(key)[0] },
          idempotencyKey: evaluated.idempotencyKey
        });
      }
    }

    for (const review of [...project.reviews].sort((a, b) =>
      compareText(a.reviewId, b.reviewId) || (a.version ?? 0) - (b.version ?? 0))) {
      const evaluated = reviewBaseHolds(project, review, project.capacity.reservations);
      if (evaluated.holds.length > 0) {
        plan.holds.push(...evaluated.holds);
        continue;
      }
      if (review.status !== "Pending") continue;
      reviewRaw.push({
        kind: "reviewer",
        projectName,
        reviewId: review.reviewId,
        reviewVersion: review.version,
        reviewRef: review.reviewRef,
        sourceRef: review.sourceRef,
        targetRef: review.targetRef,
        reviewedHeadOid: review.reviewedHeadOid,
        createdSequence: Number.isInteger(review.createdSequence) ? review.createdSequence : 0,
        idempotencyKey: evaluated.idempotencyKey
      });
    }
  }

  workerRaw.sort((a, b) =>
    a.priorityRank - b.priorityRank ||
    compareText(a.projectName, b.projectName) ||
    compareText(a.taskId, b.taskId) ||
    compareText(a.attemptId, b.attemptId));

  for (const candidate of workerRaw) {
    const project = snapshot.projects.find(x => x.projectName === candidate.projectName);
    const local = projectUsed.get(candidate.projectName);
    if (accountUsed.worker >= snapshot.accountCapacity.configuredLimits.worker) {
      addHold(plan.holds, "attempt", "AccountCapacityFull", {
        projectName: candidate.projectName,
        taskId: candidate.taskId,
        attemptId: candidate.attemptId
      });
    } else if (local.worker >= project.capacity.configuredLimits.worker) {
      addHold(plan.holds, "attempt", "ProjectCapacityFull", {
        projectName: candidate.projectName,
        taskId: candidate.taskId,
        attemptId: candidate.attemptId
      });
    } else {
      plan.workerCandidates.push(candidate);
      accountUsed.worker++;
      local.worker++;
    }
  }

  reviewRaw.sort((a, b) =>
    a.createdSequence - b.createdSequence ||
    compareText(a.projectName, b.projectName) ||
    compareText(a.reviewId, b.reviewId) ||
    a.reviewVersion - b.reviewVersion);

  for (const candidate of reviewRaw) {
    const project = snapshot.projects.find(x => x.projectName === candidate.projectName);
    const local = projectUsed.get(candidate.projectName);
    if (accountUsed.reviewer >= snapshot.accountCapacity.configuredLimits.reviewer) {
      addHold(plan.holds, "review", "AccountCapacityFull", {
        projectName: candidate.projectName, reviewId: candidate.reviewId
      });
    } else if (local.reviewer >= project.capacity.configuredLimits.reviewer) {
      addHold(plan.holds, "review", "ProjectCapacityFull", {
        projectName: candidate.projectName, reviewId: candidate.reviewId
      });
    } else {
      plan.reviewCandidates.push(candidate);
      accountUsed.reviewer++;
      local.reviewer++;
    }
  }

  return finalize(plan);
}

export const plannerPolicy = Object.freeze({
  defaultMaxAgeMs: DEFAULT_MAX_AGE_MS,
  activeSessionStatuses: ["Active", "Unknown"],
  liveReservationStatuses: [...LIVE_RESERVATION_STATUSES],
  terminalTaskLifecycles: [...TERMINAL_TASK_LIFECYCLES]
});
