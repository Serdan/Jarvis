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
  const readyMs = Math.min(Math.max(Number(args.uiReadyTimeoutMs) || 10000, 100), 25000);
  const submitMs = Math.min(Math.max(Number(args.submitTimeoutMs) || 10000, 100), 25000);
  const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
  const until = async (predicate, timeout) => {
    const deadline = Date.now() + timeout;
    do {
      const value = predicate();
      if (value) return value;
      await delay(75);
    } while (Date.now() < deadline);
    return null;
  };
  const composerSelector = '[data-composer-input-layout] [contenteditable="true"][role="textbox"]';
  const sendSelector = 'button[type="submit"][aria-label="Send"]:not(:disabled)';
  const initialUrl = location.href;
  const initialTurns = document.querySelectorAll('[data-message-author-role="user"]').length;
  const composer = await until(() => document.querySelector(composerSelector), readyMs);
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
  const send = await until(() => document.querySelector(sendSelector), readyMs);
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
  }, submitMs);
  if (!evidence) return { status: "outcomeUnknown", reason: "SubmissionUnconfirmed" };

  const conversationUrl = new URL(location.href);
  if (conversationUrl.origin !== new URL(startUrl).origin) {
    return { status: "outcomeUnknown", reason: "UnexpectedNavigation" };
  }
  conversationUrl.search = "";
  conversationUrl.hash = "";
  return {
    status: "submitted",
    conversationUrl: conversationUrl.href !== initialUrl ? conversationUrl.href : null,
    evidence: [
      ...(evidence.newUserTurn ? ["newUserTurn"] : []),
      ...(evidence.routeChanged ? ["conversationRoute"] : [])
    ],
    submittedAtUtc: new Date().toISOString()
  };
}
