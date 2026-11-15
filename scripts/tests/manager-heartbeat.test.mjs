import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { decide, runCli } from "../manager-heartbeat.mjs";

const NOW = "2026-11-15T02:00:00.000Z";
const HEAD = "6ab793928fa7f998b9d5796061cf66d3c0b02e40";
const SCRIPT = fileURLToPath(new URL("../manager-heartbeat.mjs", import.meta.url));
const config = (projectName = "Jarvis") => ({
  schemaVersion: 1, projectName, managerAgentId: "Manager",
  enabled: true,
  budgets: { worker: 2, reviewer: 2 }, maxLaunchesPerTick: 8,
  workerIds: ["Worker 1", "Worker 2"],
  reviewerIds: ["Reviewer 1", "Reviewer 2"]
});
function agent(agentId, kind, state, overrides = {}) {
  return {
    agentId, kind, state, execution: state === "Working" ? "Working" : "Stopped",
    slotAvailable: state === "Free" || state === "NeedsWork", verified: true,
    hasReadyAssignment: state === "Ready",
    // Canonical state transition time from observe_agent, not a client-local cache.
    stateEnteredAtUtc: "2026-11-14T02:00:00Z",
    assignmentKey: kind === "worker" && state === "Ready" ? "task/gen1/" + agentId : null,
    waitingOnChallenge: false,
    lastAttributedActivityAtUtc: null,
    ...overrides
  };
}
function snapshot(projectName = "Jarvis") {
  return {
    projectName, complete: true, agentActivityVerified: true,
    reviewQueueVerified: true, reviewClaimsVerified: true, challengesVerified: true,
    ledgerHead: HEAD, capturedAtUtc: NOW, unclaimedReviewCount: 2,
    claimedReviews: [],
    agents: [
      agent("Manager", "manager", "Working"),
      agent("Worker 1", "worker", "Ready"),
      agent("Worker 2", "worker", "Ready"),
      agent("Reviewer 1", "reviewer", "NeedsWork"),
      agent("Reviewer 2", "reviewer", "NeedsWork")
    ]
  };
}
const journal = (projectName = "Jarvis") => ({
  schemaVersion: 1, projectName, revision: 0, records: []
});

test("zero ready implementation and zero unclaimed reviews launch no one", () => {
  const s = snapshot();
  s.agents[1].state = s.agents[2].state = "Free";
  s.agents[1].hasReadyAssignment = s.agents[2].hasReadyAssignment = false;
  s.unclaimedReviewCount = 0;
  assert.equal(decide(config(), s, journal(), NOW).reservations.length, 0);
});

test("under budget: implementation before shared frozen reviewers", () => {
  const result = decide(config(), snapshot(), journal(), NOW);
  assert.deepEqual(result.reservations.map(x => x.agentId),
    ["Worker 1", "Worker 2", "Reviewer 1", "Reviewer 2"]);
  assert.deepEqual(result.counts, { worker: 2, reviewer: 2 });
});

test("hard worker/reviewer budgets reject oversized per-project config", () => {
  const c = config();
  c.budgets.worker = 6;
  assert.throws(() => decide(c, snapshot(), journal(), NOW), /hard limit/);
});

test("full budget counts existing live agents and unknown observations", () => {
  const s = snapshot();
  s.agents[1].state = "Working";
  s.agents[2].state = "Unknown";
  s.agents[3].state = s.agents[4].state = "Working";
  assert.equal(decide(config(), s, journal(), NOW).reservations.length, 0);
});

test("extra unconfigured working agents consume the project budget", () => {
  const s = snapshot();
  s.agents.push(agent("Worker 3", "worker", "Working"));
  assert.deepEqual(decide(config(), s, journal(), NOW).reservations
    .filter(x => x.kind === "worker").map(x => x.agentId), ["Worker 1"]);
});

test("missing or unverified agent fails closed", () => {
  const s = snapshot();
  s.agents.pop();
  assert.throws(() => decide(config(), s, journal(), NOW), /Missing verified agent/);
  const unknown = snapshot();
  unknown.agents[2].verified = false;
  assert.throws(() => decide(config(), unknown, journal(), NOW), /unverified/);
});

test("stale, future or incomplete observation fails closed", () => {
  const s = snapshot();
  assert.throws(() => decide(config(), s, journal(), "2026-11-15T02:05:00Z"),
    /stale/);
  s.capturedAtUtc = "2026-11-15T02:10:00Z";
  assert.throws(() => decide(config(), s, journal(), NOW), /future/);
  s.capturedAtUtc = NOW; s.challengesVerified = false;
  assert.throws(() => decide(config(), s, journal(), NOW), /authoritative/);
});

test("pending/uncertain launch prevents duplicate idle ticks", () => {
  const j = journal();
  const first = decide(config(), snapshot(), j, NOW);
  assert.equal(first.reservations.length, 4);
  j.records[0].status = "uncertain";
  const second = decide(config(), snapshot(), j, NOW);
  assert.equal(second.reservations.length, 0);
  assert.equal(j.records[0].status, "uncertain");
  assert.equal(j.records.length, 4);
});

test("new observed Jarvis activity releases reservation, not a successful UI send", () => {
  const j = journal();
  decide(config(), snapshot(), j, NOW);
  const s = snapshot();
  const later = "2026-11-15T02:01:00Z";
  s.capturedAtUtc = later;
  s.agents[1].lastAttributedActivityAtUtc = "2026-11-15T02:00:30Z";
  s.agents[1].activityEvidence = "ExplicitAttributedToolCall";
  s.agents[1].state = "Working";
  s.agents[1].execution = "Working";
  const r = decide(config(), s, j, later);
  assert.equal(j.records[0].status, "observed");
  assert.ok(!r.reservations.some(x => x.agentId === "Worker 1"));
  assert.equal(r.counts.worker, 2);
});

test("challenge-blocked agent and no-review demand do not launch", () => {
  const s = snapshot();
  s.agents[1].waitingOnChallenge = true;
  s.unclaimedReviewCount = 0;
  const r = decide(config(), s, journal(), NOW);
  assert.deepEqual(r.reservations.map(x => x.agentId), ["Worker 2"]);
});

test("disabled config is a stop switch independent of task state", () => {
  const c = config();
  c.enabled = false;
  assert.equal(decide(c, snapshot(), journal(), NOW).reservations.length, 0);
});

test("dry-run never mutates a journal or creates a real launch id", () => {
  const j = journal();
  const r = decide(config(), snapshot(), j, NOW, true);
  assert.equal(j.records.length, 0);
  assert.ok(r.reservations.every(x => x.launchId.startsWith("dry-run-")));
});

test("journal refuses cross-project reuse", () => {
  assert.throws(() => decide(config("Wayfold"), snapshot("Wayfold"),
    journal("Jarvis"), NOW), /Journal mismatch/);
});

test("process restart retains reservations; explicit result and operator release are audited", () => {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), "jarvis-manager-test-"));
  try {
    const conf = path.join(temp, "config.json");
    const snap = path.join(temp, "snapshot.json");
    const state = path.join(temp, "journal.json");
    fs.writeFileSync(conf, JSON.stringify(config()));
    // Actual UTC time; this is an isolated CLI persistence test.
    const current = snapshot();
    current.capturedAtUtc = new Date().toISOString();
    fs.writeFileSync(snap, JSON.stringify(current));
    const args = ["plan", "--config", conf, "--snapshot", snap,
      "--journal", state];
    const first = spawnSync(process.execPath, [SCRIPT, ...args], { encoding: "utf8" });
    assert.equal(first.status, 0, first.stderr);
    const firstDecisions = JSON.parse(first.stdout).reservations;
    assert.equal(firstDecisions.length, 4);
    const second = spawnSync(process.execPath, [SCRIPT, ...args], { encoding: "utf8" });
    assert.equal(second.status, 0, second.stderr);
    assert.equal(JSON.parse(second.stdout).reservations.length, 0);
    const begun = runCli(["begin", "--config", conf, "--journal", state,
      "--launch-id", firstDecisions[0].launchId]);
    assert.equal(begun.status, "dispatching");
    const marked = runCli(["mark", "--config", conf, "--journal", state,
      "--launch-id", firstDecisions[0].launchId, "--result", "uncertain"]);
    assert.equal(marked.status, "uncertain");
    const released = runCli(["release", "--config", conf, "--journal", state,
      "--launch-id", firstDecisions[0].launchId,
      "--reason", "Operator confirmed no active browser launch"]);
    assert.equal(released.status, "cleared");
    const records = JSON.parse(fs.readFileSync(state, "utf8")).records;
    assert.equal(records[0].recoveryReason,
      "Operator confirmed no active browser launch");
  } finally {
    fs.rmSync(temp, { recursive: true, force: true });
  }
});

test("leftover lock fails closed rather than stealing ownership", () => {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), "jarvis-manager-lock-"));
  try {
    const conf = path.join(temp, "config.json");
    const snap = path.join(temp, "snapshot.json");
    const state = path.join(temp, "journal.json");
    fs.writeFileSync(conf, JSON.stringify(config()));
    const current = snapshot();
    current.capturedAtUtc = new Date().toISOString();
    fs.writeFileSync(snap, JSON.stringify(current));
    fs.writeFileSync(state + ".lock", "crashed process");
    assert.throws(() => runCli(["plan", "--config", conf, "--journal", state,
      "--snapshot", snap]), /EEXIST/);
    assert.equal(fs.existsSync(state), false);
  } finally {
    fs.rmSync(temp, { recursive: true, force: true });
  }
});

test("a local last-seen cache cannot clear an uncertain launch", () => {
  const j = journal();
  decide(config(), snapshot(), j, NOW);
  const s = snapshot();
  s.capturedAtUtc = "2026-11-15T02:01:00Z";
  s.agents[1].lastAttributedActivityAtUtc = "2026-11-15T02:00:30Z";
  assert.throws(() => decide(config(), s, j, s.capturedAtUtc),
    /Client-local last-seen/);
  assert.equal(j.records[0].status, "reserved");
});

test("preflight refuses disabled, stale, or consumed reservations", () => {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), "jarvis-manager-check-"));
  try {
    const conf = path.join(temp, "config.json");
    const state = path.join(temp, "journal.json");
    const c = config();
    const j = journal();
    const launchId = "preflight-id";
    j.records.push({
      launchId, agentId: "Worker 1", kind: "worker",
      reservedAtUtc: new Date().toISOString(),
      ledgerHead: HEAD, status: "reserved"
    });
    fs.writeFileSync(conf, JSON.stringify(c));
    fs.writeFileSync(state, JSON.stringify(j));
    const args = ["check", "--config", conf, "--journal", state,
      "--launch-id", launchId];
    assert.equal(runCli(args).eligible, true);
    c.enabled = false;
    fs.writeFileSync(conf, JSON.stringify(c));
    assert.throws(() => runCli(args), /disabled/);
    c.enabled = true;
    fs.writeFileSync(conf, JSON.stringify(c));
    j.records[0].reservedAtUtc = "2020-01-01T00:00:00Z";
    fs.writeFileSync(state, JSON.stringify(j));
    assert.throws(() => runCli(args), /stale/);
    j.records[0].reservedAtUtc = new Date().toISOString();
    j.records[0].status = "uncertain";
    fs.writeFileSync(state, JSON.stringify(j));
    assert.throws(() => runCli(args), /no longer a fresh reservation/);
  } finally {
    fs.rmSync(temp, { recursive: true, force: true });
  }
});

test("canonical task priority rank wins over static worker label order", () => {
  const c = config();
  c.budgets.worker = 1;
  c.budgets.reviewer = 0;
  const s = snapshot();
  s.agents[1].priorityRank = 10;
  s.agents[2].priorityRank = 0;
  const r = decide(c, s, journal(), NOW);
  assert.deepEqual(r.reservations.map(x => x.agentId), ["Worker 2"]);
});

test("one global tick slot prioritizes a ready worker before an unclaimed review", () => {
  const c = config();
  c.maxLaunchesPerTick = 1;
  const r = decide(c, snapshot(), journal(), NOW);
  assert.deepEqual(r.reservations.map(x => x.agentId), ["Worker 1"]);
});

test("shared review demand bounds taskless reviewer launches", () => {
  const c = config();
  c.budgets.worker = 0;
  const s = snapshot();
  s.unclaimedReviewCount = 1;
  const r = decide(c, s, journal(), NOW);
  assert.deepEqual(r.reservations.map(x => x.agentId), ["Reviewer 1"]);
});

test("begin atomically consumes launch, never dispatches the same reservation twice", () => {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), "jarvis-manager-begin-"));
  try {
    const conf = path.join(temp, "config.json");
    const state = path.join(temp, "journal.json");
    const j = journal();
    j.records.push({ launchId: "abc", agentId: "Worker 1", kind: "worker",
      assignmentKey: "task/gen1/Worker 1",
      status: "reserved", reservedAtUtc: new Date().toISOString(),
      ledgerHead: HEAD });
    fs.writeFileSync(conf, JSON.stringify(config()));
    fs.writeFileSync(state, JSON.stringify(j));
    const args = ["begin", "--config", conf, "--journal", state,
      "--launch-id", "abc"];
    assert.equal(runCli(args).status, "dispatching");
    assert.throws(() => runCli(args), /already dispatched/);
    assert.throws(() => runCli(["mark", "--config", conf, "--journal", state,
      "--launch-id", "abc", "--result", "blocked"]), /Conflicting result/);
    const marked = runCli(["mark", "--config", conf, "--journal", state,
      "--launch-id", "abc", "--result", "submitted"]);
    assert.equal(marked.status, "submitted");
  } finally {
    fs.rmSync(temp, { recursive: true, force: true });
  }
});

test("positive activity cannot relaunch same Ready assignment", () => {
  const j = journal();
  decide(config(), snapshot(), j, NOW);
  const s = snapshot();
  s.capturedAtUtc = "2026-11-15T02:01:00Z";
  s.agents[1].lastAttributedActivityAtUtc = "2026-11-15T02:00:30Z";
  s.agents[1].activityEvidence = "ExplicitAttributedToolCall";
  const first = decide(config(), s, j, s.capturedAtUtc);
  assert.ok(!first.reservations.some(x => x.agentId === "Worker 1"));
  assert.equal(j.records[0].status, "observed");
  const next = decide(config(), s, j, s.capturedAtUtc);
  assert.ok(!next.reservations.some(x => x.agentId === "Worker 1"));
  // A new canonical assignment is eligible in the next Manager pass.
  s.agents[1].assignmentKey = "different-task/gen1/Worker 1";
  const later = decide(config(), s, j, s.capturedAtUtc);
  assert.ok(later.reservations.some(x => x.agentId === "Worker 1"));
});


test("observed Working then canonical yield to Ready relaunches same assignment once", () => {
  const c = config();
  c.budgets.worker = 1;
  c.budgets.reviewer = 0;
  const j = journal();
  const initial = snapshot();
  initial.agents[2].state = "Free";
  initial.agents[2].hasReadyAssignment = false;
  decide(c, initial, j, NOW);
  assert.equal(j.records.length, 1);

  const working = snapshot();
  working.agents[2].state = "Free";
  working.agents[2].hasReadyAssignment = false;
  working.capturedAtUtc = "2026-11-15T02:01:00Z";
  working.agents[1].state = "Working";
  working.agents[1].execution = "Working";
  working.agents[1].lastAttributedActivityAtUtc = "2026-11-15T02:00:30Z";
  working.agents[1].activityEvidence = "ExplicitAttributedToolCall";
  assert.equal(decide(c, working, j, working.capturedAtUtc).reservations.length, 0);
  assert.equal(j.records[0].status, "observed");

  const yielded = snapshot();
  yielded.agents[2].state = "Free";
  yielded.agents[2].hasReadyAssignment = false;
  yielded.capturedAtUtc = "2026-11-15T02:02:00Z";
  yielded.agents[1].lastAttributedActivityAtUtc = "2026-11-15T02:00:30Z";
  yielded.agents[1].activityEvidence = "ExplicitAttributedToolCall";
  yielded.agents[1].stateEnteredAtUtc = "2026-11-15T02:01:30Z";
  const resumed = decide(c, yielded, j, yielded.capturedAtUtc);
  assert.deepEqual(resumed.reservations.map(r => r.agentId), ["Worker 1"]);
  assert.equal(j.records.length, 2);
  assert.equal(j.records[0].status, "observed");
  assert.equal(j.records[1].status, "reserved");
  assert.equal(decide(c, yielded, j, yielded.capturedAtUtc).reservations.length, 0);
});

test("yield between Manager observations clears observed reservation and relaunches", () => {
  const c = config();
  c.budgets.worker = 1;
  c.budgets.reviewer = 0;
  const j = journal();
  const s = snapshot();
  s.agents[2].state = "Free";
  s.agents[2].hasReadyAssignment = false;
  decide(c, s, j, NOW);

  s.capturedAtUtc = "2026-11-15T02:02:00Z";
  s.agents[1].lastAttributedActivityAtUtc = "2026-11-15T02:00:30Z";
  s.agents[1].activityEvidence = "ExplicitAttributedToolCall";
  s.agents[1].stateEnteredAtUtc = "2026-11-15T02:01:30Z";
  const next = decide(c, s, j, s.capturedAtUtc);
  assert.deepEqual(next.reservations.map(r => r.agentId), ["Worker 1"]);
  assert.equal(j.records[0].status, "observed");
  assert.equal(j.records[1].status, "reserved");
});

test("blocked pre-dispatch reservation releases budget across process restart", () => {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), "jarvis-manager-blocked-"));
  try {
    const conf = path.join(temp, "config.json");
    const snap = path.join(temp, "snapshot.json");
    const state = path.join(temp, "journal.json");
    const c = config();
    c.budgets.worker = 1;
    c.budgets.reviewer = 0;
    const s = snapshot();
    s.capturedAtUtc = new Date().toISOString();
    s.agents[2].state = "Free";
    s.agents[2].hasReadyAssignment = false;
    fs.writeFileSync(conf, JSON.stringify(c));
    fs.writeFileSync(snap, JSON.stringify(s));
    const args = ["plan", "--config", conf, "--snapshot", snap, "--journal", state];
    const first = spawnSync(process.execPath, [SCRIPT, ...args], { encoding: "utf8" });
    assert.equal(first.status, 0, first.stderr);
    const [original] = JSON.parse(first.stdout).reservations;
    assert.equal(original.agentId, "Worker 1");
    const blocked = runCli(["mark", "--config", conf, "--journal", state,
      "--launch-id", original.launchId, "--result", "blocked"]);
    assert.equal(blocked.status, "blocked");
    assert.throws(() => runCli(["release", "--config", conf, "--journal", state,
      "--launch-id", original.launchId, "--reason", "Unnecessary blocked release"]),
      /Only live reservations/);

    const restarted = spawnSync(process.execPath, [SCRIPT, ...args], { encoding: "utf8" });
    assert.equal(restarted.status, 0, restarted.stderr);
    const decisions = JSON.parse(restarted.stdout);
    assert.deepEqual(decisions.reservations.map(r => r.agentId), ["Worker 1"]);
    assert.equal(decisions.counts.worker, 1);
    assert.notEqual(decisions.reservations[0].launchId, original.launchId);
    const persisted = JSON.parse(fs.readFileSync(state, "utf8"));
    assert.equal(persisted.records[0].status, "blocked");
    assert.equal(persisted.records[1].status, "reserved");
    const duplicate = spawnSync(process.execPath, [SCRIPT, ...args], { encoding: "utf8" });
    assert.equal(duplicate.status, 0, duplicate.stderr);
    assert.equal(JSON.parse(duplicate.stdout).reservations.length, 0);
  } finally {
    fs.rmSync(temp, { recursive: true, force: true });
  }
});

test("unclassified active or uncertain project agents fail closed", () => {
  for (const state of ["Working", "Unknown"]) {
    const s = snapshot();
    s.agents.push(agent("Independent agent", "other", state));
    const j = journal();
    assert.throws(() => decide(config(), s, j, NOW),
      /Unclassified live or uncertain project agent/);
    assert.equal(j.records.length, 0);
  }
  const s = snapshot();
  s.agents.push(agent("Inactive helper", "other", "Parked"));
  assert.equal(decide(config(), s, journal(), NOW).reservations.length, 4);
});

const NEXT_HEAD = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
const REVIEW = { reviewId: "frozen-review-1", reviewerId: "Reviewer 1" };

function reviewerOnlyConfig() {
  const c = config();
  c.budgets.worker = 0;
  return c;
}
function reviewerObserved(s, at = "2026-11-15T02:00:30Z") {
  s.agents[3].lastAttributedActivityAtUtc = at;
  s.agents[3].activityEvidence = "ExplicitAttributedToolCall";
}

test("taskless reviewer claim remains active across heartbeats and journal restart", () => {
  const c = reviewerOnlyConfig();
  const j = journal();
  const initial = snapshot();
  initial.unclaimedReviewCount = 1; // 2 exist in the queue over time; first reviewer gets first.
  const first = decide(c, initial, j, NOW);
  assert.deepEqual(first.reservations.map(r => r.agentId), ["Reviewer 1"]);

  const claiming = snapshot();
  claiming.capturedAtUtc = "2026-11-15T02:01:00Z";
  claiming.ledgerHead = NEXT_HEAD;
  claiming.claimedReviews = [REVIEW];
  claiming.unclaimedReviewCount = 1;
  reviewerObserved(claiming);
  const active = decide(c, claiming, j, claiming.capturedAtUtc);
  assert.equal(j.records[0].status, "observed");
  assert.deepEqual(j.records[0].observedClaimIds, [REVIEW.reviewId]);
  assert.equal(active.counts.reviewer, 2);
  assert.deepEqual(active.reservations.map(r => r.agentId), ["Reviewer 2"]);
  assert.ok(active.suppressed.some(x => x.agentId === "Reviewer 1"));

  // Later than the first tool call and after a process restart, the reviewer
  // is still NeedsWork/Stopped and still holds the claim. No second launch.
  const restarted = JSON.parse(JSON.stringify(j));
  const later = structuredClone(claiming);
  later.capturedAtUtc = "2026-11-15T02:16:00Z";
  const subsequent = decide(c, later, restarted, later.capturedAtUtc);
  assert.equal(subsequent.reservations.length, 0);
  assert.equal(subsequent.counts.reviewer, 2);
  assert.equal(restarted.records[0].status, "observed");
});

test("resolved canonical claim at a later verified head frees reviewer for other work", () => {
  const c = reviewerOnlyConfig();
  c.budgets.reviewer = 1;
  const j = journal();
  const first = snapshot();
  first.unclaimedReviewCount = 1;
  decide(c, first, j, NOW);

  const claimed = snapshot();
  claimed.capturedAtUtc = "2026-11-15T02:01:00Z";
  claimed.claimedReviews = [REVIEW];
  reviewerObserved(claimed);
  decide(c, claimed, j, claimed.capturedAtUtc);
  assert.equal(j.records[0].status, "observed");

  const resolved = structuredClone(claimed);
  resolved.capturedAtUtc = "2026-11-15T02:03:00Z";
  resolved.ledgerHead = NEXT_HEAD; // new authoritative review decision/release head
  resolved.claimedReviews = [];
  const r = decide(c, resolved, j, resolved.capturedAtUtc);
  assert.equal(j.records[0].status, "review-finished");
  assert.equal(j.records[0].completionEvidence, "CanonicalReviewClaimResolved");
  assert.deepEqual(r.reservations.map(x => x.agentId), ["Reviewer 1"]);
  assert.equal(decide(c, resolved, j, resolved.capturedAtUtc).reservations.length, 0);
});

test("explicit attributed reviewer yield resumes an existing claimed review once", () => {
  const c = reviewerOnlyConfig();
  c.budgets.reviewer = 1;
  const j = journal();
  const first = snapshot();
  first.unclaimedReviewCount = 1;
  decide(c, first, j, NOW);

  const claiming = snapshot();
  claiming.capturedAtUtc = "2026-11-15T02:01:00Z";
  claiming.claimedReviews = [REVIEW];
  claiming.unclaimedReviewCount = 0;
  reviewerObserved(claiming);
  assert.equal(decide(c, claiming, j, claiming.capturedAtUtc).reservations.length, 0);

  const yielded = structuredClone(claiming);
  yielded.capturedAtUtc = "2026-11-15T02:04:00Z";
  yielded.agents[3].reviewerYieldAtUtc = "2026-11-15T02:03:00Z";
  yielded.agents[3].reviewerYieldEvidence = "ExplicitAttributedReviewerYield";
  yielded.agents[3].lastAttributedActivityAtUtc = "2026-11-15T02:03:00Z";
  const r = decide(c, yielded, j, yielded.capturedAtUtc);
  assert.deepEqual(r.reservations.map(x => [x.agentId, x.reason]),
    [["Reviewer 1", "ResumeVerifiedPausedReview"]]);
  assert.equal(j.records[0].completionEvidence, "ExplicitReviewerYield");
  assert.equal(j.records[0].status, "review-finished");
  const persisted = JSON.parse(JSON.stringify(j));
  assert.equal(decide(c, yielded, persisted, yielded.capturedAtUtc).reservations.length, 0);
});

test("missing or contradictory reviewer claim/yield evidence fails closed", () => {
  const c = reviewerOnlyConfig();
  const initial = snapshot();
  initial.reviewClaimsVerified = false;
  assert.throws(() => decide(c, initial, journal(), NOW), /authoritative/);
  initial.reviewClaimsVerified = true;
  delete initial.claimedReviews;
  assert.throws(() => decide(c, initial, journal(), NOW), /claimed-review/);
  initial.claimedReviews = [{ reviewId: "review", reviewerId: "Unknown" }];
  assert.throws(() => decide(c, initial, journal(), NOW), /unclassified/);
  initial.claimedReviews = [];
  initial.agents[3].reviewerYieldAtUtc = "2026-11-15T02:00:00Z";
  assert.throws(() => decide(c, initial, journal(), NOW), /positively attributed/);

  // Activity alone, even after a long delay, is never an explicit yield.
  delete initial.agents[3].reviewerYieldAtUtc;
  const j = journal();
  initial.unclaimedReviewCount = 1;
  decide(c, initial, j, NOW);
  reviewerObserved(initial);
  initial.capturedAtUtc = "2026-11-15T02:01:00Z";
  decide(c, initial, j, initial.capturedAtUtc);
  const later = structuredClone(initial);
  later.capturedAtUtc = "2026-11-15T02:20:00Z";
  later.unclaimedReviewCount = 2;
  assert.ok(!decide(c, later, j, later.capturedAtUtc).reservations
    .some(r => r.agentId === "Reviewer 1"));
});

test("active review claim without a Manager journal consumes reviewer capacity", () => {
  const c = reviewerOnlyConfig();
  c.budgets.reviewer = 1;
  const s = snapshot();
  s.claimedReviews = [REVIEW];
  s.unclaimedReviewCount = 1;
  const r = decide(c, s, journal(), NOW);
  assert.equal(r.counts.reviewer, 1);
  assert.equal(r.reservations.length, 0);
  assert.ok(r.suppressed.some(x => x.agentId === "Reviewer 1" &&
    x.reason === "BudgetFull"));
});

test("same-head missing claimed review never proves session completion", () => {
  const c = reviewerOnlyConfig();
  c.budgets.reviewer = 1;
  const j = journal();
  const s = snapshot();
  s.unclaimedReviewCount = 1;
  decide(c, s, j, NOW);
  s.capturedAtUtc = "2026-11-15T02:01:00Z";
  reviewerObserved(s);
  s.claimedReviews = [REVIEW];
  decide(c, s, j, s.capturedAtUtc);
  s.capturedAtUtc = "2026-11-15T02:03:00Z";
  s.claimedReviews = []; // contradictory if head is unchanged
  assert.equal(decide(c, s, j, s.capturedAtUtc).reservations.length, 0);
  assert.equal(j.records[0].status, "observed");
});

test("observed unresolved reviewer can be explicitly operator-cleared with audited reason", () => {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), "jarvis-manager-review-recover-"));
  try {
    const conf = path.join(temp, "config.json");
    const state = path.join(temp, "journal.json");
    const c = reviewerOnlyConfig();
    c.budgets.reviewer = 1;
    fs.writeFileSync(conf, JSON.stringify(c));
    const j = journal();
    const initial = snapshot();
    initial.unclaimedReviewCount = 1;
    decide(c, initial, j, NOW);
    const inProgress = snapshot();
    inProgress.capturedAtUtc = "2026-11-15T02:01:00Z";
    inProgress.claimedReviews = [REVIEW];
    inProgress.unclaimedReviewCount = 0;
    reviewerObserved(inProgress);
    decide(c, inProgress, j, inProgress.capturedAtUtc);
    fs.writeFileSync(state, JSON.stringify(j));

    const released = runCli(["release", "--config", conf, "--journal", state,
      "--launch-id", j.records[0].launchId,
      "--reason", "Operator positively verified prior reviewer session ended"]);
    assert.equal(released.status, "cleared");
    const persisted = JSON.parse(fs.readFileSync(state, "utf8"));
    assert.equal(persisted.records[0].reviewerSessionCleared, true);
    assert.equal(persisted.records[0].recoveryReason,
      "Operator positively verified prior reviewer session ended");
    const resumed = decide(c, inProgress, persisted, inProgress.capturedAtUtc);
    assert.deepEqual(resumed.reservations.map(x => x.reason),
      ["ResumeVerifiedPausedReview"]);
    assert.equal(decide(c, inProgress, persisted, inProgress.capturedAtUtc).reservations.length, 0);
  } finally {
    fs.rmSync(temp, { recursive: true, force: true });
  }
});
