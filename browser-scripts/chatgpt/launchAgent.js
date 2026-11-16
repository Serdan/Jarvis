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
  // This script is for new Jarvis-owned home tabs only. An existing user turn
  // means this is not an empty new-chat surface, even if the URL still matches.
  if (initialUrl !== startUrl || initialTurns || initialBubbles) {
    return notSubmitted("ExistingConversation");
  }
  const normalizedPrompt = prompt.replace(/\r\n/g, "\n").replace(/\u00a0/g, " ").trim();
  const composer = await until(() => {
    const editors = document.querySelectorAll(composerSelector);
    return editors.length === 1 ? editors[0] : null;
  });
  if (!composer) return incompatible("composer", "ComposerUnavailable");
  if (location.href !== startUrl) return notSubmitted("UnexpectedStartUrl");
  const readEditor = () => composer.innerText.replace(/\r\n/g, "\n").replace(/\u00a0/g, " ");
  if ((composer.textContent || "").length > 0) {
    // Only actual editor text, not a rendered empty-paragraph newline,
    // counts as a restored draft.
    // Any restored draft on the newly Jarvis-created home tab is disposable.
    // Replace it through ProseMirror editing, not direct DOM text mutation.
    composer.focus();
    const selection = window.getSelection();
    if (!selection) return incompatible("composer", "DraftSelectionUnavailable");
    const range = document.createRange();
    range.selectNodeContents(composer);
    selection.removeAllRanges();
    selection.addRange(range);
    const removed = document.execCommand("delete", false, null);
    selection.removeAllRanges();
    if (!removed || (composer.textContent || "").length > 0) return incompatible("composer", "DraftClearFailed");
  }
  // ProseMirror can clear visible text before React disables Send. Wait for
  // three empty/inactive observations rather than inserting into stale state.
  // Never click Send to clear an unrelated restored draft.
  const settleDeadline = Math.min(deadline, Date.now() + 1800);
  let settledSamples = 0;
  while (true) {
    if (location.href !== startUrl) return notSubmitted("UnexpectedStartUrl");
    if (document.querySelectorAll('[data-message-author-role="user"]').length !== initialTurns ||
        document.querySelectorAll('[data-user-message-bubble]').length !== initialBubbles) {
      return notSubmitted("ExistingConversation");
    }
    const editors = document.querySelectorAll(composerSelector);
    if (editors.length !== 1 || editors[0] !== composer) {
      return incompatible("draftStabilization", "ComposerChanged");
    }
    if ((composer.textContent || "").length !== 0 || readEditor().trim().length !== 0) {
      return incompatible("draftStabilization", "DraftReappeared");
    }
    settledSamples = document.querySelector(sendSelector) ? 0 : settledSamples + 1;
    if (settledSamples >= 3) break;
    if (Date.now() >= settleDeadline) {
      return {
        ...notSubmitted("UnexpectedSendState"), stage: "draftStabilization",
        diagnostics: { documentReadyState: document.readyState || "unknown", ...selectorCounts() }
      };
    }
    await delay(Math.min(75, Math.max(1, settleDeadline - Date.now())));
  }

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
  if (location.href !== startUrl) return notSubmitted("UnexpectedStartUrl");
  if (document.querySelectorAll('[data-message-author-role="user"]').length !== initialTurns ||
      document.querySelectorAll('[data-user-message-bubble]').length !== initialBubbles) {
    return notSubmitted("ExistingConversation");
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
