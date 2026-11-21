/**
 * Stage A only: disposable fake BrowserBridge effect port.
 *
 * This fixture does not contact a browser, provider, Git remote, or account
 * authority. It deliberately has NO deduplication, release, or trust policy:
 * the future project-CAS adapter must fence duplicate invocations before
 * this port is called.
 */
export class FakeBrowserEffectPort {
  constructor() {
    this.scriptedOutcomes = [];
    this.attempts = [];
    this.providerSubmissions = [];
    this.providerSessions = new Map();
    this.tabs = new Set();
    this.connectionGeneration = 1;
    this.observedClock = 100;
  }

  enqueue(outcome) {
    if (![
      "accepted", "verifiedPrestartNoSend", "ackLostAfterSend",
      "timeoutAfterSend", "clientCrashAfterSend"
    ].includes(outcome)) {
      throw new Error("Unknown fake-browser outcome");
    }
    this.scriptedOutcomes.push(outcome);
  }

  async runInNewTab(intent) {
    for (const key of [
      "dispatchUuid", "effectUuid", "accountReservationReceipt",
      "projectSendClaimReceipt", "projectLedgerId", "projectEpoch"
    ]) {
      if (!intent || typeof intent[key] !== "string" || intent[key].length === 0) {
        throw new TypeError("Incomplete test intent: " + key);
      }
    }
    const outcome = this.scriptedOutcomes.shift() ?? "accepted";
    this.attempts.push({ intent: { ...intent }, outcome, generation: this.connectionGeneration });

    if (outcome === "verifiedPrestartNoSend") {
      // A true caller may rely on this only with independent no-send proof.
      return { kind: "PrestartNoSend", effectUuid: intent.effectUuid,
        evidence: "FAKE verified-before-bridge-start" };
    }

    // The fake browser is intentionally non-idempotent: if a caller repeats
    // the invocation after a lost ACK, two provider effects are observable.
    const sessionId = "fake-session-" + (this.providerSubmissions.length + 1);
    this.providerSubmissions.push({ ...intent, sessionId });
    this.providerSessions.set(intent.effectUuid, { status: "Active", sessionId });
    this.tabs.add(intent.effectUuid);

    if (outcome !== "accepted") {
      const error = new Error("OutcomeUnknown: fake " + outcome);
      error.code = "OutcomeUnknown";
      error.effectUuid = intent.effectUuid;
      throw error;
    }
    return { kind: "Submitted", effectUuid: intent.effectUuid, sessionId,
      receipt: "fake-browser-ack-" + sessionId };
  }

  closeTab(effectUuid) {
    this.tabs.delete(effectUuid);
    // Tab closure intentionally does not change providerSessions.
  }

  reconnect() {
    this.connectionGeneration += 1;
    this.tabs.clear();
    // The provider session and original submitted effects survive.
  }

  rollBackObservedClock() {
    this.observedClock -= 1000;
    // Neither provider effects nor session state depend on the local clock.
  }

  observeProvider(effectUuid) {
    const state = this.providerSessions.get(effectUuid);
    return state ? { effectUuid, ...state, source: "FAKE_PROVIDER" }
      : { effectUuid, status: "Unknown", source: "FAKE_PROVIDER" };
  }

  fakeProviderTerminal(effectUuid) {
    const state = this.providerSessions.get(effectUuid);
    if (!state) return { effectUuid, status: "Unknown", source: "FAKE_PROVIDER" };
    this.providerSessions.set(effectUuid, { ...state, status: "Terminal" });
    return { effectUuid, sessionId: state.sessionId, status: "Terminal",
      source: "FAKE_PROVIDER" };
  }
}

export const STAGE_A_NEGATIVE_CASES = Object.freeze([
  "two-workers-same-attempt-different-dispatch-uuid",
  "two-reviewers-one-frozen-review-claim",
  "account-reservation-won-project-cas-lost",
  "project-claim-ack-lost-before-effect",
  "verified-prestart-no-send",
  "browser-accepted-ack-lost",
  "browser-timeout-after-send",
  "client-crash-and-reconnect",
  "clock-rollback",
  "third-client-concurrent-cas",
  "foreign-or-replayed-effect-uuid",
  "stale-assignment-generation",
  "stale-review-version-or-source-ref",
  "session-unknown-after-tab-closure",
  "verified-terminal-once-only-account-release",
  "duplicate-foreign-or-stale-terminal-denied"
]);
