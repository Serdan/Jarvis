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
  const editor = {
    get innerText() { return content; },
    focus() { focused = true; }
  };
  const send = { click() {
    clicks++;
    if (options.noAcknowledgement) return;
    site.href = "https://chatgpt.com/c/00000000-0000-0000-0000-000000000001";
    content = "";
    userTurns++;
  }};
  const document = {
    execCommand(command, _, value) {
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
      return selector.includes('data-message-author-role="user"') ? Array(userTurns).fill({}) : [];
    }
  };
  const ctx = vm.createContext({ document, location: site, setTimeout, Date, URL });
  const run = async (prompt = "Start fresh\nRead Jarvis assignment") =>
    await vm.runInContext(source + "\nexecute(" + JSON.stringify({
      prompt, uiReadyTimeoutMs: 100, submitTimeoutMs: 100
    }) + ")", ctx);
  return { run, site, get clicks() { return clicks; }, get content() { return content; } };
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
  assert.equal(result.status, "notSubmitted");
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
  assert.equal(result.status, "notSubmitted");
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
