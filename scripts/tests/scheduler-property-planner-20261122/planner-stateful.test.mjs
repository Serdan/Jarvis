import test from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";
import { planLedger } from "../../jarvis-ledger-planner.mjs";
import { phase2DefaultOffDiagnostic } from "../../jarvis-phase2-snapshot-adapter.mjs";

// The tested production files must be byte-for-byte identical to this accepted main.
const PIN = "dcac884fc061869572b12a03befc12f11c009f2f";
const NOW = "2026-11-22T00:00:00.000Z";
const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
const SOURCE_PATHS = [
  "scripts/jarvis-ledger-planner.mjs",
  "scripts/jarvis-phase2-snapshot-adapter.mjs",
  "Client/Modules/NativeSchedulerRosterPolicy.fs"
];
const LIVE = new Set(["Reserved", "Dispatching", "Submitted", "Uncertain"]);
const clone = value => structuredClone(value);
const shaKey = (kind, parts) => kind + ":" +
  createHash("sha256").update(JSON.stringify(parts)).digest("hex");
function assertSourcePin() {
  const ancestor = execFileSync("git", ["merge-base", "--is-ancestor", PIN, "HEAD"],
    { cwd: ROOT, encoding: "utf8" });
  assert.equal(ancestor, "");
  for (const path of SOURCE_PATHS) {
    const blob = execFileSync("git", ["rev-parse", PIN + ":" + path],
      { cwd: ROOT, encoding: "utf8" }).trim();
    const disk = execFileSync("git", ["hash-object", path],
      { cwd: ROOT, encoding: "utf8" }).trim();
    assert.equal(disk, blob, "modified production module " + path);
    assert.ok(readFileSync(resolve(ROOT, path)).length > 0);
  }
}
function makeProject(name) {
  const tasks = [], agents = [], sourceLeases = [], reviews = [];
  for (let i = 0; i < 3; i++) {
    const taskId = name + "-task-" + i;
    const attemptId = name + "-attempt-" + i;
    const ownerAgentId = name + "-Worker-" + i;
    tasks.push({
      taskId, lifecycle: "InProgress", currentAttemptId: attemptId,
      attempts: [{ attemptId, phase: "Ready", version: 1,
        assignmentGeneration: 1, ownerAgentId, priorityRank: i }]
    });
    agents.push({ agentId: ownerAgentId, verified: true,
      state: "Ready", execution: "Stopped",
      currentAssignment: { taskId, attemptId, assignmentGeneration: 1 },
      unassignedDisposition: null });
    sourceLeases.push({ taskId, attemptId, assignmentGeneration: 1,
      ownerAgentId, status: "Valid", repositoryId: "fixture-" + name,
      sourceRef: "refs/heads/feature/" + taskId, targetRef: "refs/heads/main",
      baseCommit: PIN });
    reviews.push({ reviewId: name + "-review-" + i, version: 1,
      status: "Pending", claim: null, frozen: true,
      reviewRef: "refs/heads/reviews/" + name + "/" + i,
      sourceRef: "refs/heads/feature/" + taskId,
      targetRef: "refs/heads/main", reviewedHeadOid: PIN,
      createdSequence: 10 + i });
  }
  return { projectName: name,
    source: { commit: PIN, ref: "refs/heads/main", observedAtUtc: NOW },
    completeness: { tasks: true, agents: true, sourceLeases: true,
      reviews: true, challenges: true, submissions: true, capacity: true },
    capacity: { complete: true, observedAtUtc: NOW,
      configuredLimits: { worker: 3, reviewer: 2 },
      sessions: [], reservations: [] },
    tasks, agents, sourceLeases, reviews, challenges: [], submissions: [] };
}
function makeSnapshot() {
  const projects = [makeProject("Alpha"), makeProject("Beta")];
  return { schemaVersion: 2, capturedAtUtc: NOW,
    source: { commit: PIN, ref: "refs/heads/jarvis-account-admission", observedAtUtc: NOW },
    completeness: { account: true, projects: true, accountCapacity: true },
    accountCapacity: { complete: true, observedAtUtc: NOW,
      configuredLimits: { worker: 4, reviewer: 3 }, sessions: [], reservations: [] },
    projects };
}
function synchronize(s) {
  s.accountCapacity.sessions = s.projects.flatMap(p => clone(p.capacity.sessions));
  s.accountCapacity.reservations = s.projects.flatMap(p => clone(p.capacity.reservations));
  return s;
}
function isLive(x) { return LIVE.has(x.status); }
function positiveEvidence(s) {
  if (!s || s.schemaVersion !== 2 || !Array.isArray(s.projects) ||
      !s.completeness || Object.values(s.completeness).some(x => x !== true) ||
      !s.accountCapacity || s.accountCapacity.complete !== true ||
      !validPool(s.accountCapacity) || !fresh(s.capturedAtUtc) ||
      !fresh(s.source?.observedAtUtc) || !fresh(s.accountCapacity.observedAtUtc)) return false;
  if (new Set(s.projects.map(p => p.projectName)).size !== s.projects.length) return false;
  const names = new Set(s.projects.map(p => p.projectName));
  return [...s.accountCapacity.sessions, ...s.accountCapacity.reservations]
    .filter(x => x.status === "Active" || x.status === "Unknown" || isLive(x))
    .every(x => names.has(x.projectName));
}
function fresh(time) {
  const value = Date.parse(time);
  return Number.isFinite(value) && Math.abs(Date.parse(NOW) - value) <= 180000;
}
function validPool(cap) {
  return cap && ["worker", "reviewer"].every(kind =>
    Number.isInteger(cap.configuredLimits?.[kind]) && cap.configuredLimits[kind] >= 0) &&
    Array.isArray(cap.sessions) && Array.isArray(cap.reservations);
}
function occupancy(pool) {
  const out = { worker: 0, reviewer: 0 };
  for (const session of pool.sessions) {
    if (["Active", "Unknown"].includes(session.status)) out[session.kind]++;
  }
  for (const reservation of pool.reservations) {
    if (isLive(reservation)) out[reservation.kind]++;
  }
  return out;
}
function sameCapSubset(s, p) {
  for (const [field, key] of [["sessions", "sessionId"], ["reservations", "reservationId"]]) {
    const subset = s.accountCapacity[field].filter(x => x.projectName === p.projectName);
    if (JSON.stringify(subset.map(x => x[key]).sort()) !==
        JSON.stringify(p.capacity[field].map(x => x[key]).sort())) return false;
  }
  return true;
}
function eligibleProject(s, p) {
  if (!p?.source || !fresh(p.source.observedAtUtc) ||
      !Object.values(p.completeness ?? {}).every(x => x === true) ||
      p.capacity?.complete !== true || !validPool(p.capacity) ||
      !fresh(p.capacity.observedAtUtc) || !sameCapSubset(s, p) ||
      !Array.isArray(p.agents) || !Array.isArray(p.tasks) ||
      !Array.isArray(p.sourceLeases) || !Array.isArray(p.reviews) ||
      !Array.isArray(p.challenges) || !Array.isArray(p.submissions)) return false;
  const ids = p.tasks.map(x => x.taskId);
  if (new Set(ids).size !== ids.length) return false;
  const agentIds = p.agents.map(x => x.agentId);
  if (new Set(agentIds).size !== agentIds.length) return false;
  const occupied = new Set([
    ...p.capacity.sessions.filter(x => ["Active", "Unknown"].includes(x.status)).map(x => x.agentId),
    ...p.capacity.reservations.filter(isLive).map(x => x.agentId)
  ]);
  return p.agents.every(x => x.verified === true &&
    (!(x.state === "Working" || x.state === "Unknown" ||
      x.execution === "Working" || x.execution === "Unknown") || occupied.has(x.agentId))) &&
    p.capacity.sessions.filter(x => ["Active", "Unknown"].includes(x.status) && x.agentId)
      .every(x => agentIds.includes(x.agentId));
}
// Independent, intentionally compact domain model: select eligible intents first,
// then allocate projected quotas. No production helper, policy constant or sorter is reused.
function reference(s) {
  if (!positiveEvidence(s)) return { workers: [], reviewers: [] };
  const rawWorkers = [], rawReviewers = [], locals = new Map();
  for (const p of s.projects) {
    if (!eligibleProject(s, p)) continue;
    locals.set(p.projectName, occupancy(p.capacity));
    const busy = new Set(p.capacity.sessions
      .filter(x => ["Active", "Unknown"].includes(x.status)).map(x => x.agentId));
    for (const task of p.tasks) {
      for (const attempt of task.attempts) {
        if (attempt.phase !== "Ready" || !["Open", "InProgress"].includes(task.lifecycle) ||
            task.currentAttemptId !== attempt.attemptId ||
            attempt.version < 1 || attempt.assignmentGeneration < 1) continue;
        const a = p.agents.find(x => x.agentId === attempt.ownerAgentId);
        const matching = p.sourceLeases.filter(x => x.taskId === task.taskId &&
          x.attemptId === attempt.attemptId &&
          x.assignmentGeneration === attempt.assignmentGeneration);
        if (!a || a.state !== "Ready" || a.execution !== "Stopped" ||
            a.unassignedDisposition != null || busy.has(a.agentId) ||
            !a.currentAssignment ||
            a.currentAssignment.taskId !== task.taskId ||
            a.currentAssignment.attemptId !== attempt.attemptId ||
            a.currentAssignment.assignmentGeneration !== attempt.assignmentGeneration ||
            matching.length !== 1 || matching[0].status !== "Valid" ||
            matching[0].ownerAgentId !== a.agentId ||
            p.challenges.some(w => w.taskId === task.taskId && w.attemptId === attempt.attemptId) ||
            p.submissions.some(x => x.taskId === task.taskId &&
              x.attemptId === attempt.attemptId &&
              x.assignmentGeneration === attempt.assignmentGeneration &&
              ["Pending", "Uncertain"].includes(x.status))) continue;
        const key = shaKey("worker", [p.projectName, task.taskId, attempt.attemptId,
          attempt.assignmentGeneration]);
        if (p.capacity.reservations.some(x => x.idempotencyKey === key ||
            (isLive(x) && x.agentId === a.agentId))) continue;
        rawWorkers.push({ projectName: p.projectName, taskId: task.taskId,
          attemptId: attempt.attemptId, assignmentGeneration: attempt.assignmentGeneration,
          key, priority: Number.isInteger(attempt.priorityRank) ? attempt.priorityRank : 100000 });
      }
    }
    for (const r of p.reviews) {
      if (r.status !== "Pending" || r.claim !== null || r.frozen !== true ||
          typeof r.reviewedHeadOid !== "string" || !r.reviewRef ||
          !r.sourceRef || !r.targetRef || r.version < 1) continue;
      const key = shaKey("review", [p.projectName, r.reviewId, r.version]);
      if (p.capacity.reservations.some(x => x.idempotencyKey === key)) continue;
      rawReviewers.push({ projectName: p.projectName, reviewId: r.reviewId,
        reviewVersion: r.version, key, created: r.createdSequence });
    }
  }
  const cmp = (a, b) => a < b ? -1 : a > b ? 1 : 0;
  rawWorkers.sort((a,b) => a.priority - b.priority || cmp(a.projectName,b.projectName) ||
    cmp(a.taskId,b.taskId) || cmp(a.attemptId,b.attemptId));
  rawReviewers.sort((a,b) => a.created - b.created || cmp(a.projectName,b.projectName) ||
    cmp(a.reviewId,b.reviewId) || a.reviewVersion - b.reviewVersion);
  const account = occupancy(s.accountCapacity);
  const selected = (candidates, kind) => candidates.filter(c => {
    const local = locals.get(c.projectName);
    const pool = s.projects.find(p => p.projectName === c.projectName).capacity;
    if (account[kind] >= s.accountCapacity.configuredLimits[kind] ||
        local[kind] >= pool.configuredLimits[kind]) return false;
    account[kind]++; local[kind]++;
    return true;
  });
  return { workers: selected(rawWorkers, "worker").map(x => x.key),
    reviewers: selected(rawReviewers, "reviewer").map(x => x.key) };
}
function observed(s) {
  const before = JSON.stringify(s);
  const p = planLedger(s, NOW);
  assert.equal(JSON.stringify(s), before, "planner must not mutate ledger observation");
  assert.deepEqual(p.requeueIntents, [], "no planner-driven Challenge requeues");
  assert.equal(Object.hasOwn(p, "sideEffects"), false, "planner is a pure projection");
  assert.equal(new Set(p.workerCandidates.map(x => x.idempotencyKey)).size, p.workerCandidates.length);
  assert.equal(new Set(p.reviewCandidates.map(x => x.idempotencyKey)).size, p.reviewCandidates.length);
  return { workers: p.workerCandidates.map(x => x.idempotencyKey),
    reviewers: p.reviewCandidates.map(x => x.idempotencyKey), plan: p };
}
function compare(s) {
  const expectation = reference(s);
  const actual = observed(s);
  assert.deepEqual(actual.workers, expectation.workers, "worker allocation");
  assert.deepEqual(actual.reviewers, expectation.reviewers, "reviewer allocation");
  assert.deepEqual(planLedger(clone(s), NOW), actual.plan, "deterministic replay");
  const a = occupancy(s.accountCapacity);
  for (const kind of ["worker", "reviewer"]) {
    const n = kind === "worker" ? actual.workers.length : actual.reviewers.length;
    if (positiveEvidence(s)) {
      const limit = s.accountCapacity.configuredLimits[kind];
      if (a[kind] > limit) assert.equal(n, 0, "oversubscribed account cannot admit more");
      else assert.ok(a[kind] + n <= limit, "no new account oversubscription");
    }
    for (const project of s.projects) {
      if (!eligibleProject(s, project)) continue;
      const count = (kind === "worker" ? actual.plan.workerCandidates :
        actual.plan.reviewCandidates).filter(x => x.projectName === project.projectName).length;
      const existing = occupancy(project.capacity)[kind];
      const limit = project.capacity.configuredLimits[kind];
      if (existing > limit) assert.equal(count, 0,
        "oversubscribed project cannot admit more");
      else assert.ok(existing + count <= limit, "no new per-project oversubscription");
    }
  }
  return actual;
}
function xorshift(initial) {
  let state = (initial | 0) || 1;
  return n => {
    state ^= state << 13; state ^= state >>> 17; state ^= state << 5;
    return (state >>> 0) % n;
  };
}
function commandStream(seed, transitions = 14) {
  const next = xorshift(seed);
  return Array.from({ length: transitions }, (_, step) => ({
    op: next(14), project: next(2), index: next(3),
    value: next(9), step
  }));
}
function applyCommand(s, c) {
  const p = s.projects[c.project], task = p.tasks[c.index],
    attempt = task.attempts[0], agent = p.agents[c.index],
    review = p.reviews[c.index];
  const eraseSession = () => { p.capacity.sessions = p.capacity.sessions.filter(x => x.agentId !== agent.agentId); };
  const reset = () => {
    eraseSession();
    attempt.phase = "Ready"; attempt.version++;
    agent.state = "Ready"; agent.execution = "Stopped";
    agent.currentAssignment = { taskId: task.taskId, attemptId: attempt.attemptId,
      assignmentGeneration: attempt.assignmentGeneration };
    agent.unassignedDisposition = null;
  };
  switch (c.op) {
    case 0: // canonical start with observed occupancy
      reset(); attempt.phase = "Started"; agent.state = "Working"; agent.execution = "Working";
      p.capacity.sessions.push({ sessionId: "s-" + c.step + "-" + p.projectName,
        projectName: p.projectName, kind: "worker", status: "Active", agentId: agent.agentId });
      break;
    case 1: reset(); agent.state = "Parked"; agent.currentAssignment = null;
      agent.unassignedDisposition = "Parked"; break;
    case 2: reset(); break;
    case 3: attempt.priorityRank = c.value - 4; break;
    case 4: p.capacity.configuredLimits.worker = c.value % 5; break;
    case 5: s.accountCapacity.configuredLimits.worker = c.value; break;
    case 6: // observed unattributed unknown occupancy is still consumed
      p.capacity.sessions.push({ sessionId: "unknown-" + c.step + "-" + p.projectName,
        projectName: p.projectName, kind: "reviewer", status: "Unknown" }); break;
    case 7: p.capacity.sessions = p.capacity.sessions.filter(x => !x.sessionId.startsWith("unknown-")); break;
    case 8: { // a serialized proposal is recorded; no browser send
      const chosen = observed(s).plan.workerCandidates.find(x => x.projectName === p.projectName);
      if (chosen) p.capacity.reservations.push({
        reservationId: "r-" + c.step + "-" + p.projectName,
        projectName: p.projectName, kind: "worker", status: "Reserved",
        idempotencyKey: chosen.idempotencyKey, agentId: chosen.ownerAgentId });
      break;
    }
    case 9: p.capacity.reservations.forEach(x => { x.status = "Released"; }); break;
    case 10: review.status = "Claimed"; review.claim = { owner: "fixture-reviewer" }; break;
    case 11: review.version++; review.status = "Pending"; review.claim = null; break;
    case 12: p.sourceLeases[c.index].status = "Unverified"; break;
    case 13: p.sourceLeases[c.index].status = "Valid"; break;
  }
  synchronize(s);
}
function runTrace(commands, mutateOracle = false) {
  const s = synchronize(makeSnapshot());
  for (const c of commands) {
    applyCommand(s, c);
    if (mutateOracle && c.op === 6) {
      const fake = reference(s);
      fake.workers.push("worker:deliberately-forged");
      const actual = observed(s);
      assert.deepEqual(actual.workers, fake.workers, "mutant should be rejected");
    } else compare(s);
  }
}
function category(e) {
  return /worker allocation|worker:deliberately-forged|worker/.test(e.message) ? "WorkerMismatch" :
    /reviewer/.test(e.message) ? "ReviewerMismatch" : "InvariantViolation";
}
function shrink(commands, wanted, mutateOracle = false) {
  let current = [...commands];
  for (let width = Math.floor(current.length / 2); width > 0; width = Math.floor(width / 2)) {
    for (let i = 0; i + width <= current.length;) {
      const trial = [...current.slice(0,i), ...current.slice(i + width)];
      if (trial.length === 0) { i++; continue; }
      try { runTrace(trial, mutateOracle); i++; }
      catch(e) { if (category(e) === wanted) current = trial; else i++; }
    }
  }
  return current;
}
test("source-pinned exact Phase1 and Phase2 production files", assertSourcePin);
test("independent model against 320 seeded stateful 14-transition traces", () => {
  const requested = process.env.SCHEDULER_PROPERTY_SEED;
  const seedSet = requested === undefined ? Array.from({length:320}, (_,i) => i + 1) :
    [Number(requested)];
  assert.ok(seedSet.every(seed => Number.isInteger(seed) && seed >= 1 && seed <= 320),
    "SCHEDULER_PROPERTY_SEED must be an integer in [1,320]");
  for (const seed of seedSet) {
    const commands = commandStream(seed);
    try { runTrace(commands); }
    catch (e) {
      const minimal = shrink(commands, category(e));
      console.error(JSON.stringify({ pin: PIN, seed, category: category(e), minimal }, null, 2));
      throw e;
    }
  }
});
test("adversarial global and local evidence fail closed", () => {
  const cases = [
    s => { s.completeness.projects = false; },
    s => { s.accountCapacity.complete = false; },
    s => { s.accountCapacity.configuredLimits.worker = -1; },
    s => { s.accountCapacity.sessions = null; },
    s => { s.capturedAtUtc = "2026-11-21T23:00:00.000Z"; },
    s => { s.source.observedAtUtc = "2026-11-21T23:00:00.000Z"; },
    s => { s.projects[0].projectName = "Beta"; },
    s => { s.accountCapacity.sessions.push({ sessionId: "orphan", projectName: "Gamma",
      kind: "worker", status: "Unknown" }); }
  ];
  for (const mutate of cases) {
    const s = synchronize(makeSnapshot());
    mutate(s);
    const a = observed(s);
    assert.deepEqual(a.workers, []);
    assert.deepEqual(a.reviewers, []);
    assert.ok(a.plan.holds.length > 0);
  }
  for (const mutate of [
    p => { p.completeness.agents = false; },
    p => { p.capacity.configuredLimits.worker = -1; },
    p => { p.capacity.sessions = null; },
    p => { p.agents[0].verified = false; },
    p => { p.capacity.sessions.push({ sessionId: "unmirrored",
      projectName: "Alpha", kind: "worker", status: "Unknown" }); }
  ]) {
    const s = synchronize(makeSnapshot());
    mutate(s.projects[0]);
    const result = observed(s);
    assert.ok(result.plan.holds.some(x => x.projectName === "Alpha"));
    assert.ok(result.plan.workerCandidates.every(x => x.projectName !== "Alpha"));
    assert.ok(result.plan.reviewCandidates.every(x => x.projectName !== "Alpha"));
    assert.ok(result.plan.workerCandidates.some(x => x.projectName === "Beta"));
  }
});
test("missing source, stale generation, zero limits, uncertain reservation, and duplicate candidate", () => {
  const s = synchronize(makeSnapshot());
  s.projects[0].capacity.configuredLimits = { worker: 0, reviewer: 0 };
  synchronize(s);
  let result = compare(s);
  assert.ok(result.plan.holds.some(x => x.reason === "ProjectCapacityFull"));
  s.projects[0].capacity.configuredLimits = { worker: 3, reviewer: 2 };
  s.projects[0].sourceLeases[0].status = "Unverified";
  s.projects[0].agents[1].currentAssignment.assignmentGeneration = 999;
  result = compare(s);
  assert.ok(result.plan.holds.some(x => x.reason === "SourceLeaseConflict"));
  assert.ok(result.plan.holds.some(x => x.reason === "OwnerAssignmentMismatch"));
  const candidate = result.plan.workerCandidates.find(x => x.projectName === "Beta");
  s.projects[1].capacity.reservations.push({
    reservationId: "uncertain-1", projectName: "Beta",
    kind: "worker", status: "Uncertain", idempotencyKey: candidate.idempotencyKey,
    agentId: candidate.ownerAgentId
  });
  synchronize(s);
  result = compare(s);
  assert.ok(result.plan.holds.some(x =>
    x.reason === "UncertainSubmission" && x.projectName === "Beta"));
});
test("multi-client identical concurrent projections are proposals, not reservations", async () => {
  const s = synchronize(makeSnapshot());
  const projections = await Promise.all(Array.from({length: 20}, () =>
    Promise.resolve().then(() => planLedger(clone(s), NOW))));
  for (const plan of projections) assert.deepEqual(plan, projections[0]);
  assert.deepEqual(s.accountCapacity.reservations, []);
  assert.deepEqual(s.accountCapacity.sessions, []);
  // Simulated serialized reservation consumes one slot and suppresses replay.
  const c = projections[0].workerCandidates[0];
  const p = s.projects.find(x => x.projectName === c.projectName);
  p.capacity.reservations.push({ reservationId: "serial-1", projectName: c.projectName,
    kind: "worker", status: "Uncertain", idempotencyKey: c.idempotencyKey,
    agentId: c.ownerAgentId });
  synchronize(s);
  const after = compare(s);
  assert.ok(!after.workers.includes(c.idempotencyKey));
  assert.ok(after.plan.holds.some(x => x.reason === "UncertainSubmission"));
});
test("oracle negative control and typed error; minimal failing trace survives shrinking", () => {
  const trace = [{op:2,project:0,index:0,value:1,step:1},
    {op:3,project:1,index:1,value:4,step:2},
    {op:6,project:0,index:0,value:3,step:3},
    {op:0,project:1,index:1,value:3,step:4}];
  let failure = null;
  try { runTrace(trace, true); } catch (e) { failure = e; }
  assert.ok(failure instanceof assert.AssertionError, "deliberate forged candidate must fail");
  assert.equal(category(failure), "WorkerMismatch");
  const reduced = shrink(trace, "WorkerMismatch", true);
  assert.equal(reduced.length, 1, "delta debugging isolates the triggering transition");
  assert.equal(reduced[0].op, 6);
  assert.doesNotThrow(() => runTrace(reduced));
  assert.throws(() => planLedger(makeSnapshot(), NOW, { maxAgeMs: 0 }), TypeError);
});
test("Phase2 default-off diagnostic does not promote capacity or call browser effects", () => {
  const incomplete = synchronize(makeSnapshot());
  incomplete.completeness.projects = false;
  incomplete.accountCapacity.complete = false;
  incomplete.accountCapacity.configuredLimits = null;
  incomplete.accountCapacity.sessions = null;
  incomplete.accountCapacity.reservations = null;
  assert.deepEqual(observed(incomplete).workers, []);
  const input = { diagnostics: [{ scope: "membership", reason: "IncompleteRoster" }] };
  const output = phase2DefaultOffDiagnostic(input);
  assert.equal(output.enabled, false);
  assert.equal(output.sideEffects, 0);
  assert.deepEqual(output.workerCandidates, []);
  assert.deepEqual(output.reviewCandidates, []);
  assert.ok(output.holds.some(x => x.reason === "AuthenticatedWorkerReviewerCapacityUnavailable"));
  assert.ok(output.holds.some(x => x.reason === "IncompleteRoster"));
});
