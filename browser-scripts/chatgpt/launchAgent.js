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
  // Fits inside the bridge's default 15-second execution limit.
  const overallMs = Math.min(Math.max(Number(args.timeoutMs) || 13000, 100), 14000);
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
  if (!modes) return { status: "notSubmitted", reason: "ModeToggleUnavailable" };
  if (modes.chat.getAttribute("aria-pressed") !== "true") {
    modes.chat.click();
  }
  const chatReady = await until(() => {
    const chat = modeButton("Chat");
    const work = modeButton("Work");
    return chat?.getAttribute("aria-pressed") === "true" &&
      work?.getAttribute("aria-pressed") === "false";
  });
  if (!chatReady) return { status: "notSubmitted", reason: "ChatModeUnavailable" };

  const composerSelector = '[data-composer-input-layout] [contenteditable="true"][role="textbox"]';
  const sendSelector = 'button[type="submit"][aria-label="Send"]:not(:disabled)';
  const initialUrl = location.href;
  const initialTurns = document.querySelectorAll('[data-message-author-role="user"]').length;
  const composer = await until(() => document.querySelector(composerSelector));
  if (!composer) return { status: "notSubmitted", reason: "ComposerUnavailable" };
  const readEditor = () => composer.innerText.replace(/\r\n/g, "\n").replace(/\u00a0/g, " ");
  if (readEditor().trim()) return { status: "notSubmitted", reason: "ExistingDraft" };
  if (document.querySelector(sendSelector)) return { status: "notSubmitted", reason: "UnexpectedSendState" };

  composer.focus();
  // ProseMirror must observe the editing operation. Setting textContent bypasses its state.
  const inserted = document.execCommand("insertText", false, prompt);
  if (!inserted || readEditor() !== prompt.replace(/\r\n/g, "\n")) {
    return { status: "notSubmitted", reason: "TextInsertionFailed" };
  }
  const send = await until(() => document.querySelector(sendSelector));
  if (!send) return { status: "notSubmitted", reason: "SendUnavailable" };
  if (readEditor() !== prompt.replace(/\r\n/g, "\n")) {
    return { status: "notSubmitted", reason: "ComposerChanged" };
  }
  // Once this click is attempted the effect is non-idempotent. Never try again.
  try {
    send.click();
  } catch {
    return { status: "outcomeUnknown", reason: "SendClickUncertain" };
  }

  const evidence = await until(() => {
    const newUserTurn = document.querySelectorAll('[data-message-author-role="user"]').length > initialTurns;
    const routeChanged = location.href !== initialUrl && location.origin === new URL(startUrl).origin;
    const composerCleared = readEditor().trim().length === 0;
    // A new user turn is positive evidence. Some ChatGPT builds only expose route
    // transition plus cleared composer and a visible generation control.
    const generating = Boolean(document.querySelector('[aria-label="Stop generating"], [data-testid="stop-button"]'));
    if (newUserTurn || (routeChanged && composerCleared && generating)) {
      return { newUserTurn, routeChanged };
    }
    return null;
  });
  if (!evidence) return { status: "outcomeUnknown", reason: "SubmissionUnconfirmed" };

  // The user turn may render before SPA navigation. Wait for the actual URL,
  // never invent an identifier or resubmit the prompt.
  const conversationUrl = await until(() => {
    if (location.origin !== new URL(startUrl).origin) return null;
    const current = new URL(location.href);
    if (!/^\/c\/[^/]+\/?$/.test(current.pathname)) return null;
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
      "conversationRoute"
    ],
    submittedAtUtc: new Date().toISOString()
  };
}
