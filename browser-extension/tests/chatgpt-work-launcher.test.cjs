const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const source = fs.readFileSync(path.resolve(__dirname, "../../browser-scripts/chatgpt/launchWorkAgent.js"), "utf8");

// Virtual time and a deliberately narrow DOM model: no browser or network is used.
function harness(options = {}) {
  let now = 0, mode = options.initialMode || "Chat", text = options.draft || "";
  let sends = 0, workClicks = 0, turns = options.turns || 0, selected = false;
  let drifted = false, modelSelected = false, menu = false, composerGeneration = 0, lastSent;
  const location = { href: options.url || "https://chatgpt.com/", origin: "https://chatgpt.com" };
  const mkMode = name => ({
    textContent: name, disabled: name === "Work" && now < (options.workEnabledAt || 0),
    getAttribute: a => a === "aria-pressed" ? String(mode === name) : null,
    click() { if (name === "Work") { workClicks++; if (!options.modeFails) mode = "Work"; } }
  });
  let chat = mkMode("Chat"), work = mkMode("Work");
  const mkEditor = () => ({ get innerText() { return text; }, get textContent() { return text; }, focus() {} });
  let editor = mkEditor();
  const send = { click() {
    sends++; lastSent = text;
    if (options.throwSend) throw Error("uncertain click");
    if (options.noAck) return;
    turns++; text = "";
    if (!options.noRoute) location.href = "https://chatgpt.com/c/00000000-0000-0000-0000-000000000001";
  }};
  const model = { getAttribute(a) {
    if (a === "data-selected-reasoning-effort") return modelSelected ? (drifted ? "high" : options.effort || "medium") : null;
    if (a === "aria-expanded") return String(menu);
    return null;
  }, click() { menu = !menu; } };
  const terra = { innerText: "GPT-6.1 Terra", click() { modelSelected = true; menu = false; } };
  const selection = { removeAllRanges() { selected = false; }, addRange() { selected = true; } };
  const document = {
    readyState: "complete",
    createRange() { return { selectNodeContents() {} }; },
    execCommand(command, _, value) {
      if (command === "delete") { if (!selected || options.clearFails) return false; text = ""; return true; }
      if (command === "insertText") {
        if (options.insertFails) return false;
        text = value;
        if (options.effortAfterInsert) drifted = true;
        if (options.modeAfterInsert) mode = "Chat";
        if (options.turnAfterInsert) turns++;
        if (options.replaceAfterInsert && composerGeneration === 0) { editor = mkEditor(); composerGeneration++; }
        return true;
      }
      return false;
    },
    querySelector(selector) {
      if (selector.includes("Select ChatGPT model")) return options.noModel ? null : model;
      if (selector.includes('aria-label="Send"')) return !options.noSend && text ? send : null;
      return null;
    },
    querySelectorAll(selector) {
      if (selector === 'button[type="button"][aria-pressed]') {
        if (options.noModes || now < (options.modesAt || 0)) return [];
        work.disabled = now < (options.workEnabledAt || 0);
        return options.duplicateWork ? [chat, work, mkMode("Work")] : [chat, work];
      }
      if (selector === '[role="menuitemradio"]') return menu && !options.noTerra ? [terra] : [];
      if (selector === '[data-model-selection-label]') return modelSelected ? [{getAttribute: () => options.wrongModel ? "wrong" : "gpt-6.1-terra-wm"}] : [];
      if (selector.includes('data-message-author-role')) return Array(turns).fill({});
      if (selector === '[data-user-message-bubble]') return [];
      if (selector.includes('contenteditable="true"')) return options.noComposer ? [] : [editor];
      if (selector.includes('aria-label="Send"')) return options.noSend ? [] : [send];
      return [];
    }
  };
  class Clock extends Date { static now() { return now; } }
  const timer = (fn, ms) => {
    now += ms;
    if (options.replaceModesAt && now >= options.replaceModesAt && !timer.replaced) {
      chat = mkMode("Chat"); work = mkMode("Work"); timer.replaced = true;
    }
    queueMicrotask(fn);
  };
  const context = vm.createContext({document, location, window:{getSelection:()=>selection}, Date:Clock, URL, setTimeout:timer});
  return {
    run: (args = {}) => vm.runInContext(source + "\nexecute(" + JSON.stringify({prompt:"Test prompt\nsecond line", timeoutMs:750, ...args}) + ")", context),
    get sends() { return sends; }, get workClicks() { return workClicks; }, get lastSent() { return lastSent; }
  };
}

test("selects Work and Terra medium and submits exact text once", async () => {
  const h = harness(); const r = await h.run();
  assert.equal(r.status, "submitted"); assert.equal(r.selectedMode, "Work");
  assert.equal(r.selectedModel, "GPT-6.1 Terra"); assert.equal(r.selectedEffort, "medium");
  assert.equal(h.lastSent, "Test prompt\nsecond line"); assert.equal(h.sends, 1);
});
test("waits for hydrated Work control and survives mode node replacement", async () => {
  const h = harness({modesAt:75, workEnabledAt:150, replaceModesAt:225});
  assert.equal((await h.run()).status, "submitted"); assert.equal(h.sends, 1); assert.equal(h.workClicks, 1);
});
test("handles Work already selected", async () => {
  const h = harness({initialMode:"Work"}); assert.equal((await h.run()).status, "submitted"); assert.equal(h.sends, 1);
});
for (const [name, options, reason] of [
  ["missing modes", {noModes:true}, "ModeToggleUnavailable"],
  ["ambiguous Work selector", {duplicateWork:true}, "ModeToggleUnavailable"],
  ["disabled Work", {workEnabledAt:5000}, "WorkModeDisabled"],
  ["missing model control", {noModel:true}, "ModelControlUnavailable"],
  ["missing Terra", {noTerra:true}, "TerraUnavailable"],
  ["wrong effort", {effort:"high"}, "TerraMediumUnconfirmed"],
  ["changed model selector", {wrongModel:true}, "TerraMediumUnconfirmed"],
  ["missing composer", {noComposer:true}, "ComposerUnavailable"],
  ["failed draft clear", {draft:"old draft",clearFails:true}, "DraftClearFailed"],
  ["failed insertion", {insertFails:true}, "TextInsertionFailed"],
  ["missing send", {noSend:true}, "SendUnavailable"]
]) test(name + " fails closed with no send", async () => {
  const h = harness(options), r = await h.run();
  assert.equal(r.reason, reason); assert.equal(h.sends, 0); assert.equal(r.safeToDisposeCreatedTab, true);
});
test("mode changes after insertion do not send", async () => {
  const h = harness({modeAfterInsert:true}), r = await h.run();
  assert.equal(r.reason, "ModeChanged"); assert.equal(h.sends, 0);
});
test("effort changes after insertion do not send", async () => {
  const h = harness({effortAfterInsert:true}), r = await h.run();
  assert.equal(r.reason, "ModelOrEffortChanged"); assert.equal(h.sends, 0);
});
test("new user turn before send prevents cleanup and submission", async () => {
  const h = harness({turnAfterInsert:true}), r = await h.run();
  assert.equal(r.reason, "ExistingConversation"); assert.equal(r.safeToDisposeCreatedTab, false); assert.equal(h.sends, 0);
});
test("composer replacement after insertion is restabilized", async () => {
  const h = harness({replaceAfterInsert:true}), r = await h.run();
  assert.equal(r.status, "submitted"); assert.equal(h.sends, 1); assert.equal(h.lastSent, "Test prompt\nsecond line");
});
for (const [name, options, reason] of [
  ["click exception", {throwSend:true}, "SendClickUncertain"],
  ["missing acknowledgement", {noAck:true}, "SubmissionUnconfirmed"],
  ["missing durable route", {noRoute:true}, "ConversationUrlUnavailable"]
]) test(name + " is uncertain and never retried or marked disposable", async () => {
  const h = harness(options), r = await h.run();
  assert.equal(r.status, "outcomeUnknown"); assert.equal(r.reason, reason);
  assert.equal(h.sends, 1); assert.notEqual(r.safeToDisposeCreatedTab, true);
});
test("existing conversation is neither edited nor disposable", async () => {
  const h = harness({turns:1}), r = await h.run();
  assert.equal(r.reason, "ExistingConversation"); assert.equal(r.safeToDisposeCreatedTab, false); assert.equal(h.sends, 0); assert.equal(h.workClicks, 0);
});
test("unsupported private arguments never send", async () => {
  const h = harness(), r = await h.run({token:"secret"});
  assert.equal(r.reason, "UnsupportedPrivateBootstrapArguments"); assert.equal(h.sends, 0);
});
