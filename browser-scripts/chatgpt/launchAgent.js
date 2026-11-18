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
  // A new tab can remember either Chat or Work, with any model and thinking
  // effort. Preserve the user's selected model and effort; only the Chat/Work
  // mode determines the destination of this Chat agent bootstrap.
  //
  // React can display mode controls before handlers hydrate. A single .click()
  // is not evidence of success. Re-resolve semantic controls on each sample,
  // retry only when Work is STILL selected on an exact zero-turn home, and
  // share the same absolute deadline with the composer/Send state machine.
  // If Chat is already selected, do not spend a startup delay or click.
  // Once Chat is established, later mode changes fail closed in surface().
  const composerSelector = '[data-composer-input-layout] [contenteditable="true"][role="textbox"]';
  const modeRetryMs = 200;
  let lastChatClick = -Infinity;
  let sawWorkSelected = false;
  let modeReady = false;
  while (Date.now() < deadline) {
    const homeFailure = emptyHomeFailure();
    if (homeFailure) return homeFailure;
    const chat = modeButton("Chat");
    const work = modeButton("Work");
    if (chat && work) {
      const chatSelected = chat.getAttribute("aria-pressed") === "true";
      const workSelected = work.getAttribute("aria-pressed") === "true";
      // Both-on and both-off are transient/ambiguous. Model and reasoning
      // controls are distinct from these exact semantic Chat/Work buttons.
      if (chatSelected && !workSelected) {
        modeReady = true;
        break;
      }
      if (workSelected && !chatSelected) {
        sawWorkSelected = true;
        if (document.readyState === "complete" &&
            document.querySelectorAll(composerSelector).length === 1 &&
            Date.now() - lastChatClick >= modeRetryMs) {
          // Recheck all preconditions immediately before each idempotent
          // Work-to-Chat selection; do not await between reproof and click.
          const beforeClickFailure = emptyHomeFailure();
          if (beforeClickFailure) return beforeClickFailure;
          if (modeButton("Chat") === chat && modeButton("Work") === work &&
              chat.getAttribute("aria-pressed") === "false" &&
              work.getAttribute("aria-pressed") === "true") {
            lastChatClick = Date.now();
            chat.click();
          }
        }
      }
    }
    await delay(Math.min(75, Math.max(1, deadline - Date.now())));
  }
  if (!modeReady) {
    const homeFailure = emptyHomeFailure();
    if (homeFailure) return homeFailure;
    return incompatible("modeSelection",
      sawWorkSelected ? "ChatModeUnavailable" : "ModeToggleUnavailable");
  }

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
  let inserted = false;
  let sawComposer = false;
  let lastTransient = null;
  let rebinds = 0;
  let rewrites = 0;
  let lastInsertError = null;
  const readEditor = () => (activeComposer.innerText || "")
    .replace(/\r\n/g, "\n").replace(/\u00a0/g, " ");

  // Outside an already established conversation, all unsent composer text is
  // disposable. Always clear the editor and insert exactly the requested
  // prompt. Do not preserve, merge with or wait for a restored draft.
  // Submission is separate and NEVER retried after Send may have been clicked.
  const surface = stage => {
    const homeFailure = emptyHomeFailure();
    if (homeFailure) return { failure: homeFailure };
    const chat = modeButton("Chat");
    const work = modeButton("Work");
    if (!chat || !work) return { transient: "ModeChanged" };
    if (chat.getAttribute("aria-pressed") !== "true" ||
        work.getAttribute("aria-pressed") !== "false") {
      return { failure: incompatible(stage, "ModeChanged") };
    }
    const editors = document.querySelectorAll(composerSelector);
    if (editors.length !== 1) return { transient: "ComposerChanged" };
    return { chat, work, composer: editors[0] };
  };

  // Rebind after any React replacement, then clear+insert in the new editor.
  // Rewriting is only allowed before Send and on the still-empty home.
  while (true) {
    if (Date.now() >= deadline) {
      if (lastTransient) return incompatible("composer",
        lastTransient === "ComposerChanged" && !sawComposer
          ? "ComposerUnavailable" : lastTransient);
      if (!sawComposer) return incompatible("composer", "ComposerUnavailable");
      if (inserted) return incompatible("submitControl", "SendUnavailable");
      if (lastInsertError) return incompatible("composer", lastInsertError);
      return { ...notSubmitted("LaunchDeadlineExceeded"), stage: "composer" };
    }

    // The editor may appear before the page finishes initial loading.
    // Wait for the document before touching its ProseMirror state.
    if (document.readyState !== "complete") {
      await delay(Math.min(75, Math.max(1, deadline - Date.now())));
      continue;
    }
    const stage = inserted ? "submitControl" : "composer";
    const current = surface(stage);
    if (current.failure) return current.failure;
    if (current.transient) {
      if (activeComposer && ++rebinds > 10) return incompatible(stage, "SurfaceUnstable");
      activeComposer = null;
      activeModes = null;
      inserted = false;
      lastTransient = current.transient;
    } else {
      lastTransient = null;
      sawComposer = true;
      if (current.composer !== activeComposer ||
          current.chat !== activeModes?.chat || current.work !== activeModes?.work) {
        if (activeComposer && ++rebinds > 10) return incompatible(stage, "SurfaceUnstable");
        activeComposer = current.composer;
        activeModes = { chat: current.chat, work: current.work };
        inserted = false;
      }

      if (inserted) {
        if (readEditor() !== insertedPrompt) {
          inserted = false;
        } else {
          const send = document.querySelector(sendSelector);
          if (send) {
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
            activeComposer = null;
            activeModes = null;
            inserted = false;
          }
        }
      }

      if (!inserted && activeComposer) {
        if (++rewrites > 20) return incompatible("composer", lastInsertError || "SurfaceUnstable");
        activeComposer.focus();
        const selection = window.getSelection();
        if (!selection) return incompatible("composer", "DraftSelectionUnavailable");
        const range = document.createRange();
        range.selectNodeContents(activeComposer);
        selection.removeAllRanges();
        selection.addRange(range);
        const hadText = Boolean((activeComposer.textContent || "").length || readEditor().trim());
        const deleted = document.execCommand("delete", false, null);
        selection.removeAllRanges();
        if ((hadText && !deleted) ||
            (activeComposer.textContent || "").length || readEditor().trim().length) {
          return incompatible("composer", "DraftClearFailed");
        }
        // Let ProseMirror apply the deletion before inserting the new prompt.
        // This is an editor update boundary, not a restored-draft wait.
        await Promise.resolve();
        // A turn or navigation during clear cancels before any insertion.
        const afterClear = surface("composer");
        if (afterClear.failure) return afterClear.failure;
        if (afterClear.transient || afterClear.composer !== activeComposer ||
            afterClear.chat !== activeModes.chat ||
            afterClear.work !== activeModes.work) {
          activeComposer = null;
          activeModes = null;
        } else {
          if (Date.now() >= deadline) {
            return { ...notSubmitted("LaunchDeadlineExceeded"), stage: "composer" };
          }
          activeComposer.focus();
          const didInsert = document.execCommand("insertText", false, prompt);
          if (!didInsert || readEditor() !== insertedPrompt) {
            // An editor can be visible before its input handlers finish
            // hydrating. Retry only on the verified pre-Send home, clearing
            // the editor again rather than preserving any stale text.
            lastInsertError = "TextInsertionFailed";
            activeComposer = null;
            activeModes = null;
            inserted = false;
          } else {
            lastInsertError = null;
            inserted = true;
          }
        }
      }
    }
    // Give React one normal UI turn to reconcile the inserted prompt
    // before proving exact text and allowing the single Send attempt.
    await delay(Math.min(75, Math.max(1, deadline - Date.now())));
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
