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
  let mode = options.initialMode || "Chat";
  let chatClicks = 0;
  const effortLabels = ["Instant", "Medium", "High", "Extra High"];
  let effortIndex = options.initialEffortIndex ?? 3;
  const effortHydratesAt = Date.now() + (options.effortLabelDelayMs || 0);
  let pickerOpen = false;
  let pickerClicks = 0;
  const effortKeys = [];
  const picker = {
    get innerText() { return Date.now() < effortHydratesAt ? "" : "Thinking effort\n" + effortLabels[effortIndex]; },
    getAttribute(attribute) {
      return attribute === "aria-expanded" ? String(pickerOpen) : null;
    },
    click() {
      pickerClicks++;
      if (!options.pickerStaysClosed) pickerOpen = !pickerOpen;
    }
  };
  const slider = {
    getAttribute(attribute) {
      if (attribute === "aria-valuemin") return options.sliderMin ?? "0";
      if (attribute === "aria-valuemax") return options.sliderMax ?? "3";
      if (attribute === "aria-valuenow") return options.sliderValueOverride ?? String(effortIndex);
      return null;
    },
    closest() { return options.sliderLocked ? {} : null; }
  };
  const power = {
    getAttribute(attribute) {
      return attribute === "aria-keyshortcuts" && !options.noKeyboardHints
        ? "ArrowLeft ArrowRight" : null;
    },
    focus() {},
    dispatchEvent(event) {
      effortKeys.push(event.key);
      if (!options.effortChangeFails) {
        effortIndex = Math.max(0, Math.min(3,
          effortIndex + (event.key === "ArrowRight" ? 1 : -1)));
      }
      return true;
    },
    querySelector(selector) {
      return selector.includes('[role="slider"]') && !options.noSlider ? slider : null;
    }
  };
  const effortMenu = {
    getClientRects() { return pickerOpen ? [{}] : []; },
    dispatchEvent(event) {
      if (event.key === "Escape" && !options.menuRefusesEscape) pickerOpen = false;
      return true;
    },
    querySelector(selector) {
      return selector.includes('data-reasoning-slider') && !options.noPowerControl
        ? power : null;
    }
  };
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
  const document = {
    readyState: "complete",
    execCommand(command, _, value) {
      if (options.throwOnInsert) throw new Error("ProseMirror changed unexpectedly");
      if (!focused || command !== "insertText") return false;
      content = options.insertWrong ? "wrong" : value;
      if (options.effortDriftsOnInsert !== undefined) effortIndex = options.effortDriftsOnInsert;
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
      if (selector === 'button[aria-label="Select ChatGPT model"]') return options.noModelPicker ? [] : [picker];
      if (selector === '[role="menu"]') return pickerOpen && !options.noEffortMenu ? [effortMenu] : [];
      if (selector === '[role="slider"][aria-valuenow]') return pickerOpen && !options.noSlider ? [slider] : [];
      if (selector === '[data-user-message-bubble]') {
        return renderedBubble === null ? [] : [{ innerText: renderedBubble }];
      }
      return selector.includes('data-message-author-role="user"') && !options.noLegacyTurnMarkers
        ? Array(userTurns).fill({}) : [];
    }
  };
  class KeyboardEvent { constructor(type, args) { this.type = type; Object.assign(this, args); } }
  const ctx = vm.createContext({ document, location: site, setTimeout, Date, URL, KeyboardEvent });
  const run = async (prompt = "Start fresh\nRead Jarvis assignment") =>
    await vm.runInContext(source + "\nexecute(" + JSON.stringify({
      prompt, timeoutMs: options.timeoutMs || 350,
      ...(options.thinkingEffort !== undefined ? { thinkingEffort: options.thinkingEffort } : {}),
      ...(options.preflightOnly ? { preflightOnly: true } : {})
    }) + ")", ctx);
  return { run, site, get clicks() { return clicks; }, get chatClicks() { return chatClicks; },
    get mode() { return mode; }, get content() { return content; },
    get effort() { return effortLabels[effortIndex]; }, get effortKeys() { return effortKeys; },
    get pickerClicks() { return pickerClicks; } };
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


test("no explicit thinking effort preserves the selected account default", async () => {
  const h = harness({ initialEffortIndex: 1 });
  const result = await h.run();
  assert.equal(result.status, "submitted");
  assert.equal(result.thinkingEffort, undefined);
  assert.equal(h.effort, "Medium");
  assert.equal(h.pickerClicks, 0);
  assert.equal(h.clicks, 1);
});

test("matching requested thinking effort is verified without opening the picker", async () => {
  const h = harness({ thinkingEffort: "Extra High" });
  const result = await h.run();
  assert.equal(result.status, "submitted");
  assert.equal(result.thinkingEffort, "Extra High");
  assert.equal(h.pickerClicks, 0);
  assert.equal(h.clicks, 1);
});

test("selects a lower effort using the real accessible slider before Send", async () => {
  const h = harness({ thinkingEffort: "medium", initialEffortIndex: 3 });
  const result = await h.run();
  assert.equal(result.status, "submitted");
  assert.equal(result.thinkingEffort, "Medium");
  assert.equal(h.effort, "Medium");
  assert.deepEqual(h.effortKeys, ["ArrowLeft", "ArrowLeft"]);
  assert.equal(h.pickerClicks, 1);
  assert.equal(h.clicks, 1);
});

test("restores Extra High from Instant after switching Work to Chat", async () => {
  const h = harness({ thinkingEffort: "extra-high", initialEffortIndex: 0, initialMode: "Work" });
  const result = await h.run();
  assert.equal(result.status, "submitted");
  assert.equal(result.thinkingEffort, "Extra High");
  assert.equal(h.mode, "Chat");
  assert.equal(h.effort, "Extra High");
  assert.deepEqual(h.effortKeys, ["ArrowRight", "ArrowRight", "ArrowRight"]);
  assert.equal(h.clicks, 1);
});

test("invalid requested effort fails before touching the user interface", async () => {
  const h = harness({ thinkingEffort: "maximum please" });
  const result = await h.run();
  assert.equal(result.status, "notSubmitted");
  assert.equal(result.reason, "InvalidThinkingEffort");
  assert.equal(h.pickerClicks, 0);
  assert.equal(h.clicks, 0);
});

test("existing draft takes precedence over effort changes", async () => {
  const h = harness({ thinkingEffort: "High", initialEffortIndex: 3, existingDraft: "leave me alone" });
  const result = await h.run();
  assert.equal(result.status, "notSubmitted");
  assert.equal(result.reason, "ExistingDraft");
  assert.equal(h.content, "leave me alone");
  assert.equal(h.effort, "Extra High");
  assert.equal(h.pickerClicks, 0);
  assert.equal(h.clicks, 0);
});

test("missing model picker fails closed before inserting a prompt", async () => {
  const h = harness({ thinkingEffort: "High", initialEffortIndex: 3, noModelPicker: true, timeoutMs: 125 });
  const result = await h.run();
  assert.equal(result.status, "uiIncompatible");
  assert.equal(result.stage, "effortSelection");
  assert.equal(result.reason, "ModelPickerUnavailable");
  assert.equal(h.clicks, 0);
});

test("unexpected slider range fails closed rather than guessing effort mapping", async () => {
  const h = harness({ thinkingEffort: "High", initialEffortIndex: 3, sliderMax: "4", timeoutMs: 125 });
  const result = await h.run();
  assert.equal(result.status, "uiIncompatible");
  assert.equal(result.reason, "EffortSliderUnavailable");
  assert.equal(h.clicks, 0);
});

test("locked effort slider cannot silently downgrade or submit", async () => {
  const h = harness({ thinkingEffort: "High", initialEffortIndex: 3, sliderLocked: true, timeoutMs: 125 });
  const result = await h.run();
  assert.equal(result.status, "uiIncompatible");
  assert.equal(result.reason, "EffortSliderUnavailable");
  assert.equal(h.clicks, 0);
});

test("unresponsive effort selection remains a definite pre-send failure", async () => {
  const h = harness({ thinkingEffort: "High", initialEffortIndex: 3, effortChangeFails: true, timeoutMs: 125 });
  const result = await h.run();
  assert.equal(result.status, "uiIncompatible");
  assert.equal(result.reason, "EffortChangeUnconfirmed");
  assert.equal(h.clicks, 0);
});

test("effort changed during prompt insertion prevents Send", async () => {
  const h = harness({ thinkingEffort: "High", initialEffortIndex: 2, effortDriftsOnInsert: 1 });
  const result = await h.run();
  assert.equal(result.status, "uiIncompatible");
  assert.equal(result.reason, "EffortChangedBeforeSend");
  assert.equal(h.clicks, 0);
});


test("open model picker closes via Escape after selecting effort", async () => {
  const h = harness({ thinkingEffort: "High", initialEffortIndex: 3 });
  const result = await h.run();
  assert.equal(result.status, "submitted");
  assert.equal(result.thinkingEffort, "High");
  assert.equal(h.pickerClicks, 1);
  assert.equal(h.clicks, 1);
});

test("picker refusing to close fails without a Send attempt", async () => {
  const h = harness({ thinkingEffort: "High", initialEffortIndex: 3, menuRefusesEscape: true, timeoutMs: 125 });
  const result = await h.run();
  assert.equal(result.status, "uiIncompatible");
  assert.equal(result.reason, "ModelPickerDidNotClose");
  assert.equal(h.clicks, 0);
});


test("waits for model picker effort label hydration before selecting or sending", async () => {
  const h = harness({ thinkingEffort: "Extra High", effortLabelDelayMs: 85, timeoutMs: 325 });
  const result = await h.run();
  assert.equal(result.status, "submitted");
  assert.equal(result.thinkingEffort, "Extra High");
  assert.equal(h.clicks, 1);
});

test("unhydrated effort label is incompatible and never sends", async () => {
  const h = harness({ thinkingEffort: "High", effortLabelDelayMs: 5000, timeoutMs: 125 });
  const result = await h.run();
  assert.equal(result.status, "uiIncompatible");
  assert.equal(result.reason, "EffortLabelUnavailable");
  assert.equal(h.clicks, 0);
});


test("preflight-only adjusts and verifies effort without editing or sending", async () => {
  const h = harness({ thinkingEffort: "High", initialEffortIndex: 3, preflightOnly: true });
  const result = await h.run("UNSENT PREFLIGHT PROMPT");
  assert.equal(result.status, "notSubmitted");
  assert.equal(result.reason, "PreflightOnly");
  assert.equal(result.thinkingEffort, "High");
  assert.equal(result.conversationUrl, undefined);
  assert.equal(h.effort, "High");
  assert.equal(h.clicks, 0);
  assert.equal(h.content, "");
  assert.equal(JSON.stringify(result).includes("UNSENT PREFLIGHT PROMPT"), false);
});
