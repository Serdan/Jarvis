// Project-owned ChatGPT UI automation. Jarvis opens and closes the tab separately.
// The browser extension evaluates execute(args) in a USER_SCRIPT world.
async function execute(args) {
  const prompt = args?.prompt;
  const startUrl = "https://chatgpt.com/";
  let sendAttempted = false;
  // A non-submission does not prove that a tab is disposable: a different
  // session may have created a turn or navigated while this script was waiting.
  // Only a still-home zero-turn surface can authorize failed-launch cleanup.
  const safeToDisposeCreatedTab = () => {
    try {
      return !sendAttempted && location.href === startUrl &&
        document.querySelectorAll('[data-message-author-role="user"]').length === 0 &&
        document.querySelectorAll('[data-user-message-bubble]').length === 0;
    } catch {
      return false;
    }
  };
  const notSubmitted = reason => ({
    status: "notSubmitted", reason, safeToDisposeCreatedTab: safeToDisposeCreatedTab()
  });
  // This page-facing USER_SCRIPT entrypoint can never authenticate a Work
  // execution or receive a private bearer. Reject unknown fields rather than
  // accidentally treating credential/bootstrap arguments as a supported API.
  // Callers must NOT place secrets in argsJson even when this check rejects it.
  // Preserve the historically ignored non-secret prior-send hint; it is
  // not evidence of a private host execution and has no send effect.
  const permittedArgs = new Set(["prompt", "timeoutMs", "confirmedSubmittedPrompts"]);
  if (!args || typeof args !== "object" || Array.isArray(args) ||
      Object.keys(args).some(key => !permittedArgs.has(key))) {
    return notSubmitted("UnsupportedPrivateBootstrapArguments");
  }
  if (typeof prompt !== "string" || !prompt.trim() || prompt.length > 24000) {
    return notSubmitted("InvalidPrompt");
  }
  if (location.href !== startUrl) {
    return notSubmitted("UnexpectedStartUrl");
  }
  // Never change even the mode in an already established conversation.
  if (document.querySelectorAll('[data-message-author-role="user"]').length ||
      document.querySelectorAll('[data-user-message-bubble]').length) {
    return notSubmitted("ExistingConversation");
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
    sendButtons: document.querySelectorAll('button[type="submit"][aria-label="Send"]').length
  });
  const incompatible = (stage, reason) => ({
    status: "uiIncompatible",
    stage,
    reason,
    safeToDisposeCreatedTab: safeToDisposeCreatedTab(),
    diagnostics: { documentReadyState: document.readyState || "unknown", ...selectorCounts() }
  });
  try {
  // ChatGPT can remember Work mode even when opening the new-chat homepage.
  // Resolve by visible label and aria-pressed, not generated classes or DOM order.
  const modeButton = name => {
    const matches = Array.from(document.querySelectorAll('button[type="button"][aria-pressed]'))
      .filter(button => button.textContent.trim() === name);
    return matches.length === 1 ? matches[0] : null;
  };
  // Recheck the empty-home precondition after waiting for mode controls.
  // Otherwise a turn arriving during hydration could have its mode changed.
  const emptyHomeFailure = () => {
    if (location.href !== startUrl) return notSubmitted("UnexpectedStartUrl");
    if (document.querySelectorAll('[data-message-author-role="user"]').length ||
        document.querySelectorAll('[data-user-message-bubble]').length) {
      return notSubmitted("ExistingConversation");
    }
    return null;
  };
  // Mode hydration may replace these button nodes after the first observation.
  // Resolve the semantic mode again after every await; only the original
  // Work-to-Chat selection may click a mode control.
  let modeWaitFailure = null;
  const modes = await until(() => {
    modeWaitFailure = emptyHomeFailure();
    if (modeWaitFailure) return true;
    const chat = modeButton("Chat");
    const work = modeButton("Work");
    return chat && work ? { chat, work } : null;
  });
  if (modeWaitFailure) return modeWaitFailure;
  if (!modes) return incompatible("modeSelection", "ModeToggleUnavailable");
  const beforeModeFailure = emptyHomeFailure();
  if (beforeModeFailure) return beforeModeFailure;
  if (Date.now() >= deadline) {
    return { ...notSubmitted("LaunchDeadlineExceeded"), stage: "modeSelection" };
  }
  const selectedChat = modeButton("Chat");
  const selectedWork = modeButton("Work");
  if (!selectedChat || !selectedWork) return incompatible("modeSelection", "ModeToggleUnavailable");
  // Work starts disabled during hydration. Never click disabled controls.
  const workEnabled = await until(() => {
    const homeFailure = emptyHomeFailure();
    if (homeFailure) { modeWaitFailure = homeFailure; return true; }
    const work = modeButton("Work");
    return work && !work.disabled ? work : null;
  });
  if (modeWaitFailure) return modeWaitFailure;
  if (!workEnabled || workEnabled.disabled) return incompatible("modeSelection", "WorkModeDisabled");
  workEnabled.click();
  const workReady = await until(() => {
    modeWaitFailure = emptyHomeFailure();
    if (modeWaitFailure) return true;
    return modeButton("Work")?.getAttribute("aria-pressed") === "true" &&
      modeButton("Chat")?.getAttribute("aria-pressed") === "false";
  });
  if (modeWaitFailure) return modeWaitFailure;
  if (!workReady) return incompatible("modeSelection", "WorkModeUnavailable");
  // Select the requested model through the visible provider UI.
  const modelControl = await until(() => document.querySelector('button[aria-label="Select ChatGPT model"]:not(:disabled)'));
  if (!modelControl) return incompatible("modelSelection", "ModelControlUnavailable");
  modelControl.click();
  const terra = await until(() => Array.from(document.querySelectorAll('[role="menuitemradio"]'))
    .find(node => node.innerText.trim() === "GPT-6.1 Terra"));
  if (!terra) return incompatible("modelSelection", "TerraUnavailable");
  terra.click();
  const modelReady = await until(() => {
    const control = document.querySelector('button[aria-label="Select ChatGPT model"]');
    return control?.getAttribute("data-selected-reasoning-effort") === "medium" &&
      Array.from(document.querySelectorAll('[data-model-selection-label]'))
        .some(node => node.getAttribute("data-model-selection-label") === "gpt-6.1-terra-wm") ? control : null;
  });
  if (!modelReady) return incompatible("modelSelection", "TerraMediumUnconfirmed");
  // Dismiss menu without selecting another model.
  if (modelReady.getAttribute("aria-expanded") === "true") modelReady.click();
  const selectedModel = "GPT-6.1 Terra";
  const selectedEffort = "medium";

  const composerSelector = '[data-composer-input-layout] [contenteditable="true"][role="textbox"]';
  const sendSelector = 'button[type="submit"][aria-label="Send"]:not(:disabled)';
  const initialUrl = location.href;
  const initialTurns = document.querySelectorAll('[data-message-author-role="user"]').length;
  // Modern ChatGPT user messages use the bubble marker instead of the old role.
  const initialBubbles = document.querySelectorAll('[data-user-message-bubble]').length;
  if (initialUrl !== startUrl || initialTurns || initialBubbles) {
    return notSubmitted("ExistingConversation");
  }
  const normalizedPrompt = prompt.replace(/\r\n/g, "\n").replace(/\u00a0/g, " ").trim();
  const insertedPrompt = prompt.replace(/\r\n/g, "\n");
  let activeComposer = null;
  let activeModes = null;
  let settledSamples = 0;
  let inserted = false;
  let sawComposer = false;
  let lastTransient = null;
  let unexpectedSendEnabled = false;
  let rebinds = 0;
  let clears = 0;
  const readEditor = () => activeComposer.innerText.replace(/\r\n/g, "\n").replace(/\u00a0/g, " ");

  // The only recoverable state is a still-home, zero-user-turn page with
  // Work actively selected. Actual Work selection, new turns and navigation
  // are not hydration: fail closed rather than click back or edit the page.
  const surface = stage => {
    const homeFailure = emptyHomeFailure();
    if (homeFailure) return { failure: homeFailure };
    const chat = modeButton("Chat");
    const work = modeButton("Work");
    if (!chat || !work) return { transient: "ModeChanged" };
    if (chat.getAttribute("aria-pressed") !== "false" ||
        work.getAttribute("aria-pressed") !== "true") {
      return { failure: incompatible(stage, "ModeChanged") };
    }
    const model = document.querySelector('button[aria-label="Select ChatGPT model"]');
    if (model?.getAttribute("data-selected-reasoning-effort") !== "medium" ||
        !Array.from(document.querySelectorAll('[data-model-selection-label]'))
          .some(node => node.getAttribute("data-model-selection-label") === "gpt-6.1-terra-wm")) {
      return { failure: incompatible(stage, "ModelOrEffortChanged") };
    }
    const editors = document.querySelectorAll(composerSelector);
    if (editors.length !== 1) return { transient: "ComposerChanged" };
    return { chat, work, composer: editors[0] };
  };

  // A bounded pre-Send state machine. Re-snapshot on React node replacement,
  // restart the three empty/inactive observations, and use ProseMirror to clear
  // any auto-restored unsent text. Insertion can be retried BEFORE Send only;
  // submission itself is a single, irrevocable attempt.
  while (true) {
    if (Date.now() >= deadline) {
      if (lastTransient) return incompatible(sawComposer ? "draftStabilization" : "composer",
        !sawComposer && lastTransient === "ComposerChanged" ? "ComposerUnavailable" : lastTransient);
      if (!sawComposer) return incompatible("composer", "ComposerUnavailable");
      if (inserted) return incompatible("submitControl", "SendUnavailable");
      if (unexpectedSendEnabled) {
        return {
          ...notSubmitted("UnexpectedSendState"), stage: "draftStabilization",
          diagnostics: { documentReadyState: document.readyState || "unknown", ...selectorCounts() }
        };
      }
      return { ...notSubmitted("LaunchDeadlineExceeded"), stage: "draftStabilization" };
    }

    let justInserted = false;
    const stage = inserted ? "submitControl" : sawComposer ? "draftStabilization" : "composer";
    const current = surface(stage);
    if (current.failure) return current.failure;
    if (current.transient) {
      // Temporary disappearance is only tolerated inside the overall deadline.
      if (activeComposer && ++rebinds > 10) return incompatible(stage, "SurfaceUnstable");
      activeComposer = null;
      activeModes = null;
      settledSamples = 0;
      inserted = false;
      lastTransient = current.transient;
      unexpectedSendEnabled = false;
    } else {
      lastTransient = null;
      sawComposer = true;
      if (current.composer !== activeComposer ||
          current.chat !== activeModes?.chat || current.work !== activeModes?.work) {
        if (activeComposer && ++rebinds > 10) return incompatible(stage, "SurfaceUnstable");
        activeComposer = current.composer;
        activeModes = { chat: current.chat, work: current.work };
        // A previously inserted prompt on an old editor is not proof that
        // the live replacement has matching ProseMirror state.
        settledSamples = 0;
        inserted = false;
        unexpectedSendEnabled = false;
      }

      if (inserted) {
        if (readEditor() !== insertedPrompt) {
          // Draft restoration can race our insertion in another home tab.
          // It has no value, but must never be submitted as our prompt.
          inserted = false;
          settledSamples = 0;
        } else {
          const send = document.querySelector(sendSelector);
          if (send) {
            // No awaited operation separates this complete re-proof from
            // the one Send. Guard against selector replacement even here.
            const verified = surface("submitControl");
            if (verified.failure) return verified.failure;
            if (!verified.transient &&
                verified.chat === activeModes.chat &&
                verified.work === activeModes.work &&
                verified.composer === activeComposer &&
                readEditor() === insertedPrompt &&
                document.querySelector(sendSelector) === send) {
              if (Date.now() >= deadline) {
                return { ...notSubmitted("LaunchDeadlineExceeded"), stage: "submitControl" };
              }
              try {
                sendAttempted = true;
                send.click();
              } catch {
                return { status: "outcomeUnknown", reason: "SendClickUncertain" };
              }
              break;
            }
            // Unsafe-to-click changes are recoverable only by re-stabilizing
            // the new zero-turn surface, never by clicking the stale button.
            activeComposer = null;
            activeModes = null;
            settledSamples = 0;
            inserted = false;
          }
        }
      }

      if (!inserted && activeComposer) {
        if ((activeComposer.textContent || "").length !== 0 || readEditor().trim().length !== 0) {
          if (++clears > 10) return incompatible("draftStabilization", "SurfaceUnstable");
          activeComposer.focus();
          const selection = window.getSelection();
          if (!selection) return incompatible("composer", "DraftSelectionUnavailable");
          const range = document.createRange();
          range.selectNodeContents(activeComposer);
          selection.removeAllRanges();
          selection.addRange(range);
          const removed = document.execCommand("delete", false, null);
          selection.removeAllRanges();
          if (!removed || (activeComposer.textContent || "").length !== 0) {
            return incompatible("composer", "DraftClearFailed");
          }
          settledSamples = 0;
          unexpectedSendEnabled = false;
        } else {
          // A disabled Send must hold for three consecutive samples of the
          // same buttons and editor. A stale enabled Send never permits input.
          unexpectedSendEnabled = Boolean(document.querySelector(sendSelector));
          settledSamples = unexpectedSendEnabled ? 0 : settledSamples + 1;
          if (settledSamples >= 3) {
            if (Date.now() >= deadline) {
              return { ...notSubmitted("LaunchDeadlineExceeded"), stage: "draftStabilization" };
            }
            activeComposer.focus();
            const didInsert = document.execCommand("insertText", false, prompt);
            if (!didInsert || readEditor() !== insertedPrompt) {
              return incompatible("composer", "TextInsertionFailed");
            }
            inserted = true;
            justInserted = true;
            unexpectedSendEnabled = false;
          }
        }
      }
    }
    // Let React's synchronous/microtask ProseMirror update complete before
    // re-proving the surface. Do not impose another 75ms before Send.
    if (justInserted) await Promise.resolve();
    else await delay(Math.min(75, Math.max(1, deadline - Date.now())));
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
    selectedMode: "Work", selectedModel, selectedEffort,
    conversationUrl,
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
