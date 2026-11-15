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
      }
    }
  });
  const chatMode = makeMode("Chat");
  const workMode = makeMode("Work");
  const editor = {
    get innerText() { return content || (options.emptyRenderedNewline ? "\n" : ""); },
    get textContent() { return content; },
    focus() { focused = true; }
  };
  const send = { click() {
    clicks++;
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
        selectNodeContents(node) { this.composerSelected = node === editor; }
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
          if (options.routeAfterClear) site.href = "https://chatgpt.com/c/unexpected";
        }
        return true;
      }
      if (command !== "insertText") return false;
      content = options.insertWrong ? "wrong" : value;
      return true;
    },
    querySelector(selector) {
      if (selector.includes('contenteditable="true"')) return options.noComposer ? null : editor;
      if (selector.includes('aria-label="Send"')) {
        if (options.noSend) return null;
        if (content || options.sendAlwaysEnabled) return send;
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
        return options.noComposer ? [] : [editor];
      }
      if (selector === '[contenteditable="true"][role="textbox"]') {
        return options.noComposer ? [] : [editor];
      }
      if (selector === 'button[type="submit"][aria-label="Send"]') {
        return options.noSend ? [] : [send];
      }
      if (selector === 'button[type="button"][aria-pressed]') {
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
  const ctx = vm.createContext({ document, window, location: site, setTimeout, Date, URL });
  const run = async (prompt = "Start fresh\nRead Jarvis assignment", extra = {}) =>
    await vm.runInContext(source + "\nexecute(" + JSON.stringify({
      prompt, timeoutMs: options.timeoutMs || 350, ...extra
    }) + ")", ctx);
  return { run, site, get clicks() { return clicks; }, get chatClicks() { return chatClicks; },
    get mode() { return mode; }, get content() { return content; },
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
  assert.equal(result.reason, "UnexpectedSendState");
  assert.equal(h.content, "");
  assert.equal(h.clicks, 0);
});

test("a new user turn during restored-draft stabilization prevents submission", async () => {
  const h = harness({ existingDraft: "Old prompt", injectTurnAfterClear: true });
  const result = await h.run("New prompt");
  assert.equal(result.status, "notSubmitted");
  assert.equal(result.reason, "ExistingConversation");
  assert.equal(h.clicks, 0);
});

test("a route change during restored-draft stabilization prevents submission", async () => {
  const h = harness({ existingDraft: "Old prompt", routeAfterClear: true });
  const result = await h.run("New prompt");
  assert.equal(result.status, "notSubmitted");
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
  const h = harness({ noRoute: true, timeoutMs: 125 });
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
  const h = harness({ noSend: true, timeoutMs: 125 });
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
