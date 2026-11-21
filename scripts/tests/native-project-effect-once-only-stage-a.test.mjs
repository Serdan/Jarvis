/**
 * Stage A negative-space tests for a DISPOSABLE process-local model.
 * NOT real Git project/account CAS, browser sending, or Scheduler acceptance.
 */
import test from "node:test";
import assert from "node:assert/strict";
import { FakeBrowserEffectPort } from "./fixtures/native-project-effect-fake-browser.mjs";
import { FakeOnceOnlyProjectEffectFence } from "./fixtures/native-project-effect-fence-stage-a.mjs";

const base = Object.freeze({
  candidateKey: "worker/task/attempt-1/generation-2",
  candidateVersion: 2,
  sourceRef: "refs/heads/leased-source",
  ownerId: "Worker A",
  expectedProjectVersion: 0,
  expectedAccountVersion: 1,
  reservationReceipt: "account-reservation-1",
  dispatchUuid: "dispatch-original",
  effectUuid: "effect-original"
});

function make(overrides = {}) {
  const browser = new FakeBrowserEffectPort();
  const fence = new FakeOnceOnlyProjectEffectFence(browser);
  const request = { ...base, ...overrides };
  fence.registerCandidate(request.candidateKey, request.candidateVersion, request.sourceRef);
  assert.equal(fence.reserve(request.reservationReceipt), true);
  return { browser, fence, request };
}

function fakeProjectReceipt(claim, status = "VerifiedTerminal") {
  return { source: "FAKE_PROJECT_CAS", status, token: claim.token,
    effectUuid: claim.effectUuid, reservationReceipt: claim.reservationReceipt };
}

function fakeTerminal(browser, claim) {
  return browser.fakeProviderTerminal(claim.effectUuid);
}

function releaseArgs(fence, claim, projectEvidence, providerEvidence) {
  return { token: claim.token, expectedAccountVersion: fence.accountVersion,
    projectEvidence, providerEvidence };
}

test("different Workers for the same exact attempt cannot both claim", () => {
  const { fence, request, browser } = make();
  const winner = fence.claim(request);
  assert.ok(winner);
  assert.equal(fence.claim({ ...request, ownerId: "Worker B",
    dispatchUuid: "different-dispatch", effectUuid: "different-effect",
    expectedProjectVersion: fence.projectVersion }), null);
  assert.equal(fence.claims.size, 1);
  assert.equal(browser.providerSubmissions.length, 0);
});

test("competing Reviewers of one frozen review cannot both claim", () => {
  const key = "review/review-id/version-7/frozen-sha";
  const { fence, request } = make({ candidateKey: key, candidateVersion: 7,
    ownerId: "Reviewer A" });
  assert.ok(fence.claim(request));
  assert.equal(fence.claim({ ...request, ownerId: "Reviewer B",
    effectUuid: "foreign-effect", dispatchUuid: "foreign-dispatch",
    expectedProjectVersion: fence.projectVersion }), null);
});

test("a third project writer causes stale project-head CAS failure before browser", async () => {
  const { fence, browser, request } = make();
  fence.externalProjectMutation();
  assert.equal(fence.claim(request), null);
  assert.equal(browser.attempts.length, 0);
  assert.equal(browser.providerSubmissions.length, 0);
  assert.equal(fence.reservation.released, false);
});

test("account reservation won and project CAS lost retains capacity", () => {
  const { fence, request } = make();
  fence.externalProjectMutation();
  assert.equal(fence.claim(request), null);
  assert.equal(fence.reservation.released, false);
  assert.equal(fence.accountVersion, 1);
});

test("stale assignment generation or source lease fails closed", () => {
  const { fence, request, browser } = make();
  assert.equal(fence.claim({ ...request, candidateVersion: 1 }), null);
  assert.equal(fence.claim({ ...request, sourceRef: "refs/heads/stale" }), null);
  assert.equal(browser.providerSubmissions.length, 0);
});

test("stale reviewer version/ref also fails closed", () => {
  const { fence, request, browser } = make({ candidateKey: "review/frozen",
    candidateVersion: 9 });
  assert.equal(fence.claim({ ...request, candidateVersion: 8 }), null);
  assert.equal(fence.claim({ ...request, sourceRef: "refs/heads/different" }), null);
  assert.equal(browser.attempts.length, 0);
});

test("lost project claim ACK can be reconciled without another claim/send", async () => {
  const { fence, browser, request } = make();
  const claim = fence.claim(request);
  const restored = fence.observe(claim.token);
  assert.equal(restored.dispatchUuid, request.dispatchUuid);
  assert.equal(restored.effectStarts, 0);
  assert.equal(fence.claim({ ...request, expectedProjectVersion: fence.projectVersion }), null);
  assert.equal(browser.attempts.length, 0);
  // The fake record can be inspected; never infer that the browser started.
});

for (const fault of ["accepted", "ackLostAfterSend", "timeoutAfterSend",
  "clientCrashAfterSend"]) {
  test("once-only Send survives " + fault + " and rejects owner/reconnect retry", async () => {
    const { fence, browser, request } = make();
    const claim = fence.claim(request);
    browser.enqueue(fault);
    const first = await fence.send(claim.token, request.ownerId);
    assert.equal(first.kind, fault === "accepted" ? "Submitted" : "Uncertain");
    assert.equal(claim.effectStarts, 1);
    assert.equal(browser.providerSubmissions.length, 1);
    browser.closeTab(request.effectUuid);
    browser.reconnect();
    browser.rollBackObservedClock();
    assert.equal((await fence.send(claim.token, request.ownerId)).kind,
      "DeniedOrAlreadyStarted");
    assert.equal((await fence.send(claim.token, "Foreign Worker")).kind,
      "DeniedOrAlreadyStarted");
    assert.equal(browser.providerSubmissions.length, 1);
    assert.equal(fence.reservation.released, false);
  });
}

test("replayed original dispatch/effect UUIDs cannot claim a different candidate", () => {
  const { fence, request } = make();
  assert.ok(fence.claim(request));
  fence.registerCandidate("worker/other-attempt", 3, request.sourceRef);
  const second = { ...request, candidateKey: "worker/other-attempt",
    candidateVersion: 3, expectedProjectVersion: fence.projectVersion };
  assert.equal(fence.claim(second), null);
  assert.equal(fence.claim({ ...second, dispatchUuid: "new-dispatch" }), null);
  assert.equal(fence.claim({ ...second, effectUuid: "new-effect" }), null);
});

test("provider Unknown and tab closure cannot release an uncertain effect", async () => {
  const { fence, browser, request } = make();
  const claim = fence.claim(request);
  browser.enqueue("timeoutAfterSend");
  assert.equal((await fence.send(claim.token, request.ownerId)).kind, "Uncertain");
  const project = fakeProjectReceipt(claim);
  assert.equal(fence.release(releaseArgs(fence, claim, project,
    { source: "FAKE_PROVIDER", status: "Unknown", effectUuid: claim.effectUuid })), false);
  browser.closeTab(claim.effectUuid);
  browser.reconnect();
  browser.rollBackObservedClock();
  assert.equal(fence.reservation.released, false);
});

test("active provider and forged terminal cannot release before verified terminal", async () => {
  const { fence, browser, request } = make();
  const claim = fence.claim(request);
  await fence.send(claim.token, request.ownerId);
  const project = fakeProjectReceipt(claim);
  assert.equal(fence.release(releaseArgs(fence, claim, project,
    { source: "untrusted-tab", status: "Terminal",
      effectUuid: claim.effectUuid, sessionId: "fake-session-1" })), false);
  assert.equal(fence.release(releaseArgs(fence, claim, project,
    { source: "FAKE_PROVIDER", status: "Terminal",
      effectUuid: claim.effectUuid, sessionId: "foreign-session" })), false);
  assert.equal(fence.reservation.released, false);
});

test("foreign project receipt, effect UUID and reservation are all denied", async () => {
  const { fence, browser, request } = make();
  const claim = fence.claim(request);
  await fence.send(claim.token, request.ownerId);
  const terminal = fakeTerminal(browser, claim);
  for (const evidence of [
    { ...fakeProjectReceipt(claim), source: "FAKE_OTHER_LEDGER" },
    { ...fakeProjectReceipt(claim), effectUuid: "foreign-effect" },
    { ...fakeProjectReceipt(claim), reservationReceipt: "foreign-account" },
    { ...fakeProjectReceipt(claim), token: "foreign-claim" }
  ]) {
    assert.equal(fence.release(releaseArgs(fence, claim, evidence, terminal)), false);
  }
  assert.equal(fence.reservation.released, false);
});

test("stale account CAS prevents terminal release until independent reconciliation", async () => {
  const { fence, browser, request } = make();
  const claim = fence.claim(request);
  await fence.send(claim.token, request.ownerId);
  const priorAccountHead = fence.accountVersion;
  fence.externalAccountMutation();
  const project = fakeProjectReceipt(claim);
  const provider = fakeTerminal(browser, claim);
  assert.equal(fence.release({ token: claim.token, expectedAccountVersion: priorAccountHead,
    projectEvidence: project, providerEvidence: provider }), false);
  assert.equal(fence.reservation.released, false);
  assert.equal(fence.release(releaseArgs(fence, claim, project, provider)), true);
  assert.equal(fence.reservation.released, true);
});

test("verified terminal releases original account reservation exactly once", async () => {
  const { fence, browser, request } = make();
  const claim = fence.claim(request);
  await fence.send(claim.token, request.ownerId);
  const project = fakeProjectReceipt(claim);
  const terminal = fakeTerminal(browser, claim);
  assert.equal(fence.release(releaseArgs(fence, claim, project, terminal)), true);
  const after = fence.accountVersion;
  assert.equal(fence.release(releaseArgs(fence, claim, project, terminal)), false);
  assert.equal(fence.accountVersion, after);
  assert.equal(browser.providerSubmissions.length, 1);
});

test("verified prestart no-send requires two independent fake no-send proofs", async () => {
  const { fence, browser, request } = make();
  const claim = fence.claim(request);
  browser.enqueue("verifiedPrestartNoSend");
  assert.equal((await fence.send(claim.token, request.ownerId)).kind, "PrestartNoSend");
  assert.equal(browser.providerSubmissions.length, 0);
  const project = fakeProjectReceipt(claim, "VerifiedNoSend");
  assert.equal(fence.release(releaseArgs(fence, claim, project,
    { source: "FAKE_PROVIDER", status: "Unknown", effectUuid: claim.effectUuid })), false);
  assert.equal(fence.release(releaseArgs(fence, claim, project,
    { source: "FAKE_PROVIDER", status: "VerifiedNoProviderSession",
      effectUuid: claim.effectUuid })), true);
  assert.equal((await fence.send(claim.token, request.ownerId)).kind, "DeniedOrAlreadyStarted");
  assert.equal(browser.attempts.length, 1);
});

test("stale account head denies pre-send project claim", () => {
  const { fence, request, browser } = make();
  fence.externalAccountMutation();
  assert.equal(fence.claim(request), null);
  assert.equal(browser.attempts.length, 0);
  assert.equal(fence.reservation.released, false);
});

test("concurrent calls against one claim mark Dispatching before any await", async () => {
  const { fence, browser, request } = make();
  const claim = fence.claim(request);
  const [first, second, third] = await Promise.all([
    fence.send(claim.token, request.ownerId),
    fence.send(claim.token, request.ownerId),
    fence.send(claim.token, request.ownerId)
  ]);
  assert.equal(first.kind, "Submitted");
  assert.equal(second.kind, "DeniedOrAlreadyStarted");
  assert.equal(third.kind, "DeniedOrAlreadyStarted");
  assert.equal(claim.effectStarts, 1);
  assert.equal(browser.providerSubmissions.length, 1);
});

test("provider-looking terminal with matching session is insufficient while provider is Active", async () => {
  const { fence, browser, request } = make();
  const claim = fence.claim(request);
  const result = await fence.send(claim.token, request.ownerId);
  assert.equal(result.kind, "Submitted");
  assert.equal(browser.observeProvider(claim.effectUuid).status, "Active");
  const forged = { source: "FAKE_PROVIDER", effectUuid: claim.effectUuid,
    sessionId: result.sessionId, status: "Terminal" };
  assert.equal(fence.release(releaseArgs(fence, claim,
    fakeProjectReceipt(claim), forged)), false);
  assert.equal(fence.reservation.released, false);
  const verified = fakeTerminal(browser, claim);
  assert.equal(fence.release(releaseArgs(fence, claim,
    fakeProjectReceipt(claim), verified)), true);
});

test("duplicate account reservation is never minted to recover a lost browser acknowledgement", async () => {
  const { fence, browser, request } = make();
  const claim = fence.claim(request);
  browser.enqueue("ackLostAfterSend");
  assert.equal((await fence.send(claim.token, request.ownerId)).kind, "Uncertain");
  assert.equal(fence.reserve("replacement-receipt"), false);
  assert.equal(fence.reservation.receipt, request.reservationReceipt);
  assert.equal(fence.reservation.released, false);
  assert.equal(browser.providerSubmissions.length, 1);
});
