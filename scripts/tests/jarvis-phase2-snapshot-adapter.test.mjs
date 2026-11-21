import test from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdtempSync, mkdirSync, rmSync, writeFileSync, unlinkSync } from "node:fs";
import { join, dirname } from "node:path";
import { tmpdir } from "node:os";
import {
  captureCanonicalSnapshot, phase2DefaultOffDiagnostic, CanonicalSnapshotError,
  CANONICAL_REFS
} from "../jarvis-phase2-snapshot-adapter.mjs";

const NOW = "2026-11-21T07:40:00.000+01:00";
const ID = "a".repeat(32);
const REVIEW = "b".repeat(32);
const CHALLENGE_A = "c".repeat(32);
const CHALLENGE_B = "d".repeat(32);
const PROJECT_A = "11111111-1111-4111-8111-111111111111";
const PROJECT_B = "22222222-2222-4222-8222-222222222222";
const ACCOUNT_ID = "33333333-3333-4333-8333-333333333333";
const LEDGER_A = "44444444-4444-4444-8444-444444444444";
const LEDGER_B = "55555555-5555-4555-8555-555555555555";
const ACCOUNT_LEDGER = "66666666-6666-4666-8666-666666666666";
const BASE = "0".repeat(40);

function git(repo, ...args) {
  return execFileSync("git", ["-C", repo, ...args], {
    encoding: "utf8", timeout: 30000, maxBuffer: 8 * 1024 * 1024,
    env: { ...process.env, GIT_TERMINAL_PROMPT: "0", GIT_NO_REPLACE_OBJECTS: "1" }
  }).trim();
}
function put(repo, path, obj) {
  const p = join(repo, path);
  mkdirSync(dirname(p), { recursive: true });
  writeFileSync(p, JSON.stringify(obj, null, 2) + "\n", "utf8");
}
function makeRemote(root, name, ref, values) {
  const remote = join(root, name + ".git");
  const writer = join(root, name + "-writer");
  mkdirSync(writer, { recursive: true });
  git(root, "init", "--bare", "-q", remote);
  git(writer, "init", "-q");
  git(writer, "config", "user.name", "Jarvis Test");
  git(writer, "config", "user.email", "fixture@example.invalid");
  git(writer, "remote", "add", "origin", remote);
  for (const [path, obj] of Object.entries(values)) put(writer, path, obj);
  git(writer, "add", ".");
  git(writer, "commit", "-q", "-m", "seed canonical fixture");
  git(writer, "push", "-q", "origin", "HEAD:" + ref);
  return { remote, writer, head: git(writer, "rev-parse", "HEAD"), ref };
}
function update(remote, changes) {
  for (const [path, value] of Object.entries(changes)) {
    if (value === null) unlinkSync(join(remote.writer, path));
    else put(remote.writer, path, value);
  }
  git(remote.writer, "add", "-A");
  git(remote.writer, "commit", "-q", "-m", "fixture transition");
  git(remote.writer, "push", "-q", "origin", "HEAD:" + remote.ref);
  remote.head = git(remote.writer, "rev-parse", "HEAD");
}
function fixtureTask(task, state = "Ready", owner = "Worker 1", gen = 4) {
  const active = state !== "NeedsWork";
  const attemptPhase = state === "Ready" ? "Queued" : state === "Working" ? "Started" : "Finished";
  const a = {
    schemaVersion: 3, taskId: task, attemptId: ID, version: 2,
    ownerAgentId: owner, phase: attemptPhase, assignmentGeneration: gen,
    priority: 10, issuedSequence: 90, specBlobOid: "e".repeat(40),
    baseCommit: BASE, targetRef: "refs/heads/main",
    sourceRepositoryId: "source-" + task, sourceRemote: "https://example.invalid/repo",
    sourcePushRemote: "https://example.invalid/repo",
    sourceRef: "refs/heads/feature/" + task
  };
  const values = {};
  values["tasks/" + task + "/task.json"] = {
    schemaVersion: 2, taskId: task, title: task, version: 2, lifecycle: "InProgress",
    specVersion: 1, attemptId: ID
  };
  values["tasks/" + task + "/attempts/" + ID + ".json"] = a;
  if (active) {
    values["tasks/" + task + "/attempts/" + ID + "/assignments/" + gen + ".json"] = {
      schemaVersion: 2, taskId: task, attemptId: ID, generation: gen,
      ownerAgentId: owner, issuerId: "AuthenticatedUser", issuedSequence: 90,
      instructions: "test", reason: "Assignment", specBlobOid: "e".repeat(40),
      sourceBaseCommit: BASE, targetRef: "refs/heads/main",
      sourceRepositoryId: "source-" + task, sourceRef: "refs/heads/feature/" + task,
      sourceRemote: "https://example.invalid/repo",
      sourcePushRemote: "https://example.invalid/repo", previousGeneration: gen - 1
    };
  }
  values["agents/" + Buffer.from(owner).toString("base64url") + ".json"] = {
    schemaVersion: 3, agentId: owner, version: 7,
    currentAssignment: active ? { taskId: task, attemptId: ID } : null,
    execution: state === "Working" ? "Working" : "Stopped",
    unassignedDisposition: active ? null : "NeedsWork",
    stateReason: "test", stateSequence: 90, stateEnteredAtUtc: NOW,
    workingTurnStartedAtUtc: state === "Working" ? NOW : null,
    activeSessionId: state === "Working" ? "session-" + task : null,
    associatedSessionIds: []
  };
  return values;
}
function accountRoot() {
  return {
    schemaVersion: 1, accountId: ACCOUNT_ID, ledgerId: ACCOUNT_LEDGER, epoch: 1,
    migrationPhase: "LegacyActive", stateVersion: 1, configVersion: 1,
    maxConcurrentAgents: 8, nextSequence: 20,
    tokens: [{
      launchId: "held-work-token", projectId: PROJECT_B, projectLedgerId: LEDGER_B,
      projectLedgerEpoch: 1, projectAuthoritySha256: "f".repeat(64),
      roleId: "Worker", roleConfigVersion: 1, admissionOperationId: "admission-1",
      status: "Held", releaseProof: null
    }]
  };
}
function fixture() {
  const root = mkdtempSync(join(tmpdir(), "jarvis-phase2-fixture-"));
  const a = makeRemote(root, "Jarvis", CANONICAL_REFS.project, {
    "ledger.json": {
      schemaVersion: 2, projectId: PROJECT_A, ledgerId: LEDGER_A, epoch: 1,
      nextSequence: 99, createdAt: NOW, status: "active",
      settings: { maxWorkingPerAgent: 1, allowDirectSourcePush: true }
    },
    ...fixtureTask("alpha", "Ready", "Worker 1")
  });
  const b = makeRemote(root, "Other", CANONICAL_REFS.project, {
    "ledger.json": {
      schemaVersion: 2, projectId: PROJECT_B, ledgerId: LEDGER_B, epoch: 1,
      nextSequence: 99, createdAt: NOW, status: "active",
      settings: { maxWorkingPerAgent: 1, allowDirectSourcePush: true }
    },
    ...fixtureTask("beta", "Working", "Worker 2")
  });
  const account = makeRemote(root, "Account", CANONICAL_REFS.account,
    { "state.json": accountRoot() });
  const bindings = {
    account: { remoteUrl: account.remote, accountId: ACCOUNT_ID, ledgerId: ACCOUNT_LEDGER, epoch: 1 },
    projects: [
      { projectName: "Jarvis", remoteUrl: a.remote, projectId: PROJECT_A, ledgerId: LEDGER_A, epoch: 1 },
      { projectName: "Other", remoteUrl: b.remote, projectId: PROJECT_B, ledgerId: LEDGER_B, epoch: 1 }
    ],
    observedAtUtc: NOW
  };
  bindings.account.previousHead = account.head;
  bindings.projects[0].previousHead = a.head;
  bindings.projects[1].previousHead = b.head;
  return { root, a, b, account, bindings, close: () => rmSync(root, { recursive: true, force: true }) };
}
function withFixture(t) {
  const f = fixture();
  t.after(f.close);
  return f;
}
function capture(f) { return captureCanonicalSnapshot(f.bindings); }

test("real three bare Git authorities: read exact heads and preserve Ready/Working identity", t => {
  const f = withFixture(t);
  const { snapshot, authority, diagnostics } = capture(f);
  assert.equal(snapshot.schemaVersion, 2);
  assert.equal(snapshot.source.commit, f.account.head);
  assert.equal(snapshot.source.ref, CANONICAL_REFS.account);
  assert.deepEqual(snapshot.completeness, { account: true, projects: false, accountCapacity: false });
  assert.ok(diagnostics.some(d => d.reason === "CanonicalAccountProjectMembershipUnavailable"));
  assert.equal(snapshot.accountCapacity.complete, false);
  assert.equal(snapshot.accountCapacity.sessions, null);
  assert.equal(authority.account.maxConcurrentWorkAgents, 8);
  assert.equal(authority.account.head, f.account.head);
  const [a, b] = snapshot.projects;
  assert.equal(a.source.commit, f.a.head);
  assert.equal(b.source.commit, f.b.head);
  assert.equal(a.tasks[0].attempts[0].phase, "Ready");
  assert.equal(b.tasks[0].attempts[0].phase, "Started");
  assert.deepEqual(a.agents[0].currentAssignment, {
    taskId: "alpha", attemptId: ID, assignmentGeneration: 4
  });
  assert.equal(a.agents[0].state, "Ready");
  assert.equal(b.agents[0].state, "Working");
  assert.equal(b.agents[0].activeSessionId, "session-beta");
  assert.equal(a.sourceLeases[0].status, "Unverified");
  assert.equal(a.sourceLeases[0].baseCommit, BASE);
  assert.equal(a.completeness.tasks, true);
  assert.equal(a.completeness.agents, true);
  assert.equal(a.completeness.sourceLeases, true);
  assert.equal(a.completeness.capacity, false);
  assert.equal(diagnostics[0].heldWorkTokens, 1);
  assert.equal(diagnostics[0].reason, "AuthenticatedWorkerReviewerCapacityUnavailable");
});

test("default-off diagnostic never launches, claims, reserves or grants a free slot", t => {
  const f = withFixture(t);
  const report = phase2DefaultOffDiagnostic(capture(f));
  assert.equal(report.enabled, false);
  assert.equal(report.sideEffects, 0);
  assert.deepEqual(report.workerCandidates, []);
  assert.deepEqual(report.reviewCandidates, []);
  assert.ok(report.holds.some(h => h.reason === "Phase1NotIntegrated"));
  assert.ok(report.holds.some(h => h.reason === "AuthenticatedWorkerReviewerCapacityUnavailable"));
});

test("missing canonical assignment issue is incomplete; generation must not be guessed from attempt", t => {
  const f = withFixture(t);
  update(f.a, { ["tasks/alpha/attempts/" + ID + "/assignments/4.json"]: null });
  const view = capture(f);
  const p = view.snapshot.projects[0];
  assert.equal(p.completeness.agents, false);
  assert.equal(p.agents[0].currentAssignment, null);
  assert.ok(view.diagnostics.some(d => d.reason === "MissingCanonicalRecord" && d.scope === "agents"));
});

test("unassigned Working agent with null disposition is incomplete rather than free or verified", t => {
  const f = withFixture(t);
  const path = "agents/" + Buffer.from("Worker 1").toString("base64url") + ".json";
  const agent = fixtureTask("alpha", "Working")[path];
  update(f.a, { [path]: {
    ...agent, currentAssignment: null, unassignedDisposition: null, activeSessionId: null
  } });
  const view = capture(f);
  assert.equal(view.snapshot.projects[0].completeness.agents, false);
  assert.equal(view.snapshot.projects[0].agents[0].state, "Working");
  assert.equal(view.snapshot.projects[0].agents[0].currentAssignment, null);
  assert.equal(view.snapshot.projects[0].agents[0].verified, false);
  assert.ok(view.diagnostics.some(d => d.scope === "agents" && d.reason === "UnassignedWorkingAgent"));
});

test("NeedsWork is unassigned and never inferred as Ready from an old attempt", t => {
  const f = withFixture(t);
  update(f.a, fixtureTask("alpha", "NeedsWork", "Worker 1"));
  const p = capture(f).snapshot.projects[0];
  assert.equal(p.agents[0].state, "NeedsWork");
  assert.equal(p.agents[0].execution, "Stopped");
  assert.equal(p.agents[0].currentAssignment, null);
  assert.equal(p.tasks[0].attempts[0].phase, "Finished");
});

test("Unclaimed review is Pending but NOT a claimed reviewer or proven frozen source", t => {
  const f = withFixture(t);
  update(f.a, {
    ["tasks/alpha/reviews/" + REVIEW + "/review.json"]: {
      schemaVersion: 3, reviewId: REVIEW, taskId: "alpha", attemptId: ID,
      ownerAgentId: "Worker 1", kind: "Implementation", taskRef: "refs/heads/feature/alpha",
      reviewRef: "refs/heads/jarvis-review/alpha/" + REVIEW + "/r1",
      sourceVersion: 2, reviewedHeadOid: BASE, baseCommit: BASE,
      targetRef: "refs/heads/main", status: "Unclaimed", reviewerId: null,
      version: 1, createdSequence: 99, decisionSequence: 0, acknowledged: false
    }
  });
  const [r] = capture(f).snapshot.projects[0].reviews;
  assert.equal(r.reviewId, REVIEW);
  assert.equal(r.status, "Pending");
  assert.equal(r.claim, null);
  assert.equal(r.frozen, false);
  assert.equal(r.sourceRef, "refs/heads/feature/alpha");
});

test("manual zero-dependency Challenge remains a manual block without a reverse index", t => {
  const f = withFixture(t);
  const attempt = fixtureTask("alpha", "NeedsWork")["tasks/alpha/attempts/" + ID + ".json"];
  attempt.phase = "Blocked";
  update(f.a, {
    ["tasks/alpha/attempts/" + ID + ".json"]: attempt,
    ["tasks/alpha/attempts/" + ID + "/challenge-wait.json"]: {
      schemaVersion: 2, taskId: "alpha", attemptId: ID, ownerAgentId: "Worker 1",
      reason: "Operator intervention", challengeIds: [],
      status: "Waiting", version: 1, blockedAtUtc: NOW, requeuedAtUtc: null
    }
  });
  const p = capture(f).snapshot.projects[0];
  assert.equal(p.completeness.challenges, true);
  assert.deepEqual(p.challenges[0].dependencies, []);
  assert.equal(p.challenges[0].waitIndexGeneration, null);
  assert.equal(p.challenges[0].status, "Pending");
  assert.equal(p.challenges[0].requeueProvenance, null);
});

function requeuedChanges() {
  const values = fixtureTask("alpha", "Ready", "Worker 1", 5);
  values["tasks/alpha/attempts/" + ID + "/challenge-wait.json"] = {
    schemaVersion: 2, taskId: "alpha", attemptId: ID, ownerAgentId: "Worker 1",
    reason: "two outstanding conditions", challengeIds: [CHALLENGE_A, CHALLENGE_B],
    status: "Requeued", version: 2, blockedAtUtc: NOW, requeuedAtUtc: NOW
  };
  for (const id of [CHALLENGE_A, CHALLENGE_B]) {
    values["challenges/" + id + "/challenge.json"] = {
      schemaVersion: 2, challengeId: id, issuerAgentId: "Worker 1", taskId: "alpha",
      title: "dependency " + id, status: "Satisfied", version: 2,
      conditions: [], completionPolicy: "All", createdAtUtc: NOW, updatedAtUtc: NOW
    };
  }
  const index = {
    schemaVersion: 2, taskId: "alpha", attemptId: ID,
    ownerAgentId: "Worker 1", assignmentGeneration: 4
  };
  const base = "alpha/" + ID + "/4.json";
  values["challenge-waits/by-owner/" + Buffer.from("Worker 1").toString("base64url") + "/" + base] = index;
  for (const id of [CHALLENGE_A, CHALLENGE_B]) {
    values["challenge-waits/by-challenge/" + id + "/" + base] = index;
  }
  values["tasks/alpha/lifecycle/98.json"] = {
    schemaVersion: 2, taskId: "alpha", attemptId: ID,
    operationId: "requeue-operation", sequence: 98, action: "RequeueChallengeBlockedAttempt",
    actorId: "Worker 1", reason: "resume", previousLifecycle: "InProgress",
    newLifecycle: "InProgress", previousPhase: "Blocked", newPhase: "Queued",
    previousOwnerId: "Worker 1", newOwnerId: "Worker 1",
    previousGeneration: 4, newGeneration: 5
  };
  return values;
}

test("Challenge N to N+1 joins immutable owner/dependency index and lifecycle provenance", t => {
  const f = withFixture(t);
  update(f.a, requeuedChanges());
  const p = capture(f).snapshot.projects[0];
  assert.equal(p.completeness.challenges, true);
  assert.equal(p.tasks[0].attempts[0].assignmentGeneration, 5);
  assert.equal(p.challenges[0].status, "Requeued");
  assert.equal(p.challenges[0].waitIndexGeneration, 4);
  assert.deepEqual(p.challenges[0].dependencies.map(x => x.status), ["Satisfied", "Satisfied"]);
  assert.equal(p.challenges[0].requeueProvenance.previousGeneration, 4);
  assert.equal(p.challenges[0].requeueProvenance.newGeneration, 5);
  assert.equal(p.challenges[0].requeueProvenance.ledgerSequence, 98);
});

test("missing one dependency reverse index fails closed instead of silently treating all satisfied", t => {
  const f = withFixture(t);
  update(f.a, requeuedChanges());
  update(f.a, { ["challenge-waits/by-challenge/" + CHALLENGE_B + "/alpha/" + ID + "/4.json"]: null });
  const view = capture(f);
  assert.equal(view.snapshot.projects[0].completeness.challenges, false);
  assert.ok(view.diagnostics.some(d => d.reason === "WaitIndexMismatch"));
});

test("unexpected current-generation dependency reverse index is not canonical", t => {
  const f = withFixture(t);
  const changes = requeuedChanges();
  const roguePath = "challenge-waits/by-challenge/" + REVIEW + "/alpha/" + ID + "/4.json";
  changes[roguePath] = {
    schemaVersion: 2, taskId: "alpha", attemptId: ID,
    ownerAgentId: "Worker 1", assignmentGeneration: 4
  };
  update(f.a, changes);
  const view = capture(f);
  assert.equal(view.snapshot.projects[0].completeness.challenges, false);
  assert.ok(view.diagnostics.some(d => d.reason === "UnexpectedWaitReverseIndex" && d.path === roguePath));
});

for (const namespace of ["by-challenge", "by-owner"]) {
  test("requeued N4 to N5 rejects a new-generation " + namespace + " reverse index", t => {
    const f = withFixture(t);
    const changes = requeuedChanges();
    const locator = namespace === "by-challenge"
      ? CHALLENGE_A : Buffer.from("Worker 1").toString("base64url");
    const roguePath = "challenge-waits/" + namespace + "/" + locator + "/alpha/" + ID + "/5.json";
    changes[roguePath] = {
      schemaVersion: 2, taskId: "alpha", attemptId: ID,
      ownerAgentId: "Worker 1", assignmentGeneration: 5
    };
    update(f.a, changes);
    const view = capture(f);
    assert.equal(view.snapshot.projects[0].challenges[0].waitIndexGeneration, 4);
    assert.equal(view.snapshot.projects[0].completeness.challenges, false);
    assert.ok(view.diagnostics.some(d => d.scope === "challenges" &&
      d.reason === "UnexpectedWaitReverseIndex" && d.path === roguePath));
    assert.equal(phase2DefaultOffDiagnostic(view).sideEffects, 0);
  });

  test("orphan " + namespace + " reverse index without a canonical wait fails closed", t => {
    const f = withFixture(t);
    const locator = namespace === "by-challenge"
      ? CHALLENGE_A : Buffer.from("Worker 1").toString("base64url");
    const roguePath = "challenge-waits/" + namespace + "/" + locator + "/alpha/" + ID + "/4.json";
    update(f.a, {
      [roguePath]: {
        schemaVersion: 2, taskId: "alpha", attemptId: ID,
        ownerAgentId: "Worker 1", assignmentGeneration: 4
      }
    });
    const view = capture(f);
    assert.deepEqual(view.snapshot.projects[0].challenges, []);
    assert.equal(view.snapshot.projects[0].completeness.challenges, false);
    assert.ok(view.diagnostics.some(d => d.scope === "challenges" &&
      d.reason === "OrphanWaitReverseIndex" && d.path === roguePath));
    assert.equal(phase2DefaultOffDiagnostic(view).sideEffects, 0);
  });
}

test("valid older by-owner index remains immutable historical evidence", t => {
  const f = withFixture(t);
  const changes = requeuedChanges();
  const path = "challenge-waits/by-owner/" + Buffer.from("Worker 1").toString("base64url") +
    "/alpha/" + ID + "/3.json";
  changes[path] = {
    schemaVersion: 2, taskId: "alpha", attemptId: ID,
    ownerAgentId: "Worker 1", assignmentGeneration: 3
  };
  update(f.a, changes);
  const p = capture(f).snapshot.projects[0];
  assert.equal(p.completeness.challenges, true);
  assert.equal(p.challenges[0].waitIndexGeneration, 4);
});

for (const namespace of ["by-challenge", "by-owner"]) {
  test("historical " + namespace + " reverse index must still have canonical identity", t => {
    const f = withFixture(t);
    const changes = requeuedChanges();
    const locator = namespace === "by-challenge"
      ? CHALLENGE_A : Buffer.from("Impostor").toString("base64url");
    const path = "challenge-waits/" + namespace + "/" + locator + "/alpha/" + ID + "/3.json";
    changes[path] = {
      schemaVersion: 2, taskId: "alpha", attemptId: ID,
      ownerAgentId: "Worker 1", assignmentGeneration: namespace === "by-challenge" ? 2 : 3
    };
    update(f.a, changes);
    const view = capture(f);
    assert.equal(view.snapshot.projects[0].completeness.challenges, false);
    assert.ok(view.diagnostics.some(d => d.scope === "challenges" &&
      d.reason === "ReverseIndexIdentityMismatch" && d.path === path));
  });
}

test("malformed reverse-index locator with valid JSON cannot appear as complete", t => {
  const f = withFixture(t);
  const path = "challenge-waits/by-challenge/" + CHALLENGE_A + "/alpha/" + ID + "/not-a-generation.json";
  update(f.a, { [path]: {
    schemaVersion: 2, taskId: "alpha", attemptId: ID,
    ownerAgentId: "Worker 1", assignmentGeneration: 4
  } });
  const view = capture(f);
  assert.equal(view.snapshot.projects[0].completeness.challenges, false);
  assert.ok(view.diagnostics.some(d => d.reason === "MalformedReverseIndexPath" && d.path === path));
});

test("submission lacks canonical attempt generation: retain original identity and mark incomplete", t => {
  const f = withFixture(t);
  update(f.a, {
    ["tasks/alpha/integrations/" + REVIEW + ".json"]: {
      schemaVersion: 2, submissionId: REVIEW, taskId: "alpha", actorId: "Worker 1",
      sourceRef: "refs/heads/feature/alpha", submittedCommit: BASE,
      reviewId: REVIEW, reviewRef: "refs/heads/jarvis-review/alpha/" + REVIEW + "/r1",
      reviewVersion: 1, reviewedHeadOid: BASE, integrationRef: "",
      targetHeadAtSubmission: BASE, targetRef: "refs/heads/main",
      status: "Pending", createdSequence: 99, integratedCommit: null,
      completedAtUtc: null
    }
  });
  const view = capture(f);
  const p = view.snapshot.projects[0];
  assert.equal(p.completeness.submissions, false);
  assert.equal(p.submissions[0].submissionId, REVIEW);
  assert.equal(p.submissions[0].assignmentGeneration, null);
  assert.ok(view.diagnostics.some(d => d.reason === "SubmissionGenerationNotCanonical"));
});

test("wrong approved account/project identity and missing ref fail before any usable snapshot", t => {
  const f = withFixture(t);
  const bad = structuredClone(f.bindings);
  bad.projects[0].ledgerId = "00000000-0000-4000-8000-000000000000";
  assert.throws(() => captureCanonicalSnapshot(bad), CanonicalSnapshotError);
  const missing = structuredClone(f.bindings);
  missing.account.remoteUrl = join(f.root, "nonexistent.git");
  assert.throws(() => captureCanonicalSnapshot(missing), CanonicalSnapshotError);
});

test("unapproved ref, duplicate project roster and noncanonical time fail closed", t => {
  const f = withFixture(t);
  const otherRef = structuredClone(f.bindings);
  otherRef.projects[0].ref = "refs/heads/main";
  assert.throws(() => captureCanonicalSnapshot(otherRef), CanonicalSnapshotError);
  const dup = structuredClone(f.bindings);
  dup.projects[1].projectName = "Jarvis";
  assert.throws(() => captureCanonicalSnapshot(dup), CanonicalSnapshotError);
  const dupIdentity = structuredClone(f.bindings);
  dupIdentity.projects[1] = { ...dupIdentity.projects[0], projectName: "Other" };
  assert.throws(() => captureCanonicalSnapshot(dupIdentity), CanonicalSnapshotError);
  const clock = structuredClone(f.bindings);
  clock.observedAtUtc = "yesterday";
  assert.throws(() => captureCanonicalSnapshot(clock), CanonicalSnapshotError);
});


test("held account token naming an unconfigured project is not ignored", t => {
  const f = withFixture(t);
  const state = accountRoot();
  state.tokens[0].projectId = "77777777-7777-4777-8777-777777777777";
  update(f.account, { "state.json": state });
  const view = capture(f);
  assert.equal(view.snapshot.completeness.projects, false);
  assert.ok(view.diagnostics.some(d => d.reason === "HeldTokenProjectNotIncluded"));
  assert.equal(phase2DefaultOffDiagnostic(view).sideEffects, 0);
});

for (const [field, mismatch] of [
  ["projectLedgerId", "77777777-7777-4777-8777-777777777777"],
  ["projectLedgerEpoch", 2]
]) {
  test("Held WQ07 token " + field + " must match the independently verified project ledger", t => {
    const f = withFixture(t);
    const state = accountRoot();
    state.tokens[0][field] = mismatch;
    update(f.account, { "state.json": state });
    const view = capture(f);
    assert.ok(view.diagnostics.some(d => d.scope === "membership" &&
      d.reason === "HeldTokenProjectLedgerIdentityMismatch" && d.projectId === PROJECT_B &&
      d.actualLedgerId === state.tokens[0].projectLedgerId &&
      d.actualEpoch === state.tokens[0].projectLedgerEpoch &&
      d.expectedLedgerId === LEDGER_B && d.expectedEpoch === 1));
    assert.equal(view.snapshot.accountCapacity.complete, false);
    assert.equal(view.snapshot.completeness.projects, false);
    assert.equal(phase2DefaultOffDiagnostic(view).sideEffects, 0);
  });
}

test("claimed review with no independent reviewer is an incomplete canonical claim", t => {
  const f = withFixture(t);
  update(f.a, {
    ["tasks/alpha/reviews/" + REVIEW + "/review.json"]: {
      schemaVersion: 3, reviewId: REVIEW, taskId: "alpha", attemptId: ID,
      ownerAgentId: "Worker 1", kind: "Implementation", taskRef: "refs/heads/feature/alpha",
      reviewRef: "refs/heads/jarvis-review/alpha/" + REVIEW + "/r1",
      sourceVersion: 2, reviewedHeadOid: BASE, baseCommit: BASE,
      targetRef: "refs/heads/main", status: "Claimed", reviewerId: null,
      version: 2, createdSequence: 99, decisionSequence: 0, acknowledged: false
    }
  });
  const view = capture(f);
  assert.equal(view.snapshot.projects[0].completeness.reviews, false);
  assert.ok(view.diagnostics.some(d => d.reason === "ClaimedReviewMissingReviewer"));
  assert.equal(view.snapshot.projects[0].reviews[0].frozen, false);
});

test("requeue transition cannot be synthesized from wrong audit phases or identity", t => {
  const f = withFixture(t);
  const changes = requeuedChanges();
  changes["tasks/alpha/lifecycle/98.json"].previousPhase = "Started";
  update(f.a, changes);
  const view = capture(f);
  assert.equal(view.snapshot.projects[0].completeness.challenges, false);
  assert.ok(view.diagnostics.some(d => d.reason === "RequeueAuditMismatch"));
});

test("manual Challenge must not acquire a current-generation reverse index", t => {
  const f = withFixture(t);
  const a = fixtureTask("alpha", "NeedsWork")["tasks/alpha/attempts/" + ID + ".json"];
  a.phase = "Blocked";
  update(f.a, {
    ["tasks/alpha/attempts/" + ID + ".json"]: a,
    ["tasks/alpha/attempts/" + ID + "/challenge-wait.json"]: {
      schemaVersion: 2, taskId: "alpha", attemptId: ID, ownerAgentId: "Worker 1",
      reason: "Manual block", challengeIds: [], status: "Waiting",
      version: 1, blockedAtUtc: NOW, requeuedAtUtc: null
    },
    ["challenge-waits/by-owner/" + Buffer.from("Worker 1").toString("base64url") +
      "/alpha/" + ID + "/4.json"]: {
      schemaVersion: 2, taskId: "alpha", attemptId: ID,
      ownerAgentId: "Worker 1", assignmentGeneration: 4
    }
  });
  const view = capture(f);
  assert.equal(view.snapshot.projects[0].completeness.challenges, false);
  assert.ok(view.diagnostics.some(d => d.reason === "ManualWaitHasReverseIndex"));
});

test("manual Challenge with only a by-challenge current-generation reverse locator is incomplete", t => {
  const f = withFixture(t);
  const attempt = fixtureTask("alpha", "NeedsWork")["tasks/alpha/attempts/" + ID + ".json"];
  attempt.phase = "Blocked";
  const path = "challenge-waits/by-challenge/" + CHALLENGE_A + "/alpha/" + ID + "/4.json";
  update(f.a, {
    ["tasks/alpha/attempts/" + ID + ".json"]: attempt,
    ["tasks/alpha/attempts/" + ID + "/challenge-wait.json"]: {
      schemaVersion: 2, taskId: "alpha", attemptId: ID, ownerAgentId: "Worker 1",
      reason: "Manual block", challengeIds: [], status: "Waiting",
      version: 1, blockedAtUtc: NOW, requeuedAtUtc: null
    },
    [path]: { schemaVersion: 2, taskId: "alpha", attemptId: ID,
      ownerAgentId: "Worker 1", assignmentGeneration: 4 }
  });
  const view = capture(f);
  assert.equal(view.snapshot.projects[0].completeness.challenges, false);
  assert.ok(view.diagnostics.some(d => d.scope === "challenges" &&
    d.reason === "ManualWaitHasReverseIndex" && d.path === path));
  assert.equal(phase2DefaultOffDiagnostic(view).sideEffects, 0);
});

test("manual Challenge preserves genuinely historical reverse-index generations", t => {
  const f = withFixture(t);
  const attempt = fixtureTask("alpha", "NeedsWork")["tasks/alpha/attempts/" + ID + ".json"];
  attempt.phase = "Blocked";
  update(f.a, {
    ["tasks/alpha/attempts/" + ID + ".json"]: attempt,
    ["tasks/alpha/attempts/" + ID + "/challenge-wait.json"]: {
      schemaVersion: 2, taskId: "alpha", attemptId: ID, ownerAgentId: "Worker 1",
      reason: "Manual block", challengeIds: [], status: "Waiting",
      version: 1, blockedAtUtc: NOW, requeuedAtUtc: null
    },
    ["challenge-waits/by-challenge/" + CHALLENGE_A + "/alpha/" + ID + "/3.json"]:
      { schemaVersion: 2, taskId: "alpha", attemptId: ID,
        ownerAgentId: "Worker 1", assignmentGeneration: 3 }
  });
  assert.equal(capture(f).snapshot.projects[0].completeness.challenges, true);
});

test("remote non-fast-forward rewind conflicts with pinned ancestor trust", t => {
  const f = withFixture(t);
  const bad = structuredClone(f.bindings);
  // A fabricated/absent previous head never becomes an asserted ancestor.
  bad.projects[0].previousHead = "f".repeat(40);
  assert.throws(() => captureCanonicalSnapshot(bad), CanonicalSnapshotError);
});

test("missing project record never means no tasks, and no capacity is inferred", t => {
  const f = withFixture(t);
  update(f.a, { ["tasks/alpha/task.json"]: null });
  const view = capture(f);
  assert.equal(view.snapshot.projects[0].completeness.tasks, false);
  assert.equal(view.snapshot.projects[0].completeness.capacity, false);
  assert.deepEqual(view.snapshot.projects[0].tasks, []);
  assert.equal(view.snapshot.projects[0].capacity.sessions, null);
  assert.equal(view.snapshot.projects[0].capacity.reservations, null);
});


test("WQ07 malformed or duplicate tokens cannot masquerade as spare browser capacity", t => {
  const f = withFixture(t);
  const bad = accountRoot();
  bad.tokens[0].releaseProof = { projectId: PROJECT_B };
  update(f.account, { "state.json": bad });
  assert.throws(() => capture(f), CanonicalSnapshotError);

  const duplicate = accountRoot();
  duplicate.tokens.push({ ...duplicate.tokens[0] });
  update(f.account, { "state.json": duplicate });
  assert.throws(() => capture(f), CanonicalSnapshotError);

  const empty = accountRoot();
  empty.tokens = [];
  update(f.account, { "state.json": empty });
  const view = capture(f);
  assert.equal(view.snapshot.accountCapacity.complete, false);
  assert.equal(view.snapshot.accountCapacity.sessions, null);
  assert.equal(view.snapshot.accountCapacity.reservations, null);
  assert.equal(view.diagnostics[0].heldWorkTokens, 0);
});

test("contradictory source-ref versus target-ref is not a Valid source lease", t => {
  const f = withFixture(t);
  const attempt = fixtureTask("alpha")["tasks/alpha/attempts/" + ID + ".json"];
  attempt.sourceRef = "refs/heads/main";
  update(f.a, { ["tasks/alpha/attempts/" + ID + ".json"]: attempt });
  const view = capture(f);
  assert.equal(view.snapshot.projects[0].completeness.sourceLeases, false);
  assert.deepEqual(view.snapshot.projects[0].sourceLeases, []);
  assert.ok(view.diagnostics.some(d => d.reason === "IncompleteCanonicalSourceBinding"));
});

test("missing previously verified Git head fails before accepting remote advertised head", t => {
  const f = withFixture(t);
  const withoutTrust = structuredClone(f.bindings);
  delete withoutTrust.projects[0].previousHead;
  assert.throws(() => captureCanonicalSnapshot(withoutTrust), CanonicalSnapshotError);
  delete withoutTrust.account.previousHead;
  assert.throws(() => captureCanonicalSnapshot(withoutTrust), CanonicalSnapshotError);
});

test("a clean fast-forward of one project produces a new exact source SHA, not a cached snapshot", t => {
  const f = withFixture(t);
  const first = capture(f);
  const changed = fixtureTask("alpha", "Working");
  update(f.a, changed);
  const second = capture(f);
  assert.notEqual(second.snapshot.projects[0].source.commit,
    first.snapshot.projects[0].source.commit);
  assert.equal(second.snapshot.projects[0].source.commit, f.a.head);
  assert.equal(second.snapshot.projects[0].agents[0].state, "Working");
  assert.equal(second.snapshot.projects[1].source.commit, f.b.head);
  assert.equal(second.snapshot.accountCapacity.complete, false);
});


test("unexpected canonical task lifecycle is an incomplete project, never a runnable status", t => {
  const f = withFixture(t);
  const task = fixtureTask("alpha")["tasks/alpha/task.json"];
  task.lifecycle = "MadeUpRunnable";
  update(f.a, { "tasks/alpha/task.json": task });
  const view = capture(f);
  assert.equal(view.snapshot.projects[0].completeness.tasks, false);
  assert.equal(view.snapshot.projects[0].tasks.length, 0);
  assert.ok(view.diagnostics.some(d => d.reason === "InvalidTaskIdentity"));
});

test("stopped unassigned agent without explicit disposition is not available", t => {
  const f = withFixture(t);
  const agentPath = "agents/" + Buffer.from("Worker 1").toString("base64url") + ".json";
  const agent = fixtureTask("alpha", "NeedsWork") [agentPath];
  agent.unassignedDisposition = null;
  update(f.a, {
    [agentPath]: agent,
    ["tasks/alpha/attempts/" + ID + ".json"]:
      fixtureTask("alpha", "NeedsWork")["tasks/alpha/attempts/" + ID + ".json"]
  });
  const view = capture(f);
  assert.equal(view.snapshot.projects[0].completeness.agents, false);
  assert.ok(view.diagnostics.some(d => d.reason === "ContradictoryAgentDisposition"));
});

test("unknown review lifecycle is not normalized to Pending", t => {
  const f = withFixture(t);
  update(f.a, {
    ["tasks/alpha/reviews/" + REVIEW + "/review.json"]: {
      schemaVersion: 3, reviewId: REVIEW, taskId: "alpha", attemptId: ID,
      ownerAgentId: "Worker 1", kind: "Implementation", taskRef: "refs/heads/feature/alpha",
      reviewRef: "refs/heads/jarvis-review/alpha/" + REVIEW + "/r1",
      sourceVersion: 2, reviewedHeadOid: BASE, baseCommit: BASE,
      targetRef: "refs/heads/main", status: "MaybeClaimed",
      reviewerId: null, version: 1, createdSequence: 99,
      decisionSequence: 0, acknowledged: false
    }
  });
  const view = capture(f);
  assert.equal(view.snapshot.projects[0].completeness.reviews, false);
  assert.deepEqual(view.snapshot.projects[0].reviews, []);
  assert.ok(view.diagnostics.some(d => d.reason === "ReviewIdentityMismatch"));
});
