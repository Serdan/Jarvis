/**
 * Stage A fake-browser fixture self-tests, NOT native Scheduler acceptance.
 * No production source, live browser, remote Git writes or scheduling.
 *
 * Later adapter tests MUST inject this port beneath a real project-CAS
 * pre-send claim and account reservation implementation. The fake itself
 * deliberately permits duplicate effects to expose missing fences.
 */
import test from "node:test";
import assert from "node:assert/strict";
import {
  FakeBrowserEffectPort, STAGE_A_NEGATIVE_CASES
} from "./fixtures/native-project-effect-fake-browser.mjs";

const intent = (overrides = {}) => ({
  dispatchUuid: "dispatch-a",
  effectUuid: "effect-a",
  accountReservationReceipt: "account-receipt-a",
  projectSendClaimReceipt: "project-claim-a",
  projectLedgerId: "project-ledger-a",
  projectEpoch: "1",
  ...overrides
});

test("fixture is explicitly bounded and includes required negative gates", () => {
  assert.equal(STAGE_A_NEGATIVE_CASES.length, 16);
  assert.equal(new Set(STAGE_A_NEGATIVE_CASES).size, 16);
  for (const item of [
    "two-workers-same-attempt-different-dispatch-uuid",
    "two-reviewers-one-frozen-review-claim",
    "account-reservation-won-project-cas-lost",
    "browser-accepted-ack-lost",
    "session-unknown-after-tab-closure",
    "duplicate-foreign-or-stale-terminal-denied"
  ]) assert.ok(STAGE_A_NEGATIVE_CASES.includes(item));
});

test("verified prestart fixture has no browser/provider effect", async () => {
  const fake = new FakeBrowserEffectPort();
  fake.enqueue("verifiedPrestartNoSend");
  const result = await fake.runInNewTab(intent());
  assert.equal(result.kind, "PrestartNoSend");
  assert.equal(fake.attempts.length, 1);
  assert.equal(fake.providerSubmissions.length, 0);
  assert.equal(fake.observeProvider("effect-a").status, "Unknown");
});

for (const fault of [
  "ackLostAfterSend", "timeoutAfterSend", "clientCrashAfterSend"
]) {
  test("post-send " + fault + " is OutcomeUnknown despite real fake submission", async () => {
    const fake = new FakeBrowserEffectPort();
    fake.enqueue(fault);
    await assert.rejects(
      fake.runInNewTab(intent()),
      error => error?.code === "OutcomeUnknown" && error.effectUuid === "effect-a"
    );
    assert.equal(fake.providerSubmissions.length, 1);
    assert.equal(fake.providerSubmissions[0].effectUuid, "effect-a");
    assert.equal(fake.observeProvider("effect-a").status, "Active");
  });
}

test("fake accepts normal effect with exact original IDs", async () => {
  const fake = new FakeBrowserEffectPort();
  const submitted = await fake.runInNewTab(intent());
  assert.equal(submitted.kind, "Submitted");
  assert.equal(submitted.effectUuid, "effect-a");
  assert.equal(fake.providerSubmissions[0].accountReservationReceipt, "account-receipt-a");
  assert.equal(fake.providerSubmissions[0].projectSendClaimReceipt, "project-claim-a");
});

test("fake intentionally DOES NOT deduplicate: production fence is required", async () => {
  const fake = new FakeBrowserEffectPort();
  fake.enqueue("ackLostAfterSend");
  await assert.rejects(fake.runInNewTab(intent()), /OutcomeUnknown/);
  await fake.runInNewTab(intent());
  assert.equal(fake.providerSubmissions.length, 2,
    "The fake must expose duplicate sends; native project CAS must prevent them");
});

test("close-tab, reconnect and clock rollback never prove provider terminal", async () => {
  const fake = new FakeBrowserEffectPort();
  fake.enqueue("ackLostAfterSend");
  await assert.rejects(fake.runInNewTab(intent()), /OutcomeUnknown/);
  fake.closeTab("effect-a");
  assert.equal(fake.observeProvider("effect-a").status, "Active");
  fake.reconnect();
  fake.rollBackObservedClock();
  assert.equal(fake.providerSubmissions.length, 1);
  assert.equal(fake.observeProvider("effect-a").status, "Active");
  assert.equal(fake.connectionGeneration, 2);
});

test("foreign terminal cannot be mistaken for original provider terminal", async () => {
  const fake = new FakeBrowserEffectPort();
  await fake.runInNewTab(intent());
  const foreign = fake.fakeProviderTerminal("foreign-effect");
  assert.equal(foreign.status, "Unknown");
  assert.equal(fake.observeProvider("effect-a").status, "Active");
  const terminal = fake.fakeProviderTerminal("effect-a");
  assert.equal(terminal.effectUuid, "effect-a");
  assert.equal(terminal.status, "Terminal");
  // The fixture is not an account-CAS release verifier: repeat receipt is
  // possible and must be rejected/idempotent only by the future SUT.
  assert.equal(fake.fakeProviderTerminal("effect-a").status, "Terminal");
});

test("two unsynchronized callers can produce two effects; BrowserBridge is not CAS", async () => {
  const fake = new FakeBrowserEffectPort();
  const outcomes = await Promise.all([
    fake.runInNewTab(intent({ dispatchUuid: "worker-a", effectUuid: "effect-a" })),
    fake.runInNewTab(intent({ dispatchUuid: "worker-b", effectUuid: "effect-b" }))
  ]);
  assert.equal(outcomes.length, 2);
  assert.equal(fake.providerSubmissions.length, 2,
    "Actual Scheduler must serialize project claim before this port");
});

test("incomplete original-provenance input is rejected before fake send", async () => {
  const fake = new FakeBrowserEffectPort();
  await assert.rejects(
    fake.runInNewTab(intent({ projectSendClaimReceipt: "" })),
    /Incomplete test intent/
  );
  assert.equal(fake.attempts.length, 0);
  assert.equal(fake.providerSubmissions.length, 0);
});
