"use strict";
// OFFLINE PROTOTYPE ONLY. No production consumer; no authenticated site access.
// This models the trusted-input boundary on a disposable synthetic page.
// InputDispatched is NOT evidence of a provider-side Send or a conversation.
const HOME = "https://chatgpt.com/";

function makeIntent({operationId, tabId, registrationGeneration, promptDigest, selectionDigest}) {
  if (![operationId, promptDigest, selectionDigest].every(x => typeof x === "string" && x.length) ||
      !Number.isInteger(tabId) || !Number.isInteger(registrationGeneration))
    throw new TypeError("Invalid intent");
  return {operationId, tabId, registrationGeneration, promptDigest, selectionDigest,
    state:"Fresh"};
}
function validTab(tab, intent, windowId) {
  return !!tab && tab.id === intent.tabId && tab.windowId === windowId &&
    tab.url === HOME && !tab.pendingUrl && tab.status === "complete" &&
    !tab.discarded && !tab.frozen;
}
function validProbe(p, intent, now, maxAge) {
  if (!p || p.href !== HOME || p.visibilityState !== "visible" ||
      p.hasFocus !== true || p.documentReady !== "complete" ||
      p.promptDigest !== intent.promptDigest ||
      p.selectionDigest !== intent.selectionDigest ||
      p.sendCount !== 1 || p.sendEnabled !== true ||
      p.hitTestIsSend !== true || !Number.isFinite(p.sampledAt) ||
      p.sampledAt > now || now - p.sampledAt > maxAge ||
      !Number.isFinite(p.x) || !Number.isFinite(p.y) ||
      p.x < 0 || p.y < 0 || p.x >= p.viewportWidth ||
      p.y >= p.viewportHeight)
    return false;
  return true;
}
/**
 * This is intentionally a SINGLE non-retryable transport attempt, not a
 * synthetic-click recovery. A caller must have independently proved a
 * synthetic-click canary failed; it must not have clicked on THIS intent.
 * Everything before mousePressed is classifiable as no attempted input.
 *
 * Injected read-only gates model the extension's canonical authorization,
 * page pin, DOM/geometry proof and debugger transport. No runtime evaluator.
 * Persistent cross-worker CAS, consent and receipt authority are NOT built.
 */
async function dispatchOnce({
  intent, windowId, permitTrustedInput = false, syntheticCanaryFailed = false,
  syntheticClickAttempted = false, getTab, authorize, probe, sendCommand,
  now = Date.now, maxProbeAgeMs = 250
}) {
  if (!intent || intent.state !== "Fresh")
    return {status:"blocked", reason:"IntentAlreadyConsumed"};
  intent.state = "Consumed"; // hard one-shot even on gate failure
  if (!permitTrustedInput || !syntheticCanaryFailed || syntheticClickAttempted)
    return {status:"notAttempted", reason:"TrustedTransportNotAuthorized"};
  const blocked = reason => ({status:"notAttempted", reason});
  try {
    if (!(await authorize())) return blocked("RevokedBeforeInput");
    if (!validTab(await getTab(), intent, windowId)) return blocked("TabMismatch");
    const first = await probe();
    if (!validProbe(first, intent, now(), maxProbeAgeMs))
      return blocked("ComposerOrTargetMismatch");
    // mouseMoved does not press/click. It is not submission.
    await sendCommand({tabId:intent.tabId}, "Input.dispatchMouseEvent",
      {type:"mouseMoved", x:first.x, y:first.y});
    if (!(await authorize())) return blocked("RevokedBeforePress");
    if (!validTab(await getTab(), intent, windowId)) return blocked("NavigationBeforePress");
    const final = await probe();
    if (!validProbe(final, intent, now(), maxProbeAgeMs) ||
        final.x !== first.x || final.y !== first.y ||
        final.documentToken !== first.documentToken ||
        final.buttonToken !== first.buttonToken)
      return blocked("ChangedTargetBeforePress");
    if (!(await authorize())) return blocked("RevokedBeforePress");
    if (!validTab(await getTab(), intent, windowId)) return blocked("NavigationBeforePress");
    // First irreversible input. Unknown command responses cannot be retried.
    intent.state = "Attempted";
    try {
      await sendCommand({tabId:intent.tabId}, "Input.dispatchMouseEvent",
        {type:"mousePressed", x:final.x, y:final.y,
          button:"left", buttons:1, clickCount:1});
      // CDP acknowledge of mousePressed is NOT evidence of application Send.
      // Refuse an unrelated mouseReleased after navigation or revocation.
      if (!(await authorize()) || !validTab(await getTab(), intent, windowId)) {
        intent.state = "OutcomeUnknown";
        return {status:"outcomeUnknown", reason:"TargetChangedAfterPress"};
      }
      await sendCommand({tabId:intent.tabId}, "Input.dispatchMouseEvent",
        {type:"mouseReleased", x:final.x, y:final.y,
          button:"left", buttons:0, clickCount:1});
      intent.state = "InputDispatched";
      return {status:"inputDispatched", evidence:["cdpMousePressed","cdpMouseReleased"],
        providerSubmitted:false};
    } catch {
      intent.state = "OutcomeUnknown";
      return {status:"outcomeUnknown", reason:"CdpInputUncertain"};
    }
  } catch {
    // This catch covers pre-press failures only; no press has been issued.
    return blocked("PrePressTransportUnavailable");
  }
}
module.exports = {HOME, makeIntent, validTab, validProbe, dispatchOnce};
