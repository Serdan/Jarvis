// Project-owned ChatGPT UI automation. Jarvis opens and closes the tab separately.
// The browser extension evaluates execute(args) in a USER_SCRIPT world.
async function execute(args) {
  const prompt = args?.prompt;
  const startUrl = "https://chatgpt.com/";
  if (typeof prompt !== "string" || !prompt.trim() || prompt.length > 24000) {
    return { status: "notSubmitted", reason: "InvalidPrompt" };
  }
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
