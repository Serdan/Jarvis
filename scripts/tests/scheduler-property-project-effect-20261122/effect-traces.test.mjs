// Generated post-send Phase-A safety traces: REAL disposable Git clones, durable
// independent provider and account observations. These do NOT call F# StageB.
import test from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import {
  CANDIDATE, initial, step, goodProof, goodNoSend,
  checkInvariants, rng, shrink, RealGitFixture
} from "./model.mjs";
import { DurableFakeProvider } from "./provider-boundary.mjs";

const base = (kind, op, extras = {}) => ({
  kind, op, token: "claim-token-a", effect: CANDIDATE.effect, ...extras
});
const claim = op => ({
  kind: "claim", op, candidate: { ...CANDIDATE },
  expectedProject: 0, expectedAccount: 1
});
const start = op => base("start", op, { owner: CANDIDATE.owner, expectedProject: 1 });
const noSend = (op, proof = goodNoSend()) => base("noSend", op, { proof });
const terminal = (op, proof = goodProof()) => base("terminal", op, { proof });
const release = (op, account = 1) => base("release", op, { expectedAccount: account });
const command = (cmd, expect, client) => ({ kind: "project", cmd, expect, client });
const event = (kind, expect) => ({ kind, expect });

// Generator deliberately constructs reachable post-send sequences. Random noise
// and clients are seeded; every effectful command remains checked against a
// separately persisted provider receipt, not only project state equality.
export function generateEffectTrace(seed) {
  const n = rng((seed * 1597334677) >>> 0);
  const client = () => n(3);
  const op = name => `s${seed}-${name}`;
  const sent = seed % 2 === 1;
  const trace = [
    command(claim(op("claim")), "Claimed", client()),
    command(start(op("start")), "StartAuthorized", client()),
    command(start(op("retry-start")), "AlreadyStarted", client()),
    event("restart", "Restarted")
  ];
  if (sent) {
    trace.push(
      event("sendLost", "Unknown"),
      command(noSend(op("false-nosend")), "UnknownOrForeignProof", client()),
      command(release(op("unsafe-release")), "NotTerminal", client()),
      event("restart", "Restarted"),
      event("retrySend", "AlreadySubmitted"),
      command(start(op("repeat-dispatch")), "AlreadyStarted", client()),
      command(terminal(op("premature-terminal")), "UnknownOrForeignProof", client()),
      command(terminal(op("foreign-terminal"), { ...goodProof(), session: "forged" }),
        "UnknownOrForeignProof", client()),
      event("markTerminal", "TerminalObserved"),
      command(terminal(op("terminal")), "VerifiedTerminal", client()),
      command(terminal(op("terminal"), { ...goodProof(), status: "Unknown" }),
        "OperationIdReused", client()),
      command(terminal(op("terminal")), "AlreadyApplied", client()),
      command(release(op("release")), "Released", client()),
      event("accountRelease", "FakeAccountReleased"),
      command(release(op("again"), 2), "AlreadyReleased", client()),
      event("accountRelease", "AlreadyReleasedOrForeign")
    );
  } else {
    trace.push(
      event("captureDispatch", "SnapshotRetained"),
      command(noSend(op("unknown-proof"), { ...goodNoSend(), provider: "Unknown" }),
        "UnknownOrForeignProof", client()),
      event("restart", "Restarted"),
      command(noSend(op("verified-no-session")), "VerifiedNoSend", client()),
      command(release(op("release")), "Released", client()),
      event("accountRelease", "FakeAccountReleased"),
      event("attemptLateSend", "DeniedByNoSessionSeal"),
      command(release(op("again"), 2), "AlreadyReleased", client()),
      event("accountRelease", "AlreadyReleasedOrForeign")
    );
  }
  for (let i = 0; i < 16 + n(12); i++) {
    const draw = n(4);
    trace.push(draw === 0 ? event("restart", "Restarted")
      : draw === 1 ? command(start(op("noise-start-" + i)), "AlreadyStarted", client())
      : draw === 2 ? command(release(op("noise-release-" + i), 2),
          "AlreadyReleased", client())
      : command(base("observe", op("observe-" + i)), "Observed", client()));
  }
  return { sent, trace };
}

function independentOracle(f) {
  const state = f.actual();
  const receipt = f.provider.restart().observe(CANDIDATE.effect);
  const account = f.fakeAccount();
  checkInvariants(state, f.provider);
  if (state.phase === "VerifiedNoSend")
    assert.equal(f.provider.restart().noSessionSealed(CANDIDATE.effect), true,
      "durable provider seal is mandatory even when no receipt exists");
  assert.ok(!receipt || receipt.acceptedCount === 1, "provider accepts at most one send");
  if (receipt && state.phase === "VerifiedNoSend")
    throw new Error("FalseNoSendDurableProvider");
  if (receipt && receipt.status !== "Terminal" && state.released !== 0)
    throw new Error("PrematureReleaseDurableProvider");
  if (!account.active) {
    assert.equal(account.releaseCount, 1, "account released exactly once");
    assert.equal(state.released, 1, "account cannot release before project proof");
    assert.ok(state.phase === "VerifiedNoSend" && !receipt ||
      state.phase === "VerifiedTerminal" && receipt?.status === "Terminal");
  }
  if (state.phase === "Dispatching" && receipt?.status === "Active")
    assert.equal(account.active, true, "unknown provider retains reservation");
}

export function runEffectTrace(seed, trace, mutate = null) {
  const f = new RealGitFixture();
  let staleDispatch = null;
  const coverage = {
    sent: 0, unknown: 0, retried: 0, deniedNoSend: 0,
    deniedRelease: 0, terminal: 0, released: 0, noSession: 0,
    accountReleased: 0, restarts: 0, lateDenied: 0
  };
  try {
    for (let index = 0; index < trace.length; index++) {
      const action = trace[index];
      let actual;
      if (action.kind === "project") {
        const adapter = mutate?.kind === "falseNoSend" && action.cmd.kind === "noSend"
          ? (state, cmd) => {
              const out = step(state, cmd, { provider: f.provider });
              if (f.provider.observe(CANDIDATE.effect) && cmd.proof?.provider === "VerifiedNoProviderSession")
                return { state: { ...state, phase: "VerifiedNoSend", projectVersion: state.projectVersion + 1 },
                  code: "VerifiedNoSend" };
              return out;
            } : undefined;
        actual = f.apply(action.client, action.cmd, adapter);
        if (action.cmd.kind === "noSend" && actual.code === "UnknownOrForeignProof")
          coverage.deniedNoSend++;
        if (action.cmd.kind === "release" && actual.code === "NotTerminal")
          coverage.deniedRelease++;
        if (actual.code === "VerifiedTerminal") coverage.terminal++;
        if (actual.code === "VerifiedNoSend") coverage.noSession++;
        if (actual.code === "Released") coverage.released++;
      } else if (action.kind === "sendLost") {
        actual = f.provider.send(f.actual(), CANDIDATE.effect, "ackLost");
        coverage.sent++;
        if (actual.code === "Unknown") coverage.unknown++;
        assert.equal(f.actual().providerSubmissions, 0, "ACK loss never updates project Git");
      } else if (action.kind === "retrySend") {
        actual = f.provider.send(f.actual(), CANDIDATE.effect, "ackLost");
        if (actual.code === "AlreadySubmitted") coverage.retried++;
      } else if (action.kind === "markTerminal") {
        actual = f.provider.terminal(CANDIDATE.effect);
      } else if (action.kind === "accountRelease") {
        actual = f.releaseFakeAccount();
        if (actual.code === "FakeAccountReleased") coverage.accountReleased++;
      } else if (action.kind === "captureDispatch") {
        staleDispatch = structuredClone(f.actual());
        assert.equal(staleDispatch.phase, "Dispatching");
        actual = { code: "SnapshotRetained" };
      } else if (action.kind === "attemptLateSend") {
        assert.ok(staleDispatch, "late send MUST use cached authorization, not fresh project");
        actual = f.provider.send(staleDispatch, CANDIDATE.effect, "ackLost",
          mutate?.kind === "ignoreSeal" ? { ignoreNoSessionSeal: true } : {});
        if (actual.code === "DeniedByNoSessionSeal") coverage.lateDenied++;
      } else if (action.kind === "restart") {
        f.provider = f.provider.restart();
        actual = { code: "Restarted" };
        coverage.restarts++;
      } else throw new Error("Unknown trace event " + action.kind);
      independentOracle(f); // receipt oracle first: must kill forged no-send mutant
      assert.equal(actual.code, action.expect, "seed=" + seed + " index=" + index +
        " action=" + JSON.stringify(action));
    }
    return coverage;
  } finally { f.close(); }
}

test("generated multi-client post-send traces have mandatory coverage and independent durable receipts", () => {
  const combined = { sent: 0, unknown: 0, retried: 0, deniedNoSend: 0,
    deniedRelease: 0, terminal: 0, released: 0, noSession: 0,
    accountReleased: 0, restarts: 0, lateDenied: 0 };
  for (let seed = 101; seed <= 114; seed++) {
    const { sent, trace } = generateEffectTrace(seed);
    assert.ok(trace.length >= 24, "generated trace must be a multi-step history");
    let result;
    try { result = runEffectTrace(seed, trace); }
    catch (error) { throw new Error("effect seed " + seed + " trace=" +
      JSON.stringify(trace) + " cause=" + error.message); }
    for (const key of Object.keys(combined)) combined[key] += result[key];
    assert.equal(result.accountReleased, 1, "each branch includes exactly one account release");
    assert.ok(result.restarts >= 1, "provider restart mandatory");
    if (sent) {
      for (const key of ["sent", "unknown", "retried", "deniedNoSend",
        "deniedRelease", "terminal", "released"])
        assert.ok(result[key] >= 1, "post-send seed " + seed + " missing " + key);
    } else {
      assert.equal(result.sent, 0);
      assert.equal(result.noSession, 1);
      assert.equal(result.released, 1);
      assert.equal(result.lateDenied, 1, "sealed provider rejects retained stale Dispatching");
    }
  }
  for (const key of Object.keys(combined))
    assert.ok(combined[key] > 0, "absent generated effect coverage: " + key);
  console.log("EFFECT_COVERAGE " + JSON.stringify(combined));
});

test("lost ACK survives provider object restart and denies false project/account release", () => {
  const f = new RealGitFixture();
  try {
    assert.equal(f.apply(0, claim("counterexample-claim")).code, "Claimed");
    assert.equal(f.apply(1, start("counterexample-start")).code, "StartAuthorized");
    assert.equal(f.provider.send(f.actual(), CANDIDATE.effect, "ackLost").code, "Unknown");
    f.provider = f.provider.restart();
    assert.equal(f.provider.acceptedCount(CANDIDATE.effect), 1);
    assert.equal(f.actual().providerSubmissions, 0);
    assert.equal(f.apply(2, noSend("counterexample-no-session")).code, "UnknownOrForeignProof");
    assert.equal(f.apply(0, release("counterexample-release")).code, "NotTerminal");
    assert.equal(f.releaseFakeAccount().code, "DeniedByProjectProof");
    assert.equal(f.fakeAccount().active, true);
  } finally { f.close(); }
});

test("RED: retained Dispatching snapshot cannot send after project and account no-send release", () => {
  const f = new RealGitFixture();
  try {
    assert.equal(f.apply(0, claim("stale-claim")).code, "Claimed");
    assert.equal(f.apply(1, start("stale-start")).code, "StartAuthorized");
    const staleDispatch = structuredClone(f.actual());
    assert.equal(staleDispatch.phase, "Dispatching");
    assert.equal(f.apply(2, noSend("stale-nosend")).code, "VerifiedNoSend");
    assert.equal(f.apply(0, release("stale-release")).code, "Released");
    assert.equal(f.releaseFakeAccount().code, "FakeAccountReleased");
    f.provider = f.provider.restart();
    assert.equal(f.provider.send(staleDispatch, CANDIDATE.effect, "ackLost").code,
      "DeniedByNoSessionSeal");
    assert.equal(f.provider.restart().acceptedCount(CANDIDATE.effect), 0);
    assert.equal(f.fakeAccount().active, false);
    independentOracle(f);
  } finally { f.close(); }
});

test("seeded stale-dispatch negative mutation goes RED and delete-shrink replays", () => {
  const { sent, trace } = generateEffectTrace(102);
  assert.equal(sent, false);
  assert.throws(() => runEffectTrace(102, trace, { kind: "ignoreSeal" }),
    /FalseNoSendDurableProvider/);
  const terminalIndex = trace.findIndex(action => action.kind === "attemptLateSend");
  const prefix = trace.slice(0, terminalIndex + 1);
  const reproduces = candidate => {
    try {
      runEffectTrace(102, candidate, { kind: "ignoreSeal" });
      return false;
    } catch (error) {
      if (/FalseNoSendDurableProvider/.test(error.message)) return true;
      return false;
    }
  };
  assert.equal(reproduces(prefix), true);
  const minimal = shrink(prefix, reproduces);
  assert.ok(minimal.length < prefix.length);
  assert.equal(reproduces(minimal), true);
  assert.deepEqual(minimal.filter(action => action.kind !== "project").map(action => action.kind),
    ["captureDispatch", "attemptLateSend"]);
});

test("adversarial independent mutation is rejected and deterministic delete-shrink replays", () => {
  // Source oracle must reject a buggy adapter even when its own project state is
  // internally plausible. The durable receipt is not part of that project state.
  const { trace } = generateEffectTrace(101);
  assert.throws(() => runEffectTrace(101, trace, { kind: "falseNoSend" }),
    /FalseNoSendDurableProvider/);
  const noise = [{ kind: "noise" }, { kind: "noise2" }];
  const violating = [
    { kind: "claim" }, ...noise, { kind: "start" }, { kind: "send" },
    { kind: "restart" }, { kind: "noSend" }, { kind: "noise3" }
  ];
  function replay(events) {
    const root = mkdtempSync(join(tmpdir(), "provider-shrink-"));
    try {
      let project = initial();
      let provider = new DurableFakeProvider(join(root, "receipts.json"));
      for (const event of events) {
        if (event.kind === "claim")
          project = step(project, claim("shrink-claim")).state;
        if (event.kind === "start")
          project = step(project, start("shrink-start")).state;
        if (event.kind === "send")
          provider.send(project, CANDIDATE.effect, "ackLost");
        if (event.kind === "restart")
          provider = provider.restart();
        if (event.kind === "noSend" && project.phase === "Dispatching" &&
            provider.observe(CANDIDATE.effect)) {
          // Inject the exact erroneous no-send state and let independent
          // provider evidence falsify it.
          project = { ...project, phase: "VerifiedNoSend" };
        }
        checkInvariants(project, provider);
      }
      return false;
    } catch (error) {
      if (/FalseNoSendDurableProvider/.test(error.message)) return true;
      throw error;
    } finally { rmSync(root, { recursive: true, force: true }); }
  }
  assert.equal(replay(violating), true);
  const minimized = shrink(violating, replay);
  assert.ok(minimized.length < violating.length);
  assert.equal(replay(minimized), true, "minimized counterexample must replay");
  assert.deepEqual(minimized.map(x => x.kind), ["claim", "start", "send", "noSend"]);
});
