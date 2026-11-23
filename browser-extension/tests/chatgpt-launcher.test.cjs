const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
const path = require("node:path");

const source = fs.readFileSync(path.resolve(__dirname, "../../browser-scripts/chatgpt/launchAgent.js"), "utf8");

function harness(options = {}) {
  const site = { origin: "https://chatgpt.com", href: options.startUrl || "https://chatgpt.com/" };
  let content = options.existingDraft || "";
  let userTurns = options.initialUserTurns || 0;
  let lastSubmittedText = null;
  let renderedBubble = null;
  let clicks = 0;
  let focused = false;
  let visibility = options.visibilityState || "visible";
  let editorSelected = false;
  let editorDeleteFlushed = true;
  let mode = options.initialMode || "Chat";
  let chatClicks = 0;
  let modelClicks = 0;
  let selectedModel = options.selectedModel || "Latest";
  let reasoningEffort = options.reasoningEffort || "High";
  let staleSendChecks = 0;
  let staleSendObserved = 0;
  let draftWasCleared = false;
  let injectedTurn = false;
  let afterClearEffectsScheduled = false;
  let afterInsertEffectsScheduled = false;
  let insertions = 0;
  let beforeReadyInsertions = 0;
  const launchStartedAt = Date.now();
  const postInsert = (ms, callback) => {
    if (ms === undefined) return;
    if (ms === 0) queueMicrotask(callback);
    else setTimeout(callback, ms);
  };
  const scheduleImmediateEffect = (callback, ms = 0) => {
    if (options.virtualClock && ms === 0) queueMicrotask(callback);
    else setTimeout(callback, ms);
  };
  if (options.addUserTurnBeforeModeMs) {
    setTimeout(() => { userTurns++; }, options.addUserTurnBeforeModeMs);
  }
  const makeMode = name => ({
    textContent: name,
    get innerText() { return options.observableLabels === false ? undefined : name; },
    getClientRects() { return options.observableLabels === false ? [] : [1]; },
    closest() { return null; },
    getAttribute(attribute) {
      return attribute === "aria-pressed" ? String(mode === name) : null;
    },
    click() {
      if (name === "Chat") {
        chatClicks++;
        if (chatClicks === 1 && options.addTurnAfterFirstChatClickMs !== undefined) {
          setTimeout(() => { userTurns++; }, options.addTurnAfterFirstChatClickMs);
        }
        if (chatClicks === 1 && options.routeAfterFirstChatClickMs !== undefined) {
          setTimeout(() => { site.href = "https://chatgpt.com/c/unexpected-during-selection"; }, options.routeAfterFirstChatClickMs);
        }
        if (!options.modeSwitchFails && chatClicks > (options.ignoreFirstChatClicks || 0)) {
          scheduleImmediateEffect(() => {
            mode = "Chat";
            if (options.chatInstantAndProOnly && !["Instant", "Pro"].includes(reasoningEffort))
              reasoningEffort = "Instant";
          }, options.modeDelayMs || 0);
        }
        if (options.replaceModesDuringModeSwitchMs) {
          setTimeout(() => {
            chatMode = makeMode("Chat");
            workMode = makeMode("Work");
          }, options.replaceModesDuringModeSwitchMs);
        }
      } else { scheduleImmediateEffect(() => {
        mode = "Work";
        if (reasoningEffort === "Pro") reasoningEffort = "Medium";
      }, options.modeDelayMs || 0); }
    }
  });
  let chatMode = makeMode("Chat");
  let workMode = makeMode("Work");
  const editor = {
    get innerText() { return content || (options.emptyRenderedNewline ? "\n" : ""); },
    get textContent() { return content; },
    focus() { focused = true; }
  };
  let activeEditor = editor;
  const send = { click() {
    clicks++;
    if (options.sendClickThrows) throw new Error("send click outcome could not be observed");
    if (options.noAcknowledgement) return;
    if (options.optimisticLocalRoute) {
      site.href = "https://chatgpt.com/c/local-chatgpt%3Aclient-only-placeholder";
    }
    if (!options.noRoute) scheduleImmediateEffect(() => {
      const prefix = options.startUrl?.endsWith("/project") ? options.startUrl.slice(0, -"/project".length) : "https://chatgpt.com";
      site.href = prefix + "/c/00000000-0000-0000-0000-000000000001";
    }, options.routeDelayMs || 0);
    const submittedText = content;
    lastSubmittedText = submittedText;
    content = "";
    userTurns++;
    if (options.renderUserBubble) {
      setTimeout(() => {
        renderedBubble = options.bubbleTextOverride ?? submittedText;
      }, options.bubbleDelayMs || 0);
    }
  }};
  const selection = {
    removeAllRanges() { editorSelected = false; },
    addRange(range) { editorSelected = range.composerSelected === true; }
  };
  let expanded = false;
  let view = "simple";
  const efforts = ["Instant", "Medium", "High", "Extra High"];
  const values = ["none", "medium", "high", "max"];
  const activeEfforts = () => mode === "Chat" && options.chatInstantAndProOnly
    ? ["Instant", "Pro"] : efforts;
  let proSelections = 0;
  const picker = {
    getAttribute(name) {
      if (name === "aria-expanded") return String(expanded);
      if (name !== "data-selected-reasoning-effort") return null;
      return options.forceEffortMismatchWhileDraft && content
        ? "high" : reasoningEffort === "Pro" ? "medium" : values[efforts.indexOf(reasoningEffort)];
    },
    click() { expanded = !expanded; }
  };
  const radio = name => ({
    textContent: name,
    get innerText() { return options.observableLabels === false ? undefined : name; },
    getClientRects() { return options.observableLabels === false ? [] : [1]; },
    getAttribute(attr) {
      if (attr === "aria-disabled" && options.disabledModels?.includes(name)) return "true";
      return attr === "aria-checked" ? String(selectedModel === name) : null;
    },
    hasAttribute() { return false; },
    closest() { return options.hiddenModelRadiosInSimple && view === "simple" ? {} : null; },
    click() {
      if (this.closest()) throw new Error("Hidden model radio was clicked");
      modelClicks++; selectedModel = name; view = "simple"; expanded = false;
    }
  });
  const toggle = { closest() { return null; }, click() { view = "advanced"; } };
  const knob = { getAttribute(name) { return String(name === "aria-valuemax" ? (options.maxEffortStep ?? activeEfforts().length - 1) : activeEfforts().indexOf(reasoningEffort)); } };
  const power = { closest() { return null; }, querySelector(sel) { return sel === '[role="slider"]' ? knob : null; } };
  const powerLabel = {
    get innerText() { return options.observableLabels === false ? undefined : reasoningEffort; },
    getClientRects() { return options.observableLabels === false ? [] : [1]; },
    closest() { return null; },
    get textContent() { return reasoningEffort; },
    getAttribute(name) { return name === "data-power-disabled" ? "false" : null; }
  };
  const disabledMedium = {
    textContent: "Medium", closest() { return null; },
    getAttribute(name) { return name === "aria-disabled" ? "true" : "false"; },
    hasAttribute(name) { return name === "data-disabled"; }
  };
  const keyboard = { closest() { return null; }, focus() {}, dispatchEvent(event) {
    const available = activeEfforts();
    const n = Math.max(0, Math.min(available.length - 1, available.indexOf(reasoningEffort) + (event.key === "ArrowRight" ? 1 : -1)));
    reasoningEffort = available[n];
    if (reasoningEffort === "Pro") proSelections++;
  } };
  const menu = {
    getClientRects() { return [1]; },
    querySelector(sel) { return sel === '[data-model-picker-view]' ? { getAttribute() { return view; } } : null; },
    querySelectorAll(sel) {
      if (sel === '[role="menuitemradio"]') {
        const choices = options.modelChoices || ["Latest", "GPT-6.2", "GPT-6", "GPT-5.6 Sol", "GPT-6.1 Terra"];
        const items = (view === "simple" && !options.hiddenModelRadiosInSimple ? [selectedModel] : choices).map(radio);
        if (mode === "Chat" && options.chatInstantAndProOnly && !options.omitEffortOption)
          items.push(disabledMedium);
        return items;
      }
      if (sel === '[data-power-disabled]') return options.missingEffortLabel ? [] : [powerLabel];
      if (sel === '[data-model-picker-view-toggle]') return [toggle];
      if (sel === '[data-model-picker-power-slider]') return [power];
      if (sel === '[data-reasoning-slider]') return [keyboard];
      return [];
    }
  };
  const document = {
    get visibilityState() { return visibility; },
    readyState: options.documentHydrationDelayMs ? "loading" : "complete",
    createRange() {
      return {
        composerSelected: false,
        selectNodeContents(node) { this.composerSelected = node === activeEditor; }
      };
    },
    execCommand(command, _, value) {
      if (options.throwOnInsert && command === "insertText") throw new Error("ProseMirror changed unexpectedly");
      if (!focused) return false;
      if (command === "delete") {
        if (!editorSelected || options.clearFails) return false;
        content = options.clearLeavesText ? "residual draft" : "";
        editorDeleteFlushed = false;
        queueMicrotask(() => { editorDeleteFlushed = true; });
        if (!content) {
          draftWasCleared = true;
          staleSendChecks = options.staleSendChecks || 0;
          if (!afterClearEffectsScheduled) {
            afterClearEffectsScheduled = true;
            if (options.routeAfterClear) site.href = "https://chatgpt.com/c/unexpected";
            if (options.draftReappearsAfterClearMs) {
              setTimeout(() => { content = options.restoredDraftText || "Unrelated draft restored after clear"; }, options.draftReappearsAfterClearMs);
            }
            if (options.replaceComposerAfterClearMs) {
              setTimeout(() => { activeEditor = Object.create(editor); }, options.replaceComposerAfterClearMs);
            }
            if (options.replaceModesAfterClearMs) {
              setTimeout(() => {
                chatMode = makeMode("Chat");
                workMode = makeMode("Work");
              }, options.replaceModesAfterClearMs);
            }
            if (options.switchToWorkAfterClearMs) {
              setTimeout(() => { mode = "Work"; }, options.switchToWorkAfterClearMs);
            }
          }
        }
        return true;
      }
      if (command !== "insertText") return false;
      if (options.requireClearMicrotask && !editorDeleteFlushed) return false;
      if (document.readyState !== "complete") beforeReadyInsertions++;
      insertions++;
      if (insertions <= (options.rejectInsertCount || 0)) return false;
      content = options.insertWrong ? "wrong" : value;
      if (!afterInsertEffectsScheduled) {
        afterInsertEffectsScheduled = true;
        postInsert(options.replaceComposerAfterInsertMs, () => { activeEditor = Object.create(editor); });
        postInsert(options.replaceModesAfterInsertMs, () => {
          chatMode = makeMode("Chat");
          workMode = makeMode("Work");
        });
        postInsert(options.restoreDraftAfterInsertMs, () => { content = "Competing tab restored an unsent draft"; });
        postInsert(options.switchToWorkAfterInsertMs, () => { mode = "Work"; });
        postInsert(options.hideTabAfterInsertMs, () => { visibility = "hidden"; });
        postInsert(options.resetEffortAfterInsertMs,
          () => { reasoningEffort = options.resetEffortTo || "Medium"; });
        postInsert(options.addTurnAfterInsertMs, () => { userTurns++; });
        postInsert(options.routeAfterInsertMs, () => { site.href = "https://chatgpt.com/c/new-route"; });
      }
      return true;
    },
    querySelector(selector) {
      if (selector === 'button[aria-label="Select ChatGPT model"]') return options.noPicker ? null : picker;
      if (selector === '[data-model-selection-label="gpt-6.1-terra-wm"]') return selectedModel === "GPT-6.1 Terra" ? {} : null;
      if (selector.includes('contenteditable="true"')) return options.noComposer ? null : activeEditor;
      if (selector.includes('aria-label="Send"')) {
        if (options.noSend || (options.workSendDisabled && mode === "Work")) return null;
        if (options.sendAvailableAfterMs !== undefined &&
            (options.virtualClock ? virtualClock.now : Date.now() - launchStartedAt) <
              options.sendAvailableAfterMs) return null;
        if (content || options.sendAlwaysEnabled) return send;
        if (options.staleSendDurationMs && Date.now() - launchStartedAt < options.staleSendDurationMs) {
          staleSendObserved++;
          return send;
        }
        if (staleSendChecks > 0) {
          staleSendChecks--;
          staleSendObserved++;
          return send;
        }
        return null;
      }
      if (selector.includes('Stop generating')) return null;
      return null;
    },
    querySelectorAll(selector) {
      if (selector === '[role="menu"]') return expanded ? [menu] : [];
      if (selector === '[data-model-selection-label]') return [];
      if (selector === '[data-composer-input-layout] [contenteditable="true"][role="textbox"]') {
        return options.noComposer ? [] : [activeEditor];
      }
      if (selector === '[contenteditable="true"][role="textbox"]') {
        return options.noComposer ? [] : [activeEditor];
      }
      if (selector === 'button[type="submit"][aria-label="Send"]') {
        return options.noSend ? [] : [send];
      }
      if (selector === 'button[type="button"][aria-pressed]') {
        if (options.modeButtonsAvailableAfterMs &&
            Date.now() - launchStartedAt < options.modeButtonsAvailableAfterMs) return [];
        if (options.noModeToggle) return [];
        return options.duplicateChatMode ? [chatMode, makeMode("Chat"), workMode] : [chatMode, workMode];
      }
      if (selector === '[data-user-message-bubble]') {
        return renderedBubble === null ? [] : [{ innerText: renderedBubble }];
      }
      if (selector.includes('data-message-author-role="user"') &&
          options.injectTurnAfterClear && draftWasCleared && !injectedTurn) {
        userTurns++;
        injectedTurn = true;
      }
      return selector.includes('data-message-author-role="user"') && !options.noLegacyTurnMarkers
        ? Array(userTurns).fill({}) : [];
    }
  };
  if (options.documentHydrationDelayMs) {
    setTimeout(() => { document.readyState = "complete"; }, options.documentHydrationDelayMs);
  }
  const window = { getSelection: () => options.selectionUnavailable ? null : selection };
  // Test-only virtual time makes the pre-Send deadline boundary deterministic.
  // Wall-clock scheduling at 100 ms can otherwise race the third inactive sample.
  const virtualClock = options.virtualClock ? { now: 0, delays: [] } : null;
  const clockDate = virtualClock
    ? class extends Date { static now() { return virtualClock.now; } }
    : Date;
  const schedule = virtualClock
    ? (callback, ms) => {
      virtualClock.delays.push(ms);
      virtualClock.now += ms;
      queueMicrotask(callback);
    }
    : setTimeout;
  const ctx = vm.createContext({ document, window, location: site, setTimeout: schedule, Date: clockDate, URL, KeyboardEvent: class { constructor(type, init) { Object.assign(this, init); } } });
  const run = async (prompt = "Start fresh\nRead Jarvis assignment", extra = {}) =>
    await vm.runInContext(source + "\nexecute(" + JSON.stringify({
      prompt, timeoutMs: (options.timeoutMs || 350) + 5000, ...extra
    }) + ")", ctx);
  return { run, site, get clicks() { return clicks; }, get insertions() { return insertions; }, get chatClicks() { return chatClicks; },
    get selectedModel() { return selectedModel; }, get reasoningEffort() { return reasoningEffort; }, get modelClicks() { return modelClicks; }, get beforeReadyInsertions() { return beforeReadyInsertions; },
    get proSelections() { return proSelections; }, get mode() { return mode; }, get content() { return content; },
    get virtualClockState() { return virtualClock && { now: virtualClock.now, delays: [...virtualClock.delays] }; },
    get staleSendObserved() { return staleSendObserved; }, get lastSubmittedText() { return lastSubmittedText; } };
}

test("hidden launch is not rejected merely for lacking visibility", async () => {
  const h = harness({ visibilityState: "hidden" });
  const result = await h.run("Background launch is a requirement");
  assert.equal(result.status, "submitted");
  assert.equal(result.selectedEffort, "Medium");
  assert.equal(h.clicks, 1);
});

test("visibility lost after insertion cannot authorize duplicate Send", async () => {
  const h = harness({ hideTabAfterInsertMs: 0, timeoutMs: 1100 });
  const result = await h.run("Do not duplicate this message");
  assert.equal(result.status, "submitted");
  assert.equal(result.selectedEffort, "Medium");
  assert.equal(h.clicks, 1);
});

test("inserts multiline prompt through editor and submits once", async () => {
  const h = harness();
  const result = await h.run();
  assert.equal(result.status, "submitted");
  assert.equal(result.conversationUrl, h.site.href);
  assert.equal(h.clicks, 1);
  assert.deepEqual(Array.from(result.evidence), ["newUserTurn", "conversationRoute"]);
});

test("replaces arbitrary restored draft with exact intended bootstrap once", async () => {
  const h = harness({ existingDraft: "Personal unsent text from another session" });
  const result = await h.run("Worker 2 bootstrap");
  assert.equal(result.status, "submitted");
  assert.equal(result.safeToDisposeCreatedTab, undefined);
  assert.equal(h.lastSubmittedText, "Worker 2 bootstrap");
  assert.equal(h.clicks, 1);
  assert.equal(JSON.stringify(result).includes("Personal unsent text"), false);
});

test("always clears and replaces an unsent draft without waiting for Send to disable", async () => {
  const h = harness({ existingDraft: "Stale text", staleSendChecks: 3, timeoutMs: 900 });
  const result = await h.run("New exact prompt");
  assert.equal(result.status, "submitted");
  assert.equal(h.lastSubmittedText, "New exact prompt");
  assert.equal(h.clicks, 1);
});

test("a permanently enabled Send does not prevent clearing a restored draft", async () => {
  const h = harness({ existingDraft: "Old prompt", sendAlwaysEnabled: true, timeoutMs: 300 });
  const result = await h.run("New prompt");
  assert.equal(result.status, "submitted");
  assert.equal(h.lastSubmittedText, "New prompt");
  assert.equal(h.clicks, 1);
});

test("an already enabled Send does not prevent an exact new prompt", async () => {
  const h = harness({ sendAlwaysEnabled: true, timeoutMs: 250 });
  const result = await h.run("New prompt");
  assert.equal(result.status, "submitted");
  assert.equal(h.lastSubmittedText, "New prompt");
  assert.equal(h.clicks, 1);
});

test("a new user turn during restored-draft stabilization prevents submission", async () => {
  const h = harness({ existingDraft: "Old prompt", injectTurnAfterClear: true });
  const result = await h.run("New prompt");
  assert.equal(result.status, "notSubmitted");
  assert.equal(result.safeToDisposeCreatedTab, false);
  assert.equal(result.reason, "ExistingConversation");
  assert.equal(h.clicks, 0);
});

test("a route change during restored-draft stabilization prevents submission", async () => {
  const h = harness({ existingDraft: "Old prompt", routeAfterClear: true });
  const result = await h.run("New prompt");
  assert.equal(result.status, "notSubmitted");
  assert.equal(result.safeToDisposeCreatedTab, false);
  assert.equal(result.reason, "UnexpectedStartUrl");
  assert.equal(h.clicks, 0);
});

test("replaces an identical restored draft instead of refusing", async () => {
  const h = harness({ existingDraft: "Worker 1 bootstrap" });
  const result = await h.run("Worker 1 bootstrap");
  assert.equal(result.status, "submitted");
  assert.equal(h.lastSubmittedText, "Worker 1 bootstrap");
  assert.equal(h.clicks, 1);
});

test("empty editor's visual paragraph newline is not mistaken for a real restored draft", async () => {
  const h = harness({ emptyRenderedNewline: true, clearFails: true });
  const result = await h.run("New bootstrap");
  assert.equal(result.status, "submitted");
  assert.equal(h.lastSubmittedText, "New bootstrap");
  assert.equal(h.clicks, 1);
});

test("clears whitespace-only restored composer content", async () => {
  const h = harness({ existingDraft: "   " });
  const result = await h.run("Worker 3 bootstrap");
  assert.equal(result.status, "submitted");
  assert.equal(h.lastSubmittedText, "Worker 3 bootstrap");
  assert.equal(h.clicks, 1);
});

test("no prior-send evidence is needed or considered", async () => {
  const h = harness({ existingDraft: "Unrelated text" });
  const result = await h.run("New bootstrap", { confirmedSubmittedPrompts: ["irrelevant", 123] });
  assert.equal(result.status, "submitted");
  assert.equal(h.lastSubmittedText, "New bootstrap");
  assert.equal(h.clicks, 1);
});

test("failed draft deletion never inserts or sends", async () => {
  const h = harness({ existingDraft: "Old bootstrap", clearFails: true });
  const result = await h.run("New bootstrap");
  assert.equal(result.status, "uiIncompatible");
  assert.equal(result.safeToDisposeCreatedTab, true);
  assert.equal(result.reason, "DraftClearFailed");
  assert.equal(h.content, "Old bootstrap");
  assert.equal(h.clicks, 0);
});

test("partially cleared draft never inserts or sends", async () => {
  const h = harness({ existingDraft: "Old bootstrap", clearLeavesText: true });
  const result = await h.run("New bootstrap");
  assert.equal(result.status, "uiIncompatible");
  assert.equal(result.reason, "DraftClearFailed");
  assert.equal(h.content, "residual draft");
  assert.equal(h.clicks, 0);
});

test("unavailable selection preserves existing text without sending", async () => {
  const h = harness({ existingDraft: "Old bootstrap", selectionUnavailable: true });
  const result = await h.run("New bootstrap");
  assert.equal(result.status, "uiIncompatible");
  assert.equal(result.reason, "DraftSelectionUnavailable");
  assert.equal(h.content, "Old bootstrap");
  assert.equal(h.clicks, 0);
});

test("existing conversation on a home-looking URL is not edited", async () => {
  const h = harness({ existingDraft: "A personal draft", initialUserTurns: 1, initialMode: "Work" });
  const result = await h.run("New bootstrap");
  assert.equal(result.status, "notSubmitted");
  assert.equal(result.safeToDisposeCreatedTab, false);
  assert.equal(result.reason, "ExistingConversation");
  assert.equal(h.content, "A personal draft");
  assert.equal(h.chatClicks, 0);
  assert.equal(h.clicks, 0);
});

test("does not click Send if editor rejects insertion", async () => {
  const h = harness({ insertWrong: true });
  const result = await h.run();
  assert.equal(result.status, "uiIncompatible");
  assert.equal(result.stage, "composer");
  assert.equal(result.diagnostics?.documentReadyState, "complete");
  assert.equal(result.reason, "TextInsertionFailed");
  assert.equal(h.clicks, 0);
});

test("after clicking, missing acknowledgement is uncertain and never resent", async () => {
  const h = harness({ noAcknowledgement: true });
  const result = await h.run();
  assert.equal(result.status, "outcomeUnknown");
  assert.equal(result.safeToDisposeCreatedTab, undefined);
  assert.equal(result.reason, "SubmissionUnconfirmed");
  assert.equal(result.stage, "acknowledgement");
  assert.equal(result.timing.budgetAtSendMs >= 3000, true);
  assert.equal(result.timing.elapsedSinceSendMs > 0, true);
  assert.equal(h.clicks, 1);
});

test("missing composer fails before submission", async () => {
  const h = harness({ noComposer: true });
  const result = await h.run();
  assert.equal(result.status, "uiIncompatible");
  assert.equal(result.stage, "composer");
  assert.equal(result.diagnostics?.documentReadyState, "complete");
  assert.equal(result.reason, "ComposerUnavailable");
  assert.equal(h.clicks, 0);
});

test("rejects an existing conversation rather than appending a bootstrap message", async () => {
  const h = harness({ startUrl: "https://chatgpt.com/c/existing" });
  const result = await h.run();
  assert.equal(result.status, "notSubmitted");
  assert.equal(result.safeToDisposeCreatedTab, false);
  assert.equal(result.reason, "UnexpectedStartUrl");
  assert.equal(h.clicks, 0);
});

test("waits for the conversation URL after the user turn appears", async () => {
  const h = harness({ routeDelayMs: 150 });
  const result = await h.run();
  assert.equal(result.status, "submitted");
  assert.equal(result.conversationUrl, h.site.href);
  assert.equal(h.clicks, 1);
});

test("keeps the submission uncertain when route never appears", async () => {
  const h = harness({ noRoute: true, timeoutMs: 350 });
  const result = await h.run();
  assert.equal(result.status, "outcomeUnknown");
  assert.equal(result.reason, "ConversationUrlUnavailable");
  assert.equal(result.stage, "conversationRoute");
  assert.equal(result.timing.budgetAtSendMs >= 3000, true);
  assert.equal(h.clicks, 1);
});

test("leaves Chat selected without toggling and submits normally", async () => {
  const h = harness({ initialMode: "Chat" });
  const result = await h.run();
  assert.equal(result.status, "submitted");
  assert.equal(h.chatClicks, 0);
  assert.equal(h.clicks, 1);
});

test("switches Work to Chat before entering the prompt", async () => {
  const h = harness({ initialMode: "Work", modeDelayMs: 80 });
  const result = await h.run();
  assert.equal(result.status, "submitted");
  assert.equal(h.chatClicks, 1);
  assert.equal(h.mode, "Chat");
  assert.equal(h.clicks, 1);
});

test("does not submit if Work cannot be switched to Chat", async () => {
  const h = harness({ initialMode: "Work", modeSwitchFails: true, timeoutMs: 125 });
  const result = await h.run();
  assert.equal(result.status, "notSubmitted");
  assert.equal(result.reason, "PreferredConfigurationsUnavailable");
  assert.equal(result.attempts[0].reason, "ModeUnavailable");
  assert.equal(h.clicks, 0);
});

test("does not submit when mode selection controls are unavailable", async () => {
  const h = harness({ noModeToggle: true, timeoutMs: 125 });
  const result = await h.run();
  assert.equal(result.status, "notSubmitted");
  assert.equal(result.reason, "PreferredConfigurationsUnavailable");
  assert.equal(result.attempts.every(a => a.reason === "ModeUnavailable"), true);
  assert.equal(h.clicks, 0);
});

test("DOM diagnostics report selector counts without leaking prompt text", async () => {
  const h = harness({ throwOnInsert: true });
  const result = await h.run("SECRET BOOTSTRAP PROMPT");
  assert.equal(result.status, "uiIncompatible");
  assert.equal(result.diagnostics.documentReadyState, "complete");
  assert.equal(result.diagnostics.modeButtons, 2);
  assert.equal(JSON.stringify(result).includes("SECRET BOOTSTRAP PROMPT"), false);
  assert.equal(h.clicks, 0);
});

test("missing Send control is an explicit incompatibility, never a send attempt", async () => {
  const h = harness({ noSend: true, timeoutMs: 350 });
  const result = await h.run();
  assert.equal(result.status, "uiIncompatible");
  assert.equal(result.stage, "submitControl");
  assert.equal(result.reason, "SendUnavailable");
  assert.equal(h.clicks, 0);
});

test("unexpected pre-send DOM exception returns incompatibility without leaking text", async () => {
  const h = harness({ throwOnInsert: true });
  const result = await h.run("PRIVATE PROMPT");
  assert.equal(result.status, "uiIncompatible");
  assert.equal(result.reason, "UnexpectedUiError");
  assert.equal(result.stage, "unexpected");
  assert.equal(h.clicks, 0);
  assert.equal(JSON.stringify(result).includes("PRIVATE PROMPT"), false);
});


test("current ChatGPT user bubble confirms successful delivery after generation already finished", async () => {
  const h = harness({
    noLegacyTurnMarkers: true, renderUserBubble: true,
    routeDelayMs: 10, bubbleDelayMs: 80
  });
  const result = await h.run("Hi, ChatGPT!");
  assert.equal(result.status, "submitted");
  assert.equal(result.conversationUrl, h.site.href);
  assert.deepEqual(Array.from(result.evidence),
    ["newUserTurn", "matchingUserBubble", "conversationRoute"]);
  assert.equal(h.clicks, 1);
});

test("different user bubble and changed route cannot falsely confirm prompt delivery", async () => {
  const h = harness({
    noLegacyTurnMarkers: true, renderUserBubble: true,
    bubbleTextOverride: "Someone else's message", timeoutMs: 160
  });
  const result = await h.run("Hi, ChatGPT!");
  assert.equal(result.status, "outcomeUnknown");
  assert.equal(result.reason, "SubmissionUnconfirmed");
  assert.equal(h.clicks, 1);
  assert.equal(JSON.stringify(result).includes("Hi, ChatGPT!"), false);
});

test("a delayed route still resolves after current ChatGPT renders the matching user bubble", async () => {
  const h = harness({
    noLegacyTurnMarkers: true, renderUserBubble: true,
    bubbleDelayMs: 10, routeDelayMs: 135
  });
  const result = await h.run("Hi, ChatGPT!");
  assert.equal(result.status, "submitted");
  assert.equal(result.conversationUrl, h.site.href);
  assert.equal(h.clicks, 1);
});


test("waits through optimistic local-chatgpt URL until a server-issued conversation UUID exists", async () => {
  const h = harness({
    noLegacyTurnMarkers: true, renderUserBubble: true,
    optimisticLocalRoute: true, routeDelayMs: 180, bubbleDelayMs: 5
  });
  const result = await h.run("Hi, ChatGPT!");
  assert.equal(result.status, "submitted");
  assert.equal(result.conversationUrl,
    "https://chatgpt.com/c/00000000-0000-0000-0000-000000000001");
  assert.equal(h.clicks, 1);
});

test("only a client-local temporary conversation route is still uncertain and cannot close the tab", async () => {
  const h = harness({
    noLegacyTurnMarkers: true, renderUserBubble: true,
    optimisticLocalRoute: true, noRoute: true, timeoutMs: 170
  });
  const result = await h.run("Hi, ChatGPT!");
  assert.equal(result.status, "outcomeUnknown");
  assert.equal(result.reason, "ConversationUrlUnavailable");
  assert.equal(h.clicks, 1);
  assert.equal(result.conversationUrl, undefined);
});

test("existing conversation with draft is not touched", async () => {
  const h = harness({ startUrl: "https://chatgpt.com/c/existing", existingDraft: "Personal text" });
  const result = await h.run("Jarvis bootstrap");
  assert.equal(result.status, "notSubmitted");
  assert.equal(result.reason, "UnexpectedStartUrl");
  assert.equal(h.content, "Personal text");
  assert.equal(h.clicks, 0);
});

test("fresh Chat editor inserts immediately despite stale Send enabled state", async () => {
  const h = harness({ staleSendDurationMs: 2050, timeoutMs: 4400 });
  const result = await h.run("New task");
  assert.equal(result.status, "submitted");
  assert.equal(h.lastSubmittedText, "New task");
  assert.equal(h.clicks, 1);
});

test("a restored unsent draft during hydration is replaced only on the still-zero-turn home", async () => {
  const h = harness({ existingDraft: "Unsaved text", draftReappearsAfterClearMs: 50, staleSendChecks: 5, timeoutMs: 1500 });
  const result = await h.run("Task prompt");
  assert.equal(result.status, "submitted");
  assert.equal(h.lastSubmittedText, "Task prompt");
  assert.equal(h.clicks, 1);
  assert.equal(h.insertions, 2);
});

test("a replaced composer during stabilization is resnapshotted before editing", async () => {
  const h = harness({ existingDraft: "Old", replaceComposerAfterClearMs: 50, staleSendChecks: 5, timeoutMs: 1400 });
  const result = await h.run("Task prompt");
  assert.equal(result.status, "submitted");
  assert.equal(h.lastSubmittedText, "Task prompt");
  assert.equal(h.clicks, 1);
  assert.equal(h.insertions, 2);
});

test("same-selected Chat mode controls may re-render during stabilization", async () => {
  const h = harness({ existingDraft: "Old", replaceModesAfterClearMs: 50, staleSendChecks: 5, timeoutMs: 1400 });
  const result = await h.run("Task prompt");
  assert.equal(result.status, "submitted");
  assert.equal(h.lastSubmittedText, "Task prompt");
  assert.equal(h.clicks, 1);
  assert.equal(h.chatClicks, 0);
});

test("effort resetting after prompt insertion is repaired before the sole Send", async () => {
  const h = harness({ resetEffortAfterInsertMs: 0, resetEffortTo: "Instant", timeoutMs: 2600 });
  const result = await h.run("Exact unique task prompt");
  assert.equal(result.status, "submitted");
  assert.equal(h.reasoningEffort, "Medium");
  assert.equal(h.lastSubmittedText, "Exact unique task prompt");
  assert.equal(h.clicks, 1);
});

test("persistent post-insert effort mismatch never submits a prompt", async () => {
  const h = harness({ forceEffortMismatchWhileDraft: true, timeoutMs: 900 });
  const result = await h.run("Do not send on incoherent effort");
  assert.equal(result.status, "uiIncompatible");
  assert.equal(h.clicks, 0);
});

test("mode switching away from Chat immediately after insertion prevents sending", async () => {
  const h = harness({ switchToWorkAfterInsertMs: 0 });
  const result = await h.run("Task prompt");
  assert.equal(result.status, "uiIncompatible");
  assert.equal(result.reason, "ModeChanged");
  assert.equal(h.clicks, 0);
});

test("persistent enabled Send does not block replacing the composer", async () => {
  const h = harness({ sendAlwaysEnabled: true, timeoutMs: 220 });
  const result = await h.run("New task");
  assert.equal(result.status, "submitted");
  assert.equal(h.lastSubmittedText, "New task");
  assert.equal(h.clicks, 1);
});

test("throwing Send click yields uncertain outcome with exactly one attempt", async () => {
  const h = harness({ sendClickThrows: true });
  const result = await h.run("Task prompt");
  assert.equal(result.status, "outcomeUnknown");
  assert.equal(result.reason, "SendClickUncertain");
  assert.equal(result.stage, "sendClick");
  assert.equal(result.timing.budgetAtSendMs >= 3000, true);
  assert.equal(h.clicks, 1);
  assert.equal(h.lastSubmittedText, null);
});

test("an absent Send control cannot bypass the overall deadline", async () => {
  const h = harness({ timeoutMs: 100, virtualClock: true, noSend: true });
  const result = await h.run("Must not send");
  assert.equal(result.status, "uiIncompatible");
  assert.equal(result.reason, "SendUnavailable");
  assert.equal(h.clicks, 0);
  assert.equal(h.virtualClockState.now, 2100);
});

test("full launch budget keeps eight seconds exclusively for post-Send evidence", async () => {
  const h = harness({ timeoutMs: 22000, virtualClock: true, noSend: true });
  const result = await h.run("No Send; leave time for server route");
  assert.equal(result.status, "uiIncompatible");
  assert.equal(result.reason, "SendUnavailable");
  assert.equal(h.clicks, 0);
  // Harness adds 5000ms: 27000 overall; selection+composer deadline 19000.
  // No late Send is attempted during the final 8000ms acknowledgement reserve.
  assert.equal(h.virtualClockState.now, 19000);
});

test("a conversation appearing while mode controls hydrate cannot have its mode changed", async () => {
  const h = harness({
    initialMode: "Work",
    modeButtonsAvailableAfterMs: 110,
    addUserTurnBeforeModeMs: 40,
    timeoutMs: 500
  });
  const result = await h.run("Must not touch existing conversation");
  assert.equal(result.status, "notSubmitted");
  assert.equal(result.reason, "ExistingConversation");
  assert.equal(result.safeToDisposeCreatedTab, false);
  assert.equal(h.chatClicks, 0);
  assert.equal(h.mode, "Work");
  assert.equal(h.clicks, 0);
});

test("Work to Chat selection survives replacement of the clicked mode buttons", async () => {
  const h = harness({ initialMode: "Work", modeDelayMs: 90, replaceModesDuringModeSwitchMs: 25, timeoutMs: 750 });
  const result = await h.run("Mode hydration prompt");
  assert.equal(result.status, "submitted");
  assert.equal(h.mode, "Chat");
  assert.equal(h.chatClicks, 1);
  assert.equal(h.lastSubmittedText, "Mode hydration prompt");
  assert.equal(h.clicks, 1);
});

for (const reRenderMs of [90, 160]) {
  test(`same Chat controls re-render after inactive sample at ${reRenderMs}ms without premature Send`, async () => {
    const h = harness({ existingDraft: "Old", replaceModesAfterClearMs: reRenderMs, timeoutMs: 1000 });
    const result = await h.run("Safe re-snapshot");
    assert.equal(result.status, "submitted");
    assert.equal(h.lastSubmittedText, "Safe re-snapshot");
    assert.equal(h.clicks, 1);
    assert.equal(h.insertions, 1);
  });
}

test("composer replacement after insertion restarts pre-Send stabilization without duplicate click", async () => {
  const h = harness({ replaceComposerAfterInsertMs: 0, timeoutMs: 1150 });
  const result = await h.run("New task");
  assert.equal(result.status, "submitted");
  assert.equal(h.lastSubmittedText, "New task");
  assert.equal(h.insertions, 2);
  assert.equal(h.clicks, 1);
});

test("mode button replacement after insertion re-proves Chat and sends exactly once", async () => {
  const h = harness({ replaceModesAfterInsertMs: 0, timeoutMs: 1150 });
  const result = await h.run("New task");
  assert.equal(result.status, "submitted");
  assert.equal(h.lastSubmittedText, "New task");
  assert.equal(h.insertions, 2);
  assert.equal(h.clicks, 1);
});

test("another tab restoring a different draft after insertion cannot cause the wrong prompt to send", async () => {
  const h = harness({ restoreDraftAfterInsertMs: 0, timeoutMs: 1150 });
  const result = await h.run("Worker 22 unique prompt");
  assert.equal(result.status, "submitted");
  assert.equal(h.lastSubmittedText, "Worker 22 unique prompt");
  assert.equal(h.insertions, 2);
  assert.equal(h.clicks, 1);
});

test("two parallel new tabs sharing a persisted unsent draft each send only their own prompt", async () => {
  const a = harness({ existingDraft: "Persisted stale shared text", draftReappearsAfterClearMs: 45, timeoutMs: 1350 });
  const b = harness({ existingDraft: "Persisted stale shared text", replaceModesAfterClearMs: 95, timeoutMs: 1350 });
  const [ra, rb] = await Promise.all([a.run("Agent A task"), b.run("Agent B task")]);
  assert.equal(ra.status, "submitted");
  assert.equal(rb.status, "submitted");
  assert.equal(a.lastSubmittedText, "Agent A task");
  assert.equal(b.lastSubmittedText, "Agent B task");
  assert.equal(a.clicks, 1);
  assert.equal(b.clicks, 1);
});

test("actual switch to Work after prompt insertion fails closed without Send", async () => {
  const h = harness({ switchToWorkAfterInsertMs: 0, timeoutMs: 650 });
  const result = await h.run("Must not send");
  assert.equal(result.status, "uiIncompatible");
  assert.equal(result.reason, "ModeChanged");
  assert.equal(h.mode, "Work");
  assert.equal(h.clicks, 0);
});

test("new user turn after insertion always prevents a Send", async () => {
  const h = harness({ addTurnAfterInsertMs: 0, timeoutMs: 650 });
  const result = await h.run("Must not send");
  assert.equal(result.status, "notSubmitted");
  assert.equal(result.reason, "ExistingConversation");
  assert.equal(result.safeToDisposeCreatedTab, false);
  assert.equal(h.clicks, 0);
});

test("route change after insertion always prevents a Send", async () => {
  const h = harness({ routeAfterInsertMs: 0, timeoutMs: 650 });
  const result = await h.run("Must not send");
  assert.equal(result.status, "notSubmitted");
  assert.equal(result.reason, "UnexpectedStartUrl");
  assert.equal(result.safeToDisposeCreatedTab, false);
  assert.equal(h.clicks, 0);
});


test("ignored pre-hydration Chat click retries on the same untouched new-chat home", async () => {
  const h = harness({ initialMode: "Work", ignoreFirstChatClicks: 1, timeoutMs: 1100,
    selectedModel: "Latest", reasoningEffort: "High" });
  const result = await h.run("Hydration recovery");
  assert.equal(result.status, "submitted");
  assert.equal(h.chatClicks >= 2, true);
  assert.equal(h.mode, "Chat");
  assert.equal(h.clicks, 1);
  assert.equal(h.lastSubmittedText, "Hydration recovery");
  assert.equal(h.selectedModel, "Latest");
  assert.equal(h.reasoningEffort, "Medium");
  assert.equal(h.modelClicks, 0);
});

test("multiple ignored hydration clicks eventually select Chat without duplicate Send", async () => {
  const h = harness({ initialMode: "Work", ignoreFirstChatClicks: 2, timeoutMs: 1600 });
  const result = await h.run("Hydration repeat");
  assert.equal(result.status, "submitted");
  assert.equal(h.chatClicks >= 3, true);
  assert.equal(h.clicks, 1);
});

test("permanently ignored mode clicks fail closed before Send", async () => {
  const h = harness({ initialMode: "Work", modeSwitchFails: true, timeoutMs: 710 });
  const result = await h.run("Must not send");
  assert.equal(result.status, "notSubmitted");
  assert.equal(result.reason, "PreferredConfigurationsUnavailable");
  assert.equal(result.attempts[0].reason, "ModeUnavailable");
  assert.equal(result.safeToDisposeCreatedTab, true);
  assert.equal(h.chatClicks >= 2, true);
  assert.equal(h.mode, "Work");
  assert.equal(h.clicks, 0);
});

test("a user turn arriving after an ignored mode click prevents all retries and Send", async () => {
  const h = harness({ initialMode: "Work", ignoreFirstChatClicks: 2,
    addTurnAfterFirstChatClickMs: 30, timeoutMs: 900 });
  const result = await h.run("Must not touch another turn");
  assert.equal(result.status, "notSubmitted");
  assert.equal(result.reason, "ExistingConversation");
  assert.equal(result.safeToDisposeCreatedTab, false);
  assert.equal(h.chatClicks, 1);
  assert.equal(h.clicks, 0);
});

test("route navigation after an ignored mode click prevents retry and Send", async () => {
  const h = harness({ initialMode: "Work", ignoreFirstChatClicks: 3,
    routeAfterFirstChatClickMs: 30, timeoutMs: 900 });
  const result = await h.run("Must not send after navigation");
  assert.equal(result.status, "notSubmitted");
  assert.equal(result.reason, "UnexpectedStartUrl");
  assert.equal(result.safeToDisposeCreatedTab, false);
  assert.equal(h.chatClicks, 1);
  assert.equal(h.clicks, 0);
});

test("ambiguous Chat buttons do not authorize a mode click or Send", async () => {
  const h = harness({ initialMode: "Work", duplicateChatMode: true, timeoutMs: 300 });
  const result = await h.run("No ambiguous selection");
  assert.equal(result.status, "notSubmitted");
  assert.equal(result.reason, "PreferredConfigurationsUnavailable");
  assert.equal(result.attempts[0].reason, "ModeUnavailable");
  assert.equal(h.chatClicks, 0);
  assert.equal(h.clicks, 0);
});

for (const { selectedModel, reasoningEffort } of [
  { selectedModel: "Latest", reasoningEffort: "Instant" },
  { selectedModel: "GPT-6.2", reasoningEffort: "Medium" },
  { selectedModel: "GPT-6", reasoningEffort: "Extra High" },
  { selectedModel: "GPT-5.6 Sol", reasoningEffort: "Medium" },
  { selectedModel: "Pro", reasoningEffort: "High" }
]) {
  test("new Chat tab selects preferred model and effort from " + selectedModel + " and " + reasoningEffort, async () => {
    const h = harness({ initialMode: "Chat", selectedModel, reasoningEffort });
    const result = await h.run("Preserve selected model");
    assert.equal(result.status, "submitted");
    assert.equal(h.chatClicks, 0);
    assert.equal(h.clicks, 1);
    assert.equal(h.selectedModel, "Latest");
    assert.equal(h.reasoningEffort, "Medium");
  });
}

test("Work mode with a restored draft always sends only the intended prompt", async () => {
  const h = harness({ initialMode: "Work", selectedModel: "Latest",
    reasoningEffort: "High", existingDraft: "Saved unrelated draft", timeoutMs: 1400 });
  const result = await h.run("Exact intended new task");
  assert.equal(result.status, "submitted");
  assert.equal(h.lastSubmittedText, "Exact intended new task");
  assert.equal(h.chatClicks >= 1, true);
  assert.equal(h.modelClicks, 0);
  assert.equal(h.clicks, 1);
});


test("clear and insert waits for the editor microtask, without preserving any old text", async () => {
  const h = harness({ existingDraft: "Old unrelated text",
    requireClearMicrotask: true, timeoutMs: 600 });
  const result = await h.run("Only intended prompt");
  assert.equal(result.status, "submitted");
  assert.equal(h.lastSubmittedText, "Only intended prompt");
  assert.equal(h.clicks, 1);
});


test("temporary unhydrated editor insertion retries after clearing and succeeds once", async () => {
  const h = harness({ initialMode: "Chat", existingDraft: "Discard me",
    requireClearMicrotask: true, rejectInsertCount: 2, timeoutMs: 1400 });
  const result = await h.run("Exact intended prompt");
  assert.equal(result.status, "submitted");
  assert.equal(h.lastSubmittedText, "Exact intended prompt");
  assert.equal(h.insertions, 3);
  assert.equal(h.clicks, 1);
});


test("composer visible before document loading completes is not edited early", async () => {
  const h = harness({ initialMode: "Chat", existingDraft: "Discardable draft",
    documentHydrationDelayMs: 145, timeoutMs: 900 });
  const result = await h.run("Delayed ready prompt");
  assert.equal(result.status, "submitted");
  assert.equal(h.lastSubmittedText, "Delayed ready prompt");
  assert.equal(h.beforeReadyInsertions, 0);
  assert.equal(h.clicks, 1);
});

test("absent model picker fails closed without sending", async () => {
  const h = harness({ noPicker: true, virtualClock: true });
  const result = await h.run("Never send");
  assert.equal(result.status, "notSubmitted");
  assert.equal(result.reason, "PreferredConfigurationsUnavailable");
  assert.equal(result.attempts[0].reason, "ModelPickerUnavailable");
  assert.equal(h.clicks, 0);
});

test("disabled Chat Medium falls back to Work Terra without selecting Pro", async () => {
  const h = harness({ chatInstantAndProOnly: true, reasoningEffort: "Instant", timeoutMs: 12000 });
  const result = await h.run("Never send", { selectionOnly: true });
  assert.equal(result.status, "selectionOnly");
  assert.equal(result.selectedMode, "Work");
  assert.equal(result.selectedModel, "GPT-6.1 Terra");
  assert.equal(result.selectedEffort, "Medium");
  assert.equal(result.attempts[0].reason, "EffortUnavailable");
  assert.equal(h.clicks, 0);
  assert.equal(h.proSelections, 0);
});


test("when Chat Medium and Work Terra Medium cannot be selected, fall back to Chat Instant", async () => {
  const h = harness({ maxEffortStep: 0, reasoningEffort: "Instant", timeoutMs: 18000 });
  const result = await h.run("Exactly one fallback", { selectionOnly: true });
  assert.equal(result.status, "selectionOnly");
  assert.equal(result.selectedMode, "Chat");
  assert.equal(result.selectedModel, "Latest");
  assert.equal(result.selectedEffort, "Instant");
  assert.deepEqual(Array.from(result.attempts, item => ({
    mode: item.mode, model: item.model, effort: item.effort, reason: item.reason
  })), [
    { mode: "Chat", model: "Latest", effort: "Medium", reason: "EffortUnavailable" },
    { mode: "Work", model: "GPT-6.1 Terra", effort: "Medium", reason: "EffortUnavailable" }
  ]);
  assert.equal(h.clicks, 0);
});

test("does not click Send when a slow composer consumes the post-send confirmation reserve", async () => {
  const h = harness({ virtualClock: true, sendAvailableAfterMs: 3800 });
  const result = await h.run("Do not attempt an unconfirmable send", { timeoutMs: 6000 });
  assert.equal(result.status, "uiIncompatible");
  assert.equal(result.stage, "submitControl");
  assert.equal(result.reason, "SendUnavailable");
  assert.equal(h.clicks, 0);
  assert.equal(h.virtualClockState.now <= 3100, true);
});

test("waits for delayed post-Send user bubble and durable UUID without replay", async () => {
  const h = harness({
    noLegacyTurnMarkers: true, renderUserBubble: true, optimisticLocalRoute: true,
    bubbleDelayMs: 650, routeDelayMs: 1150, timeoutMs: 3000
  });
  const result = await h.run("Delayed acknowledgment is still one send");
  assert.equal(result.status, "submitted");
  assert.equal(result.conversationUrl,
    "https://chatgpt.com/c/00000000-0000-0000-0000-000000000001");
  assert.deepEqual(Array.from(result.evidence),
    ["newUserTurn", "matchingUserBubble", "conversationRoute"]);
  assert.equal(h.clicks, 1);
});

test("Pro with a medium attribute cannot pass as Medium even without an effort option", async () => {
  const h = harness({ chatInstantAndProOnly: true, omitEffortOption: true,
    reasoningEffort: "Pro", timeoutMs: 12000 });
  const result = await h.run("No paid message for a selection check", { selectionOnly: true });
  assert.equal(result.status, "selectionOnly");
  assert.equal(result.selectedMode, "Work");
  assert.equal(result.selectedModel, "GPT-6.1 Terra");
  assert.equal(result.selectedEffort, "Medium");
  assert.equal(result.attempts[0].reason, "EffortUnavailable");
  assert.equal(h.clicks, 0);
  assert.equal(h.proSelections, 0);
});

test("missing visible effort label cannot authorize a send from an attribute alone", async () => {
  const h = harness({ missingEffortLabel: true, reasoningEffort: "Medium", virtualClock: true });
  const result = await h.run("Never send");
  assert.equal(result.status, "notSubmitted");
  assert.equal(h.clicks, 0);
});

test("Latest is selected instead of explicit GPT-6.2 and the checked alias is observed without inventing a version", async () => {
  const h = harness({ selectedModel: "GPT-6.2", reasoningEffort: "Medium", virtualClock: true });
  const result = await h.run("Selection only", { selectionOnly: true, timeoutMs: 18000 });
  assert.equal(result.status, "selectionOnly");
  assert.equal(h.selectedModel, "Latest");
  assert.equal(result.selectedModel, "Latest");
  assert.deepEqual(JSON.parse(JSON.stringify(result.observedSelection)), { mode: "Chat", model: "Latest", effort: "Medium" });
  assert.equal(h.clicks, 0);
});

test("explicit 5.6 Instant override uses the rendered Sol label on the current account", async () => {
  const h = harness({ selectedModel: "Latest", reasoningEffort: "High", virtualClock: true });
  const result = await h.run("Selection only", { modelOverride: "gpt-5.6-instant", selectionOnly: true, timeoutMs: 18000 });
  assert.equal(result.status, "selectionOnly");
  assert.equal(h.selectedModel, "GPT-5.6 Sol");
  assert.equal(result.selectedModel, "GPT-5.6 Sol");
  assert.equal(result.observedSelection.model, "GPT-5.6 Sol");
  assert.equal(result.selectedEffort, "Instant");
  assert.equal(h.clicks, 0);
});

test("legacy explicit 5.6 label is supported without reporting Sol", async () => {
  const h = harness({ modelChoices: ["Latest", "GPT-5.6"], virtualClock: true });
  const result = await h.run("Selection only", { modelOverride: "gpt-5.6-instant", selectionOnly: true, timeoutMs: 18000 });
  assert.equal(result.status, "selectionOnly");
  assert.equal(result.selectedModel, "GPT-5.6");
  assert.equal(result.observedSelection.model, "GPT-5.6");
  assert.equal(h.clicks, 0);
});

test("explicit Work override cannot silently fall back when its Send is disabled", async () => {
  const h = harness({ selectedModel: "Latest", reasoningEffort: "Medium", workSendDisabled: true, virtualClock: true });
  const result = await h.run("Must not send", { modelOverride: "work-terra-medium", timeoutMs: 18000 });
  assert.equal(result.status, "uiIncompatible");
  assert.equal(result.reason, "SendUnavailable");
  assert.equal(h.mode, "Work");
  assert.equal(h.clicks, 0);
});

test("explicit override rejects prototype property and unsupported value without selecting or sending", async () => {
  for (const modelOverride of ["constructor", "__proto__", "toString", "unknown"]) {
    const h = harness({ virtualClock: true });
    const result = await h.run("Must not send", { modelOverride, timeoutMs: 18000 });
    assert.equal(result.status, "notSubmitted");
    assert.equal(result.reason, "UnsupportedModelOverride");
    assert.equal(h.modelClicks, 0);
    assert.equal(h.clicks, 0);
  }
});

test("project entry selects Latest Medium without a Chat/Work mode toggle and sends once", async () => {
  const startUrl = "https://chatgpt.com/g/g-p-abc123/project";
  const h = harness({ startUrl, noModeToggle: true, selectedModel: "GPT-6", reasoningEffort: "High" });
  const result = await h.run("Project launch", { startUrl, timeoutMs: 18000 });
  assert.equal(result.status, "submitted");
  assert.equal(result.selectedModel, "Latest");
  assert.equal(result.selectedEffort, "Medium");
  assert.equal(result.observedSelection.model, "Latest");
  assert.equal(result.observedSelection.mode, null);
  assert.equal(result.conversationUrl, "https://chatgpt.com/g/g-p-abc123/c/00000000-0000-0000-0000-000000000001");
  assert.equal(h.clicks, 1);
});

test("when Latest and Work are unavailable no explicit model or Send is silently chosen", async () => {
  const h = harness({ modelChoices: ["GPT-6.2", "GPT-6"], selectedModel: "GPT-6.2", virtualClock: true });
  const result = await h.run("Must not send", { timeoutMs: 18000 });
  assert.equal(result.status, "notSubmitted");
  assert.equal(result.reason, "PreferredConfigurationsUnavailable");
  assert.equal(h.selectedModel, "GPT-6.2");
  assert.equal(h.clicks, 0);
});

test("observations are unknown if the picker label cannot be rendered, not inferred from the preference", async () => {
  const h = harness({ observableLabels: false, virtualClock: true });
  const result = await h.run("Selection only", { selectionOnly: true, timeoutMs: 18000 });
  assert.equal(result.status, "selectionOnly");
  assert.deepEqual(JSON.parse(JSON.stringify(result.observedSelection)), { mode: null, model: null, effort: null });
  assert.equal(h.clicks, 0);
});

test("default disabled Work Send falls back to confirmed Latest Instant before the one Send", async () => {
  const h = harness({ chatInstantAndProOnly: true, reasoningEffort: "Instant", workSendDisabled: true, virtualClock: true });
  const result = await h.run("One fallback send", { timeoutMs: 27000 });
  assert.equal(result.status, "submitted", JSON.stringify(result));
  assert.equal(result.selectedMode, "Chat");
  assert.equal(result.selectedModel, "Latest");
  assert.equal(result.selectedEffort, "Instant");
  assert.deepEqual(JSON.parse(JSON.stringify(result.observedSelection)), { mode: "Chat", model: "Latest", effort: "Instant" });
  assert.equal(h.clicks, 1);
});

test("disabled Latest uses only the explicit Work fallback and reports its checked model", async () => {
  const h = harness({ disabledModels: ["Latest"], selectedModel: "GPT-6.2", reasoningEffort: "Medium", virtualClock: true });
  const result = await h.run("Selection only", { selectionOnly: true, timeoutMs: 18000 });
  assert.equal(result.status, "selectionOnly");
  assert.equal(result.selectedMode, "Work");
  assert.equal(result.selectedModel, "GPT-6.1 Terra");
  assert.deepEqual(JSON.parse(JSON.stringify(result.observedSelection)), { mode: "Work", model: "GPT-6.1 Terra", effort: "Medium" });
  assert.equal(result.attempts[0].reason, "ModelUnavailable");
  assert.equal(h.clicks, 0);
});

test("unavailable explicit 5.6 override does not fall back to Latest", async () => {
  const h = harness({ modelChoices: ["Latest", "GPT-6.2"], virtualClock: true });
  const result = await h.run("Must not send", { modelOverride: "gpt-5.6-instant", timeoutMs: 18000 });
  assert.equal(result.status, "notSubmitted");
  assert.equal(result.reason, "PreferredConfigurationsUnavailable");
  assert.equal(result.observedSelection, null);
  assert.equal(h.modelClicks, 0);
  assert.equal(h.clicks, 0);
});

test("project entry rejects explicit Work without switching or sending", async () => {
  const startUrl = "https://chatgpt.com/g/g-p-abc123/project";
  const h = harness({ startUrl, noModeToggle: true, virtualClock: true });
  const result = await h.run("Must not send", { startUrl, modelOverride: "work-terra-medium", timeoutMs: 18000 });
  assert.equal(result.status, "notSubmitted");
  assert.equal(result.attempts[0].reason, "ModeUnavailable");
  assert.equal(result.observedSelection, null);
  assert.equal(h.modelClicks, 0);
  assert.equal(h.clicks, 0);
});

for (const selectedModel of ["Latest", "GPT-6.2"]) {
  test(`hidden simple-view model radios: confirm visible checked Latest from ${selectedModel}`, async () => {
    const h = harness({ selectedModel, hiddenModelRadiosInSimple: true,
      reasoningEffort: "High", virtualClock: true });
    const result = await h.run("Selection only", { selectionOnly: true, timeoutMs: 18000 });
    assert.equal(result.status, "selectionOnly", JSON.stringify(result));
    assert.equal(h.selectedModel, "Latest");
    assert.deepEqual(JSON.parse(JSON.stringify(result.observedSelection)),
      { mode: "Chat", model: "Latest", effort: "Medium" });
    assert.equal(h.clicks, 0);
  });
}
