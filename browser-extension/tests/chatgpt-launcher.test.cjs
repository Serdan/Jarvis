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
  let clicks = 0;
  let focused = false;
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
    if (!options.noRoute) setTimeout(() => { site.href = "https://chatgpt.com/c/00000000-0000-0000-0000-000000000001"; }, options.routeDelayMs || 0);
    content = "";
    userTurns++;
  }};
  const document = {
    readyState: "complete",
    execCommand(command, _, value) {
      if (options.throwOnInsert) throw new Error("ProseMirror changed unexpectedly");
      if (!focused || command !== "insertText") return false;
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
      return selector.includes('data-message-author-role="user"') ? Array(userTurns).fill({}) : [];
    }
  };
  const ctx = vm.createContext({ document, location: site, setTimeout, Date, URL });
  const run = async (prompt = "Start fresh\nRead Jarvis assignment") =>
    await vm.runInContext(source + "\nexecute(" + JSON.stringify({
      prompt, timeoutMs: options.timeoutMs || 350
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
