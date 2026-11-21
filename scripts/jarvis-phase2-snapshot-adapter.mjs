/**
 * Phase-2 read-only preparation. No Scheduler, launch, reservation or browser effect.
 * Git is read from disposable repositories at exact fetched commits, not stale
 * worktree files or last-seen caches. Capacity remains UNKNOWN until a distinct,
 * authenticated account-wide registry can prove live worker/reviewer occupancy.
 */
import { execFileSync } from "node:child_process";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const PROJECT_REF = "refs/heads/jarvis-orchestration";
const ACCOUNT_REF = "refs/heads/jarvis-account-admission";
const SHA = /^[a-f0-9]{40}(?:[a-f0-9]{24})?$/;
const REF = /^refs\/heads\/[A-Za-z0-9][A-Za-z0-9_.\/-]*$/;
const WAIT_STATUS = new Map([["Waiting", "Pending"], ["Eligible", "Eligible"], ["Requeued", "Requeued"]]);
const MAX_RECORDS = 40000;
const MAX_BLOB_BYTES = 4 * 1024 * 1024;

export class CanonicalSnapshotError extends Error {
  constructor(reason) {
    super(reason);
    this.name = "CanonicalSnapshotError";
  }
}

function fail(reason) { throw new CanonicalSnapshotError(reason); }
function validString(x) { return typeof x === "string" && x.length > 0; }
function validPositive(x) { return Number.isSafeInteger(x) && x > 0; }
function validTime(x) { return typeof x === "string" && /(?:Z|[+-]\d\d:\d\d)$/.test(x) && Number.isFinite(Date.parse(x)); }
function issue(issues, scope, reason, path) { issues.push({ scope, reason, path }); }
function identifier(x) { return typeof x === "string" && /^[A-Za-z0-9_-]{1,100}$/.test(x); }
function encodedAgent(id) { return Buffer.from(id, "utf8").toString("base64url"); }
function readRecord(records, path, schema, issues, scope) {
  const raw = records.get(path);
  if (raw === undefined) {
    issue(issues, scope, "MissingCanonicalRecord", path);
    return null;
  }
  if (raw === null || typeof raw !== "object" || Array.isArray(raw) || raw.schemaVersion !== schema) {
    issue(issues, scope, "InvalidCanonicalSchema", path);
    return null;
  }
  return raw;
}
function listPaths(records, regex) { return [...records.keys()].filter(p => regex.test(p)).sort(); }
function unknownCapacity(atUtc) {
  // Null means unobserved. [] would incorrectly assert that no sessions exist.
  return { complete: false, observedAtUtc: atUtc, configuredLimits: null, sessions: null, reservations: null };
}
function sourceInfo(view) {
  return { commit: view.head, ref: view.ref, observedAtUtc: view.observedAtUtc };
}
function projectCompleteness(issues) {
  return Object.fromEntries(["tasks", "agents", "sourceLeases", "reviews", "challenges", "submissions", "capacity"]
    .map(name => [name, name !== "capacity" && !issues.some(x => x.scope === name)]));
}
function currentAttempt(tasks, taskId, attemptId) {
  const task = tasks.find(t => t.taskId === taskId);
  return task?.attempts.find(a => a.attemptId === attemptId) ?? null;
}
function normalizeProject(view, diagnostics) {
  const r = view.records;
  const issues = [];
  const taskPaths = listPaths(r, /^tasks\/[A-Za-z0-9_-]+\/task\.json$/);
  const attemptPaths = listPaths(r, /^tasks\/[A-Za-z0-9_-]+\/attempts\/[a-f0-9]{32}\.json$/);
  const tasks = [];
  const attemptsSeen = new Set();
  for (const path of taskPaths) {
    const t = readRecord(r, path, 2, issues, "tasks");
    const match = /^tasks\/([^/]+)\/task\.json$/.exec(path);
    if (!t || t.taskId !== match[1] || !validPositive(t.version) ||
        !["Open", "InProgress", "Implemented", "Integrated", "Closed", "Cancelled"].includes(t.lifecycle)) {
      issue(issues, "tasks", "InvalidTaskIdentity", path);
      continue;
    }
    const attempts = [];
    for (const ap of attemptPaths.filter(p => p.startsWith("tasks/" + t.taskId + "/attempts/"))) {
      attemptsSeen.add(ap);
      const a = readRecord(r, ap, 3, issues, "tasks");
      const id = /^tasks\/[^/]+\/attempts\/([a-f0-9]{32})\.json$/.exec(ap)[1];
      if (!a || a.taskId !== t.taskId || a.attemptId !== id ||
          !validPositive(a.version) || !validPositive(a.assignmentGeneration) ||
          !validString(a.ownerAgentId) ||
          !["Queued", "Started", "Finished", "Deferred", "Blocked", "Superseded", "Abandoned"].includes(a.phase)) {
        issue(issues, "tasks", "InvalidAttemptIdentity", ap);
        continue;
      }
      attempts.push({
        attemptId: id, phase: a.phase === "Queued" ? "Ready" : a.phase,
        version: a.version, assignmentGeneration: a.assignmentGeneration,
        ownerAgentId: a.ownerAgentId, priorityRank: Number.isSafeInteger(a.priority) ? -a.priority : null,
        canonicalPhase: a.phase, canonicalPath: ap
      });
    }
    tasks.push({
      taskId: t.taskId, lifecycle: t.lifecycle, version: t.version,
      currentAttemptId: validString(t.attemptId) ? t.attemptId : null,
      attempts
    });
    if (validString(t.attemptId) && !attempts.some(a => a.attemptId === t.attemptId)) {
      issue(issues, "tasks", "CurrentAttemptMissing", path);
    }
  }
  for (const ap of attemptPaths) {
    if (!attemptsSeen.has(ap)) issue(issues, "tasks", "OrphanAttempt", ap);
  }

  const agents = [];
  const agentPaths = listPaths(r, /^agents\/[A-Za-z0-9_-]+\.json$/);
  for (const path of agentPaths) {
    const a = readRecord(r, path, 3, issues, "agents");
    if (!a || !validString(a.agentId) || path !== "agents/" + encodedAgent(a.agentId) + ".json" ||
        !validPositive(a.version) || !["Working", "Stopped"].includes(a.execution)) {
      issue(issues, "agents", "InvalidAgentIdentityOrExecution", path);
      continue;
    }
    const assigned = a.currentAssignment !== null && a.currentAssignment !== undefined;
    let assignment = null;
    if (assigned) {
      const { taskId, attemptId } = a.currentAssignment;
      const attempt = currentAttempt(tasks, taskId, attemptId);
      if (!attempt || attempt.ownerAgentId !== a.agentId ||
          !tasks.some(t => t.taskId === taskId && t.currentAttemptId === attemptId)) {
        issue(issues, "agents", "AssignmentMissingOrStale", path);
      } else {
        const issuePath = "tasks/" + taskId + "/attempts/" + attemptId +
          "/assignments/" + attempt.assignmentGeneration + ".json";
        const issued = readRecord(r, issuePath, 2, issues, "agents");
        if (issued && issued.taskId === taskId && issued.attemptId === attemptId &&
            issued.ownerAgentId === a.agentId && issued.generation === attempt.assignmentGeneration &&
            validPositive(issued.issuedSequence)) {
          assignment = { taskId, attemptId, assignmentGeneration: issued.generation };
        } else {
          issue(issues, "agents", "AssignmentIssueMismatch", issuePath);
        }
      }
    }
    const state = a.execution === "Working" ? "Working" :
      assigned ? "Ready" : a.unassignedDisposition === "Parked" ? "Parked" : "NeedsWork";
    if ((assigned && a.unassignedDisposition != null) ||
        (!assigned && a.execution === "Stopped" &&
         !["Parked", "NeedsWork"].includes(a.unassignedDisposition)) ||
        (!assigned && a.execution === "Working" && a.unassignedDisposition != null)) {
      issue(issues, "agents", "ContradictoryAgentDisposition", path);
    }
    agents.push({
      agentId: a.agentId, verified: true, version: a.version, state, execution: a.execution,
      currentAssignment: assignment, unassignedDisposition: a.unassignedDisposition ?? null,
      activeSessionId: a.activeSessionId ?? null, associatedSessionIds: a.associatedSessionIds ?? []
    });
  }
  // A missing agent record is not equivalent to an idle owner.
  for (const task of tasks) {
    for (const attempt of task.attempts) {
      if (!agents.some(a => a.agentId === attempt.ownerAgentId))
        issue(issues, "agents", "AttemptOwnerMissing", attempt.canonicalPath);
    }
  }

  const sourceLeases = [];
  for (const path of attemptPaths) {
    const a = r.get(path);
    if (!a || typeof a !== "object" || !a.sourceRef && !a.sourceRepositoryId) continue;
    const id = /^tasks\/([^/]+)\/attempts\/([a-f0-9]{32})\.json$/.exec(path);
    if (!a.sourceRepositoryId || !a.sourceRef || !a.targetRef || !SHA.test(a.baseCommit ?? "") ||
        !REF.test(a.sourceRef) || !REF.test(a.targetRef) || a.sourceRef === a.targetRef ||
        !validString(a.sourceRemote)) {
      issue(issues, "sourceLeases", "IncompleteCanonicalSourceBinding", path);
      continue;
    }
    sourceLeases.push({
      taskId: id[1], attemptId: id[2], ownerAgentId: a.ownerAgentId,
      assignmentGeneration: a.assignmentGeneration,
      repositoryId: a.sourceRepositoryId, sourceRef: a.sourceRef,
      targetRef: a.targetRef, baseCommit: a.baseCommit,
      // Git-ledger binding is real, but source ref / physical checkout is NOT verified by this reader.
      status: "Unverified", sourceRemote: a.sourceRemote
    });
  }

  const reviews = [];
  for (const path of listPaths(r, /^tasks\/[^/]+\/reviews\/[a-f0-9]{32}\/review\.json$/)) {
    const a = readRecord(r, path, 3, issues, "reviews");
    const m = /^tasks\/([^/]+)\/reviews\/([a-f0-9]{32})\/review\.json$/.exec(path);
    if (!a || a.taskId !== m[1] || a.reviewId !== m[2] ||
        !validPositive(a.version) ||
        !["Unclaimed", "Claimed", "Accepted", "ChangesRequested", "Withdrawn"].includes(a.status) ||
        !tasks.some(t => t.taskId === a.taskId)) {
      issue(issues, "reviews", "ReviewIdentityMismatch", path);
      continue;
    }
    const pending = a.status === "Unclaimed";
    if (pending && a.reviewerId) issue(issues, "reviews", "UnclaimedReviewerIdentityConflict", path);
    if (a.status === "Claimed" && !validString(a.reviewerId)) {
      issue(issues, "reviews", "ClaimedReviewMissingReviewer", path);
    }
    reviews.push({
      reviewId: a.reviewId, taskId: a.taskId, attemptId: a.attemptId,
      version: a.version, status: pending ? "Pending" : a.status,
      claim: pending ? null : (a.status === "Claimed" ? { reviewerId: a.reviewerId ?? null } : null),
      // This ledger holds a review-ref CLAIM; the exact code-repository ref must be verified independently.
      frozen: false, reviewRef: a.reviewRef ?? null, sourceRef: a.taskRef ?? null,
      targetRef: a.targetRef ?? null, reviewedHeadOid: a.reviewedHeadOid ?? null,
      createdSequence: a.createdSequence
    });
  }

  const challenges = [];
  const waits = listPaths(r, /^tasks\/[^/]+\/attempts\/[a-f0-9]{32}\/challenge-wait\.json$/);
  for (const path of waits) {
    const wait = readRecord(r, path, 2, issues, "challenges");
    const m = /^tasks\/([^/]+)\/attempts\/([a-f0-9]{32})\/challenge-wait\.json$/.exec(path);
    const attempt = currentAttempt(tasks, m[1], m[2]);
    if (!wait || wait.taskId !== m[1] || wait.attemptId !== m[2] ||
        !validString(wait.ownerAgentId) || !Array.isArray(wait.challengeIds) ||
        !WAIT_STATUS.has(wait.status) || !attempt || wait.ownerAgentId !== attempt.ownerAgentId) {
      issue(issues, "challenges", "InvalidWaitOrAttempt", path);
      continue;
    }
    if ((wait.status !== "Requeued" && attempt.phase !== "Blocked") ||
        (wait.status === "Requeued" && !["Ready", "Started"].includes(attempt.phase))) {
      issue(issues, "challenges", "WaitAttemptPhaseConflict", path);
    }
    const linkedIds = new Set();
    const dependencies = [];
    for (const id of wait.challengeIds) {
      if (!/^[a-f0-9]{32}$/.test(id) || linkedIds.has(id)) {
        issue(issues, "challenges", "InvalidOrDuplicateDependency", path);
        continue;
      }
      linkedIds.add(id);
      const challengePath = "challenges/" + id + "/challenge.json";
      const c = readRecord(r, challengePath, 2, issues, "challenges");
      if (!c || c.challengeId !== id || !["Pending", "Blocked", "Satisfied"].includes(c.status)) {
        issue(issues, "challenges", "DependencyUnproven", challengePath);
        continue;
      }
      dependencies.push({ dependencyId: id, status: c.status === "Satisfied" ? "Satisfied" : "Pending" });
    }

    const requeueEvents = listPaths(r, new RegExp("^tasks/" + m[1] + "/lifecycle/[0-9]+\\.json$"))
      .map(p => ({ path: p, data: r.get(p) }))
      .filter(x => x.data?.action === "RequeueChallengeBlockedAttempt" &&
        x.data?.attemptId === m[2] && x.data?.newGeneration === attempt.assignmentGeneration);
    let requeueProvenance = null;
    if (wait.status === "Requeued") {
      if (requeueEvents.length !== 1 || !validTime(wait.requeuedAtUtc)) {
        issue(issues, "challenges", "RequeueAuditUnproven", path);
      } else {
        const e = requeueEvents[0].data;
        if (e.schemaVersion !== 2 || e.taskId !== m[1] ||
            requeueEvents[0].path !== "tasks/" + m[1] + "/lifecycle/" + e.sequence + ".json" ||
            e.previousGeneration + 1 !== e.newGeneration ||
            e.previousOwnerId !== wait.ownerAgentId || e.newOwnerId !== wait.ownerAgentId ||
            e.previousPhase !== "Blocked" || e.newPhase !== "Queued" ||
            (wait.challengeIds.length === 0 && e.actorId !== wait.ownerAgentId) ||
            !validPositive(e.sequence) || !validString(e.operationId)) {
          issue(issues, "challenges", "RequeueAuditMismatch", requeueEvents[0].path);
        } else {
          requeueProvenance = {
            operationId: e.operationId, ledgerSequence: e.sequence, actor: e.actorId,
            atUtc: wait.requeuedAtUtc, action: e.action,
            previousPhase: e.previousPhase, newPhase: e.newPhase,
            previousOwnerAgentId: e.previousOwnerId, newOwnerAgentId: e.newOwnerId,
            previousGeneration: e.previousGeneration, newGeneration: e.newGeneration
          };
        }
      }
    }

    let indexGeneration = null;
    if (linkedIds.size > 0) {
      const generation = wait.status === "Requeued"
        ? requeueProvenance?.previousGeneration : attempt.assignmentGeneration;
      if (!validPositive(generation)) issue(issues, "challenges", "WaitIndexGenerationUnknown", path);
      else {
        const base = m[1] + "/" + m[2] + "/" + generation + ".json";
        const indexFiles = [
          "challenge-waits/by-owner/" + encodedAgent(wait.ownerAgentId) + "/" + base
        ];
        for (const id of linkedIds) indexFiles.push("challenge-waits/by-challenge/" + id + "/" + base);
        for (const p of indexFiles) {
          const i = readRecord(r, p, 2, issues, "challenges");
          if (!i || i.taskId !== wait.taskId || i.attemptId !== wait.attemptId ||
              i.ownerAgentId !== wait.ownerAgentId || i.assignmentGeneration !== generation) {
            issue(issues, "challenges", "WaitIndexMismatch", p);
          }
        }
        indexGeneration = generation;
      }
    } else {
      // Historical reverse-index entries from earlier waits are allowed, not one
      // at the immutable generation of this manual block.
      const manualGeneration = wait.status === "Requeued"
        ? requeueProvenance?.previousGeneration : attempt.assignmentGeneration;
      const currentManualIndex = "challenge-waits/by-owner/" +
        encodedAgent(wait.ownerAgentId) + "/" + m[1] + "/" + m[2] +
        "/" + manualGeneration + ".json";
      if (validPositive(manualGeneration) && r.has(currentManualIndex)) {
        issue(issues, "challenges", "ManualWaitHasReverseIndex", currentManualIndex);
      }
    }
    challenges.push({
      waitId: m[1] + ":" + m[2], taskId: m[1], attemptId: m[2],
      ownerAgentId: wait.ownerAgentId, status: WAIT_STATUS.get(wait.status),
      waitIndexGeneration: indexGeneration, dependencies,
      ownerReleaseProvenance: null, requeueProvenance,
      canonicalPath: path
    });
  }
  for (const path of listPaths(r, /^challenge-waits\/by-(?:owner|challenge)\//)) {
    const value = r.get(path);
    if (typeof value !== "object" || value === null || value.schemaVersion !== 2) {
      issue(issues, "challenges", "MalformedReverseIndex", path);
    }
  }

  const submissions = [];
  for (const path of listPaths(r, /^tasks\/[^/]+\/integrations\/[a-f0-9]{32}\.json$/)) {
    const s = readRecord(r, path, 2, issues, "submissions");
    const m = /^tasks\/([^/]+)\/integrations\/([a-f0-9]{32})\.json$/.exec(path);
    if (!s || s.taskId !== m[1] || s.submissionId !== m[2] ||
        !["Pending", "Integrated"].includes(s.status)) {
      issue(issues, "submissions", "SubmissionIdentityMismatch", path);
      continue;
    }
    const review = reviews.find(x => x.reviewId === s.reviewId && x.taskId === s.taskId);
    submissions.push({
      submissionId: s.submissionId, taskId: s.taskId,
      attemptId: review?.attemptId ?? null,
      // Canonical integration submissions do NOT store the attempt generation.
      assignmentGeneration: null, status: s.status, canonicalPath: path,
      sourceRef: s.sourceRef, submittedCommit: s.submittedCommit,
      reviewId: s.reviewId, reviewedHeadOid: s.reviewedHeadOid
    });
    issue(issues, "submissions", "SubmissionGenerationNotCanonical", path);
  }

  diagnostics.push(...issues.map(x => ({ projectName: view.projectName, ...x })));
  return {
    projectName: view.projectName, projectId: view.projectId,
    source: sourceInfo(view), completeness: projectCompleteness(issues),
    capacity: unknownCapacity(view.observedAtUtc),
    tasks, agents, sourceLeases, reviews, challenges, submissions
  };
}
function git(cwd, args) {
  const env = { ...process.env, GIT_TERMINAL_PROMPT: "0", GIT_NO_REPLACE_OBJECTS: "1",
    GIT_CONFIG_NOSYSTEM: "1", GIT_OPTIONAL_LOCKS: "0" };
  for (const key of ["GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR",
    "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES",
    "GIT_INDEX_FILE", "GIT_NAMESPACE"]) delete env[key];
  try {
    return execFileSync("git", ["-C", cwd, ...args], {
      env, timeout: 30000, encoding: "utf8", maxBuffer: 32 * 1024 * 1024,
      stdio: ["ignore", "pipe", "pipe"]
    });
  } catch {
    fail("GitReadUnavailable");
  }
}
function verifyBinding(binding, expectedRef, project) {
  if (!binding || !validString(binding.remoteUrl) ||
      !validString(binding.ledgerId) || !validPositive(binding.epoch) ||
      !validString(project ? binding.projectId : binding.accountId) ||
      (project && !identifier(binding.projectName)) ||
      !SHA.test(binding.previousHead ?? "")) {
    fail("ApprovedLedgerBindingMissing");
  }
  // No arbitrary ref override; a canonical ledger is not main or a worktree.
  if (binding.ref && binding.ref !== expectedRef) fail("UnexpectedCanonicalRef");
}
function readGitAuthority(binding, ref, project, observedAtUtc) {
  verifyBinding(binding, ref, project);
  const temp = mkdtempSync(join(tmpdir(), "jarvis-phase2-read-"));
  try {
    git(temp, ["init", "--bare", "-q"]);
    const advertised = () => {
      const rows = git(temp, ["ls-remote", "--exit-code", binding.remoteUrl, ref]).trim().split("\n");
      if (rows.length !== 1 || !rows[0].endsWith("\t" + ref)) fail("AmbiguousCanonicalRemoteRef");
      const oid = rows[0].split("\t")[0];
      if (!SHA.test(oid)) fail("InvalidCanonicalRemoteOid");
      return oid;
    };
    const before = advertised();
    git(temp, ["fetch", "--no-tags", "--no-write-fetch-head", "--refmap=",
      binding.remoteUrl, ref + ":refs/phase2/snapshot"]);
    const head = git(temp, ["rev-parse", "--verify", "refs/phase2/snapshot^{commit}"]).trim();
    if (before !== head) fail("CanonicalRemoteChangedDuringFetch");
    // A HEAD returned by a remote is not itself a history-trust anchor.
    // Every observation must advance a previously verified canonical head.
    {
      try {
        git(temp, ["fetch", "--no-tags", "--no-write-fetch-head", "--refmap=",
          binding.remoteUrl, binding.previousHead + ":refs/phase2/previous"]);
        git(temp, ["merge-base", "--is-ancestor", binding.previousHead, head]);
      } catch {
        fail("CanonicalRemoteRewindOrUnprovenAncestry");
      }
    }
    const entries = new Map();
    const trees = git(temp, ["ls-tree", "-r", "-z", head]).split("\0").filter(Boolean);
    if (trees.length > MAX_RECORDS) fail("CanonicalRecordCountLimit");
    for (const row of trees) {
      const m = /^(100644) blob ([a-f0-9]{40}(?:[a-f0-9]{24})?)\t(.+)$/.exec(row);
      if (!m) fail("UnexpectedGitTreeObject");
      const path = m[3];
      const selected = project
        ? path === "ledger.json" ||
          /^(?:tasks|agents|challenges|challenge-waits)\//.test(path)
        : path === "state.json";
      if (!selected || (!path.endsWith(".json") && path !== "ledger.json")) continue;
      const text = git(temp, ["show", head + ":" + path]);
      if (Buffer.byteLength(text, "utf8") > MAX_BLOB_BYTES) fail("CanonicalRecordSizeLimit");
      let record;
      try { record = JSON.parse(text); }
      catch { fail("MalformedCanonicalJson:" + path); }
      if (entries.has(path)) fail("DuplicateCanonicalPath");
      entries.set(path, record);
    }
    if (advertised() !== head) fail("CanonicalRemoteChangedDuringRead");
    const root = entries.get(project ? "ledger.json" : "state.json");
    if (!root || root.schemaVersion !== (project ? 2 : 1) ||
        root.ledgerId !== binding.ledgerId || root.epoch !== binding.epoch ||
        root[project ? "projectId" : "accountId"] !==
          binding[project ? "projectId" : "accountId"]) {
      fail("CanonicalLedgerIdentityOrEpochMismatch");
    }
    if (project && (!validPositive(root.nextSequence) || root.status !== "active" ||
        !root.settings || !validPositive(root.settings.maxWorkingPerAgent) ||
        typeof root.settings.allowDirectSourcePush !== "boolean")) {
      fail("InvalidCanonicalProjectHeader");
    }
    if (!project) {
      const phases = new Set(["LegacyActive", "Frozen", "Preparing", "Validated",
        "Activated", "Aborted"]);
      if (!Array.isArray(root.tokens) || !validPositive(root.maxConcurrentAgents) ||
          !validPositive(root.stateVersion) || !validPositive(root.configVersion) ||
          !validPositive(root.nextSequence) || !phases.has(root.migrationPhase)) {
        fail("InvalidCanonicalAccountState");
      }
      const tokenIds = new Set();
      let held = 0;
      for (const token of root.tokens) {
        if (!token || !validString(token.launchId) || tokenIds.has(token.launchId) ||
            !validString(token.projectId) || !validString(token.projectLedgerId) ||
            !validPositive(token.projectLedgerEpoch) || !validString(token.roleId) ||
            !validPositive(token.roleConfigVersion) || !validString(token.admissionOperationId) ||
            !/^[a-f0-9]{64}$/.test(token.projectAuthoritySha256 ?? "") ||
            !["Held", "Released"].includes(token.status) ||
            (token.status === "Held" && token.releaseProof != null) ||
            (token.status === "Released" && (!token.releaseProof ||
              token.releaseProof.projectId !== token.projectId ||
              token.releaseProof.projectLedgerId !== token.projectLedgerId ||
              token.releaseProof.projectLedgerEpoch !== token.projectLedgerEpoch ||
              token.releaseProof.projectAuthoritySha256 !== token.projectAuthoritySha256))) {
          fail("InvalidCanonicalAccountToken");
        }
        if (token.status === "Held") held++;
        tokenIds.add(token.launchId);
      }
      if (held > root.maxConcurrentAgents) fail("CanonicalWorkAdmissionCapacityContradiction");
    }
    return { ref, head, observedAtUtc, records: entries, root };
  } finally {
    rmSync(temp, { recursive: true, force: true });
  }
}

/**
 * Fresh READ-ONLY snapshots from independently approved account/project Git
 * authorities. Calling this never performs a scheduler operation. The current
 * WQ07 account tokens cannot prove worker/reviewer browser occupancy or limits.
 */
export function captureCanonicalSnapshot({ account, projects, observedAtUtc = new Date().toISOString() }) {
  if (!validTime(observedAtUtc) || !Array.isArray(projects) || projects.length === 0 ||
      new Set(projects.map(x => x?.projectName)).size !== projects.length) {
    fail("CanonicalMembershipOrTimeMissing");
  }
  const accountView = readGitAuthority(account, ACCOUNT_REF, false, observedAtUtc);
  const diagnostics = [{
    scope: "accountCapacity", reason: "AuthenticatedWorkerReviewerCapacityUnavailable",
    heldWorkTokens: accountView.root.tokens.filter(t => t.status === "Held").length,
    accountHead: accountView.head
  }, {
    scope: "membership", reason: "CanonicalAccountProjectMembershipUnavailable",
    // WQ07 tokens do not enumerate inactive projects; caller-supplied membership
    // cannot be promoted to a verified complete account-wide project roster.
    accountHead: accountView.head
  }];
  const normalizedProjects = [];
  for (const binding of projects) {
    const view = readGitAuthority(binding, PROJECT_REF, true, observedAtUtc);
    normalizedProjects.push(normalizeProject({ ...view, projectName: binding.projectName,
      projectId: binding.projectId }, diagnostics));
  }
  const configuredIds = new Set(projects.map(p => p.projectId));
  for (const token of accountView.root.tokens) {
    if (token.status === "Held" && !configuredIds.has(token.projectId)) {
      diagnostics.push({
        scope: "membership", reason: "HeldTokenProjectNotIncluded",
        projectId: token.projectId, accountHead: accountView.head
      });
    }
  }
  return {
    snapshot: {
      schemaVersion: 2, capturedAtUtc: observedAtUtc,
      source: sourceInfo(accountView),
      completeness: { account: true, projects: false, accountCapacity: false },
      accountCapacity: unknownCapacity(observedAtUtc),
      projects: normalizedProjects
    },
    diagnostics,
    authority: {
      account: { head: accountView.head, ref: ACCOUNT_REF, stateVersion: accountView.root.stateVersion,
        maxConcurrentWorkAgents: accountView.root.maxConcurrentAgents },
      projects: normalizedProjects.map(p => ({ projectName: p.projectName, ...p.source }))
    }
  };
}

/**
 * Default-off dry-run boundary. Phase-1 is an unintegrated accepted source,
 * so the only legal outcome today is an explicit HOLD, not a shadow planner.
 */
export function phase2DefaultOffDiagnostic(capture) {
  return {
    enabled: false, sideEffects: 0,
    workerCandidates: [], reviewCandidates: [],
    holds: [
      { scope: "runtime", reason: "Phase1NotIntegrated" },
      { scope: "account", reason: "AuthenticatedWorkerReviewerCapacityUnavailable" },
      ...capture.diagnostics
    ]
  };
}

export const CANONICAL_REFS = Object.freeze({ account: ACCOUNT_REF, project: PROJECT_REF });
