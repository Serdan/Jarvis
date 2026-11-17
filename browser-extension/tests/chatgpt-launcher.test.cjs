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
  let editorSelected = false;
  let mode = options.initialMode || "Chat";
  let chatClicks = 0;
  let staleSendChecks = 0;
  let staleSendObserved = 0;
  let draftWasCleared = false;
  let injectedTurn = false;
  let afterClearEffectsScheduled = false;
  let afterInsertEffectsScheduled = false;
  let insertions = 0;
  const launchStartedAt = Date.now();
  const postInsert = (ms, callback) => {
    if (ms === undefined) return;
    if (ms === 0) queueMicrotask(callback);
    else setTimeout(callback, ms);
  };
  if (options.addUserTurnBeforeModeMs) {
    setTimeout(() => { userTurns++; }, options.addUserTurnBeforeModeMs);
  }
  const makeMode = name => ({
    textContent: name,
    getAttribute(attribute) {
      return attribute === "aria-pressed" ? String(mode === name) : null;
    },
    click() {
      if (name === "Chat") {
        chatClicks++;
        if (!options.modeSwitchFails) {
          setTimeout(() => { mode = "Chat"; }, options.modeDelayMs || 0);
        }
        if (options.replaceModesDuringModeSwitchMs) {
          setTimeout(() => {
            chatMode = makeMode("Chat");
            workMode = makeMode("Work");
          }, options.replaceModesDuringModeSwitchMs);
        }
      }
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
    if (!options.noRoute) setTimeout(() => { site.href = "https://chatgpt.com/c/00000000-0000-0000-0000-000000000001"; }, options.routeDelayMs || 0);
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
  const document = {
    readyState: "complete",
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
      insertions++;
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
        postInsert(options.addTurnAfterInsertMs, () => { userTurns++; });
        postInsert(options.routeAfterInsertMs, () => { site.href = "https://chatgpt.com/c/new-route"; });
      }
      return true;
    },
    querySelector(selector) {
      if (selector.includes('contenteditable="true"')) return options.noComposer ? null : activeEditor;
      if (selector.includes('aria-label="Send"')) {
        if (options.noSend) return null;
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
        return options.noModeToggle ? [] : [chatMode, workMode];
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
  const ctx = vm.createContext({ document, window, location: site, setTimeout: schedule, Date: clockDate, URL });
  const run = async (prompt = "Start fresh\nRead Jarvis assignment", extra = {}) =>
    await vm.runInContext(source + "\nexecute(" + JSON.stringify({
      prompt, timeoutMs: options.timeoutMs || 350, ...extra
    }) + ")", ctx);
  return { run, site, get clicks() { return clicks; }, get insertions() { return insertions; }, get chatClicks() { return chatClicks; },
    get mode() { return mode; }, get content() { return content; },
    get virtualClockState() { return virtualClock && { now: virtualClock.now, delays: [...virtualClock.delays] }; },
    get staleSendObserved() { return staleSendObserved; }, get lastSubmittedText() { return lastSubmittedText; } };
}

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

test("waits for delayed React Send disable after clearing restored draft", async () => {
  const h = harness({ existingDraft: "Worker 5 stale unsent draft", staleSendChecks: 3, timeoutMs: 900 });
  const result = await h.run("Worker 4 new prompt");
  assert.equal(result.status, "submitted");
  assert.equal(h.staleSendObserved, 3);
  assert.equal(h.lastSubmittedText, "Worker 4 new prompt");
  assert.equal(h.clicks, 1);
});

test("never sends when restored-draft Send stays enabled beyond bounded stabilization", async () => {
  const h = harness({ existingDraft: "Old prompt", sendAlwaysEnabled: true, timeoutMs: 300 });
  const result = await h.run("New prompt");
  assert.equal(result.status, "notSubmitted");
  assert.equal(result.safeToDisposeCreatedTab, true);
  assert.equal(result.reason, "UnexpectedSendState");
  assert.equal(result.stage, "draftStabilization");
  assert.equal(result.diagnostics.sendButtons, 1);
  assert.equal(h.lastSubmittedText, null);
  assert.equal(h.clicks, 0);
  assert.equal(JSON.stringify(result).includes("Old prompt"), false);
});

test("unexpected enabled Send on an empty editor never permits insertion", async () => {
  const h = harness({ sendAlwaysEnabled: true, timeoutMs: 250 });
  const result = await h.run("New prompt");
  assert.equal(result.status, "notSubmitted");
  assert.equal(result.safeToDisposeCreatedTab, true);
  assert.equal(result.reason, "UnexpectedSendState");
  assert.equal(h.content, "");
  assert.equal(h.clicks, 0);
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
  assert.equal(result.status, "uiIncompatible");
  assert.equal(result.stage, "modeSelection");
  assert.equal(result.diagnostics?.documentReadyState, "complete");
  assert.equal(result.reason, "ChatModeUnavailable");
  assert.equal(h.clicks, 0);
});

test("does not submit when mode selection controls are unavailable", async () => {
  const h = harness({ noModeToggle: true, timeoutMs: 125 });
  const result = await h.run();
  assert.equal(result.status, "uiIncompatible");
  assert.equal(result.stage, "modeSelection");
  assert.equal(result.diagnostics?.documentReadyState, "complete");
  assert.equal(result.reason, "ModeToggleUnavailable");
  assert.equal(h.clicks, 0);
});

test("DOM diagnostics report selector counts, never prompt or raw HTML", async () => {
  const h = harness({ noModeToggle: true, timeoutMs: 125 });
  const result = await h.run("SECRET BOOTSTRAP PROMPT");
  assert.equal(result.status, "uiIncompatible");
  assert.equal(result.stage, "modeSelection");
  assert.equal(result.diagnostics.modeButtons, 0);
  assert.equal(result.diagnostics.chatLabels, 0);
  assert.equal(result.diagnostics.workLabels, 0);
  assert.equal(result.diagnostics.documentReadyState, "complete");
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

test("fresh empty Chat editor waits longer than the former 1800ms stabilization deadline", async () => {
  const h = harness({ staleSendDurationMs: 2050, timeoutMs: 4400 });
  const result = await h.run("Hydration-safe new task");
  assert.equal(result.status, "submitted");
  assert.equal(h.staleSendObserved > 20, true);
  assert.equal(h.lastSubmittedText, "Hydration-safe new task");
  assert.equal(h.clicks, 1);
});

test("a restored unsent draft during hydration is replaced only on the still-zero-turn home", async () => {
  const h = harness({ existingDraft: "Unsaved text", draftReappearsAfterClearMs: 50, staleSendChecks: 5, timeoutMs: 1500 });
  const result = await h.run("Task prompt");
  assert.equal(result.status, "submitted");
  assert.equal(h.lastSubmittedText, "Task prompt");
  assert.equal(h.clicks, 1);
  assert.equal(h.insertions, 1);
});

test("a replaced composer during stabilization is resnapshotted before editing", async () => {
  const h = harness({ existingDraft: "Old", replaceComposerAfterClearMs: 50, staleSendChecks: 5, timeoutMs: 1400 });
  const result = await h.run("Task prompt");
  assert.equal(result.status, "submitted");
  assert.equal(h.lastSubmittedText, "Task prompt");
  assert.equal(h.clicks, 1);
  assert.equal(h.insertions, 1);
});

test("same-selected Chat mode controls may re-render during stabilization", async () => {
  const h = harness({ existingDraft: "Old", replaceModesAfterClearMs: 50, staleSendChecks: 5, timeoutMs: 1400 });
  const result = await h.run("Task prompt");
  assert.equal(result.status, "submitted");
  assert.equal(h.lastSubmittedText, "Task prompt");
  assert.equal(h.clicks, 1);
  assert.equal(h.chatClicks, 0);
});

test("mode switching away from Chat during stabilization prevents sending", async () => {
  const h = harness({ existingDraft: "Old", switchToWorkAfterClearMs: 50, staleSendChecks: 5 });
  const result = await h.run("Task prompt");
  assert.equal(result.status, "uiIncompatible");
  assert.equal(result.reason, "ModeChanged");
  assert.equal(h.clicks, 0);
});

test("persistent enabled Send exhausts the overall budget without editing", async () => {
  const h = harness({ sendAlwaysEnabled: true, timeoutMs: 220 });
  const result = await h.run("New task");
  assert.equal(result.status, "notSubmitted");
  assert.equal(result.reason, "UnexpectedSendState");
  assert.equal(h.content, "");
  assert.equal(h.clicks, 0);
});

test("throwing Send click yields uncertain outcome with exactly one attempt", async () => {
  const h = harness({ sendClickThrows: true });
  const result = await h.run("Task prompt");
  assert.equal(result.status, "outcomeUnknown");
  assert.equal(result.reason, "SendClickUncertain");
  assert.equal(h.clicks, 1);
  assert.equal(h.lastSubmittedText, null);
});

test("deadline exactly at the third inactive sample cannot edit or send", async () => {
  const h = harness({ timeoutMs: 100, virtualClock: true });
  const result = await h.run("Must not send");
  assert.equal(result.status, "notSubmitted");
  assert.equal(result.reason, "LaunchDeadlineExceeded");
  assert.equal(result.stage, "draftStabilization");
  assert.deepEqual(h.virtualClockState, { now: 100, delays: [75, 25] });
  assert.equal(h.content, "");
  assert.equal(h.clicks, 0);
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
