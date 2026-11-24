// Project-owned ChatGPT UI automation. Jarvis opens and closes the tab separately.
// The browser extension evaluates execute(args) in a USER_SCRIPT world.
async function execute(args) {
  let observedSelection = null;
  const result = await executeLaunch(args, value => { observedSelection = value; });
  return { ...result, observedSelection };
}

async function executeLaunch(args, observeSelection) {
  const prompt = args?.prompt;
  // Explicit launch destination; the ordinary home remains the default.
  const startUrl = args?.startUrl ?? "https://chatgpt.com/";
  const validStartUrl = (() => {
    try {
      const url = new URL(startUrl);
      return url.origin === "https://chatgpt.com" &&
        !url.search && !url.hash &&
        (url.pathname === "/" ||
          /^\/g\/g-p-[a-z0-9]+\/project\/?$/.test(url.pathname));
    } catch { return false; }
  })();
  const projectStart = validStartUrl && new URL(startUrl).pathname !== "/";
  const atStart = () => {
    if (!validStartUrl) return false;
    try {
      const expected = new URL(startUrl);
      const current = new URL(location.href);
      return current.origin === expected.origin &&
        current.pathname.replace(/\/$/, "") === expected.pathname.replace(/\/$/, "") &&
        !current.search && !current.hash;
    } catch { return false; }
  };
  let sendAttempted = false;
  let sendAttemptedAt = null;
  // A non-submission does not prove that a tab is disposable: a different
  // session may have created a turn or navigated while this script was waiting.
  // Only a still-home zero-turn surface can authorize failed-launch cleanup.
  const safeToDisposeCreatedTab = () => {
    try {
      return !sendAttempted && atStart() &&
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
  const permittedArgs = new Set(["prompt", "timeoutMs", "confirmedSubmittedPrompts", "selectionOnly", "startUrl", "modelOverride"]);
  if (!args || typeof args !== "object" || Array.isArray(args) ||
      Object.keys(args).some(key => !permittedArgs.has(key))) {
    return notSubmitted("UnsupportedPrivateBootstrapArguments");
  }
  if (typeof prompt !== "string" || !prompt.trim() || prompt.length > 24000) {
    return notSubmitted("InvalidPrompt");
  }
  if (!atStart()) {
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
  // Re-evaluate on every fresh launch; never cache usage availability.
  // Strict fallback order: Chat Latest High, Work GPT-6.1 Terra Medium, Chat Latest Instant.
  // Advance only when the current selection cannot be confirmed before Send.
  const defaultPreferences = [
    { mode: "Chat", model: "Latest", effort: "high", level: 2, label: "High" },
    { mode: "Work", model: "GPT-6.1 Terra", effort: "medium", level: 1, label: "Medium" },
    { mode: "Chat", model: "Latest", effort: "none", level: 0, label: "Instant" }
  ];
  // Latest is a selectable alias, not a hard-coded version. Do not infer
  // the alias's backing model from this account's current version.
  const modelOverrides = new Map([
    ["latest-high", [defaultPreferences[0]]],
    ["latest-medium", [{ mode: "Chat", model: "Latest", effort: "medium", level: 1, label: "Medium" }]],
    ["gpt-5.6-instant", [{ mode: "Chat", model: "GPT-5.6", alternateModels: ["GPT-5.6 Sol"], effort: "none", level: 0, label: "Instant" }]],
    ["work-terra-medium", [defaultPreferences[1]]]
  ]);
  if (args.modelOverride !== undefined && !modelOverrides.has(args.modelOverride)) {
    return notSubmitted("UnsupportedModelOverride");
  }
  const preferences = args.modelOverride === undefined
    ? defaultPreferences : modelOverrides.get(args.modelOverride);
  // Only the default sequence permits pre-Send Work -> Chat Instant fallback.
  const sendFallback = preferences.find(target => target.mode === "Chat" && target.effort === "none");
  const composerSelector = '[data-composer-input-layout] [contenteditable="true"][role="textbox"]';
  const pickerSelector = 'button[aria-label="Select ChatGPT model"]';
  const projectChat = { getAttribute: name => name === "aria-pressed" ? "true" : null };
  const projectWork = { getAttribute: name => name === "aria-pressed" ? "false" : null };
  const modeButton = name => {
    if (projectStart) return name === "Chat" ? projectChat : projectWork;
    const matches = Array.from(document.querySelectorAll('button[type="button"][aria-pressed]'))
      .filter(button => button.textContent.trim() === name);
    return matches.length === 1 ? matches[0] : null;
  };
  const emptyHomeFailure = () => {
    if (!atStart()) return notSubmitted("UnexpectedStartUrl");
    if (document.querySelectorAll('[data-message-author-role="user"],[data-user-message-bubble]').length)
      return notSubmitted("ExistingConversation");
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
  const radioText = item => (item?.innerText || item?.textContent || "").trim();
  const radio = model => {
    const matches = Array.from(modelMenu()?.querySelectorAll('[role="menuitemradio"]') || [])
      .filter(item => radioText(item) === model);
    return matches.length === 1 ? matches[0] : null;
  };
  const modelRadio = target => {
    for (const name of [target.model, ...(target.alternateModels || [])]) {
      const item = radio(name);
      if (item) return item;
    }
    return null;
  };
  // Observation is intentionally narrower than selector confirmation: do not
  // report a hidden label or infer a version from the checked Latest alias.
  const renderedLabel = node => {
    try {
      if (!node?.getClientRects?.().length || node.closest('[aria-hidden="true"],[inert]')) return null;
      const value = (node.innerText || "").trim();
      return value && value.length <= 128 ? value : null;
    } catch { return null; }
  };
  // The closed trigger independently exposes the named effort and selected
  // attribute. It does not identify the Chat model; that must be checked in
  // the menu again at the final Send boundary.
  const closedPickerProof = (control, target) => Boolean(control &&
    control.getAttribute("aria-expanded") === "false" && !modelMenu() &&
    control.getAttribute("data-selected-reasoning-effort") === target.effort &&
    (control.innerText || control.textContent || "").trim() === target.label);
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
  let confirmedModelLabel = null;
  let confirmedObservation = null;
  let confirmedPicker = null;

  const choose = async (target, untilAt, finalCheck = false) => {
    // Selection is provisional until selectionOnly completes or Send is
    // attempted on the re-verified surface. Never publish a failed repair.
    confirmedModelLabel = null;
    confirmedObservation = null;
    confirmedPicker = null;
    // Project landing pages expose the Chat model picker, but no mode switch.
    // Use the same checked-radio and effort verification as ordinary Chat.
    if (projectStart && target.mode !== "Chat") return "ModeUnavailable";
    const home = () => emptyHomeFailure();
    const retryWait = () => delay(Math.min(100, Math.max(1, untilAt - Date.now())));
    // Chat/Work controls can appear before their React click handlers hydrate.
    let lastModeClick = -Infinity;
    const modeReady = projectStart || await poll(() => {
      if (home()) return null;
      const chat = modeButton("Chat");
      const work = modeButton("Work");
      if (!chat || !work) return null;
      const chatOn = chat.getAttribute("aria-pressed") === "true";
      const workOn = work.getAttribute("aria-pressed") === "true";
      if (chatOn === workOn) return null;
      if (target.mode === "Chat" ? chatOn : workOn) return true;
      const wanted = target.mode === "Chat" ? chat : work;
      if (!finalCheck && document.readyState === "complete" &&
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

    const enterAdvanced = async () => {
      if (home()) return false;
      if (menuView() === "advanced") return true;
      if (menuView() !== "simple") return false;
      const toggle = activeMenuControl('[data-model-picker-view-toggle]');
      if (!toggle) return false;
      toggle.click();
      return Boolean(await poll(() => !home() && menuView() === "advanced", untilAt));
    };
    const checked = item => !unavailableRadio(item) && item.getAttribute("aria-checked") === "true";
    const hidden = item => Boolean(item?.closest('[aria-hidden="true"],[inert]'));

    if (!await openPicker()) return "ModelPickerUnavailable";
    let model = modelRadio(target);
    if (!model && menuView() === "simple") {
      if (!await enterAdvanced()) return "ModelListUnavailable";
      model = modelRadio(target);
    }
    if (unavailableRadio(model)) return "ModelUnavailable";
    if (!checked(model)) {
      if (!await enterAdvanced()) return "ModelListUnavailable";
      model = modelRadio(target);
      if (unavailableRadio(model) || hidden(model)) return "ModelUnavailable";
      if (home()) return "HomeChanged";
      const clickedLabel = radioText(model);
      model.click();
      await retryWait();
      if (!await openPicker()) return "ModelSelectionUnconfirmed";
      if (!await poll(() => checked(radio(clickedLabel)), untilAt))
        return "ModelSelectionUnconfirmed";
      model = radio(clickedLabel);
    }
    if (!checked(model)) return "ModelSelectionUnconfirmed";
    const checkedModelLabel = radioText(model);

    // The simple view can retain hidden model radios. Their checked state
    // verifies selection, but their hidden text is not a rendered observation.
    // Observe the same checked label in the advanced view when necessary.
    if (hidden(model)) {
      if (!await enterAdvanced()) return "ModelListUnavailable";
      model = radio(checkedModelLabel);
      if (!checked(model) || hidden(model)) return "ModelSelectionUnconfirmed";
    }
    const modelObservation = renderedLabel(model);

    // Return to the effort slider by re-selecting only the visible, already
    // checked radio. Verify that exact model again in the simple view.
    if (menuView() !== "simple") {
      const selectedRadio = radio(checkedModelLabel);
      if (!checked(selectedRadio) || hidden(selectedRadio) || home())
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
      const selectedRadio = radio(checkedModelLabel);
      if (!checked(selectedRadio))
        return "ModelChanged";
      const effort = control.getAttribute("data-selected-reasoning-effort");
      const power = activeMenuControl('[data-model-picker-power-slider]');
      const powerLabel = activeMenuControl('[data-power-disabled]');
      // Quota filtering compresses the slider: position 1 can be Pro while
      // the trigger still reports "medium". Check the named option and label.
      const effortOption = radio(target.label);
      if (effortOption && unavailableRadio(effortOption)) return "EffortUnavailable";
      const knob = power?.querySelector('[role="slider"]');
      const position = knob?.getAttribute("aria-valuenow");
      const maximum = knob?.getAttribute("aria-valuemax");
      const step = position == null || position.trim() === "" ? NaN : Number(position);
      const maxStep = maximum == null || maximum.trim() === "" ? NaN : Number(maximum);
      const keyboard = activeMenuControl('[data-reasoning-slider]');
      const visibleEffort = powerLabel?.textContent?.trim();
      // Missing controls or labels may be transient during hydration.
      if (!keyboard || !visibleEffort || !Number.isInteger(step) ||
          !Number.isInteger(maxStep) || step < 0 || step > maxStep) {
        await retryWait();
        continue;
      }
      if (powerLabel.getAttribute("data-power-disabled") === "true" ||
          power?.querySelector('[data-locked="true"]') || target.level > maxStep)
        return "EffortUnavailable";
      if (step === target.level && visibleEffort !== target.label)
        return "EffortUnavailable";
      if (effort === target.effort && visibleEffort === target.label) {
        if (target.mode === "Work" &&
            !document.querySelector('[data-model-selection-label="gpt-6.1-terra-wm"]'))
          return "ModelUnconfirmed";
        const activeModes = projectStart ? [] : [modeButton("Chat"), modeButton("Work")]
          .filter(button => button?.getAttribute("aria-pressed") === "true");
        const observation = {
          mode: activeModes.length === 1 ? renderedLabel(activeModes[0]) : null,
          model: modelObservation,
          effort: renderedLabel(powerLabel)
        };
        // Escape follows the actual reasoning-menu dismissal path. A
        // trigger click is only a fallback when Escape did not close it.
        if (control.getAttribute("aria-expanded") === "true") {
          keyboard.dispatchEvent(new KeyboardEvent("keydown", {
            key: "Escape", code: "Escape", bubbles: true, cancelable: true
          }));
        }
        const closed = () => !home() && closedPickerProof(picker(), target);
        const closureCutoff = Math.min(untilAt, Date.now() + 250);
        if (!closed() && !await poll(closed, closureCutoff)) {
          const stillOpen = picker();
          if (!home() && stillOpen === control &&
              stillOpen.getAttribute("aria-expanded") === "true") stillOpen.click();
          if (!closed() && !await poll(closed, Math.min(untilAt, Date.now() + 250)))
            return "SelectionChanged";
        }
        if (!finalCheck) await delay(80);
        // A replacement trigger may have the same effort, but the caller
        // must re-prove model identity on that replacement before Send.
        if (home() || modeButton(target.mode)?.getAttribute("aria-pressed") !== "true" ||
            !closedPickerProof(picker(), target))
          return "SelectionChanged";
        confirmedModelLabel = checkedModelLabel;
        confirmedObservation = observation;
        confirmedPicker = control;
        return null;
      }
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
  let selectedMode = selected.mode;
  let selectedModel = confirmedModelLabel;
  if (args.selectionOnly === true) {
    observeSelection(confirmedObservation);
    return { status: "selectionOnly", selectedMode, selectedModel,
      selectedEffort: selected.label, attempts };
  }

  const sendSelector = 'button[type="submit"][aria-label="Send"]:not(:disabled):not([aria-disabled="true"])';
  const initialUrl = location.href;
  const initialTurns = document.querySelectorAll('[data-message-author-role="user"]').length;
  // Modern ChatGPT user messages use the bubble marker instead of the old role.
  const initialBubbles = document.querySelectorAll('[data-user-message-bubble]').length;
  const projectPrefix = projectStart ? new URL(startUrl).pathname.replace(/\/project\/?$/, "") : null;
  if (!atStart() || initialTurns || initialBubbles) {
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
  let finalChecks = 0;
  let lastInsertError = null;
  let sendDisabledSince = null;
  let sendFallbackAttempted = false;
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
    if (!closedPickerProof(modelControl, selected)) {
      return { transient: "SelectedEffortChanged" };
    }
    if (selectedMode === "Work") {
      // Work may temporarily omit its model label as React replaces the
      // composer. Reject contradictory labels, not an absent transient label.
      const labels = Array.from(document.querySelectorAll('[data-model-selection-label]'))
        .filter(node => node.getClientRects().length &&
          !node.closest('[aria-hidden="true"],[inert]'));
      if (labels.length && !labels.some(node =>
          node.getAttribute("data-model-selection-label") === "gpt-6.1-terra-wm")) {
        return { failure: incompatible(stage, "SelectedModelChanged") };
      }
    }
    const editors = document.querySelectorAll(composerSelector);
    if (editors.length !== 1) return { transient: "ComposerChanged" };
    return { chat, work, modelControl, composer: editors[0] };
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
        let failure;
        do {
          if (++effortRepairs > 2)
            return incompatible(stage, "SelectedEffortUnstable");
          const cutoff = Math.min(preSendDeadline, Date.now() + 2500);
          failure = await choose(selected, cutoff);
          // Never proceed using a stale confirmation after a failed repair.
        } while (failure && effortRepairs < 2 && Date.now() < preSendDeadline);
        if (failure) return incompatible(stage, "SelectedEffortRecoveryFailed");
        selectedModel = confirmedModelLabel;
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
          if (!send && selectedMode === "Work" && !sendFallbackAttempted && sendFallback) {
            // The picker may still offer Work after its quota is exhausted.
            // Wait for the normal composer hydration, then fall back before
            // any Send attempt. Never retry after clicking Send.
            sendDisabledSince ??= Date.now();
            if (Date.now() - sendDisabledSince >= 1500 &&
                preSendDeadline - Date.now() >= 3500) {
              sendFallbackAttempted = true;
              attempts.push({ mode: selectedMode, model: selectedModel,
                effort: selected.label, reason: "SendUnavailable" });
              const fallback = sendFallback;
              const failure = await choose(fallback, preSendDeadline - 1000);
              if (failure) {
                attempts.push({ mode: fallback.mode, model: fallback.model,
                  effort: fallback.label, reason: failure });
                return { ...notSubmitted("PreferredConfigurationsUnavailable"), attempts };
              }
              selected = fallback;
              selectedMode = fallback.mode;
              selectedModel = confirmedModelLabel;
              activeComposer = null;
              activeModes = null;
              inserted = false;
              lastTransient = null;
              sendDisabledSince = null;
              continue;
            }
          } else if (send) {
            sendDisabledSince = null;
          }
          if (send) {
            const verified = surface("submitControl");
            if (verified.failure) return verified.failure;
            if (!verified.transient &&
                verified.chat === activeModes.chat &&
                verified.work === activeModes.work &&
                verified.composer === activeComposer &&
                readEditor() === insertedPrompt &&
                document.querySelector(sendSelector) === send) {
              // Chat has no grounded closed-picker model identity signal.
              // Reopen and re-prove (or repair) the selected configuration
              // only when the exact draft and enabled Send are ready. This
              // is a bounded pre-Send check, not a model polling loop.
              if (++finalChecks > 3)
                return incompatible("submitControl", "SelectedModelUnstable");
              const cutoff = Math.min(preSendDeadline, Date.now() + 2500);
              const failure = await choose(selected, cutoff, true);
              const homeFailure = emptyHomeFailure();
              if (homeFailure) return homeFailure;
              if (failure)
                return incompatible("submitControl", "FinalSelectionUnconfirmed");
              const final = surface("submitControl");
              if (final.failure) return final.failure;
              if (!final.transient &&
                  final.chat === activeModes.chat &&
                  final.work === activeModes.work &&
                  final.modelControl === confirmedPicker &&
                  final.composer === activeComposer &&
                  readEditor() === insertedPrompt &&
                  document.querySelector(sendSelector) === send &&
                  closedPickerProof(confirmedPicker, selected)) {
                if (Date.now() >= preSendDeadline) {
                  return { ...notSubmitted("LaunchDeadlineExceeded"), stage: "submitControl" };
                }
                // Report only the last checked UI model, never Latest's
                // inferred backing version or an earlier provisional choice.
                selectedModel = confirmedModelLabel;
                observeSelection(confirmedObservation);
                try {
                  sendAttempted = true;
                  sendAttemptedAt = Date.now();
                  send.click();
                } catch {
                  return unknownAfterSend("sendClick", "SendClickUncertain");
                }
                break;
              }
              // Picker activity can replace controls or disturb the draft.
              // An unchanged editor with the exact draft does not need a
              // destructive rewrite just because the controls re-rendered.
              // Either way, the next Send candidate needs fresh model proof.
              if (!final.transient && final.composer === activeComposer &&
                  readEditor() === insertedPrompt) {
                activeModes = { chat: final.chat, work: final.work };
                continue;
              }
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
          // Multiline plain text must go through the editor's paste parser.
          // Chromium insertText may construct blocks whose rendered newlines
          // differ from the intended ProseMirror document.
          let didInsert;
          if (prompt.includes("\n") && typeof DataTransfer === "function" &&
              typeof ClipboardEvent === "function") {
            const clipboardData = new DataTransfer();
            clipboardData.setData("text/plain", prompt);
            activeComposer.dispatchEvent(new ClipboardEvent("paste", {
              bubbles: true, cancelable: true, clipboardData
            }));
            didInsert = true; // Exact readEditor proof below, not event cancellation.
          } else didInsert = document.execCommand("insertText", false, prompt);
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

  // Only the browser's current route can identify the conversation. Project
  // sidebar links are shared mutable state and have no causal binding to Send.
  const expectedOrigin = new URL(startUrl).origin;
  const currentPage = () => {
    const current = new URL(location.href);
    return current.origin === expectedOrigin ? current : null;
  };
  const durableRoute = current => {
    if (!current) return null;
    const prefix = (projectPrefix || "") + "/c/";
    const routePath = projectStart ? current.pathname : current.pathname.toLowerCase();
    if (!routePath.startsWith(prefix) ||
        !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\/?$/i
          .test(routePath.slice(prefix.length))) return null;
    current.username = "";
    current.password = "";
    current.search = "";
    current.hash = "";
    return current.href;
  };
  const matchesPrompt = turn => (turn.innerText || "")
    .replace(/\r\n/g, "\n").replace(/\u00a0/g, " ").trim() === normalizedPrompt;
  const newBubbles = () => Array.from(document.querySelectorAll('[data-user-message-bubble]'))
    .slice(initialBubbles);
  const submitted = (conversationUrl, evidence) => ({
    status: "submitted",
    selectedMode, selectedModel, selectedEffort: selected.label,
    attempts, conversationUrl, evidence,
    submittedAtUtc: new Date().toISOString()
  });

  if (projectStart) {
    // A matching rendered turn acknowledges Send independently of route binding.
    // The SPA may change its URL before replacing the previous route's DOM.
    // Require an observed scoped durable route without the matching turn, then
    // a matching turn on that same route. Co-observation at a route transition,
    // persistent text, and a change of marker alone do not establish freshness.
    let projectRouteAcknowledgement = null;
    let previousSample = null;
    const bound = await until(() => {
      const current = currentPage();
      if (!current) {
        previousSample = null;
        return null;
      }
      const conversationUrl = durableRoute(current);
      const bubbles = newBubbles();
      const matchingBubble = bubbles.some(matchesPrompt);
      const matchingLegacy = Array.from(document.querySelectorAll('[data-message-author-role="user"]'))
        .slice(initialTurns).some(matchesPrompt);
      const matchingTurn = matchingBubble ? "matchingUserBubble"
        : bubbles.length === 0 && matchingLegacy ? "matchingLegacyUserTurn" : null;
      const freshOnRoute = conversationUrl && previousSample &&
        previousSample.conversationUrl === conversationUrl && !previousSample.hasMatchingTurn;
      // Track either marker for freshness even when bubble evidence takes
      // precedence for acknowledgement. A marker change is not a new turn.
      previousSample = { conversationUrl, hasMatchingTurn: matchingBubble || matchingLegacy };
      if (!matchingTurn) return null;
      projectRouteAcknowledgement = matchingTurn;
      return freshOnRoute ? { conversationUrl, matchingTurn } : null;
    });
    if (bound) return submitted(bound.conversationUrl,
      ["newUserTurn", bound.matchingTurn, "conversationRoute"]);
    if (!projectRouteAcknowledgement)
      return unknownAfterSend("acknowledgement", "SubmissionUnconfirmed");
    // Typed, bounded acknowledgement for the bridge's narrow retention
    // policy. This is not submission success and supplies no conversation ID.
    return {
      ...unknownAfterSend("conversationRoute", "ConversationUrlUnavailable"),
      conversationUrl: null,
      projectRouteAcknowledgement,
      evidence: ["newUserTurn", projectRouteAcknowledgement]
    };
  }

  // Preserve ordinary home/legacy acknowledgement behavior.
  const evidence = await until(() => {
    const legacyTurn = document.querySelectorAll('[data-message-author-role="user"]').length > initialTurns;
    const matchingBubble = newBubbles().some(matchesPrompt);
    const newUserTurn = legacyTurn || matchingBubble;
    const routeChanged = location.href !== initialUrl && location.origin === expectedOrigin;
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

  // A temporary /c/local-chatgpt:... route is not a server-issued UUID.
  const conversationUrl = await until(() => durableRoute(currentPage()));
  if (!conversationUrl) return unknownAfterSend("conversationRoute", "ConversationUrlUnavailable");
  return submitted(conversationUrl, [
    ...(evidence.newUserTurn ? ["newUserTurn"] : []),
    ...(evidence.matchingBubble ? ["matchingUserBubble"] : []),
    "conversationRoute"
  ]);
  } catch {
    // Unexpected DOM exceptions are still provider compatibility failures if
    // we have not attempted Send; after Send, the outcome is unknowable.
    return sendAttempted
      ? unknownAfterSend("unexpected", "UnexpectedUiErrorAfterSend")
      : incompatible("unexpected", "UnexpectedUiError");
  }
}
