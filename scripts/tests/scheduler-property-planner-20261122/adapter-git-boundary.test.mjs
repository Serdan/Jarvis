import test from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdtempSync, mkdirSync, rmSync, writeFileSync, unlinkSync } from "node:fs";
import { join, dirname } from "node:path";
import { tmpdir } from "node:os";
import { captureCanonicalSnapshot, phase2DefaultOffDiagnostic,
  CanonicalSnapshotError, CANONICAL_REFS } from "../../jarvis-phase2-snapshot-adapter.mjs";
import { planLedger } from "../../jarvis-ledger-planner.mjs";

const AT = "2026-11-22T00:00:00.000Z";
const ATTEMPT = "a".repeat(32);
const PROJECTS = [
  { alias: "Alpha", id: "11111111-1111-4111-8111-111111111111",
    ledger: "22222222-2222-4222-8222-222222222222" },
  { alias: "Beta", id: "33333333-3333-4333-8333-333333333333",
    ledger: "44444444-4444-4444-8444-444444444444" }
];
const ACCOUNT = "55555555-5555-4555-8555-555555555555";
const ACCOUNT_LEDGER = "66666666-6666-4666-8666-666666666666";
const git = (dir, ...args) => execFileSync("git", ["-C", dir, ...args], {
  encoding: "utf8", timeout: 30000, maxBuffer: 3_000_000,
  env: { ...process.env, GIT_TERMINAL_PROMPT: "0", GIT_NO_REPLACE_OBJECTS: "1" }
}).trim();
function write(root, rel, data) {
  const path = join(root, rel);
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, typeof data === "string" ? data : JSON.stringify(data) + "\n", "utf8");
}
function bare(root, label, ref, records) {
  const remote = join(root, label + ".git"), writer = join(root, label + "-work");
  mkdirSync(writer, {recursive:true});
  git(root, "init", "--bare", "-q", remote);
  git(writer, "init", "-q");
  git(writer, "config", "user.name", "Worker 14 fixture");
  git(writer, "config", "user.email", "worker14@example.invalid");
  git(writer, "remote", "add", "origin", remote);
  for (const [path, data] of Object.entries(records)) write(writer, path, data);
  git(writer, "add", ".");
  git(writer, "commit", "-q", "-m", "source-pinned fixture genesis");
  git(writer, "push", "-q", "origin", "HEAD:" + ref);
  return { remote, writer, ref, head: git(writer, "rev-parse", "HEAD") };
}
function evolve(f, changes) {
  for (const [path, data] of Object.entries(changes)) {
    if (data === null) unlinkSync(join(f.writer,path));
    else write(f.writer,path,data);
  }
  git(f.writer, "add", "-A");
  git(f.writer, "commit", "-q", "-m", "disposable adversarial transition");
  git(f.writer, "push", "-q", "origin", "HEAD:" + f.ref);
  f.head = git(f.writer,"rev-parse","HEAD");
}
function ledgerRecords(p) {
  const task = p.alias.toLowerCase() + "-task";
  const owner = p.alias + " Worker";
  const prefix = "tasks/" + task;
  return {
    "ledger.json": { schemaVersion: 2, projectId: p.id,
      ledgerId: p.ledger, epoch: 1, nextSequence: 90, createdAt: AT,
      status: "active", settings: { maxWorkingPerAgent: 1, allowDirectSourcePush: false } },
    [prefix + "/task.json"]: { schemaVersion: 2, taskId: task, title: task,
      version: 1, lifecycle: "InProgress", specVersion: 1, attemptId: ATTEMPT },
    [prefix + "/attempts/" + ATTEMPT + ".json"]: {
      schemaVersion: 3, taskId: task, attemptId: ATTEMPT, version: 1,
      ownerAgentId: owner, phase: "Queued", assignmentGeneration: 1,
      priority: 3, issuedSequence: 80, specBlobOid: "f".repeat(40),
      baseCommit: "a".repeat(40), targetRef: "refs/heads/main",
      sourceRepositoryId: "repo-" + p.alias,
      sourceRemote: "https://example.invalid/source",
      sourcePushRemote: "https://example.invalid/source",
      sourceRef: "refs/heads/feature/" + task
    },
    [prefix + "/attempts/" + ATTEMPT + "/assignments/1.json"]: {
      schemaVersion: 2, taskId: task, attemptId: ATTEMPT,
      generation: 1, ownerAgentId: owner, issuerId: "Fixture",
      issuedSequence: 80, instructions: "fixture", reason: "test",
      specBlobOid: "f".repeat(40), sourceBaseCommit: "a".repeat(40),
      targetRef: "refs/heads/main", sourceRepositoryId: "repo-" + p.alias,
      sourceRef: "refs/heads/feature/" + task,
      sourceRemote: "https://example.invalid/source",
      sourcePushRemote: "https://example.invalid/source", previousGeneration: 0
    },
    ["agents/" + Buffer.from(owner).toString("base64url") + ".json"]: {
      schemaVersion: 3, agentId: owner, version: 1,
      currentAssignment: { taskId: task, attemptId: ATTEMPT },
      execution: "Stopped", unassignedDisposition: null,
      stateReason: "test", stateSequence: 80, stateEnteredAtUtc: AT,
      workingTurnStartedAtUtc: null, activeSessionId: null, associatedSessionIds: []
    }
  };
}
function fixture(t) {
  const root = mkdtempSync(join(tmpdir(), "scheduler-property-planner-w14-"));
  t.after(() => rmSync(root, {recursive: true, force: true}));
  const projects = PROJECTS.map(p => bare(root, p.alias, CANONICAL_REFS.project, ledgerRecords(p)));
  const account = bare(root, "Account", CANONICAL_REFS.account, {
    "state.json": { schemaVersion: 1, accountId: ACCOUNT, ledgerId: ACCOUNT_LEDGER,
      epoch: 1, migrationPhase: "LegacyActive", stateVersion: 1, configVersion: 1,
      maxConcurrentAgents: 9, nextSequence: 21, tokens: [] }
  });
  const bindings = {
    observedAtUtc: AT,
    account: { remoteUrl: account.remote, previousHead: account.head,
      accountId: ACCOUNT, ledgerId: ACCOUNT_LEDGER, epoch: 1 },
    projects: projects.map((p, i) => ({ projectName: PROJECTS[i].alias,
      remoteUrl: p.remote, previousHead: p.head, projectId: PROJECTS[i].id,
      ledgerId: PROJECTS[i].ledger, epoch: 1 }))
  };
  return { root, projects, account, bindings };
}
test("three real local bare Git authorities: Phase2 preserves heads but cannot prove native capacity", t => {
  const f = fixture(t);
  const before = [f.account, ...f.projects].map(x => git(x.remote, "rev-parse", x.ref));
  const result = captureCanonicalSnapshot(f.bindings);
  const after = [f.account, ...f.projects].map(x => git(x.remote, "rev-parse", x.ref));
  assert.deepEqual(after, before, "read-only adapter cannot advance canonical remotes");
  assert.equal(result.snapshot.source.commit, f.account.head);
  assert.deepEqual(result.snapshot.projects.map(x => x.source.commit), f.projects.map(x => x.head));
  assert.deepEqual(result.snapshot.projects.map(x => x.tasks[0].attempts[0].phase), ["Ready","Ready"]);
  assert.ok(result.snapshot.projects.every(x => x.sourceLeases[0].status === "Unverified"));
  assert.deepEqual(result.snapshot.completeness, { account: true, projects: false, accountCapacity: false });
  assert.equal(result.snapshot.accountCapacity.complete, false);
  assert.equal(result.snapshot.accountCapacity.sessions, null);
  assert.equal(result.snapshot.accountCapacity.reservations, null);
  assert.deepEqual(planLedger(result.snapshot, AT).workerCandidates, []);
  const diagnostic = phase2DefaultOffDiagnostic(result);
  assert.equal(diagnostic.enabled, false);
  assert.equal(diagnostic.sideEffects, 0);
  assert.deepEqual(diagnostic.workerCandidates, []);
  assert.deepEqual(diagnostic.reviewCandidates, []);
  assert.ok(diagnostic.holds.some(x => x.reason === "AuthenticatedWorkerReviewerCapacityUnavailable"));
  // WQ07 admission quota 9 is not an authenticated Worker/Reviewer quota or live occupancy proof.
  assert.equal(result.authority.account.maxConcurrentWorkAgents, 9);
});
test("real remote missing assignment: no generation guessed and no dispatch", t => {
  const f = fixture(t);
  evolve(f.projects[0], { ["tasks/alpha-task/attempts/" + ATTEMPT + "/assignments/1.json"]: null });
  const a = captureCanonicalSnapshot(f.bindings);
  const alpha = a.snapshot.projects[0];
  assert.equal(alpha.completeness.agents, false);
  assert.equal(alpha.agents[0].currentAssignment, null);
  assert.ok(a.diagnostics.some(x => x.scope === "agents" && x.reason === "MissingCanonicalRecord"));
  assert.deepEqual(planLedger(a.snapshot, AT).workerCandidates, []);
  assert.equal(phase2DefaultOffDiagnostic(a).sideEffects, 0);
});
test("corrupted/truncated real canonical Git JSON has typed fail-closed error", t => {
  const f = fixture(t);
  evolve(f.projects[1], { "ledger.json": "{\"schemaVersion\":2,\"projectId\":\"truncated" });
  assert.throws(() => captureCanonicalSnapshot(f.bindings), e =>
    e instanceof CanonicalSnapshotError && /MalformedCanonicalJson/.test(e.message));
  assert.equal(git(f.projects[1].remote, "rev-parse", f.projects[1].ref), f.projects[1].head);
});
test("real cross-project identity mismatch fails closed before planner or scheduler", t => {
  const f = fixture(t);
  f.bindings.projects[1].projectId = PROJECTS[0].id;
  assert.throws(() => captureCanonicalSnapshot(f.bindings), e =>
    e instanceof CanonicalSnapshotError && /IdentityOrEpochMismatch/.test(e.message));
});
