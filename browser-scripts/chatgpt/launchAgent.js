// Project-owned ChatGPT UI automation. Jarvis opens and closes the tab separately.
// The browser extension evaluates execute(args) in a USER_SCRIPT world.
async function execute(args) {
  const prompt = args?.prompt;
  const startUrl = "https://chatgpt.com/";
  if (typeof prompt !== "string" || !prompt.trim() || prompt.length > 24000) {
    return { status: "notSubmitted", reason: "InvalidPrompt" };
  }
  const effortLabels = ["Instant", "Medium", "High", "Extra High"];
  const rawEffort = args?.thinkingEffort;
  const normalizedEffort = typeof rawEffort === "string"
    ? rawEffort.trim().toLowerCase().replace(/[\s_-]/g, "") : null;
  const effortIndex = rawEffort === undefined ? null
    : effortLabels.findIndex(label => label.toLowerCase().replace(/\s/g, "") === normalizedEffort);
  if (effortIndex === -1) return { status: "notSubmitted", reason: "InvalidThinkingEffort" };
  if (location.href !== startUrl) {
    return { status: "notSubmitted", reason: "UnexpectedStartUrl" };
  }
  // A single budget for hydration, insertion, acknowledgement and route capture.
  // Leaves a three-second margin inside the bridge's 30-second execution limit.
  const overallMs = Math.min(Math.max(Number(args.timeoutMs) || 27000, 100), 27000);
  const deadline = Date.now() + overallMs;
  const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
  const until = async predicate => {
    while (Date.now() < deadline) {
      const value = predicate();
      if (value) return value;
      await delay(Math.min(75, Math.max(1, deadline - Date.now())));
    }
    return predicate();
  };
  // Effort-selection failures must resolve well before the generic 30-second
  // browser execution deadline; no possible Send has happened at this stage.
  const untilWithin = async (predicate, maxMs) => {
    const stop = Math.min(deadline, Date.now() + maxMs);
    while (Date.now() < stop) {
      const value = predicate();
      if (value) return value;
      await delay(Math.min(75, Math.max(1, stop - Date.now())));
    }
    return predicate();
  };
  // No arbitrary DOM, innerHTML, draft text, or cookies leave the page.
  // Only bounded, structural selector observations useful for repairing the script.
  const selectorCounts = () => ({
    modeButtons: document.querySelectorAll('button[type="button"][aria-pressed]').length,
    chatLabels: Array.from(document.querySelectorAll('button[type="button"][aria-pressed]'))
      .filter(button => button.textContent.trim() === "Chat").length,
    workLabels: Array.from(document.querySelectorAll('button[type="button"][aria-pressed]'))
      .filter(button => button.textContent.trim() === "Work").length,
    composers: document.querySelectorAll('[data-composer-input-layout] [contenteditable="true"][role="textbox"]').length,
    editableTextboxes: document.querySelectorAll('[contenteditable="true"][role="textbox"]').length,
    sendButtons: document.querySelectorAll('button[type="submit"][aria-label="Send"]').length,
    modelPickers: document.querySelectorAll('button[aria-label="Select ChatGPT model"]').length,
    effortSliders: document.querySelectorAll('[role="slider"][aria-valuenow]').length
  });
  const incompatible = (stage, reason) => ({
    status: "uiIncompatible",
    stage,
    reason,
    diagnostics: { documentReadyState: document.readyState || "unknown", ...selectorCounts() }
  });
  let sendAttempted = false;
  try {
  // ChatGPT can remember Work mode even when opening the new-chat homepage.
  // Resolve by visible label and aria-pressed, not generated classes or DOM order.
  const modeButton = name => {
    const matches = Array.from(document.querySelectorAll('button[type="button"][aria-pressed]'))
      .filter(button => button.textContent.trim() === name);
    return matches.length === 1 ? matches[0] : null;
  };
  const modes = await until(() => {
    const chat = modeButton("Chat");
    const work = modeButton("Work");
    return chat && work ? { chat, work } : null;
  });
  if (!modes) return incompatible("modeSelection", "ModeToggleUnavailable");
  if (modes.chat.getAttribute("aria-pressed") !== "true") {
    modes.chat.click();
  }
  const chatReady = await until(() => {
    const chat = modeButton("Chat");
    const work = modeButton("Work");
    return chat?.getAttribute("aria-pressed") === "true" &&
      work?.getAttribute("aria-pressed") === "false";
  });
  if (!chatReady) return incompatible("modeSelection", "ChatModeUnavailable");

  const composerSelector = '[data-composer-input-layout] [contenteditable="true"][role="textbox"]';
  const sendSelector = 'button[type="submit"][aria-label="Send"]:not(:disabled)';
  const initialUrl = location.href;
  const initialTurns = document.querySelectorAll('[data-message-author-role="user"]').length;
  // Current ChatGPT renders user messages under data-user-message-bubble,
  // without the older data-message-author-role marker.
  const initialBubbles = document.querySelectorAll('[data-user-message-bubble]').length;
  const normalizedPrompt = prompt.replace(/\r\n/g, "\n").replace(/\u00a0/g, " ").trim();
  const composer = await until(() => document.querySelector(composerSelector));
  if (!composer) return incompatible("composer", "ComposerUnavailable");
  const readEditor = () => composer.innerText.replace(/\r\n/g, "\n").replace(/\u00a0/g, " ");
  if (readEditor().trim()) return { status: "notSubmitted", reason: "ExistingDraft" };
  if (document.querySelector(sendSelector)) return { status: "notSubmitted", reason: "UnexpectedSendState" };

  // An explicit effort request must be selected through the actual UI.
  // Omitting thinkingEffort leaves the account's current choice alone.
  const modelPicker = () => {
    const matches = Array.from(document.querySelectorAll('button[aria-label="Select ChatGPT model"]'));
    return matches.length === 1 ? matches[0] : null;
  };
  const currentEffort = () => {
    const lines = (modelPicker()?.innerText || "").split(/\r?\n/).map(x => x.trim()).filter(Boolean);
    return lines.length === 2 && lines[0] === "Thinking effort" && effortLabels.includes(lines[1])
      ? lines[1] : null;
  };
  if (effortIndex !== null) {
    const picker = await untilWithin(modelPicker, 4500);
    if (!picker) return incompatible("effortSelection", "ModelPickerUnavailable");
    const requested = effortLabels[effortIndex];
    const selected = await untilWithin(currentEffort, 4500);
    if (!selected) return incompatible("effortSelection", "EffortLabelUnavailable");
    if (selected !== requested) {
      picker.click();
      const powerControl = () => {
        const visible = Array.from(document.querySelectorAll('[role="menu"]'))
          .filter(menu => menu.getClientRects().length > 0);
        const matching = visible.filter(menu =>
          menu.querySelector('[role="menuitem"][aria-label="Power"][data-reasoning-slider="true"]'));
        return matching.length === 1
          ? matching[0].querySelector('[role="menuitem"][aria-label="Power"][data-reasoning-slider="true"]')
          : null;
      };
      const sliderState = () => {
        const power = powerControl();
        const slider = power?.querySelector('[role="slider"][aria-valuenow]');
        if (!slider || slider.getAttribute("aria-valuemin") !== "0" ||
            slider.getAttribute("aria-valuemax") !== String(effortLabels.length - 1) ||
            slider.closest('[aria-disabled="true"]') ||
            !power.getAttribute("aria-keyshortcuts")?.includes("ArrowLeft") ||
            !power.getAttribute("aria-keyshortcuts")?.includes("ArrowRight")) return null;
        const index = Number(slider.getAttribute("aria-valuenow"));
        return Number.isInteger(index) && index >= 0 && index < effortLabels.length
          ? { power, index } : null;
      };
      const initialSlider = await untilWithin(sliderState, 3000);
      if (!initialSlider) return incompatible("effortSelection", "EffortSliderUnavailable");
      if (effortLabels[initialSlider.index] !== selected)
        return incompatible("effortSelection", "EffortSliderLabelMismatch");
      for (let index = initialSlider.index; index !== effortIndex;) {
        const step = effortIndex > index ? 1 : -1;
        const key = step > 0 ? "ArrowRight" : "ArrowLeft";
        const state = sliderState();
        if (!state || state.index !== index)
          return incompatible("effortSelection", "EffortSliderChanged");
        state.power.focus();
        state.power.dispatchEvent(new KeyboardEvent("keydown", {
          key, code: key, bubbles: true, cancelable: true
        }));
        const next = index + step;
        const advanced = await untilWithin(() => {
          const updated = sliderState();
          return updated?.index === next && currentEffort() === effortLabels[next];
        }, 1800);
        if (!advanced) return incompatible("effortSelection", "EffortChangeUnconfirmed");
        index = next;
      }
      if (currentEffort() !== requested)
        return incompatible("effortSelection", "EffortSelectionUnconfirmed");
      // Radix may close the menu automatically. Clicking the trigger again
      // can reopen it, so close an actually open menu with Escape instead.
      if (picker.getAttribute("aria-expanded") === "true") {
        const menu = Array.from(document.querySelectorAll('[role="menu"]'))
          .find(item => item.getClientRects().length > 0 &&
            item.querySelector('[role="menuitem"][aria-label="Power"][data-reasoning-slider="true"]'));
        if (!menu) return incompatible("effortSelection", "EffortMenuUnavailable");
        menu.dispatchEvent(new KeyboardEvent("keydown", {
          key: "Escape", code: "Escape", bubbles: true, cancelable: true
        }));
        if (!await untilWithin(() => picker.getAttribute("aria-expanded") === "false", 2000))
          return incompatible("effortSelection", "ModelPickerDidNotClose");
      }
    }
    if (currentEffort() !== requested)
      return incompatible("effortSelection", "EffortSelectionUnconfirmed");
  }
  // The composer can receive a restored draft during the mode/effort transition.
  if (readEditor().trim()) return { status: "notSubmitted", reason: "ExistingDraft" };
  // Safe live preflight: exercise selectors/settings without editing or sending.
  if (args?.preflightOnly === true) return {
    status: "notSubmitted", reason: "PreflightOnly",
    ...(effortIndex !== null ? { thinkingEffort: effortLabels[effortIndex] } : {})
  };

  composer.focus();
  // ProseMirror must observe the editing operation. Setting textContent bypasses its state.
  const inserted = document.execCommand("insertText", false, prompt);
  if (!inserted || readEditor() !== prompt.replace(/\r\n/g, "\n")) {
    return incompatible("composer", "TextInsertionFailed");
  }
  const send = await until(() => document.querySelector(sendSelector));
  if (!send) return incompatible("submitControl", "SendUnavailable");
  if (readEditor() !== prompt.replace(/\r\n/g, "\n")) {
    return incompatible("composer", "ComposerChanged");
  }
  if (effortIndex !== null && currentEffort() !== effortLabels[effortIndex]) {
    return incompatible("effortSelection", "EffortChangedBeforeSend");
  }
  // Once this click is attempted the effect is non-idempotent. Never try again.
  try {
    sendAttempted = true;
    send.click();
  } catch {
    return { status: "outcomeUnknown", reason: "SendClickUncertain" };
  }

  const evidence = await until(() => {
    const legacyTurn = document.querySelectorAll('[data-message-author-role="user"]').length > initialTurns;
    // Match the actual newly rendered user bubble, not a sidebar title or
    // arbitrary page text. Never include prompt content in diagnostics.
    const matchingBubble = Array.from(document.querySelectorAll('[data-user-message-bubble]'))
      .slice(initialBubbles)
      .some(bubble => (bubble.innerText || "").replace(/\r\n/g, "\n").replace(/\u00a0/g, " ").trim() === normalizedPrompt);
    const newUserTurn = legacyTurn || matchingBubble;
    const routeChanged = location.href !== initialUrl && location.origin === new URL(startUrl).origin;
    const composerCleared = readEditor().trim().length === 0;
    // Some builds only expose route transition plus cleared composer and a
    // generation control. A matching user bubble is stronger evidence even if
    // generation already completed before acknowledgement was checked.
    const generating = Boolean(document.querySelector('[aria-label="Stop generating"], [data-testid="stop-button"]'));
    if (newUserTurn || (routeChanged && composerCleared && generating)) {
      return { newUserTurn, routeChanged, matchingBubble };
    }
    return null;
  });
  if (!evidence) return { status: "outcomeUnknown", reason: "SubmissionUnconfirmed" };

  // The user turn may render before SPA navigation. Wait for the actual URL,
  // never invent an identifier or resubmit the prompt.
  const conversationUrl = await until(() => {
    if (location.origin !== new URL(startUrl).origin) return null;
    const current = new URL(location.href);
    // ChatGPT can first navigate to a temporary /c/local-chatgpt:... route.
    // Only the server-issued UUID is a durable conversation URL. Closing
    // an agent tab on the optimistic route risks interrupting creation.
    if (!/^\/c\/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\/?$/i.test(current.pathname)) return null;
    current.search = "";
    current.hash = "";
    return current.href;
  });
  if (!conversationUrl) return { status: "outcomeUnknown", reason: "ConversationUrlUnavailable" };
  return {
    status: "submitted",
    conversationUrl,
    ...(effortIndex !== null ? { thinkingEffort: effortLabels[effortIndex] } : {}),
    evidence: [
      ...(evidence.newUserTurn ? ["newUserTurn"] : []),
      ...(evidence.matchingBubble ? ["matchingUserBubble"] : []),
      "conversationRoute"
    ],
    submittedAtUtc: new Date().toISOString()
  };
  } catch {
    // Unexpected DOM exceptions are still provider compatibility failures if
    // we have not attempted Send; after Send, the outcome is unknowable.
    return sendAttempted
      ? { status: "outcomeUnknown", reason: "UnexpectedUiErrorAfterSend" }
      : incompatible("unexpected", "UnexpectedUiError");
  }
}
