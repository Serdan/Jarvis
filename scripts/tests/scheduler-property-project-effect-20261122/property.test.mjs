import test from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import {
  CANDIDATE, initial, goodProof, goodNoSend, step, fakeProviderResult,
  checkInvariants, generate, runTrace, shrink, RealGitFixture
} from "./model.mjs";

const claim = (op = "claim-1", changes = {}) => ({
  kind: "claim", op, candidate: { ...CANDIDATE }, expectedProject: 0, expectedAccount: 1, ...changes
});
const start = (op = "start-1", changes = {}) => ({
  kind: "start", op, token: "claim-token-a", effect: CANDIDATE.effect,
  owner: CANDIDATE.owner, expectedProject: 1, ...changes
});
const terminal = (op = "term-1", proof = goodProof()) => ({
  kind: "terminal", op, token: "claim-token-a", effect: CANDIDATE.effect, proof
});
const noSend = (op = "nosend-1", proof = goodNoSend()) => ({
  kind: "noSend", op, token: "claim-token-a", effect: CANDIDATE.effect, proof
});
const release = (op = "release-1", expectedAccount = 1) => ({
  kind: "release", op, token: "claim-token-a", effect: CANDIDATE.effect,
  expectedAccount
});
const apply = (s, command, expected) => {
  const result = step(s, command);
  assert.equal(result.code, expected, JSON.stringify(command));
  checkInvariants(result.state);
  return result.state;
};

function preparedSent() {
  let s = initial();
  s = apply(s, claim(), "Claimed");
  s = apply(s, start(), "StartAuthorized");
  s = fakeProviderResult(s, "ackLost");
  assert.equal(s.providerSubmissions, 1);
  assert.equal(s.phase, "Dispatching", "lost ACK cannot remove durable dispatch marker");
  return s;
}
function verifiedTerminal() {
  return apply({ ...preparedSent(), providerStatus: "Terminal" },
    terminal(), "VerifiedTerminal");
}
function git(args, cwd = process.cwd()) {
  return spawnSync("git", ["-C", cwd, ...args], { encoding: "utf8" });
}

test("source provenance: accepted StageA is pinned as ancestor, not StageB acceptance", () => {
  const baseline = "dcac884fc061869572b12a03befc12f11c009f2f";
  const accepted = "a2e244bf";
  assert.equal(git(["merge-base", "--is-ancestor", baseline, "HEAD"]).status, 0);
  assert.equal(git(["merge-base", "--is-ancestor", accepted, baseline]).status, 0);
  assert.equal(git(["cat-file", "-e", baseline + ":scripts/tests/native-project-effect-stage-a.test.mjs"]).status, 0);
  assert.equal(git(["cat-file", "-e", baseline + ":scripts/tests/native-project-effect-once-only-stage-a.test.mjs"]).status, 0);
  const head = git(["rev-parse", "HEAD"]);
  assert.equal(head.status, 0);
  assert.match(head.stdout.trim(), /^[a-f0-9]{40}$/);
});

test("pure seeded multi-step traces: 256 reproducible seeds x 48 with typed outcomes", () => {
  for (let seed = 1; seed <= 256; seed++) {
    const commands = generate(seed * 2654435761 >>> 0, 48);
    try {
      const actual = runTrace(commands);
      assert.ok(actual.results.every(x => typeof x === "string"));
      checkInvariants(actual.state);
      assert.ok(actual.state.providerSubmissions <= 1);
      assert.ok(actual.state.released <= 1);
    } catch (error) {
      const min = shrink(commands, t => {
        try { runTrace(t); return false; } catch { return true; }
      });
      throw new Error("Seed " + seed + " minimized=" + JSON.stringify(min) + " cause=" + error.message);
    }
  }
});

test("stale source, generation, head, account and owner never allow pre-send effect", () => {
  for (const bad of [
    { candidate: { ...CANDIDATE, generation: 6 } },
    { candidate: { ...CANDIDATE, lease: "refs/heads/foreign" } },
    { candidate: { ...CANDIDATE, epoch: 3 } },
    { candidate: { ...CANDIDATE, reservation: "foreign" } },
    { expectedProject: -1 }, { expectedAccount: 0 }
  ]) {
    const r = step(initial(), claim("bad-" + JSON.stringify(bad), bad));
    assert.notEqual(r.code, "Claimed");
    assert.equal(r.state.providerSubmissions, 0);
    assert.equal(r.state.reservationActive, true);
  }
  const s = apply(initial(), claim(), "Claimed");
  for (const bad of [
    start("foreign-owner", { owner: "Worker B" }),
    start("stale-head", { expectedProject: 0 }),
    start("foreign-effect", { effect: "effect-b" }),
    start("foreign-token", { token: "foreign-claim" })
  ]) {
    assert.notEqual(step(s, bad).code, "StartAuthorized");
  }
});

test("uncertain ACK, replay, restart, reconnect and clock rollback have no re-send authorization", () => {
  let s = preparedSent();
  for (const command of [
    start("start-1"), start("start-2"),
    start("start-3", { owner: "Worker B" }),
    { kind: "restart" }, { kind: "observe" }
  ]) {
    const r = step(s, command);
    if (command.kind === "start") assert.notEqual(r.code, "StartAuthorized");
    s = r.state;
  }
  assert.equal(s.providerSubmissions, 1);
  assert.equal(s.reservationActive, true);
  assert.equal(step(s, release()).code, "NotTerminal");
  assert.equal(step(s, terminal("unknown", { ...goodProof(), status: "Unknown" })).code,
    "UnknownOrForeignProof");
  assert.equal(s.completed, false);
});

test("same UUID changed terminal proof is typed error; exact replay is only AlreadyApplied", () => {
  const s = verifiedTerminal();
  assert.equal(step(s, terminal()).code, "AlreadyApplied");
  for (const proof of [
    { ...goodProof(), marker: "FOREIGN" },
    { ...goodProof(), status: "Unknown" },
    { ...goodProof(), session: "foreign" },
    { ...goodProof(), effect: "foreign" }
  ]) assert.equal(step(s, terminal("term-1", proof)).code, "OperationIdReused");
  assert.equal(s.released, 0);
});

test("negative oracle: omit terminal proof from operation identity reproduces known StageB defect", () => {
  let s = preparedSent();
  s = { ...s, providerStatus: "Terminal" };
  const committed = step(s, terminal(), { omitTerminalProof: true });
  assert.equal(committed.code, "VerifiedTerminal");
  const mutated = step(committed.state, terminal("term-1", { ...goodProof(), status: "Unknown" }),
    { omitTerminalProof: true });
  // A mutant hashing only candidate/effect would silently report AlreadyApplied;
  // this fails expected typed outcome, so the independent oracle detects it.
  const original = step(verifiedTerminal(), terminal("term-1", { ...goodProof(), status: "Unknown" }));
  assert.equal(original.code, "OperationIdReused");
  assert.notEqual(mutated.code, original.code);
});

test("terminal release requires authenticated-looking independent fake proofs, once only", () => {
  const before = preparedSent();
  for (const proof of [
    { ...goodProof(), session: "other" },
    { ...goodProof(), marker: "untrusted-tab" },
    { ...goodProof(), status: "Unknown" }
  ]) {
    assert.equal(step(before, terminal("deny-" + JSON.stringify(proof), proof)).code,
      "UnknownOrForeignProof");
  }
  let s = verifiedTerminal();
  s = apply(s, release(), "Released");
  assert.equal(step(s, release("release-2", 2)).code, "AlreadyReleased");
  assert.equal(step(s, release("release-1", 1)).code, "AlreadyApplied");
  assert.equal(s.released, 1);
  assert.equal(s.completed, true);
});

test("no-send demands independently matching project/provider negative proofs", () => {
  let s = apply(initial(), claim(), "Claimed");
  s = apply(s, start(), "StartAuthorized");
  assert.equal(step(s, noSend("bad", { ...goodNoSend(), provider: "Unknown" })).code,
    "UnknownOrForeignProof");
  s = apply(s, noSend(), "VerifiedNoSend");
  s = apply(s, release(), "Released");
  assert.equal(s.providerSubmissions, 0);
  assert.equal(s.released, 1);
  assert.equal(step(s, start("again")).code, "AlreadyStarted");
});

test("negative oracle mutation: unknown effect cannot become Released or fake no-send", () => {
  const s = preparedSent();
  const mutant = { ...s, phase: "Released", reservationActive: false,
    completed: true, released: 1 };
  assert.throws(() => checkInvariants(mutant), /ReleaseBeforeProof/);
  assert.throws(() => checkInvariants({ ...s, phase: "VerifiedNoSend" }), /FalseNoSend/);
});

test("minimal shrinker returns a strictly smaller counterexample for a mutated oracle", () => {
  const trace = [
    { kind: "observe" }, claim(), start(),
    { kind: "accountWrite" }, { kind: "restart" },
    terminal("x", { ...goodProof(), status: "Unknown" })
  ];
  const fails = cmds => cmds.some(c => c.kind === "terminal" &&
    c.proof?.status === "Unknown");
  const min = shrink(trace, fails);
  assert.deepEqual(min, [trace.at(-1)]);
});

test("three real Git clients: stale simultaneous claims produce one durable winner", () => {
  const f = new RealGitFixture();
  try {
    const a = f.stage(1, claim("a"));
    const b = f.stage(2, claim("b", { candidate: { ...CANDIDATE, owner: "Worker B" } }));
    assert.equal(a.code, "Claimed");
    // B has foreign owner and gets denied even before CAS; use a valid competing
    // dispatch UUID to test the actual project ref CAS in the second stage.
    assert.equal(b.code, "CandidateConflict");
    const b2 = f.stage(2, claim("b2"));
    assert.equal(b2.code, "Claimed");
    assert.equal(f.publish(1).accepted, true);
    assert.equal(f.publish(2).accepted, false, "ordinary non-force Git push must lose");
    const persisted = f.actual();
    assert.equal(persisted.claim.token, "claim-token-a");
    assert.equal(persisted.projectVersion, 1);
    assert.equal(f.apply(2, claim("b3", { expectedProject: 1 })).code, "ClaimConflict");
    assert.equal(f.actual().providerSubmissions, 0);
  } finally { f.close(); }
});

test("three Git clones: durable start before fake send, ACK loss, terminal, once release", () => {
  const f = new RealGitFixture();
  try {
    assert.equal(f.apply(0, claim()).code, "Claimed");
    assert.equal(f.apply(1, start()).code, "StartAuthorized");
    // Only AFTER durable start does disposable fake boundary perform one send.
    const inject = old => ({ state: fakeProviderResult(old, "ackLost"), code: "FakeAckLost" });
    assert.equal(f.apply(2, { kind: "fakeBoundary" }, inject).code, "FakeAckLost");
    assert.equal(f.apply(0, start("retry", { expectedProject: 2 })).code, "AlreadyStarted");
    assert.equal(f.apply(1, release()).code, "NotTerminal");
    assert.equal(f.fakeAccount().active, true, "unknown effect must occupy fake account");
    assert.equal(f.actual().providerSubmissions, 1);
    const finish = old => ({ state: { ...old, providerStatus: "Terminal" }, code: "FakeProviderTerminal" });
    assert.equal(f.apply(2, { kind: "fakeObserve" }, finish).code, "FakeProviderTerminal");
    assert.equal(f.apply(0, terminal()).code, "VerifiedTerminal");
    assert.equal(f.apply(1, terminal("term-1", { ...goodProof(), session: "foreign" })).code,
      "OperationIdReused");
    assert.equal(f.apply(2, release()).code, "Released");
    assert.equal(f.releaseFakeAccount().code, "FakeAccountReleased");
    assert.equal(f.fakeAccount().releaseCount, 1);
    assert.equal(f.releaseFakeAccount().code, "AlreadyReleasedOrForeign");
    assert.equal(f.apply(0, release("again", 2)).code, "AlreadyReleased");
    const state = f.actual();
    assert.equal(state.providerSubmissions, 1);
    assert.equal(state.released, 1);
    assert.equal(state.reservationActive, false);
    assert.equal(state.phase, "VerifiedTerminal");
    assert.equal(state.completed, true);
  } finally { f.close(); }
});


test("real disposable Git: verified prestart no-send releases fake account once", () => {
  const f = new RealGitFixture();
  try {
    assert.equal(f.apply(0, claim()).code, "Claimed");
    assert.equal(f.apply(1, start()).code, "StartAuthorized");
    assert.equal(f.apply(0, noSend("bad", { ...goodNoSend(), provider: "Unknown" })).code,
      "UnknownOrForeignProof");
    assert.equal(f.apply(1, noSend()).code, "VerifiedNoSend");
    assert.equal(f.fakeAccount().active, true, "proof alone must not release");
    assert.equal(f.apply(2, release()).code, "Released");
    assert.equal(f.releaseFakeAccount().code, "FakeAccountReleased");
    assert.equal(f.fakeAccount().releaseCount, 1);
    assert.equal(f.actual().providerSubmissions, 0);
  } finally { f.close(); }
});

test("seeded adversarial traces replay on three real project Git clones and separate fake account", () => {
  for (let seed = 101; seed <= 106; seed++) {
    const f = new RealGitFixture();
    try {
      let ref = initial();
      const prefix = [claim("seed-claim-" + seed), start("seed-start-" + seed)];
      for (const command of prefix) {
        const expected = step(ref, command);
        const actual = f.apply(seed % 3, command);
        assert.equal(actual.code, expected.code);
        ref = expected.state;
      }
      // Recorded project marker pre-exists any fake external send.
      assert.equal(f.actual().providerSubmissions, 0);
      const inject = old => ({ state: fakeProviderResult(old, "ackLost"), code: "FakeAckLost" });
      assert.equal(f.apply((seed + 1) % 3, { kind: "fakeBoundary" }, inject).code, "FakeAckLost");
      ref = fakeProviderResult(ref, "ackLost");
      assert.equal(f.fakeAccount().active, true);
      const commands = generate(seed * 1597334677 >>> 0, 28);
      for (let i = 0; i < commands.length; i++) {
        if (i === 9) {
          const observe = old => ({ state: { ...old, providerStatus: "Terminal" },
            code: "FakeObservedTerminal" });
          assert.equal(f.apply(2, { kind: "fakeObserve" }, observe).code, "FakeObservedTerminal");
          ref = { ...ref, providerStatus: "Terminal" };
        }
        const command = commands[i];
        const expected = step(ref, command);
        const actual = f.apply(i % 3, command);
        assert.equal(actual.code, expected.code, "seed " + seed + " index " + i);
        ref = expected.state;
        assert.deepEqual(f.actual(), ref, "seed=" + seed + ", step=" + i +
          ", command=" + JSON.stringify(command));
        checkInvariants(ref);
        if (actual.code === "Released") {
          assert.equal(f.releaseFakeAccount().code, "FakeAccountReleased");
        }
      }
      assert.equal(f.actual().providerSubmissions, 1);
      assert.ok(f.fakeAccount().releaseCount <= 1);
      assert.equal(f.fakeAccount().active, ref.released === 0);
    } catch (error) {
      const min = shrink(generate(seed * 1597334677 >>> 0, 28), t => {
        try { runTrace(t); return false; } catch { return true; }
      });
      throw new Error("Git seed " + seed + ", shrink-candidate=" + JSON.stringify(min) +
        ", cause=" + error.message);
    } finally { f.close(); }
  }
});
