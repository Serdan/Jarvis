/**
 * DISPOSABLE STAGE-A MODEL ONLY. This is NOT a Git-CAS implementation, native
 * Scheduler authority, browser adapter, provider verifier, or production code.
 * All "CAS", roster, project and provider evidence is fake and process-local.
 */
export class FakeOnceOnlyProjectEffectFence {
  constructor(browser) {
    this.browser = browser;
    this.projectVersion = 0;
    this.accountVersion = 0;
    this.reservation = null;
    this.candidates = new Map();
    this.claims = new Map();
    this.claimedCandidates = new Set();
    this.usedDispatches = new Set();
    this.usedEffects = new Set();
  }

  registerCandidate(key, version, sourceRef) {
    this.candidates.set(key, { version, sourceRef });
  }

  reserve(receipt) {
    if (this.reservation || !receipt) return false;
    this.reservation = { receipt, released: false };
    this.accountVersion++;
    return true;
  }

  externalProjectMutation() { this.projectVersion++; }
  externalAccountMutation() { this.accountVersion++; }

  claim({ candidateKey, candidateVersion, sourceRef, ownerId,
    expectedProjectVersion, expectedAccountVersion,
    reservationReceipt, dispatchUuid, effectUuid }) {
    const canonical = this.candidates.get(candidateKey);
    if (!canonical || !ownerId || !sourceRef || !dispatchUuid || !effectUuid ||
        canonical.version !== candidateVersion || canonical.sourceRef !== sourceRef ||
        !this.reservation || this.reservation.released ||
        this.reservation.receipt !== reservationReceipt ||
        expectedAccountVersion !== this.accountVersion ||
        expectedProjectVersion !== this.projectVersion ||
        this.claimedCandidates.has(candidateKey) ||
        this.usedDispatches.has(dispatchUuid) ||
        this.usedEffects.has(effectUuid)) return null;
    const token = "fake-project-claim-" + (this.claims.size + 1);
    const claim = {
      token, candidateKey, candidateVersion, sourceRef, ownerId,
      dispatchUuid, effectUuid, reservationReceipt, status: "Claimed",
      effectStarts: 0, response: null
    };
    this.projectVersion++;
    this.claimedCandidates.add(candidateKey);
    this.usedDispatches.add(dispatchUuid);
    this.usedEffects.add(effectUuid);
    this.claims.set(token, claim);
    return claim;
  }

  observe(token) {
    const claim = this.claims.get(token);
    return claim ? { ...claim } : null;
  }

  async send(token, ownerId) {
    const claim = this.claims.get(token);
    if (!claim || claim.ownerId !== ownerId || claim.status !== "Claimed") {
      return { kind: "DeniedOrAlreadyStarted" };
    }
    // The marker is committed synchronously BEFORE the asynchronous fake send.
    claim.status = "Dispatching";
    claim.effectStarts++;
    const intent = {
      dispatchUuid: claim.dispatchUuid, effectUuid: claim.effectUuid,
      accountReservationReceipt: claim.reservationReceipt,
      projectSendClaimReceipt: claim.token,
      projectLedgerId: "FAKE-project-ledger", projectEpoch: "1"
    };
    try {
      const response = await this.browser.runInNewTab(intent);
      claim.status = response.kind;
      claim.response = response;
      return response;
    } catch (error) {
      claim.status = "Uncertain";
      claim.response = { kind: "Uncertain", reason: error?.code ?? "Unknown" };
      return claim.response;
    }
  }

  /**
   * Project and provider evidence are explicitly fake! A nonmatching,
   * incomplete, stale, nonterminal or untrusted observation never releases.
   * There is no fallback from tab closure, time, local liveness, or worker yield.
   */
  release({ token, expectedAccountVersion, projectEvidence, providerEvidence }) {
    const claim = this.claims.get(token);
    const account = this.reservation;
    if (!claim || !account || account.released ||
        claim.reservationReceipt !== account.receipt ||
        expectedAccountVersion !== this.accountVersion ||
        !projectEvidence || projectEvidence.source !== "FAKE_PROJECT_CAS" ||
        projectEvidence.token !== token ||
        projectEvidence.effectUuid !== claim.effectUuid ||
        projectEvidence.reservationReceipt !== account.receipt) return false;

    if (claim.status === "PrestartNoSend") {
      if (projectEvidence.status !== "VerifiedNoSend" ||
          providerEvidence?.source !== "FAKE_PROVIDER" ||
          providerEvidence?.status !== "VerifiedNoProviderSession" ||
          providerEvidence?.effectUuid !== claim.effectUuid) return false;
    } else {
      if (!["Submitted", "Uncertain"].includes(claim.status) ||
          projectEvidence.status !== "VerifiedTerminal" ||
          providerEvidence?.source !== "FAKE_PROVIDER" ||
          providerEvidence?.status !== "Terminal" ||
          providerEvidence?.effectUuid !== claim.effectUuid ||
          !providerEvidence?.sessionId ||
          this.browser.observeProvider(claim.effectUuid).status !== "Terminal" ||
          this.browser.observeProvider(claim.effectUuid).sessionId !==
            providerEvidence.sessionId) return false;
    }
    account.released = true;
    claim.status = "Released";
    this.accountVersion++;
    return true;
  }
}
