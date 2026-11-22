// Independent Phase-A reference model. Test-only; no production/browser/account clients.
import { createHash } from "node:crypto";
import { spawnSync } from "node:child_process";
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { DurableFakeProvider } from "./provider-boundary.mjs";

const copy = value => structuredClone(value);
const fingerprint = obj => createHash("sha256").update(JSON.stringify(obj)).digest("hex");
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
export const CANDIDATE = Object.freeze({
  task: "task-a", attempt: "attempt-a", generation: 7,
  lease: "refs/heads/immutable-source", owner: "Worker A",
  projectLedger: "project-ledger-a", epoch: 2,
  reservation: "fake-reservation-a", effect: "effect-a", dispatch: "dispatch-a"
});
export const goodProof = () => ({
  marker: "DISPOSABLE_PROVIDER_TERMINAL", status: "Terminal",
  effect: CANDIDATE.effect, session: "fake-session-1"
});
export const goodNoSend = () => ({
  project: "VerifiedNoSend", provider: "VerifiedNoProviderSession",
  effect: CANDIDATE.effect
});
export function initial() {
  return {
    projectVersion: 0, accountVersion: 1, candidate: copy(CANDIDATE),
    claim: null, phase: "Idle", reservationActive: true,
    providerSubmissions: 0, providerStatus: "Unknown",
    providerSession: null, released: 0, operationHashes: {},
    completed: false
  };
}
// Codes are intentionally typed data, not truthy/falsey protocol results.
export const CODES = Object.freeze([
  "ProjectConflict", "AccountConflict", "CandidateConflict", "ClaimConflict",
  "Unclaimed", "ForeignOwner", "AlreadyStarted", "StartAuthorized",
  "Claimed", "VerifiedTerminal", "VerifiedNoSend", "UnknownOrForeignProof",
  "NotTerminal", "Released", "AlreadyReleased", "AlreadyApplied", "OperationIdReused",
  "ProjectMutated", "AccountMutated", "Observed", "Restarted"
]);
const response = (state, code) => ({ state, code });
// A command must include a new immutable id for each new effectful transition.
// Reusing an id with changed proof is NEVER "AlreadyApplied".
export function step(original, cmd, options = {}) {
  const state = copy(original);
  if (cmd.kind === "observe") return response(state, "Observed");
  if (cmd.kind === "restart") return response(state, "Restarted");
  if (cmd.kind === "projectWrite") {
    state.projectVersion++; return response(state, "ProjectMutated");
  }
  if (cmd.kind === "accountWrite") {
    state.accountVersion++; return response(state, "AccountMutated");
  }
  const key = cmd.op;
  if (typeof key !== "string" || !key.length) throw new Error("Missing op");
  const raw = { ...cmd };
  const intent = options.omitTerminalProof && cmd.kind === "terminal"
    ? { kind: cmd.kind, op: cmd.op, effect: cmd.effect }
    : raw;
  const hash = fingerprint(intent);
  if (Object.hasOwn(state.operationHashes, key)) {
    return response(state, state.operationHashes[key] === hash ? "AlreadyApplied" : "OperationIdReused");
  }
  // Recording the intent occurs only after successful authorization.
  const record = code => { state.operationHashes[key] = hash; return response(state, code); };
  if (cmd.kind === "claim") {
    if (cmd.expectedProject !== state.projectVersion) return response(state, "ProjectConflict");
    if (cmd.expectedAccount !== state.accountVersion || !state.reservationActive)
      return response(state, "AccountConflict");
    if (!same(cmd.candidate, state.candidate)) return response(state, "CandidateConflict");
    if (state.claim) return response(state, "ClaimConflict");
    state.claim = { ...copy(state.candidate), token: "claim-token-a" };
    state.projectVersion++;
    return record("Claimed");
  }
  if (!state.claim) return response(state, "Unclaimed");
  if (cmd.token !== state.claim.token || cmd.effect !== state.claim.effect)
    return response(state, "CandidateConflict");
  if (cmd.kind === "start") {
    if (cmd.owner !== state.claim.owner) return response(state, "ForeignOwner");
    if (state.phase !== "Idle") return response(state, "AlreadyStarted");
    if (cmd.expectedProject !== state.projectVersion) return response(state, "ProjectConflict");
    // Durable project marker is written BEFORE any fake provider invocation.
    state.phase = "Dispatching";
    state.projectVersion++;
    return record("StartAuthorized");
  }
  if (cmd.kind === "terminal") {
    const proof = cmd.proof || {};
    if (state.phase !== "Dispatching") return response(state, "NotTerminal");
    const valid = options.provider
      ? options.provider.verifyTerminal(proof, state.claim.effect)
      : state.providerSubmissions === 1 &&
        proof.marker === "DISPOSABLE_PROVIDER_TERMINAL" &&
        proof.status === "Terminal" && proof.effect === state.claim.effect &&
        proof.session === state.providerSession && state.providerStatus === "Terminal";
    if (!valid) return response(state, "UnknownOrForeignProof");
    if (options.provider) {
      const receipt = options.provider.observe(state.claim.effect);
      state.providerSubmissions = receipt.acceptedCount;
      state.providerStatus = receipt.status;
      state.providerSession = receipt.session;
    }
    state.phase = "VerifiedTerminal";
    state.projectVersion++;
    return record("VerifiedTerminal");
  }
  if (cmd.kind === "noSend") {
    const proof = cmd.proof || {};
    if (state.phase !== "Dispatching" || state.providerSubmissions !== 0 ||
        proof.project !== "VerifiedNoSend" ||
        proof.provider !== "VerifiedNoProviderSession" ||
        proof.effect !== state.claim.effect ||
        !options.provider?.verifyNoSession(proof, state.claim.effect))
      return response(state, "UnknownOrForeignProof");
    state.phase = "VerifiedNoSend";
    state.projectVersion++;
    return record("VerifiedNoSend");
  }
  if (cmd.kind === "release") {
    if (cmd.expectedAccount !== state.accountVersion) return response(state, "AccountConflict");
    if (state.released || !state.reservationActive) return response(state, "AlreadyReleased");
    if (!["VerifiedTerminal", "VerifiedNoSend"].includes(state.phase)) return response(state, "NotTerminal");
    // A static claim is not durable authority: recheck independent provider on release.
    if (state.phase === "VerifiedNoSend" &&
        (!options.provider || options.provider.observe(state.claim.effect)))
      return response(state, "UnknownOrForeignProof");
    state.reservationActive = false;
    state.released++;
    state.accountVersion++;
    state.completed = true;
    return record("Released");
  }
  throw new Error("Unknown command " + cmd.kind);
}
// Fake boundary is DISPOSABLE, non-authenticating, and NEVER invokes BrowserBridge.
// It is intentionally separate from durable pre-send transaction.
export function fakeProviderResult(original, effect = "ackLost") {
  const s = copy(original);
  if (s.phase !== "Dispatching") throw new Error("No committed dispatch marker");
  if (effect !== "noSend") {
    s.providerSubmissions++;
    s.providerStatus = effect === "terminal" ? "Terminal" : "Active";
    s.providerSession = "fake-session-" + s.providerSubmissions;
  }
  return s;
}
export function checkInvariants(s, provider = null) {
  if (s.providerSubmissions > 1) throw new Error("DuplicateProviderSend");
  if (provider?.acceptedCount(CANDIDATE.effect) > 1)
    throw new Error("DuplicateDurableProviderSend");
  if (s.released > 1) throw new Error("DuplicateRelease");
  if (s.released && s.reservationActive) throw new Error("ReleaseStateContradiction");
  if (s.released && !["VerifiedTerminal", "VerifiedNoSend"].includes(s.phase))
    throw new Error("ReleaseBeforeProof");
  if (s.providerSubmissions && s.phase === "VerifiedNoSend")
    throw new Error("FalseNoSend");
  if (provider?.observe(CANDIDATE.effect) && s.phase === "VerifiedNoSend")
    throw new Error("FalseNoSendDurableProvider");
  if (s.completed !== Boolean(s.released)) throw new Error("FalseCompletion");
  return true;
}
export function rng(seed) {
  let x = seed >>> 0 || 0x811c9dc5;
  return max => {
    x ^= x << 13; x ^= x >>> 17; x ^= x << 5;
    return (x >>> 0) % max;
  };
}
export function generate(seed, length = 64) {
  const next = rng(seed);
  const trace = [];
  let opId = 0;
  for (let i = 0; i < length; i++) {
    const choice = next(15);
    const op = "op-" + seed + "-" + (++opId);
    const base = { op, token: "claim-token-a", effect: CANDIDATE.effect };
    const command = choice < 3
      ? { kind: "claim", op, candidate: choice === 1 ? { ...CANDIDATE, generation: 6 } : copy(CANDIDATE),
          expectedProject: choice === 2 ? -1 : 0, expectedAccount: 1 }
      : choice < 5 ? { kind: "start", ...base, owner: choice === 3 ? "Worker A" : "Worker B", expectedProject: 1 }
      : choice < 8 ? { kind: "terminal", ...base, proof: choice === 5 ? goodProof() : {
          ...goodProof(), status: choice === 6 ? "Unknown" : "Terminal", session: "foreign" } }
      : choice < 10 ? { kind: "noSend", ...base, proof: choice === 8 ? goodNoSend() : { ...goodNoSend(), provider: "Unknown" } }
      : choice === 10 ? { kind: "release", ...base, expectedAccount: 1 }
      : choice === 11 ? { kind: "projectWrite" }
      : choice === 12 ? { kind: "accountWrite" }
      : choice === 13 ? { kind: "restart" } : { kind: "observe" };
    trace.push(command);
    if (choice === 13 && i > 2) trace.push(copy(trace[next(trace.length - 1)]));
  }
  return trace;
}
export function runTrace(trace, options = {}) {
  let state = initial();
  const results = [];
  for (const cmd of trace) {
    const out = step(state, cmd, options);
    state = out.state;
    results.push(out.code);
    checkInvariants(state);
  }
  return { state, results };
}
export function shrink(trace, fails) {
  let current = trace.slice();
  let span = Math.max(1, Math.floor(current.length / 2));
  while (span >= 1) {
    let changed = false;
    for (let i = 0; i + span <= current.length; i++) {
      const candidate = [...current.slice(0, i), ...current.slice(i + span)];
      if (candidate.length && fails(candidate)) {
        current = candidate; changed = true; break;
      }
    }
    if (!changed) span = Math.floor(span / 2);
  }
  return current;
}
function git(cwd, ...args) {
  const result = spawnSync("git", ["-C", cwd, ...args], { encoding: "utf8" });
  if (result.status !== 0)
    throw new Error("Git " + args.join(" ") + " exit " + result.status + ": " + result.stderr);
  return result.stdout.trim();
}
export class RealGitFixture {
  constructor() {
    this.root = mkdtempSync(join(tmpdir(), "project-effect-property-"));
    this.bare = join(this.root, "project.git");
    mkdirSync(this.bare);
    git(this.bare, "init", "-q", "--bare");
    const seed = join(this.root, "seed");
    git(this.root, "clone", "-q", this.bare, seed);
    git(seed, "config", "user.name", "Test Only");
    git(seed, "config", "user.email", "project-effect@example.invalid");
    git(seed, "checkout", "-q", "-b", "main");
    writeFileSync(join(seed, "state.json"), JSON.stringify(initial(), null, 2));
    git(seed, "add", "state.json");
    git(seed, "commit", "-qm", "seed disposable state");
    git(seed, "push", "-q", "origin", "HEAD:refs/heads/main");
    git(this.bare, "symbolic-ref", "HEAD", "refs/heads/main");
    this.clients = ["a", "b", "c"].map(n => {
      const path = join(this.root, n);
      git(this.root, "clone", "-q", this.bare, path);
      git(path, "config", "user.name", "Test " + n);
      git(path, "config", "user.email", n + "@example.invalid");
      return path;
    });
    // Physically separate disposable fake-account Git repository. Its receipt
    // authenticates nothing; it is an observable negative/release boundary.
    this.accountBare = join(this.root, "account.git");
    mkdirSync(this.accountBare);
    git(this.accountBare, "init", "-q", "--bare");
    this.accountClient = join(this.root, "account-client");
    git(this.root, "clone", "-q", this.accountBare, this.accountClient);
    git(this.accountClient, "config", "user.name", "Fake Account");
    git(this.accountClient, "config", "user.email", "fake-account@example.invalid");
    git(this.accountClient, "checkout", "-q", "-b", "main");
    writeFileSync(join(this.accountClient, "reservation.json"),
      JSON.stringify({ receipt: CANDIDATE.reservation, candidate: CANDIDATE.task,
        active: true, releaseCount: 0 }, null, 2));
    git(this.accountClient, "add", "reservation.json");
    git(this.accountClient, "commit", "-qm", "seed fake account reservation");
    git(this.accountClient, "push", "-q", "origin", "HEAD:refs/heads/main");
    git(this.accountBare, "symbolic-ref", "HEAD", "refs/heads/main");
    this.provider = new DurableFakeProvider(join(this.root, "provider", "receipts.json"));
  }
  fakeAccount() {
    git(this.accountClient, "fetch", "-q", "origin", "main");
    git(this.accountClient, "checkout", "-q", "-B", "fake-txn", "origin/main");
    return JSON.parse(readFileSync(join(this.accountClient, "reservation.json"), "utf8"));
  }
  releaseFakeAccount() {
    const state = this.actual();
    if (!state.completed || state.released !== 1 ||
        !["VerifiedTerminal", "VerifiedNoSend"].includes(state.phase))
      return { code: "DeniedByProjectProof" };
    const receipt = this.provider.observe(state.candidate.effect);
    if (state.phase === "VerifiedNoSend" && receipt)
      return { code: "DeniedByDurableProvider" };
    if (state.phase === "VerifiedTerminal" &&
        (!receipt || receipt.status !== "Terminal" ||
         receipt.session !== state.providerSession))
      return { code: "DeniedByDurableProvider" };
    const account = this.fakeAccount();
    if (!account.active || account.receipt !== state.candidate.reservation)
      return { code: "AlreadyReleasedOrForeign" };
    account.active = false;
    account.releaseCount++;
    writeFileSync(join(this.accountClient, "reservation.json"), JSON.stringify(account, null, 2));
    git(this.accountClient, "add", "reservation.json");
    git(this.accountClient, "commit", "-qm", "one fake account release");
    git(this.accountClient, "push", "-q", "origin", "HEAD:refs/heads/main");
    return { code: "FakeAccountReleased", account: this.fakeAccount() };
  }
  snapshot(client = 0) {
    const cwd = this.clients[client];
    git(cwd, "fetch", "-q", "origin", "main");
    git(cwd, "checkout", "-q", "-B", "test-txn", "origin/main");
    return JSON.parse(readFileSync(join(cwd, "state.json"), "utf8"));
  }
  stage(client, command, adapter = (s, cmd) => step(s, cmd, { provider: this.provider })) {
    const cwd = this.clients[client];
    const before = this.snapshot(client);
    const out = adapter(before, command);
    if (same(out.state, before)) return { ...out, committed: false };
    writeFileSync(join(cwd, "state.json"), JSON.stringify(out.state, null, 2));
    git(cwd, "add", "state.json");
    git(cwd, "commit", "-qm", "test-only " + command.kind);
    return { ...out, committed: true };
  }
  publish(client) {
    const cwd = this.clients[client];
    const result = spawnSync("git", ["-C", cwd, "push", "-q", "origin", "HEAD:refs/heads/main"], { encoding: "utf8" });
    return { accepted: result.status === 0, stderr: result.stderr, status: result.status };
  }
  apply(client, command, adapter = (s, cmd) => step(s, cmd, { provider: this.provider })) {
    const out = this.stage(client, command, adapter);
    if (!out.committed) return out;
    const push = this.publish(client);
    if (!push.accepted) return { state: this.snapshot(client), code: "ProjectConflict", committed: false };
    return out;
  }
  actual() { return this.snapshot(0); }
  close() { rmSync(this.root, { recursive: true, force: true }); }
}
