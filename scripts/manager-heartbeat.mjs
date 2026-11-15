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
const TERMINAL_RECORDS = new Set(["blocked", "observed", "review-finished", "cleared"]);
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
    snapshot.reviewQueueVerified === true && snapshot.reviewClaimsVerified === true &&
    snapshot.challengesVerified === true,
    "Snapshot lacks authoritative task, activity, review or challenge evidence");
  assert(typeof snapshot.ledgerHead === "string" &&
    /^[0-9a-f]{40,64}$/.test(snapshot.ledgerHead), "Missing ledger head");
  const observed = validTime(snapshot.capturedAtUtc, "capturedAtUtc");
  const now = validTime(nowUtc, "nowUtc");
  assert(observed <= now + 1000 && now - observed <= 180000,
    "Snapshot is stale or from the future");
  assert(Number.isInteger(snapshot.unclaimedReviewCount) &&
    snapshot.unclaimedReviewCount >= 0, "Invalid review count");
  assert(Array.isArray(snapshot.agents) && Array.isArray(snapshot.claimedReviews),
    "Missing agents or canonical claimed-review enumeration");
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
      const attributedAt = validTime(agent.lastAttributedActivityAtUtc, "lastAttributedActivityAtUtc");
      assert(attributedAt <= observed + 1000,
        "Attributed activity is newer than the verified snapshot");
      assert(agent.activityEvidence === "ExplicitAttributedToolCall",
        "Client-local last-seen is not proof of agent activity");
    }
    if (agent.reviewerYieldAtUtc !== undefined && agent.reviewerYieldAtUtc !== null) {
      assert(agent.kind === "reviewer" &&
        agent.reviewerYieldEvidence === "ExplicitAttributedReviewerYield",
        "Reviewer yield requires positively attributed explicit yield evidence");
      assert(validTime(agent.reviewerYieldAtUtc, "reviewerYieldAtUtc") <= observed + 1000,
        "Reviewer yield is after the snapshot");
    }
    if (agent.claimActivityProof !== undefined) {
      const proof = agent.claimActivityProof;
      assert(agent.kind === "reviewer" && agent.lastAttributedActivityAtUtc &&
        proof?.kind === "PostActivityVerifiedReviewHead" &&
        proof.activityAtUtc === agent.lastAttributedActivityAtUtc &&
        proof.ledgerHead === snapshot.ledgerHead,
        "Claim/activity proof must bind this reviewer, activity and review head");
      const activityReadAt = validTime(proof.activityReadCompletedAtUtc,
        "activityReadCompletedAtUtc");
      const reviewReadAt = validTime(proof.reviewHeadReadStartedAtUtc,
        "reviewHeadReadStartedAtUtc");
      assert(Date.parse(proof.activityAtUtc) <= activityReadAt &&
        activityReadAt < reviewReadAt && reviewReadAt <= observed + 1000,
        "Claim/activity proof lacks a post-activity canonical review read");
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
  // Exhaustive claim enumeration must come from the same verified ledger head
  // as the review queue. A missing claim is not inferred from agent state.
  const reviewIds = new Set();
  for (const claim of snapshot.claimedReviews) {
    assert(claim && typeof claim.reviewId === "string" && claim.reviewId.trim() &&
      !reviewIds.has(claim.reviewId) && typeof claim.reviewerId === "string" &&
      agents.get(claim.reviewerId)?.kind === "reviewer",
      "Invalid, duplicate or unclassified canonical claimed review");
    reviewIds.add(claim.reviewId);
  }
  for (const agent of agents.values()) {
    if (agent.claimActivityProof !== undefined) {
      assert(snapshot.claimedReviews.some(claim => claim.reviewerId === agent.agentId),
        "Claim/activity proof requires a claim in the rechecked review head");
    }
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
      [...LIVE_RECORDS, ...TERMINAL_RECORDS].includes(rec.status),
      "Invalid journal record");
    validTime(rec.reservedAtUtc, "journal reservation");
    if (rec.status === "observed" || rec.status === "review-finished") {
      validTime(rec.observedAtUtc, "observedAtUtc");
    }
    if (rec.observedClaimIds !== undefined) {
      assert(rec.kind === "reviewer" && Array.isArray(rec.observedClaimIds) &&
        rec.observedClaimIds.every(x => typeof x === "string" && x.trim()) &&
        new Set(rec.observedClaimIds).size === rec.observedClaimIds.length &&
        (rec.observedClaimIds.length === 0 ||
          /^[0-9a-f]{40,64}$/.test(rec.claimSeenLedgerHead ?? "")),
        "Invalid observed reviewer claims");
    }
    if (rec.claimObservedActivityAtUtc !== undefined) {
      assert(rec.kind === "reviewer" && rec.observedClaimIds?.length > 0 &&
        /^[0-9a-f]{40,64}$/.test(rec.claimSeenLedgerHead ?? "") &&
        validTime(rec.claimObservedActivityAtUtc, "claimObservedActivityAtUtc") >=
        validTime(rec.observedAtUtc, "observedAtUtc"),
        "Invalid claim-present activity evidence");
    }
    if (rec.claimActivityProvenance !== undefined ||
        rec.claimActivityProofLedgerHead !== undefined) {
      assert(rec.claimActivityProvenance === "PostActivityVerifiedReviewHead" &&
        rec.claimObservedActivityAtUtc !== undefined &&
        /^[0-9a-f]{40,64}$/.test(rec.claimActivityProofLedgerHead ?? ""),
        "Invalid ordered claim/activity provenance");
    }
    if (rec.reviewerSessionCleared !== undefined) {
      assert(rec.reviewerSessionCleared === true && rec.kind === "reviewer" &&
        rec.status === "cleared" && typeof rec.observedAtUtc === "string" &&
        typeof rec.recoveryReason === "string" && rec.recoveryReason.length >= 12,
        "Invalid observed reviewer operator recovery");
      assert(validTime(rec.releasedAtUtc, "reviewer recovery time") >=
        validTime(rec.observedAtUtc, "observedAtUtc"),
        "Reviewer recovery cannot predate observed activity");
    }
    // Keep historical completion provenance valid after a later audited
    // operator release moves a finished reviewer record to cleared.
    if (rec.status === "review-finished" ||
        (rec.status === "cleared" && rec.completionEvidence !== undefined)) {
      assert(rec.kind === "reviewer" &&
        ["CanonicalReviewClaimResolved", "ExplicitReviewerYield"].includes(rec.completionEvidence) &&
        /^[0-9a-f]{40,64}$/.test(rec.completionLedgerHead ?? ""),
        "Missing reviewer completion provenance");
      validTime(rec.completedAtUtc, "completedAtUtc");
      if (rec.completionEvidence === "CanonicalReviewClaimResolved") {
        assert(rec.observedClaimIds?.length > 0 &&
          rec.claimSeenLedgerHead !== rec.completionLedgerHead,
          "Reviewer claim resolution requires a later canonical ledger head");
      }
    }
    if (rec.explicitYieldAtUtc !== undefined) {
      assert((rec.status === "review-finished" ||
        (rec.status === "cleared" && rec.reviewerSessionCleared === true)) &&
        rec.completionEvidence === "ExplicitReviewerYield" &&
        validTime(rec.explicitYieldAtUtc, "explicitYieldAtUtc") >=
        validTime(rec.observedAtUtc, "observedAtUtc") &&
        Date.parse(rec.explicitYieldAtUtc) <= Date.parse(rec.completedAtUtc),
        "Invalid recorded explicit reviewer yield");
    }
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


function reviewerSessionEnd(rec, agent, currentClaims, snapshot) {
  const yieldAt = agent?.reviewerYieldAtUtc;
  const latestActivity = agent?.lastAttributedActivityAtUtc;
  // Taskless reviewers do not have an implementation Ready transition.
  // Only a separately verified, explicit reviewer yield after THIS launch
  // can resume a still-claimed review. A generic tool call is not a yield.
  if (yieldAt && Date.parse(yieldAt) > Date.parse(rec.reservedAtUtc) &&
      Date.parse(yieldAt) >= Date.parse(rec.observedAtUtc) &&
      (latestActivity == null || Date.parse(latestActivity) <= Date.parse(yieldAt))) {
    return "ExplicitReviewerYield";
  }
  // A claim observed in an earlier authoritative head and now absent from
  // an exhaustive later head was decided/released. Missing old journal
  // evidence or equal-head contradictory claims cannot prove completion.
  if (rec.observedClaimIds?.length && currentClaims.length === 0 &&
      snapshot.ledgerHead !== rec.claimSeenLedgerHead) {
    return "CanonicalReviewClaimResolved";
  }
  return null;
}

// An explicit reviewer yield or operator recovery is evidence at a point in
// time, not a permanent lease release. Activity after that boundary means the
// previous browser session might have resumed while its claim is still active.
function reviewerPauseStillProven(rec, agent, snapshot, ended) {
  const activityAt = agent?.lastAttributedActivityAtUtc == null ? -Infinity :
    Date.parse(agent.lastAttributedActivityAtUtc);
  const capturedAt = Date.parse(snapshot.capturedAtUtc);
  if (ended === "ExplicitReviewerYield" || rec.reviewerSessionCleared === true) {
    // Preserve the attributed yield time over journal restarts. A legacy
    // terminal record without this field needs fresh canonical yield proof;
    // completedAtUtc is not the same as the actual yield boundary.
    const yieldTimes = [rec.explicitYieldAtUtc, agent?.reviewerYieldAtUtc]
      .filter(time => typeof time === "string")
      .map(time => Date.parse(time))
      .filter(time => time >= Date.parse(rec.observedAtUtc) && time <= capturedAt);
    if (yieldTimes.some(time => activityAt <= time)) return true;
  }
  if (rec.reviewerSessionCleared === true) {
    const clearedAt = Date.parse(rec.releasedAtUtc);
    return clearedAt <= capturedAt && activityAt <= clearedAt;
  }
  return false;
}

// Terminal labels prove that a reviewer session ended only at a point in time.
// Even with zero claims, a later attributed call can mean the old tab resumed.
function reviewerSessionResumedAfterEnd(rec, agent, snapshot) {
  if (rec.status !== "review-finished" && rec.reviewerSessionCleared !== true) return false;
  // Missing/regressed attribution cannot prove the old browser session ended.
  if (agent?.lastAttributedActivityAtUtc == null) return true;
  const activityAt = Date.parse(agent.lastAttributedActivityAtUtc);
  const observedAt = Date.parse(rec.observedAtUtc);
  const capturedAt = Date.parse(snapshot.capturedAtUtc);
  const orderedClaimActivity = rec.claimActivityProvenance === "PostActivityVerifiedReviewHead" ?
    rec.claimObservedActivityAtUtc : null;
  // A previously read claim head plus a newer activity sample is not ordered
  // evidence: the tool call might be AFTER the canonical claim resolution.
  // Old journals without this proof remain guarded until an actual later
  // reviewer yield or an operator-confirmed tab closure.
  if (rec.completionEvidence === "CanonicalReviewClaimResolved" && !orderedClaimActivity) {
    const laterEnds = [agent.reviewerYieldAtUtc,
      rec.reviewerSessionCleared === true ? rec.releasedAtUtc : null];
    return !laterEnds.some(time => typeof time === "string" &&
      Date.parse(time) >= observedAt && Date.parse(time) >= activityAt &&
      Date.parse(time) <= capturedAt);
  }
  if (activityAt < Date.parse(orderedClaimActivity ?? rec.observedAtUtc)) return true;
  if (activityAt <= observedAt) return false;
  const endTimes = [];
  if (rec.status === "review-finished") {
    if (rec.completionEvidence === "ExplicitReviewerYield") endTimes.push(rec.explicitYieldAtUtc);
    else if (rec.completionEvidence === "CanonicalReviewClaimResolved") {
      // Claim disappearance is known only between ledger snapshots; the
      // Manager's completedAtUtc is not an authoritative decision timestamp.
      // Activity newly seen during that interval may be post-resolution.
      // Only a claim head explicitly re-read after this tool call can bound
      // the later canonical decision against old session activity.
      endTimes.push(orderedClaimActivity);
    }
  }
  if (rec.reviewerSessionCleared === true) endTimes.push(rec.releasedAtUtc);
  endTimes.push(agent.reviewerYieldAtUtc);
  // A later explicit yield can end resumed activity. Missing or old evidence
  // never establishes an end merely because the agent reports Stopped.
  return !endTimes.some(time => typeof time === "string" &&
    Date.parse(time) >= observedAt && Date.parse(time) <= capturedAt &&
    Date.parse(time) >= activityAt);
}

export function decide(config, snapshot, journal, nowUtc, dryRun = false) {
  validateConfig(config);
  const agents = validateSnapshot(config, snapshot, nowUtc);
  validateJournal(journal, config.projectName);
  // Both modes evaluate identical transitions; dry-run uses private records.
  // Only persistence and real reservation ID allocation are conditional.
  const records = dryRun ? journal.records.map(rec => ({ ...rec })) : journal.records;
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
  const unresolvedReviewers = new Set();
  const reviewerSessionEnds = new Map();
  const claimsByReviewer = new Map();
  for (const claim of snapshot.claimedReviews) {
    if (!claimsByReviewer.has(claim.reviewerId)) claimsByReviewer.set(claim.reviewerId, []);
    claimsByReviewer.get(claim.reviewerId).push(claim.reviewId);
  }
  const latestReviewerRecord = new Map();
  for (const rec of records) {
    if (rec.kind === "reviewer") latestReviewerRecord.set(rec.agentId, rec);
  }

  // A reviewer may be NeedsWork/Stopped while working on a frozen claim.
  // Keep observed reviewer sessions and their capacity until a positive
  // review resolution or explicitly attributed cooperative yield.
  for (const rec of records) {
    const agent = agents.get(rec.agentId);
    if (LIVE_RECORDS.has(rec.status) || rec.status === "observed") {
      assert(agent?.kind === rec.kind, "Unknown journal owner/kind: " + rec.agentId);
    }
    const claims = claimsByReviewer.get(rec.agentId) ?? [];
    if (rec.kind === "reviewer" && rec.status === "observed") {
      const updated = {
        ...rec,
        observedClaimIds: [...new Set([...(rec.observedClaimIds ?? []), ...claims])],
        claimSeenLedgerHead: rec.claimSeenLedgerHead ?? (claims.length ? snapshot.ledgerHead : undefined)
      };
      // An independently read claim head and activity timestamp have no
      // causal ordering. Only a fresh head re-read after that activity counts.
      const claimActivity = agent?.lastAttributedActivityAtUtc;
      if (claims.length && claimActivity && agent.claimActivityProof &&
          (updated.claimActivityProvenance !== "PostActivityVerifiedReviewHead" ||
            Date.parse(claimActivity) > Date.parse(updated.claimObservedActivityAtUtc))) {
        updated.claimObservedActivityAtUtc = claimActivity;
        updated.claimActivityProvenance = "PostActivityVerifiedReviewHead";
        updated.claimActivityProofLedgerHead = snapshot.ledgerHead;
      }
      const ended = reviewerSessionEnd(updated, agent, claims, snapshot);
      if (ended) {
        reviewerSessionEnds.set(rec.launchId, ended);
        // Make dry-run and normal planning enforce the same session boundary,
        // including the first snapshot observing claim disappearance.
        const completed = { ...updated, status: "review-finished",
          completionEvidence: ended,
          explicitYieldAtUtc: ended === "ExplicitReviewerYield" ? agent.reviewerYieldAtUtc : undefined };
        if (reviewerSessionResumedAfterEnd(completed, agent, snapshot))
          unresolvedReviewers.add(rec.agentId);
      } else unresolvedReviewers.add(rec.agentId);
      rec.observedClaimIds = updated.observedClaimIds;
      if (updated.claimSeenLedgerHead) rec.claimSeenLedgerHead = updated.claimSeenLedgerHead;
      if (updated.claimObservedActivityAtUtc)
        rec.claimObservedActivityAtUtc = updated.claimObservedActivityAtUtc;
      if (updated.claimActivityProvenance)
        rec.claimActivityProvenance = updated.claimActivityProvenance;
      if (updated.claimActivityProofLedgerHead)
        rec.claimActivityProofLedgerHead = updated.claimActivityProofLedgerHead;
      if (ended) {
        rec.status = "review-finished";
        rec.completionEvidence = ended;
        rec.completionLedgerHead = snapshot.ledgerHead;
        rec.completedAtUtc = snapshot.capturedAtUtc;
        if (ended === "ExplicitReviewerYield") rec.explicitYieldAtUtc = agent.reviewerYieldAtUtc;
      }
      continue;
    }
    if (rec.status === "observed" && rec.kind === "worker" &&
      rec.assignmentKey && agent?.assignmentKey === rec.assignmentKey &&
      !yieldedAfterActivity(agent, rec.observedAtUtc)) {
      observedAssignments.add(rec.agentId);
    }
    if (!LIVE_RECORDS.has(rec.status)) continue;
    const activity = agent?.lastAttributedActivityAtUtc;
    const observed = activity != null &&
      Date.parse(activity) > Date.parse(rec.reservedAtUtc) &&
      Date.parse(activity) <= Date.parse(snapshot.capturedAtUtc);
    if (observed) {
      if (rec.kind === "reviewer") {
        const updated = {
          ...rec, observedAtUtc: activity,
          observedClaimIds: claims,
          claimSeenLedgerHead: claims.length ? snapshot.ledgerHead : undefined
        };
        const ended = reviewerSessionEnd(updated, agent, claims, snapshot);
        if (ended) reviewerSessionEnds.set(rec.launchId, ended);
        else {
          unresolvedReviewers.add(rec.agentId);
          observedThisPass.add(rec.agentId);
        }
        rec.observedClaimIds = claims;
        if (claims.length) rec.claimSeenLedgerHead = snapshot.ledgerHead;
        if (claims.length && agent.claimActivityProof) {
          rec.claimObservedActivityAtUtc = activity;
          rec.claimActivityProvenance = "PostActivityVerifiedReviewHead";
          rec.claimActivityProofLedgerHead = snapshot.ledgerHead;
        }
        rec.status = ended ? "review-finished" : "observed";
        rec.observedAtUtc = activity;
        if (ended) {
          rec.completionEvidence = ended;
          rec.completionLedgerHead = snapshot.ledgerHead;
          rec.completedAtUtc = snapshot.capturedAtUtc;
          if (ended === "ExplicitReviewerYield") rec.explicitYieldAtUtc = agent.reviewerYieldAtUtc;
        }
      } else {
        if (!yieldedAfterActivity(agent, activity)) observedThisPass.add(rec.agentId);
        if (rec.assignmentKey && agent.assignmentKey === rec.assignmentKey &&
          !yieldedAfterActivity(agent, activity)) observedAssignments.add(rec.agentId);
        rec.status = "observed";
        rec.observedAtUtc = activity;
      }
      continue;
    }
    reserved.add(rec.agentId);
    result.counts[rec.kind]++;
  }
  // Only the latest explicit yield can pause a claimed review. A later
  // unresolved launch or any non-yield completion invalidates that pause.
  const pausedClaimReviewers = new Set();
  for (const [reviewerId, rec] of latestReviewerRecord) {
    // Session-level uncertainty is independent of the current review queue.
    if (reviewerSessionResumedAfterEnd(rec, agents.get(reviewerId), snapshot))
      unresolvedReviewers.add(reviewerId);
    const ended = reviewerSessionEnds.get(rec.launchId) ??
      (rec.status === "review-finished" ? rec.completionEvidence : null);
    const claims = claimsByReviewer.get(reviewerId) ?? [];
    if (reviewerPauseStillProven(rec, agents.get(reviewerId), snapshot, ended) &&
        claims.every(id => (rec.observedClaimIds ?? []).includes(id)) &&
        !unresolvedReviewers.has(reviewerId) && !reserved.has(reviewerId)) {
      pausedClaimReviewers.add(reviewerId);
    }
  }
  // Count both canonical claims and unresolved taskless sessions, including
  // reviewers whose task slot looks available. Never count the same agent
  // twice, and never classify paused, explicitly yielded claims as active.
  for (const agent of agents.values()) {
    if (!["worker", "reviewer"].includes(agent.kind)) continue;
    const active = ACTIVE_STATES.has(agent.state) ||
      agent.execution === "Working" || agent.execution === "Unknown" ||
      (agent.kind === "reviewer" &&
        (unresolvedReviewers.has(agent.agentId) ||
        ((claimsByReviewer.get(agent.agentId)?.length ?? 0) > 0 &&
          !pausedClaimReviewers.has(agent.agentId))));
    if (active && !reserved.has(agent.agentId)) {
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
      else if (kind === "reviewer" && (unresolvedReviewers.has(agentId) ||
        ((claimsByReviewer.get(agentId)?.length ?? 0) > 0 &&
          !pausedClaimReviewers.has(agentId)))) reason = "ReviewerSessionOrClaimActive";
      else if (agent.waitingOnChallenge) reason = "ChallengeBlocked";
      else if (kind === "worker" &&
        !(agent.state === "Ready" && agent.execution === "Stopped" &&
          agent.hasReadyAssignment)) reason = "NoReadyAssignment";
      else if (kind === "reviewer" &&
        !((agent.state === "NeedsWork" || agent.state === "Free") &&
        agent.execution === "Stopped" && agent.slotAvailable &&
        !agent.hasReadyAssignment &&
        (remainingReviews > 0 ||
          (pausedClaimReviewers.has(agentId) &&
            (claimsByReviewer.get(agentId)?.length ?? 0) > 0))))
        reason = "NoUnclaimedReviewOrReviewerBusy";
      if (reason) {
        result.suppressed.push({ agentId, kind, reason });
        continue;
      }
      const launchId = dryRun ? "dry-run-" + kind + "-" + agentId :
        randomUUID();
      result.reservations.push({
        launchId, agentId, kind,
        reason: kind === "worker" ? "ReadyImplementation" :
          pausedClaimReviewers.has(agentId) && (claimsByReviewer.get(agentId)?.length ?? 0) > 0 ?
            "ResumeVerifiedPausedReview" : "UnclaimedFrozenReview"
      });
      result.counts[kind]++;
      reserved.add(agentId);
      if (kind === "reviewer" && result.reservations.at(-1).reason === "UnclaimedFrozenReview")
        remainingReviews--;
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
        assert(LIVE_RECORDS.has(rec.status) ||
          (rec.kind === "reviewer" &&
            (rec.status === "observed" || rec.status === "review-finished")),
          "Only live reservations or observed/finished reviewer sessions can be released");
        if (rec.kind === "reviewer" &&
            (rec.status === "observed" || rec.status === "review-finished")) {
          rec.reviewerSessionCleared = true;
        }
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
