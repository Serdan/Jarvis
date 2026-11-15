#!/usr/bin/env node
// Project-scoped Manager launch planner. No browser actions or schedules run here.
// Jarvis Manager owns the actual browser launch and reports its outcome afterward.
import { randomUUID } from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const HARD_LIMITS = Object.freeze({ worker: 5, reviewer: 3 });
const ACTIVE_STATES = new Set(["Working", "Unknown"]);
const ALLOWED_STATES = new Set(["Ready", "Working", "NeedsWork", "Free",
  "Blocked", "Parked", "Stopped", "Unknown"]);
const ALLOWED_EXECUTIONS = new Set(["Working", "Stopped", "Unknown"]);
const LIVE_RECORDS = new Set(["reserved", "dispatching", "submitted", "uncertain", "blocked"]);
const LAUNCH_RESULTS = new Set(["submitted", "uncertain", "blocked"]);

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function validTime(value, label) {
  assert(typeof value === "string" && Number.isFinite(Date.parse(value)) &&
    /(?:Z|[+-]\d\d:\d\d)$/.test(value), label + " must be an offset-aware ISO timestamp");
  return Date.parse(value);
}

export function validateConfig(config) {
  assert(config && config.schemaVersion === 1, "Unsupported Manager config");
  assert(typeof config.projectName === "string" && config.projectName.trim(),
    "Missing projectName");
  assert(typeof config.managerAgentId === "string" && config.managerAgentId.trim(),
    "Missing managerAgentId");
  assert(typeof config.enabled === "boolean", "enabled must be explicit");
  assert(config.budgets && typeof config.budgets === "object", "Missing budgets");
  assert(Number.isInteger(config.maxLaunchesPerTick) &&
    config.maxLaunchesPerTick >= 1 && config.maxLaunchesPerTick <= 8,
    "maxLaunchesPerTick must be 1..8");
  const seen = new Set([config.managerAgentId]);
  for (const kind of ["worker", "reviewer"]) {
    const ids = config[kind + "Ids"];
    assert(Array.isArray(ids), "Missing " + kind + "Ids");
    const budget = config.budgets[kind];
    assert(Number.isInteger(budget) && budget >= 0 &&
      budget <= HARD_LIMITS[kind], "Budget exceeds " + kind + " hard limit");
    assert(ids.length >= budget, "Insufficient configured " + kind + " IDs");
    for (const id of ids) {
      assert(typeof id === "string" && id.trim() && !seen.has(id),
        "Invalid or duplicate agent ID: " + id);
      seen.add(id);
    }
  }
  return config;
}

export function validateSnapshot(config, snapshot, nowUtc) {
  assert(snapshot && snapshot.projectName === config.projectName,
    "Snapshot project mismatch");
  assert(snapshot.complete === true && snapshot.agentActivityVerified === true &&
    snapshot.reviewQueueVerified === true && snapshot.challengesVerified === true,
    "Snapshot lacks authoritative task, activity, review or challenge evidence");
  assert(typeof snapshot.ledgerHead === "string" &&
    /^[0-9a-f]{40,64}$/.test(snapshot.ledgerHead), "Missing ledger head");
  const observed = validTime(snapshot.capturedAtUtc, "capturedAtUtc");
  const now = validTime(nowUtc, "nowUtc");
  assert(observed <= now + 1000 && now - observed <= 180000,
    "Snapshot is stale or from the future");
  assert(Number.isInteger(snapshot.unclaimedReviewCount) &&
    snapshot.unclaimedReviewCount >= 0, "Invalid review count");
  assert(Array.isArray(snapshot.agents), "Missing agents");
  const agents = new Map();
  for (const agent of snapshot.agents) {
    assert(agent && typeof agent.agentId === "string" &&
      !agents.has(agent.agentId), "Duplicate/invalid agent observation");
    assert(["worker", "reviewer", "manager", "other"].includes(agent.kind),
      "Unknown agent kind");
    assert(ALLOWED_STATES.has(agent.state) &&
      ALLOWED_EXECUTIONS.has(agent.execution), "Unknown agent state/execution");
    assert(agent.verified === true, "Agent state unverified: " + agent.agentId);
    assert(typeof agent.waitingOnChallenge === "boolean" &&
      typeof agent.hasReadyAssignment === "boolean" &&
      typeof agent.slotAvailable === "boolean",
      "Missing agent assignment/challenge evidence: " + agent.agentId);
    assert(agent.kind !== "worker" || agent.state !== "Ready" ||
      (typeof agent.assignmentKey === "string" && agent.assignmentKey.length > 0),
      "Ready worker requires canonical assignment identity");
    assert(agent.priorityRank === undefined ||
      (Number.isInteger(agent.priorityRank) && agent.priorityRank >= 0 &&
       agent.priorityRank <= 100000), "Invalid canonical task priority rank");
    if (agent.lastAttributedActivityAtUtc !== null) {
      validTime(agent.lastAttributedActivityAtUtc, "lastAttributedActivityAtUtc");
      assert(agent.activityEvidence === "ExplicitAttributedToolCall",
        "Client-local last-seen is not proof of agent activity");
    }
    agents.set(agent.agentId, agent);
  }
  for (const kind of ["worker", "reviewer"]) {
    for (const id of config[kind + "Ids"]) {
      const agent = agents.get(id);
      assert(agent && agent.kind === kind, "Missing verified agent: " + id);
    }
  }
  assert(agents.has(config.managerAgentId) &&
    agents.get(config.managerAgentId).kind === "manager",
    "Missing verified Manager agent");
  return agents;
}

function freshJournal(projectName) {
  return { schemaVersion: 1, projectName, revision: 0, records: [] };
}

export function validateJournal(journal, projectName) {
  assert(journal && journal.schemaVersion === 1 &&
    journal.projectName === projectName && Array.isArray(journal.records),
    "Journal mismatch or corrupt journal; do not reset automatically");
  const ids = new Set();
  for (const rec of journal.records) {
    assert(rec && typeof rec.launchId === "string" && !ids.has(rec.launchId),
      "Invalid/duplicate journal launch ID");
    assert(typeof rec.agentId === "string" &&
      ["worker", "reviewer"].includes(rec.kind) &&
      [...LIVE_RECORDS, "observed", "cleared"].includes(rec.status),
      "Invalid journal record");
    validTime(rec.reservedAtUtc, "journal reservation");
    ids.add(rec.launchId);
  }
  return journal;
}

export function decide(config, snapshot, journal, nowUtc, dryRun = false) {
  validateConfig(config);
  const agents = validateSnapshot(config, snapshot, nowUtc);
  validateJournal(journal, config.projectName);
  const result = {
    projectName: config.projectName,
    ledgerHead: snapshot.ledgerHead,
    capturedAtUtc: snapshot.capturedAtUtc,
    enabled: config.enabled,
    counts: { worker: 0, reviewer: 0 },
    reservations: [],
    suppressed: []
  };
  const reserved = new Set();
  const observedThisPass = new Set();
  const observedAssignments = new Set();

  // Latest agent-attributed activity is positive evidence of a bootstrapped
  // session. It may release a reservation; neither time elapsed nor a
  // successful UI submission is proof the agent started.
  for (const rec of journal.records) {
    if (rec.status === "observed" && rec.kind === "worker" &&
      rec.assignmentKey && agents.get(rec.agentId)?.assignmentKey === rec.assignmentKey) {
      observedAssignments.add(rec.agentId);
    }
    if (!LIVE_RECORDS.has(rec.status)) continue;
    const agent = agents.get(rec.agentId);
    const activity = agent?.lastAttributedActivityAtUtc;
    const observed = activity !== null && activity !== undefined &&
      Date.parse(activity) > Date.parse(rec.reservedAtUtc) &&
      Date.parse(activity) <= Date.parse(snapshot.capturedAtUtc);
    if (observed) {
      observedThisPass.add(rec.agentId);
      if (rec.kind === "worker" && rec.assignmentKey &&
        agent.assignmentKey === rec.assignmentKey) observedAssignments.add(rec.agentId);
      if (!dryRun) {
        rec.status = "observed";
        rec.observedAtUtc = activity;
      }
      continue;
    }
    reserved.add(rec.agentId);
    result.counts[rec.kind]++;
  }
  // Count every live/uncertain agent in the project, not just our configured
  // agents. Unidentified activity must not be ignored to free capacity.
  for (const agent of agents.values()) {
    if (!["worker", "reviewer"].includes(agent.kind)) continue;
    if ((ACTIVE_STATES.has(agent.state) ||
      agent.execution === "Working" || agent.execution === "Unknown") &&
      !reserved.has(agent.agentId)) {
      result.counts[agent.kind]++;
    }
  }
  if (!config.enabled) {
    result.suppressed.push({ reason: "DisabledByConfiguration" });
    return result;
  }
  let remainingReviews = snapshot.unclaimedReviewCount;
  for (const kind of ["worker", "reviewer"]) {
    const order = [...config[kind + "Ids"]];
    if (kind === "worker") {
      order.sort((a, b) => (agents.get(a).priorityRank ?? 100000) -
        (agents.get(b).priorityRank ?? 100000));
    }
    for (const agentId of order) {
      const agent = agents.get(agentId);
      let reason = null;
      if (result.reservations.length >= config.maxLaunchesPerTick) reason = "TickLimit";
      else if (result.counts[kind] >= config.budgets[kind]) reason = "BudgetFull";
      else if (reserved.has(agentId)) reason = "PriorLaunchPendingOrUncertain";
      else if (observedThisPass.has(agentId) || observedAssignments.has(agentId))
        reason = "AlreadyObservedThisAssignment";
      else if (agent.waitingOnChallenge) reason = "ChallengeBlocked";
      else if (kind === "worker" &&
        !(agent.state === "Ready" && agent.execution === "Stopped" &&
          agent.hasReadyAssignment)) reason = "NoReadyAssignment";
      else if (kind === "reviewer" &&
        !((agent.state === "NeedsWork" || agent.state === "Free") &&
        agent.execution === "Stopped" && agent.slotAvailable &&
        !agent.hasReadyAssignment &&
        remainingReviews > 0)) reason = "NoUnclaimedReviewOrReviewerBusy";
      if (reason) {
        result.suppressed.push({ agentId, kind, reason });
        continue;
      }
      const launchId = dryRun ? "dry-run-" + kind + "-" + agentId :
        randomUUID();
      result.reservations.push({
        launchId, agentId, kind,
        reason: kind === "worker" ? "ReadyImplementation" : "UnclaimedFrozenReview"
      });
      result.counts[kind]++;
      reserved.add(agentId);
      if (kind === "reviewer") remainingReviews--;
      if (!dryRun) {
        journal.records.push({
          launchId, agentId, kind,
          reservedAtUtc: nowUtc,
          assignmentKey: agent.assignmentKey ?? null,
          ledgerHead: snapshot.ledgerHead,
          status: "reserved"
        });
      }
    }
  }
  return result;
}

function readJson(file) {
  return JSON.parse(fs.readFileSync(file, "utf8"));
}

function loadJournal(file, project) {
  return fs.existsSync(file) ? validateJournal(readJson(file), project) :
    freshJournal(project);
}

function durableWrite(file, object) {
  const folder = path.dirname(file);
  fs.mkdirSync(folder, { recursive: true, mode: 0o700 });
  const tmp = file + ".tmp-" + randomUUID();
  let fd;
  try {
    fd = fs.openSync(tmp, "wx", 0o600);
    fs.writeFileSync(fd, JSON.stringify(object, null, 2) + "\n");
    fs.fsyncSync(fd);
    fs.closeSync(fd);
    fd = undefined;
    fs.renameSync(tmp, file);
    const dir = fs.openSync(folder, "r");
    try { fs.fsyncSync(dir); } finally { fs.closeSync(dir); }
  } finally {
    if (fd !== undefined) fs.closeSync(fd);
    if (fs.existsSync(tmp)) fs.unlinkSync(tmp);
  }
}

function withExclusiveLock(file, callback) {
  fs.mkdirSync(path.dirname(file), { recursive: true, mode: 0o700 });
  const lock = file + ".lock";
  // A crashed owner leaves the lock behind: fail closed, require operator
  // verification before manual recovery. No time-based lock stealing.
  let fd;
  try {
    fd = fs.openSync(lock, "wx", 0o600);
    fs.writeFileSync(fd, String(process.pid) + "\n");
    fs.fsyncSync(fd);
    return callback();
  } finally {
    if (fd !== undefined) {
      fs.closeSync(fd);
      fs.unlinkSync(lock);
    }
  }
}

function options(argv) {
  const out = {};
  for (let i = 0; i < argv.length; i += 2) {
    assert(argv[i]?.startsWith("--") && i + 1 < argv.length,
      "Expected --option value");
    const key = argv[i].slice(2);
    assert(!Object.hasOwn(out, key), "Duplicate option: " + key);
    out[key] = argv[i + 1];
  }
  return out;
}

export function runCli(argv) {
  const [action, ...rest] = argv;
  assert(["plan", "dry-run", "mark", "release", "status", "check", "begin"].includes(action),
    "Usage: manager-heartbeat.mjs plan|dry-run|check|begin|mark|release|status --config FILE --journal FILE [--snapshot FILE]");
  const opts = options(rest);
  assert(opts.config && opts.journal, "Config and journal are required");
  const config = validateConfig(readJson(opts.config));
  if (action === "status") {
    return { projectName: config.projectName,
      journal: loadJournal(opts.journal, config.projectName) };
  }
  if (action === "check") {
    assert(opts["launch-id"], "Launch ID required");
    assert(config.enabled, "Manager launch switch is disabled");
    const journal = loadJournal(opts.journal, config.projectName);
    const rec = journal.records.find(x => x.launchId === opts["launch-id"]);
    assert(rec && rec.status === "reserved",
      "Launch is no longer a fresh reservation");
    const age = Date.now() - validTime(rec.reservedAtUtc, "reservation");
    assert(age >= -1000 && age <= 180000, "Launch reservation is stale");
    return { projectName: config.projectName, launchId: rec.launchId,
      agentId: rec.agentId, kind: rec.kind, eligible: true };
  }
  if (action === "dry-run") {
    assert(opts.snapshot, "Snapshot required");
    return decide(config, readJson(opts.snapshot),
      loadJournal(opts.journal, config.projectName), new Date().toISOString(), true);
  }
  return withExclusiveLock(opts.journal, () => {
    const journal = loadJournal(opts.journal, config.projectName);
    let result;
    if (action === "plan") {
      assert(opts.snapshot, "Snapshot required");
      result = decide(config, readJson(opts.snapshot), journal,
        new Date().toISOString());
    } else {
      assert(opts["launch-id"], "Launch ID required");
      const rec = journal.records.find(x => x.launchId === opts["launch-id"]);
      assert(rec, "Unknown launch ID");
      if (action === "begin") {
        assert(config.enabled, "Manager launch switch is disabled");
        assert(rec.status === "reserved", "Launch already dispatched or closed");
        const age = Date.now() - validTime(rec.reservedAtUtc, "reservation");
        assert(age >= -1000 && age <= 180000, "Launch reservation is stale");
        rec.status = "dispatching";
        rec.dispatchedAtUtc = new Date().toISOString();
        result = { launchId: rec.launchId, status: rec.status,
          agentId: rec.agentId, kind: rec.kind };
      } else if (action === "mark") {
        assert(LAUNCH_RESULTS.has(opts.result), "Invalid launch result");
        assert((opts.result === "blocked" && rec.status === "reserved") ||
          (opts.result !== "blocked" && rec.status === "dispatching") ||
          rec.status === opts.result,
          "Conflicting result; manual recovery required");
        rec.status = opts.result;
        rec.reportedAtUtc ??= new Date().toISOString();
        result = { launchId: rec.launchId, status: rec.status };
      } else {
        assert(typeof opts.reason === "string" && opts.reason.trim().length >= 12,
          "Explicit operator recovery reason required");
        assert(LIVE_RECORDS.has(rec.status), "Only live reservations can be released");
        rec.status = "cleared";
        rec.recoveryReason = opts.reason;
        rec.releasedAtUtc = new Date().toISOString();
        result = { launchId: rec.launchId, status: "cleared" };
      }
    }
    journal.revision++;
    durableWrite(opts.journal, journal);
    return result;
  });
}

if (process.argv[1] && fileURLToPath(import.meta.url) === path.resolve(process.argv[1])) {
  try {
    console.log(JSON.stringify(runCli(process.argv.slice(2)), null, 2));
  } catch (error) {
    console.error("ManagerPlanner: " + error.message);
    process.exitCode = 1;
  }
}
