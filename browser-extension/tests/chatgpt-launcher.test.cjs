const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
const path = require("node:path");

const source = fs.readFileSync(path.resolve(__dirname, "../../browser-scripts/chatgpt/launchAgent.js"), "utf8");

function harness(options = {}) {
  const site = { origin: "https://chatgpt.com", href: options.startUrl || "https://chatgpt.com/" };
  let content = options.existingDraft || "";
  let userTurns = 0;
  let renderedBubble = null;
  let clicks = 0;
  let focused = false;
  let editorSelected = false;
  let mode = options.initialMode || "Chat";
  let chatClicks = 0;
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
    get innerText() { return content; },
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
        return true;
      }
      if (command !== "insertText") return false;
      content = options.insertWrong ? "wrong" : value;
      return true;
    },
    querySelector(selector) {
      if (selector.includes('contenteditable="true"')) return options.noComposer ? null : editor;
      if (selector.includes('aria-label="Send"')) return content && !options.noSend ? send : null;
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
    get mode() { return mode; }, get content() { return content; } };
}

test("inserts multiline prompt through editor and submits once", async () => {
  const h = harness();
  const result = await h.run();
  assert.equal(result.status, "submitted");
  assert.equal(result.conversationUrl, h.site.href);
  assert.equal(h.clicks, 1);
  assert.deepEqual(Array.from(result.evidence), ["newUserTurn", "conversationRoute"]);
});

test("does not overwrite a restored draft", async () => {
  const h = harness({ existingDraft: "unsent text" });
  const result = await h.run();
  assert.equal(result.status, "notSubmitted");
  assert.equal(result.reason, "ExistingDraft");
  assert.equal(h.clicks, 0);
  assert.equal(h.content, "unsent text");
});

test("clears only an exact previously confirmed bootstrap and sends the new one once", async () => {
  const oldPrompt = "You are Worker 1 in Jarvis.\nRead your Jarvis inbox.";
  const nextPrompt = "You are Worker 2 in Jarvis.\nRead your Jarvis inbox.";
  const h = harness({ existingDraft: oldPrompt });
  const result = await h.run(nextPrompt, { confirmedSubmittedPrompts: [oldPrompt] });
  assert.equal(result.status, "submitted");
  assert.equal(h.clicks, 1);
  assert.equal(h.content, "");
  assert.equal(JSON.stringify(result).includes(oldPrompt), false);
});

test("rejects any restored draft without exact confirmation, without deleting it", async () => {
  const h = harness({ existingDraft: "Unsent personal text" });
  const result = await h.run("Start new Jarvis agent", {
    confirmedSubmittedPrompts: ["Different previously sent bootstrap"]
  });
  assert.equal(result.status, "notSubmitted");
  assert.equal(result.reason, "ExistingDraft");
  assert.equal(h.content, "Unsent personal text");
  assert.equal(h.clicks, 0);
});

test("rejects invalid stale-bootstrap evidence rather than trusting a partial list", async () => {
  const h = harness({ existingDraft: "Old bootstrap" });
  const result = await h.run("New bootstrap", {
    confirmedSubmittedPrompts: ["Old bootstrap", 123]
  });
  assert.equal(result.reason, "ExistingDraft");
  assert.equal(h.content, "Old bootstrap");
  assert.equal(h.clicks, 0);
});

test("failed stale-draft deletion never inserts or submits another prompt", async () => {
  const h = harness({ existingDraft: "Old bootstrap", clearFails: true });
  const result = await h.run("New bootstrap", {
    confirmedSubmittedPrompts: ["Old bootstrap"]
  });
  assert.equal(result.status, "uiIncompatible");
  assert.equal(result.reason, "StaleDraftClearFailed");
  assert.equal(h.content, "Old bootstrap");
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

test("does not resend an identical restored bootstrap even with prior-send evidence", async () => {
  const prompt = "You are Worker 1 in Jarvis. Read your inbox.";
  const h = harness({ existingDraft: prompt });
  const result = await h.run(prompt, { confirmedSubmittedPrompts: [prompt] });
  assert.equal(result.status, "notSubmitted");
  assert.equal(result.reason, "ExistingDraft");
  assert.equal(h.content, prompt);
  assert.equal(h.clicks, 0);
});
