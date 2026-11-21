// Project-owned ChatGPT UI automation. Jarvis opens and closes the tab separately.
// The browser extension evaluates execute(args) in a USER_SCRIPT world.
async function execute(args) {
  const prompt = args?.prompt;
  const startUrl = "https://chatgpt.com/";
  let sendAttempted = false;
  let sendAttemptedAt = null;
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
  const permittedArgs = new Set(["prompt", "timeoutMs", "confirmedSubmittedPrompts", "selectionOnly"]);
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
  // ChatGPT has intermittently ignored a Send even when the DOM editor and
  // submit button looked ready in hidden tabs. Do not consume an uncertain
  // external effect from an inactive page: callers must create an active tab.
  if (document.visibilityState !== "visible") {
    return { ...notSubmitted("InactiveLaunchTab"), stage: "visibility" };
  }
  // A single budget for hydration, insertion, acknowledgement and route capture.
  // Leaves a three-second margin inside the bridge's 30-second execution limit.
  const overallMs = Math.min(Math.max(Number(args.timeoutMs) || 27000, 100), 27000);
  const deadline = Date.now() + overallMs;
  // Structural timing only: never return the prompt or raw page state.
  const unknownAfterSend = (stage, reason) => {
    const at = sendAttemptedAt ?? Date.now();
    return { status: "outcomeUnknown", reason, stage, timing: {
      budgetAtSendMs: Math.max(0, deadline - at),
      elapsedSinceSendMs: Math.max(0, Date.now() - at)
    } };
  };
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
  // Re-evaluate *every new session*; never cache a previous fallback choice.
  // The visible Work GPT-6.2 model is Astra; its default thinking effort is Medium.
  const preferences = [
    { mode: "Chat", model: "GPT-6", effort: "max", level: 3, label: "Extra High" },
    { mode: "Work", model: "GPT-6.2 Astra", effort: "medium", level: 1, label: "Medium" },
    { mode: "Chat", model: "GPT-6", effort: "none", level: 0, label: "Instant" }
  ];
  const composerSelector = '[data-composer-input-layout] [contenteditable="true"][role="textbox"]';
  const pickerSelector = 'button[aria-label="Select ChatGPT model"]';
  const modeButton = name => {
    const matches = Array.from(document.querySelectorAll('button[type="button"][aria-pressed]'))
      .filter(button => button.textContent.trim() === name);
    return matches.length === 1 ? matches[0] : null;
  };
  const emptyHomeFailure = () => {
    if (location.href !== startUrl) return notSubmitted("UnexpectedStartUrl");
    if (document.querySelectorAll('[data-message-author-role="user"],[data-user-message-bubble]').length)
      return notSubmitted("ExistingConversation");
    if (document.visibilityState !== "visible")
      return { ...notSubmitted("InactiveLaunchTab"), stage: "visibility" };
    return null;
  };
  const poll = async (predicate, untilAt) => {
    while (Date.now() < untilAt) {
      const value = predicate();
      if (value) return value;
      await delay(Math.min(75, Math.max(1, untilAt - Date.now())));
    }
    return predicate();
  };
  const picker = () => document.querySelector(pickerSelector);
  // Other site menus (sidebar, projects) are not model menus.
  const modelMenu = () => Array.from(document.querySelectorAll('[role="menu"]'))
    .find(menu => menu.getClientRects().length && menu.querySelector('[data-model-picker-view]')) || null;
  const menuView = () => modelMenu()?.querySelector('[data-model-picker-view]')?.getAttribute('data-model-picker-view');
  const radio = model => Array.from(modelMenu()?.querySelectorAll('[role="menuitemradio"]') || [])
    .find(item => (item.innerText || item.textContent || "").trim() === model) || null;
  const unavailableRadio = item => !item || item.getAttribute("aria-disabled") === "true" ||
    item.hasAttribute("data-disabled");
  const activeMenuControl = selector => Array.from(modelMenu()?.querySelectorAll(selector) || [])
    .find(item => !item.closest('[aria-hidden="true"],[inert]')) || null;
  const attempts = [];
  // Selection and React hydration can consume nearly the whole UI budget.
  // Reserve up to eight seconds after Send for the actual user bubble and
  // server-issued route, especially on slow background tabs. Preserve the
  // original three-second floor for deliberately short fixture timeouts.
  const acknowledgementReserveMs = Math.min(8000, Math.max(3000, overallMs - 12000));
  const selectionDeadline = deadline - acknowledgementReserveMs - 1500;
  // A slow composer must never use the time needed to confirm a Send and
  // capture the server-issued conversation route. After Send, the effect
  // is uncertain until confirmed and must never be replayed.
  const preSendDeadline = deadline - acknowledgementReserveMs;
  let selected = null;

  const choose = async (target, untilAt) => {
    const home = () => emptyHomeFailure();
    const retryWait = () => delay(Math.min(100, Math.max(1, untilAt - Date.now())));
    // Chat/Work controls can appear before their React click handlers hydrate.
    let lastModeClick = -Infinity;
    const modeReady = await poll(() => {
      if (home()) return null;
      const chat = modeButton("Chat");
      const work = modeButton("Work");
      if (!chat || !work) return null;
      const chatOn = chat.getAttribute("aria-pressed") === "true";
      const workOn = work.getAttribute("aria-pressed") === "true";
      if (chatOn === workOn) return null;
      if (target.mode === "Chat" ? chatOn : workOn) return true;
      const wanted = target.mode === "Chat" ? chat : work;
      if (document.readyState === "complete" &&
          document.querySelectorAll(composerSelector).length === 1 &&
          !wanted.disabled && Date.now() - lastModeClick >= 220) {
        lastModeClick = Date.now();
        if (!home()) wanted.click();
      }
      return null;
    }, untilAt);
    if (!modeReady) return "ModeUnavailable";

    let lastPickerClick = -Infinity;
    const openPicker = () => poll(() => {
      if (home()) return null;
      const control = picker();
      if (!control || control.disabled) return null;
      if (control.getAttribute("aria-expanded") === "true" && modelMenu()) return control;
      if (document.readyState === "complete" && Date.now() - lastPickerClick >= 220) {
        lastPickerClick = Date.now();
        control.click();
      }
      return null;
    }, untilAt);

    if (!await openPicker()) return "ModelPickerUnavailable";
    let model = radio(target.model);
    if (!model && menuView() === "simple") {
      const toggle = activeMenuControl('[data-model-picker-view-toggle]');
      if (toggle) toggle.click();
      await poll(() => menuView() === "advanced", untilAt);
      model = radio(target.model);
    }
    if (unavailableRadio(model)) return "ModelUnavailable";
    if (model.getAttribute("aria-checked") !== "true") {
      if (menuView() !== "advanced") {
        const toggle = activeMenuControl('[data-model-picker-view-toggle]');
        if (!toggle) return "ModelListUnavailable";
        toggle.click();
        if (!await poll(() => menuView() === "advanced", untilAt)) return "ModelListUnavailable";
      }
      model = radio(target.model);
      if (unavailableRadio(model) || model.closest('[aria-hidden="true"],[inert]'))
        return "ModelUnavailable";
      if (home()) return "HomeChanged";
      model.click();
      await retryWait();
      if (!await openPicker()) return "ModelSelectionUnconfirmed";
      if (!await poll(() => radio(target.model)?.getAttribute("aria-checked") === "true", untilAt))
        return "ModelSelectionUnconfirmed";
    }

    // The model list and reasoning slider are separate, mutually exclusive
    // views. Re-selecting the already checked radio returns to the slider.
    if (menuView() !== "simple") {
      const selectedRadio = radio(target.model);
      if (!selectedRadio || selectedRadio.closest('[aria-hidden="true"],[inert]'))
        return "EffortPickerUnavailable";
      selectedRadio.click();
      await retryWait();
      if (!await openPicker()) return "EffortPickerUnavailable";
      if (!await poll(() => menuView() === "simple", untilAt))
        return "EffortPickerUnavailable";
    }

    let lastValue = null;
    let stalls = 0;
    while (Date.now() < untilAt) {
      if (home()) return "HomeChanged";
      const control = picker();
      const menu = modelMenu();
      if (!menu || menuView() !== "simple" || !control) {
        if (!await openPicker()) return "EffortPickerUnavailable";
        await retryWait();
        continue;
      }
      const selectedRadio = radio(target.model);
      if (unavailableRadio(selectedRadio) || selectedRadio.getAttribute("aria-checked") !== "true")
        return "ModelChanged";
      const effort = control.getAttribute("data-selected-reasoning-effort");
      const power = activeMenuControl('[data-model-picker-power-slider]');
      const powerLabel = activeMenuControl('[data-power-disabled]');
      if (effort === target.effort) {
        if (powerLabel?.getAttribute("data-power-disabled") === "true" ||
            power?.querySelector('[data-locked="true"]')) return "EffortUnavailable";
        if (target.mode === "Work" &&
            !document.querySelector('[data-model-selection-label="gpt-6.2-astra-wm"]'))
          return "ModelUnconfirmed";
        // Close the picker before editing. No menu click can submit a prompt.
        if (control.getAttribute("aria-expanded") === "true") control.click();
        await delay(80);
        if (home() || !modeButton(target.mode) ||
            modeButton(target.mode)?.getAttribute("aria-pressed") !== "true" ||
            picker()?.getAttribute("data-selected-reasoning-effort") !== target.effort)
          return "SelectionChanged";
        return null;
      }
      const knob = power?.querySelector('[role="slider"]');
      const step = Number(knob?.getAttribute("aria-valuenow"));
      const maxStep = Number(knob?.getAttribute("aria-valuemax"));
      const keyboard = activeMenuControl('[data-reasoning-slider]');
      // The slider can become visible before its keyboard handler and value
      // finish hydrating. Treat missing controls as transient, not exhausted.
      if (!keyboard || !Number.isFinite(step) || !Number.isFinite(maxStep)) {
        await retryWait();
        continue;
      }
      if (target.level > maxStep || power?.querySelector('[data-locked="true"]'))
        return "EffortUnavailable";
      if (lastValue === step) stalls++;
      else stalls = 0;
      if (stalls >= 12) return "EffortUnavailable";
      lastValue = step;
      keyboard.focus();
      keyboard.dispatchEvent(new KeyboardEvent("keydown", {
        key: step < target.level ? "ArrowRight" : "ArrowLeft",
        code: step < target.level ? "ArrowRight" : "ArrowLeft",
        bubbles: true, cancelable: true
      }));
      await delay(Math.min(130, Math.max(1, untilAt - Date.now())));
    }
    return "EffortUnavailable";
  };

  for (let i = 0; i < preferences.length; i++) {
    const target = preferences[i];
    if (emptyHomeFailure()) return emptyHomeFailure();
    const reserve = (preferences.length - i - 1) * 1400;
    const cutoff = Math.min(selectionDeadline, Date.now() +
      Math.max(1300, Math.min(7500, selectionDeadline - Date.now() - reserve)));
    if (cutoff <= Date.now()) break;
    const failure = await choose(target, cutoff);
    if (!failure) { selected = target; break; }
    attempts.push({ mode: target.mode, model: target.model, effort: target.label, reason: failure });
  }
  if (!selected) {
    const homeFailure = emptyHomeFailure();
    if (homeFailure) return homeFailure;
    // Temporary quota/model unavailability must not permanently suspend a
    // recurring schedule. The next invocation will re-evaluate from #1.
    return { ...notSubmitted("PreferredConfigurationsUnavailable"), attempts };
  }
  const selectedMode = selected.mode;
  const selectedModel = selected.model;
  const selectedEffort = selected.effort;
  if (args.selectionOnly === true) {
    return { status: "selectionOnly", selectedMode, selectedModel,
      selectedEffort: selected.label, attempts };
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
  let effortRepairs = 0;
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
    if (chat.getAttribute("aria-pressed") !== (selectedMode === "Chat" ? "true" : "false") ||
        work.getAttribute("aria-pressed") !== (selectedMode === "Work" ? "true" : "false")) {
      return { failure: incompatible(stage, "ModeChanged") };
    }
    const modelControl = picker();
    if (!modelControl) return { transient: "ModelControlChanged" };
    if (modelControl.getAttribute("data-selected-reasoning-effort") !== selectedEffort) {
      return { transient: "SelectedEffortChanged" };
    }
    if (selectedMode === "Work") {
      // Work may temporarily omit its model label as React replaces the
      // composer. Reject contradictory labels, not an absent transient label.
      const labels = Array.from(document.querySelectorAll('[data-model-selection-label]'))
        .filter(node => node.getClientRects().length &&
          !node.closest('[aria-hidden="true"],[inert]'));
      if (labels.length && !labels.some(node =>
          node.getAttribute("data-model-selection-label") === "gpt-6.2-astra-wm")) {
        return { failure: incompatible(stage, "SelectedModelChanged") };
      }
    }
    const editors = document.querySelectorAll(composerSelector);
    if (editors.length !== 1) return { transient: "ComposerChanged" };
    return { chat, work, composer: editors[0] };
  };

  // Rebind after any React replacement, then clear+insert in the new editor.
  // Rewriting is only allowed before Send and on the still-empty home.
  while (true) {
    if (Date.now() >= preSendDeadline) {
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
      await delay(Math.min(75, Math.max(1, preSendDeadline - Date.now())));
      continue;
    }
    const stage = inserted ? "submitControl" : "composer";
    const current = surface(stage);
    if (current.failure) return current.failure;
    if (current.transient) {
      if (current.transient === "SelectedEffortChanged") {
        // React or another browser tab can reset the persisted effort after a
        // successful picker selection, including after typing the prompt.
        // Repair only before Send. Keep the exact unsent draft and prove it
        // again; a changed editor is rebound and rewritten by the normal loop.
        if (++effortRepairs > 2)
          return incompatible(stage, "SelectedEffortUnstable");
        const cutoff = Math.min(preSendDeadline, Date.now() + 2500);
        const failure = await choose(selected, cutoff);
        if (failure && effortRepairs >= 2)
          return incompatible(stage, "SelectedEffortRecoveryFailed");
        lastTransient = current.transient;
        continue;
      }
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
              if (Date.now() >= preSendDeadline) {
                return { ...notSubmitted("LaunchDeadlineExceeded"), stage: "submitControl" };
              }
              try {
                sendAttempted = true;
                sendAttemptedAt = Date.now();
                send.click();
              } catch {
                return unknownAfterSend("sendClick", "SendClickUncertain");
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
          if (Date.now() >= preSendDeadline) {
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
    await delay(Math.min(75, Math.max(1, preSendDeadline - Date.now())));
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
  if (!evidence) return unknownAfterSend("acknowledgement", "SubmissionUnconfirmed");

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
  if (!conversationUrl) return unknownAfterSend("conversationRoute", "ConversationUrlUnavailable");
  return {
    status: "submitted",
    selectedMode, selectedModel, selectedEffort: selected.label,
    attempts,
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
      ? unknownAfterSend("unexpected", "UnexpectedUiErrorAfterSend")
      : incompatible("unexpected", "UnexpectedUiError");
  }
}