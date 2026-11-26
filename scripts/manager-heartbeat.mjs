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
// A blocked-before-dispatch reservation is terminal, not a live browser effect.
const LIVE_RECORDS = new Set(["reserved", "dispatching", "submitted", "uncertain"]);
const TERMINAL_RECORDS = new Set(["blocked", "observed", "cleared"]);
const LAUNCH_RESULTS = new Set(["submitted", "uncertain", "blocked"]);
const REVIEW_ID_PATTERN = /^[a-f0-9]{32}$/;
const TASK_ID_PATTERN = /^[A-Za-z0-9_-]{1,80}$/;
const REVIEWABLE_AGENT_STATES = new Set(["Ready", "NeedsWork", "Free"]);
const UNASSIGNED_DISPOSITIONS = new Set(["NeedsWork", "Parked", "TasklessYield"]);

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function validTime(value, label) {
  assert(typeof value === "string" && Number.isFinite(Date.parse(value)) &&
    /(?:Z|[+-]\d\d:\d\d)$/.test(value), label + " must be an offset-aware ISO timestamp");
  return Date.parse(value);
}

function reviewKey(review) {
  return `${review.taskId}/${review.reviewId}`;
}

function reviewIdentity(review, mode) {
  return {
    mode,
    taskId: review.taskId,
    attemptId: review.attemptId ?? null,
    reviewerId: review.reviewerId ?? null,
    reviewId: review.reviewId,
    version: review.version
  };
}

function validateReviewerInboxes(config, snapshot) {
  assert(Array.isArray(snapshot.reviewerInboxes), "Missing per-reviewer inbox evidence");
  const inboxes = new Map();
  const unclaimed = new Map();
  const claimed = new Map();
  for (const inbox of snapshot.reviewerInboxes) {
    assert(inbox && typeof inbox.reviewerId === "string" &&
      config.reviewerIds.includes(inbox.reviewerId) && !inboxes.has(inbox.reviewerId),
      "Unexpected or duplicate reviewer inbox");
    assert(inbox.snapshotCommit === snapshot.ledgerHead,
      "Reviewer inbox ledger head mismatch: " + inbox.reviewerId);
    assert(Array.isArray(inbox.reviews), "Missing reviewer inbox reviews: " + inbox.reviewerId);
    const seen = new Set();
    for (const review of inbox.reviews) {
      assert(review && typeof review === "object" && !Array.isArray(review),
        "Invalid review in inbox: " + inbox.reviewerId);
      assert(typeof review.taskId === "string" && TASK_ID_PATTERN.test(review.taskId) &&
        (review.attemptId == null || (typeof review.attemptId === "string" &&
          REVIEW_ID_PATTERN.test(review.attemptId))) &&
        typeof review.reviewId === "string" && REVIEW_ID_PATTERN.test(review.reviewId) &&
        Number.isInteger(review.version) && review.version >= 1 &&
        Number.isSafeInteger(review.createdSequence) && review.createdSequence >= 1 &&
        ["Unclaimed", "Claimed"].includes(review.status),
        "Invalid canonical review identity: " + inbox.reviewerId);
      const key = reviewKey(review);
      assert(!seen.has(key), "Duplicate review in inbox: " + key);
      seen.add(key);
      if (review.status === "Unclaimed") {
        assert(review.reviewerId == null || review.reviewerId === "",
          "Unclaimed review has a reviewer identity: " + key);
        const fingerprint = JSON.stringify([review.attemptId ?? null, review.version,
          review.createdSequence ?? null]);
        assert(!unclaimed.has(key) || unclaimed.get(key) === fingerprint,
          "Inconsistent unclaimed review across reviewer inboxes: " + key);
        unclaimed.set(key, fingerprint);
      } else {
        assert(review.reviewerId === inbox.reviewerId,
          "Claimed review is not owned by the inbox reviewer: " + key);
        assert(!claimed.has(key) || claimed.get(key) === inbox.reviewerId,
          "Claimed review appears under multiple reviewers: " + key);
        claimed.set(key, inbox.reviewerId);
      }
    }
    inboxes.set(inbox.reviewerId, inbox.reviews);
  }
  assert(inboxes.size === config.reviewerIds.length, "Missing or extra reviewer inbox");
  for (const reviewerId of config.reviewerIds) {
    assert(inboxes.has(reviewerId), "Missing reviewer inbox: " + reviewerId);
  }
  for (const key of claimed.keys()) {
    assert(!unclaimed.has(key), "Review is both claimed and unclaimed: " + key);
  }
  return inboxes;
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
    if (agent.unassignedDisposition !== undefined && agent.unassignedDisposition !== null) {
      assert(UNASSIGNED_DISPOSITIONS.has(agent.unassignedDisposition),
        "Invalid canonical unassigned disposition: " + agent.agentId);
    }
    if (agent.kind === "reviewer" && agent.hasReadyAssignment) {
      assert(agent.unassignedDisposition == null,
        "Assigned reviewer cannot retain an unassigned disposition: " + agent.agentId);
    }
    if (agent.kind === "reviewer" && agent.execution === "Stopped" &&
      !agent.hasReadyAssignment && ["NeedsWork", "Free", "Parked"].includes(agent.state)) {
      assert(UNASSIGNED_DISPOSITIONS.has(agent.unassignedDisposition),
        "Missing canonical reviewer unassigned disposition: " + agent.agentId);
      assert(agent.state === "Parked"
        ? ["Parked", "TasklessYield"].includes(agent.unassignedDisposition)
        : agent.unassignedDisposition === "NeedsWork",
      "Reviewer state/disposition mismatch: " + agent.agentId);
    }
    if (agent.lastAttributedActivityAtUtc !== null) {
      validTime(agent.lastAttributedActivityAtUtc, "lastAttributedActivityAtUtc");
      assert(agent.activityEvidence === "ExplicitAttributedToolCall",
        "Client-local last-seen is not proof of agent activity");
    }
    if (agent.stateEnteredAtUtc !== undefined && agent.stateEnteredAtUtc !== null) {
      const enteredAt = validTime(agent.stateEnteredAtUtc, "stateEnteredAtUtc");
      assert(enteredAt <= observed + 1000,
        "stateEnteredAtUtc is after the authoritative snapshot");
    }
    // Unknown capacity classification must never free a worker/reviewer slot.
    assert(!(agent.kind === "other" &&
      (ACTIVE_STATES.has(agent.state) || agent.execution === "Working" ||
       agent.execution === "Unknown")),
      "Unclassified live or uncertain project agent: " + agent.agentId);
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
  const reviewerInboxes = validateReviewerInboxes(config, snapshot);
  return { agents, reviewerInboxes };
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
      [...LIVE_RECORDS, ...TERMINAL_RECORDS].includes(rec.status),
      "Invalid journal record");
    if (rec.review !== undefined && rec.review !== null) {
      assert(rec.kind === "reviewer" && rec.review &&
        typeof rec.review === "object" && !Array.isArray(rec.review) &&
        ["claimed", "unclaimed"].includes(rec.review.mode) &&
        typeof rec.review.taskId === "string" && TASK_ID_PATTERN.test(rec.review.taskId) &&
        (rec.review.attemptId == null || (typeof rec.review.attemptId === "string" &&
          REVIEW_ID_PATTERN.test(rec.review.attemptId))) &&
        typeof rec.review.reviewId === "string" && REVIEW_ID_PATTERN.test(rec.review.reviewId) &&
        Number.isInteger(rec.review.version) && rec.review.version >= 1 &&
        (rec.review.reviewerId == null ||
          (typeof rec.review.reviewerId === "string" &&
            (rec.review.mode === "claimed" ? rec.review.reviewerId === rec.agentId :
              rec.review.reviewerId === ""))),
        "Invalid journal review identity");
    }
    validTime(rec.reservedAtUtc, "journal reservation");
    if (rec.status === "observed") validTime(rec.observedAtUtc, "observedAtUtc");
    ids.add(rec.launchId);
  }
  return journal;
}

// State Ready/Stopped alone may be a stale pre-launch snapshot. The canonical
// state-entered time must be newer than the positively attributed launch
// activity: this proves a later yield/stop, even for the SAME assignment.
function yieldedAfterActivity(agent, activityAtUtc) {
  return agent?.state === "Ready" && agent.execution === "Stopped" &&
    agent.hasReadyAssignment === true &&
    typeof agent.stateEnteredAtUtc === "string" &&
    Date.parse(agent.stateEnteredAtUtc) > Date.parse(activityAtUtc);
}

function tasklessYieldedReviewer(agent) {
  return agent?.kind === "reviewer" && agent.state === "Parked" &&
    agent.execution === "Stopped" && agent.hasReadyAssignment === false &&
    agent.slotAvailable === true && agent.unassignedDisposition === "TasklessYield";
}

// Legacy observed/cleared reviewer records are not terminal execution proof.
// The old planner accepted actor-only activity and an arbitrary recovery reason.
// Only a blocked-before-send or explicitly recorded pre-send release is safe.
function reviewerReservationUnresolved(rec) {
  return rec.kind === "reviewer" &&
    !(rec.status === "blocked" && rec.dispatchedAtUtc == null) &&
    !(rec.status === "cleared" && rec.releasedFromStatus === "reserved" &&
      rec.dispatchedAtUtc == null);
}

export function decide(config, snapshot, journal, nowUtc, dryRun = false) {
  validateConfig(config);
  const { agents, reviewerInboxes } = validateSnapshot(config, snapshot, nowUtc);
  validateJournal(journal, config.projectName);
  const result = {
    projectName: config.projectName,
    ledgerHead: snapshot.ledgerHead,
    capturedAtUtc: snapshot.capturedAtUtc,
    enabled: config.enabled,
    counts: { worker: 0, reviewer: 0 },
    reservations: [],
    reviewHolds: [],
    unresolvedReviewerLaunches: [],
    suppressed: []
  };
  const reserved = new Set();
  const observedThisPass = new Set();
  const observedAssignments = new Set();

  // Latest agent-attributed activity is positive evidence of a bootstrapped
  // session. It may release a reservation; neither time elapsed nor a
  // successful UI submission is proof the agent started.
  for (const rec of journal.records) {
    if (rec.kind === "reviewer") {
      if (reviewerReservationUnresolved(rec)) {
        reserved.add(rec.agentId);
        result.counts.reviewer++;
        result.unresolvedReviewerLaunches.push({
          launchId: rec.launchId, agentId: rec.agentId, status: rec.status,
          review: rec.review ?? null, holdReason: "OriginalExecutionTerminationUnproven"
        });
      }
      continue;
    }
    if (rec.status === "observed" && rec.kind === "worker" &&
      rec.assignmentKey && agents.get(rec.agentId)?.assignmentKey === rec.assignmentKey &&
      !yieldedAfterActivity(agents.get(rec.agentId), rec.observedAtUtc)) {
      observedAssignments.add(rec.agentId);
    }
    if (!LIVE_RECORDS.has(rec.status)) continue;
    const agent = agents.get(rec.agentId);
    const activity = agent?.lastAttributedActivityAtUtc;
    const activityObserved = activity !== null && activity !== undefined &&
      Date.parse(activity) > Date.parse(rec.reservedAtUtc) &&
      Date.parse(activity) <= Date.parse(snapshot.capturedAtUtc);
    if (activityObserved) {
      if (!yieldedAfterActivity(agent, activity)) observedThisPass.add(rec.agentId);
      if (rec.assignmentKey && agent.assignmentKey === rec.assignmentKey &&
        !yieldedAfterActivity(agent, activity)) observedAssignments.add(rec.agentId);
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
  const visibleReviewKeys = new Set();
  for (const reviews of reviewerInboxes.values()) {
    for (const review of reviews) visibleReviewKeys.add(reviewKey(review));
  }
  const reservedReviewDemands = new Set();
  let unscopedReviewerReservation = false;
  for (const rec of journal.records) {
    if (rec.kind !== "reviewer") continue;
    if (!reviewerReservationUnresolved(rec)) continue;
    if (rec.review) {
      const key = reviewKey(rec.review);
      if (visibleReviewKeys.has(key)) reservedReviewDemands.add(key);
    } else {
      // Old journal records do not identify their review request. Keep an
      // uncertain/active legacy reviewer launch as a wildcard review hold.
      unscopedReviewerReservation = true;
    }
  }
  const addReservation = (agent, kind, reason, review = null) => {
    // No trusted provider execution binding exists. Expose exact review demand
    // without minting a dispatchable reservation or consuming real capacity.
    if (kind === "reviewer") {
      result.reviewHolds.push({ agentId: agent.agentId, kind, reason, review,
        holdReason: "ProviderExecutionIdentityUnbound" });
      reserved.add(agent.agentId);
      reservedReviewDemands.add(reviewKey(review));
      return;
    }
    const launchId = dryRun ? "dry-run-" + kind + "-" + agent.agentId : randomUUID();
    const reservation = { launchId, agentId: agent.agentId, kind, reason };
    if (review) reservation.review = review;
    result.reservations.push(reservation);
    result.counts[kind]++;
    reserved.add(agent.agentId);
    if (review) reservedReviewDemands.add(reviewKey(review));
    if (!dryRun) {
      const record = {
        launchId, agentId: agent.agentId, kind,
        reservedAtUtc: nowUtc,
        assignmentKey: kind === "worker" ? (agent.assignmentKey ?? null) : null,
        ledgerHead: snapshot.ledgerHead,
        status: "reserved"
      };
      if (review) record.review = review;
      journal.records.push(record);
    }
  };

  const workerOrder = [...config.workerIds].sort((a, b) =>
    (agents.get(a).priorityRank ?? 100000) - (agents.get(b).priorityRank ?? 100000));
  for (const agentId of workerOrder) {
    const agent = agents.get(agentId);
    let reason = null;
    if (result.reservations.length >= config.maxLaunchesPerTick) reason = "TickLimit";
    else if (result.counts.worker >= config.budgets.worker) reason = "BudgetFull";
    else if (reserved.has(agentId)) reason = "PriorLaunchPendingOrUncertain";
    else if (observedThisPass.has(agentId) || observedAssignments.has(agentId))
      reason = "AlreadyObservedThisAssignment";
    else if (agent.waitingOnChallenge) reason = "ChallengeBlocked";
    else if (!(agent.state === "Ready" && agent.execution === "Stopped" &&
      agent.hasReadyAssignment)) reason = "NoReadyAssignment";
    if (reason) {
      result.suppressed.push({ agentId, kind: "worker", reason });
      continue;
    }
    addReservation(agent, "worker", "ReadyImplementation");
  }

  // A claim is already owned by its reviewer. Resume that reviewer first;
  // never transfer or claim review work from the Manager session.
  for (const agentId of config.reviewerIds) {
    const agent = agents.get(agentId);
    const claimed = reviewerInboxes.get(agentId).filter(review => review.status === "Claimed");
    if (claimed.length === 0) continue;
    const resumableClaim = claimed.find(review => !reservedReviewDemands.has(reviewKey(review)));
    let reason = null;
    if (result.reservations.length + result.reviewHolds.length >= config.maxLaunchesPerTick) reason = "TickLimit";
    else if (result.counts.reviewer + result.reviewHolds.length >= config.budgets.reviewer) reason = "BudgetFull";
    else if (reserved.has(agentId)) reason = "PriorLaunchPendingOrUncertain";
    else if (observedThisPass.has(agentId)) reason = "AlreadyObservedThisPass";
    else if (agent.waitingOnChallenge) reason = "ChallengeBlocked";
    else if (unscopedReviewerReservation) reason = "PriorReviewerLaunchPendingOrActive";
    else if (!resumableClaim) reason = "PriorReviewLaunchPendingOrActive";
    else if (agent.state === "Parked" && agent.unassignedDisposition === "Parked")
      reason = "OperatorParked";
    else if (agent.execution !== "Stopped" ||
      !(REVIEWABLE_AGENT_STATES.has(agent.state) || tasklessYieldedReviewer(agent)))
      reason = "NoRunnableClaimedReview";
    if (reason) {
      result.suppressed.push({ agentId, kind: "reviewer", reason });
      continue;
    }
    addReservation(agent, "reviewer", "ClaimedReviewResume",
      reviewIdentity(resumableClaim, "claimed"));
  }

  // Unclaimed demand is visible only through each reviewer's canonical inbox.
  // Reserve its immutable identity across reviewers and across Manager ticks.
  for (const agentId of config.reviewerIds) {
    const agent = agents.get(agentId);
    const reviews = reviewerInboxes.get(agentId);
    const hasClaimedReview = reviews.some(review => review.status === "Claimed");
    const unclaimed = reviews.find(review => review.status === "Unclaimed" &&
      !reservedReviewDemands.has(reviewKey(review)));
    let reason = null;
    if (result.reservations.length + result.reviewHolds.length >= config.maxLaunchesPerTick) reason = "TickLimit";
    else if (result.counts.reviewer + result.reviewHolds.length >= config.budgets.reviewer) reason = "BudgetFull";
    else if (reserved.has(agentId)) reason = "PriorLaunchPendingOrUncertain";
    else if (observedThisPass.has(agentId)) reason = "AlreadyObservedThisPass";
    else if (agent.waitingOnChallenge) reason = "ChallengeBlocked";
    else if (hasClaimedReview) reason = "ClaimedReviewAlreadyOwned";
    else if (!unclaimed) reason = reviews.some(review => review.status === "Unclaimed")
      ? "ReviewAlreadyReserved" : "NoUnclaimedReviewForReviewer";
    else if (unscopedReviewerReservation) reason = "PriorReviewerLaunchPendingOrActive";
    else if (agent.state === "Parked" && agent.unassignedDisposition === "Parked")
      reason = "OperatorParked";
    else if (!((agent.state === "NeedsWork" || agent.state === "Free" ||
      tasklessYieldedReviewer(agent)) &&
      agent.execution === "Stopped" && agent.slotAvailable && !agent.hasReadyAssignment))
      reason = "NoAvailableReviewerSlot";
    if (reason) {
      result.suppressed.push({ agentId, kind: "reviewer", reason });
      continue;
    }
    addReservation(agent, "reviewer", "UnclaimedFrozenReview",
      reviewIdentity(unclaimed, "unclaimed"));
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
    assert(rec.kind !== "reviewer", "Provider execution identity unbound; reviewer dispatch held");
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
        assert(rec.kind !== "reviewer", "Provider execution identity unbound; reviewer dispatch held");
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
        assert(rec.kind !== "reviewer" || opts.result !== "blocked" || rec.dispatchedAtUtc == null,
          "Reviewer no-send proof conflicts with recorded dispatch");
        rec.status = opts.result;
        rec.reportedAtUtc ??= new Date().toISOString();
        result = { launchId: rec.launchId, status: rec.status };
      } else {
        assert(typeof opts.reason === "string" && opts.reason.trim().length >= 12,
          "Explicit operator recovery reason required");
        assert(LIVE_RECORDS.has(rec.status), "Only live reservations can be released");
        assert(rec.kind !== "reviewer" ||
          (rec.status === "reserved" && rec.dispatchedAtUtc == null),
          "Reviewer release requires exact trusted terminal no-start evidence; not available on this route");
        rec.releasedFromStatus = rec.status;
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
