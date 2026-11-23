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


// Conditional Phase B: exact accepted native F# B1, disposable only. This
// augments the reviewed harness in a temporary project; no StageB edits.
if (process.env.JARVIS_STAGEB_SOURCE_ROOT) {
  test("Phase B accepted native F# seeded differential safety projection", async () => {
    const { readFileSync, writeFileSync, mkdtempSync: temp, rmSync: remove } = await import("node:fs");
    const { resolve, join: pathJoin } = await import("node:path");
    const { tmpdir: tempRoot } = await import("node:os");
    const { spawnSync } = await import("node:child_process");
    const root = resolve(process.env.JARVIS_STAGEB_SOURCE_ROOT);
    const accepted = "74fcda395857669c5db0f80ef997b4f2d6e37fcd";
    const command = (exe, args, options = {}) =>
      spawnSync(exe, args, { encoding: "utf8", maxBuffer: 16 * 1024 * 1024,
        timeout: 300000, ...options });
    const sha = command("git", ["-C", root, "rev-parse", "--verify", "HEAD"]);
    assert.equal(sha.status, 0, sha.stderr);
    assert.equal(sha.stdout.trim(), accepted, "Native source must equal accepted SHA");
    const clean = command("git", ["-C", root, "status", "--porcelain"]);
    assert.equal(clean.status, 0, clean.stderr);
    assert.equal(clean.stdout.trim(), "", "Accepted native source must be clean");
    const nativeDir = pathJoin(root, "scripts/tests/native-project-effect-stage-b");
    const project = readFileSync(pathJoin(nativeDir, "NativeProjectEffectStageB.fsproj"), "utf8");
    const program = readFileSync(pathJoin(nativeDir, "Program.fs"), "utf8");
    const marker = "let main _ =\n    try\n";
    assert.equal(program.split(marker).length, 2, "Native entrypoint changed; fail closed");
    const { createHash } = await import("node:crypto");
    const seeds = Array.from({ length: 8 }, (_, i) => i + 101);
    const guidFor = name => {
      const h = createHash("sha256").update("phaseb-w13:" + name).digest("hex");
      return [h.slice(0, 8), h.slice(8, 12), h.slice(12, 16),
        h.slice(16, 20), h.slice(20, 32)].join("-");
    };
    // Execute the actual generated commands, in order, on disposable B1 clones.
    // Projection intentionally does not silently equate provider authentication,
    // prospective seals, or independent provider/account histories with B1.
    const projectTrace = seed => {
      const { trace } = generateEffectTrace(seed);
      let state = "None", account = "Reserved", callbacks = 0;
      let terminalOp = null, providerTerminal = false;
      const events = [], unsupported = [];
      const note = (index, reason) => unsupported.push({ index, kind: trace[index].kind,
        op: trace[index].cmd?.op ?? null, reason });
      for (const [index, action] of trace.entries()) {
        const cmd = action.cmd;
        const client = action.client ?? ((seed + index) % 3);
        const op = cmd?.op ?? ("s" + seed + "-" + action.kind + "-" + index);
        let kind, code = "Observed", proofStatus = "Terminal";
        if (action.kind === "project") {
          switch (cmd.kind) {
            case "claim":
              kind = "claim";
              if (state === "None") { state = "Claimed"; code = "Applied"; }
              else code = "Denied";
              break;
            case "start":
              kind = "start";
              if (state === "Claimed") {
                state = "Dispatching"; callbacks++; code = "Applied";
                note(index, "B1 combines dispatch marker and fake callback; Phase A separates start/send");
              } else code = "Denied";
              break;
            case "noSend":
              kind = "noSend";
              if (state === "Claimed") { state = "VerifiedNoSend"; terminalOp = op; code = "Applied"; }
              else code = "Denied";
              note(index, "B1 allows pre-dispatch fake no-send only; Phase A provider seal and post-start proof are not equivalent");
              break;
            case "terminal":
              if (cmd.proof?.session === "forged") {
                note(index, "B1 cannot authenticate a provider session; foreign-session proof unsupported");
                continue;
              }
              if (!providerTerminal && cmd.proof?.status === "Terminal") {
                note(index, "B1 cannot prove real provider terminal timing; premature terminal omitted");
                continue;
              }
              kind = "terminal";
              proofStatus = cmd.proof?.status ?? "Unknown";
              if (state === "Dispatching" && proofStatus === "Terminal") {
                state = "VerifiedFakeTerminal"; terminalOp = op; code = "Applied";
              } else if (state === "VerifiedFakeTerminal" && op === terminalOp) {
                code = proofStatus === "Terminal" ? "AlreadyApplied" : "Reused";
              } else code = "Denied";
              note(index, "B1 fake terminal marker is not authenticated provider receipt");
              break;
            case "release":
              kind = "release";
              if ((state === "VerifiedFakeTerminal" || state === "VerifiedNoSend") &&
                  account === "Reserved") { account = "Released"; code = "Applied"; }
              else code = "Denied";
              note(index, "B1 release writes fake account CAS without a separate Phase A project Released state");
              break;
            case "observe":
              kind = "observe"; break;
            default: throw new Error("Unprojected project command: " + cmd.kind);
          }
        } else {
          switch (action.kind) {
            case "restart":
              kind = "observe";
              note(index, "Native fresh ledger read is not provider process restart");
              break;
            case "sendLost":
              kind = "observe";
              note(index, "Native port callback occurs in start; independent provider ACK-loss is unsupported");
              break;
            case "retrySend":
              kind = "retry";
              code = "Denied";
              note(index, "Native guarded retry proves no new callback, not provider receipt deduplication");
              break;
            case "markTerminal":
              kind = "observe";
              providerTerminal = true;
              note(index, "Provider terminal observation has no independent native provider store");
              break;
            case "captureDispatch":
              kind = "observe";
              note(index, "Retained stale provider authorization is outside B1 fake-port adapter");
              break;
            case "attemptLateSend":
              kind = "late"; code = "Denied";
              note(index, "Native guarded retry is not an external stale-provider callback");
              break;
            case "accountRelease":
              kind = "observe";
              note(index, "Native fake-account release occurred with prior release command");
              break;
            default: throw new Error("Unprojected effect event: " + action.kind);
          }
        }
        events.push({ kind, op, operationId: guidFor(op), client, code,
          state, account, callbacks, proofStatus, index });
      }
      assert.ok(events.length >= 20, "Generated native-representable event coverage missing");
      assert.ok(events.some(x => x.kind === "claim") && events.some(x => x.kind === "start"));
      return { seed, events, unsupported };
    };
    const projected = seeds.map(projectTrace);
    assert.ok(new Set(projected.map(p => p.events.map(e => e.op).join("|"))).size === seeds.length,
      "Generated histories must remain genuinely seed-distinct");
    assert.ok(projected.every(p => p.unsupported.length > 0),
      "B1 must not claim full cross-provider Phase A history parity");
    const quote = JSON.stringify;
    const histories = projected.map(p => "(" + p.seed + ", [| " +
      p.events.map(e => "(" + [
        quote(e.kind), quote(e.op), quote(e.operationId), String(e.client),
        quote(e.code), quote(e.state), quote(e.account),
        String(e.callbacks), quote(e.proofStatus)
      ].join(", ") + ")").join("; ") + " |])").join(";\n                ");
    const injected = String.raw`        test "PhaseB projected generated per-event B1 safety (full traces unsupported)" (fun () ->
            let classify result =
                match result with
                | Ok written when written.AlreadyApplied -> "AlreadyApplied"
                | Ok _ -> "Applied"
                | Error OperationIdReused -> "Reused"
                | Error _ -> "Denied"
            let histories = [| @@HISTORIES@@ |]
            for (seed, history) in histories do
                withFixture (fun f ->
                    let c = makeClaim f f.Worker
                    let mutable calls = 0
                    let mutable terminalId = Guid.Empty
                    let port (_: NativeEffectRecord) =
                        calls <- calls + 1
                        Ok (fakeTerminal c.EffectId "Unknown")
                    for (index, (kind, op, operation, client, expectedCode,
                                 expectedStatus, expectedAccount, expectedCalls, proofStatus))
                                 in history |> Array.indexed do
                        let id = Guid.Parse operation
                        let binding = match client with
                                      | 0 -> f.One | 1 -> f.Two | _ -> f.Three
                        let accountBinding = match client with
                                             | 0 -> f.AccountTwo
                                             | 1 -> f.AccountThree
                                             | _ -> f.AccountOne
                        let code =
                            match kind with
                            | "claim" ->
                                claim binding { c with OperationId = id } |> classify
                            | "start" | "retry" | "late" ->
                                let result =
                                    match dispatchFakePort binding f.Worker c.EffectId id port with
                                    | Ok _ -> "Applied"
                                    | Error _ -> "Denied"
                                // A controlled synthetic mutation bypasses only the
                                // fixture's guarded port, never the accepted B1 source.
                                if kind = "late" && seed = 102 &&
                                   Environment.GetEnvironmentVariable("NATIVE_DIFF_MUTANT") = "1" then
                                    port (get f f.Worker) |> ignore
                                result
                            | "noSend" ->
                                let proof =
                                    { Marker = "STAGE_B_DISPOSABLE_FAKE_NO_SEND/v1"
                                      Reason = "projected-" + op }
                                let result =
                                    recordFakeNoSend binding f.Worker c.EffectId id proof |> classify
                                if result = "Applied" then terminalId <- id
                                result
                            | "terminal" ->
                                let result =
                                    recordFakeTerminal binding f.Worker c.EffectId id
                                        (fakeTerminal c.EffectId proofStatus) |> classify
                                if result = "Applied" then terminalId <- id
                                result
                            | "release" ->
                                releaseFakeReservation binding accountBinding f.Worker
                                    c.EffectId terminalId id |> classify
                            | "observe" ->
                                get f f.Worker |> ignore
                                "Observed"
                            | _ -> failwithf "Unknown projected kind %s" kind
                        let state = (get f f.Worker).Status
                        let account = (fakeState f c.Account).Status
                        check (code = expectedCode)
                            (sprintf "NativeOutcome seed=%d index=%d op=%s got=%s expected=%s"
                                seed index op code expectedCode)
                        check (state = expectedStatus)
                            (sprintf "NativeStateDifferential seed=%d index=%d op=%s got=%s expected=%s"
                                seed index op state expectedStatus)
                        check (account = expectedAccount)
                            (sprintf "NativeAccountDifferential seed=%d index=%d op=%s got=%s expected=%s"
                                seed index op account expectedAccount)
                        check (calls = expectedCalls)
                            (sprintf "UnsafeLateProviderAccept seed=%d index=%d op=%s calls=%d expected=%d"
                                seed index op calls expectedCalls)
                        printfn "NATIVE_PHASEB_EVENT %d|%d|%s|%s|%s|%s|%d|%d"
                            seed index op code state account calls client
                ))
`;
    const source = program.replace(marker, marker + injected.replace("@@HISTORIES@@", histories));
    for (const seed of seeds) {
      assert.ok(source.includes("s" + seed + "-claim"),
        "Generated native history missing seed " + seed + " command identity");
    }
    const scratch = temp(pathJoin(tempRoot(), "jarvis-native-phaseb-w13-"));
    try {
      const fsproj = project
        .replace("../../../Client/Client.fsproj", pathJoin(root, "Client/Client.fsproj"))
        .replace("../../../Client/Modules/NativeProjectEffectFence.fs",
          pathJoin(root, "Client/Modules/NativeProjectEffectFence.fs"));
      assert.notEqual(fsproj, project, "Native F# exact-source reference missing");
      writeFileSync(pathJoin(scratch, "NativeProjectEffectStageB.fsproj"), fsproj);
      writeFileSync(pathJoin(scratch, "Program.fs"), source);
      const result = command("dotnet", ["run", "-c", "Release",
        "-p:BuildProjectReferences=false", "--project",
        pathJoin(scratch, "NativeProjectEffectStageB.fsproj")],
        { cwd: scratch, env: { ...process.env, DOTNET_NOLOGO: "1" } });
      const transcript = (result.stdout || "") + "\n" + (result.stderr || "");
      console.log("Native accepted SHA:", accepted, "seeds:", seeds.join(","));
      console.log("Scoped .NET11 Release exit:", result.status, "signal:", result.signal);
      for (const p of projected)
        console.log("NATIVE_PHASEB_UNSUPPORTED " + JSON.stringify({
          seed: p.seed, count: p.unsupported.length,
          reasons: [...new Set(p.unsupported.map(e => e.reason))] }));
      console.log(result.status === 0
        ? transcript.split(/\r?\n/).filter(s => /PASS |Release PASS/.test(s)).join("\n")
        : transcript.split(/\r?\n/).filter(s => /error FS|FAIL|UnsafeLate|NativeOutcome|NativeState|NativeAccount|Unexpected/.test(s)).slice(0, 22).join("\n"));
      if (process.env.NATIVE_DIFF_MUTANT === "1") {
        assert.notEqual(result.status, 0, "Unsafe late-send mutant must be RED");
        assert.match(transcript, /UnsafeLateProviderAccept/);
      } else {
        assert.equal(result.status, 0, "Native source-bound GREEN failed:\n" + transcript);
        assert.match(transcript, /PASS PhaseB projected generated per-event B1 safety/);
        const expectedEvents = projected.flatMap(p => p.events.map((e, ordinal) =>
          [p.seed, ordinal, e.op, e.code, e.state, e.account,
            e.callbacks, e.client].join("|")));
        const nativeEvents = result.stdout.split(/\r?\n/).filter(x =>
          x.startsWith("NATIVE_PHASEB_EVENT ")).map(x =>
          x.slice("NATIVE_PHASEB_EVENT ".length));
        assert.deepEqual(nativeEvents, expectedEvents,
          "Each native typed event/outcome/account/callback must match the generated projection");
        assert.match(transcript, /STAGE_B_DISPOSABLE_TEST_ONLY .NET 11 Release PASS 24/);
      }
    } finally {
      remove(scratch, { recursive: true, force: true });
    }
  });
}
