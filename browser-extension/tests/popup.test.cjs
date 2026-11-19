"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const source = fs.readFileSync(path.join(__dirname, "..", "popup.js"), "utf8");
const tick = () => new Promise(resolve => setImmediate(resolve));

class Element {
  constructor(tagName = "div") {
    this.tagName = tagName;
    this.children = [];
    this.attributes = {};
    this.textContent = "";
    this.value = "";
    this.disabled = false;
    this.onclick = null;
    this.parent = null;
  }
  append(...children) {
    for (const child of children) {
      child.parent = this;
      this.children.push(child);
    }
  }
  replaceChildren(...children) { this.children = []; this.append(...children); }
  setAttribute(name, value) { this.attributes[name] = value; }
  querySelector(selector) {
    if (selector === "[data-unregister-error]")
      return this.children.find(child => Object.hasOwn(child.attributes, "data-unregister-error")) || null;
    return null;
  }
  remove() {
    if (this.parent) this.parent.children = this.parent.children.filter(child => child !== this);
  }
}

async function setup({ failRow = false, failCurrent = false, ignoreRow = false } = {}) {
  const elements = new Map();
  const document = {
    getElementById(id) {
      if (!elements.has(id)) elements.set(id, new Element());
      return elements.get(id);
    },
    createElement(tagName) { return new Element(tagName); }
  };
  let registrations = [{
    registrationId: "reg-17", registrationGeneration: 1, tabId: 17,
    alias: "agent", agentCreated: true, availability: "ready",
    displayUrl: "https://chatgpt.com/c/123"
  }];
  let closed = 0;
  const messages = [];
  const chrome = {
    runtime: {
      sendMessage: async message => {
        messages.push(message);
        if (message.kind === "getState") return {
          ok: true, connected: true, paired: true, port: 43822,
          allowAgentCreatedTabs: true, windows: [], registrations: registrations.map(x => ({ ...x }))
        };
        if (message.kind === "unregisterRegistration") {
          if (failRow) return { ok: false, error: "RegistrationStale" };
          if (!ignoreRow) registrations = registrations.filter(r => r.registrationId !== message.registrationId);
          return { ok: true };
        }
        if (message.kind === "unregister") {
          if (failCurrent) return { ok: false, error: "Tab unregister failed" };
          registrations = registrations.filter(r => r.tabId !== message.tabId);
          return { ok: true };
        }
        throw Error("Unexpected request: " + message.kind);
      }
    },
    tabs: { query: async () => [{ id: 17, title: "Agent", url: "https://chatgpt.com/" }] },
    windows: { getCurrent: async () => ({ id: 1 }) }
  };
  vm.runInNewContext(source, { chrome, document, URL });
  await tick(); await tick();
  const row = () => document.getElementById("tabs").children[0];
  const rowButton = () => row()?.children.find(el => el.tagName === "button");
  return { document, row, rowButton, messages, get registrations() { return registrations; },
    get closed() { return closed; } };
}

test("per-row Unregister removes registration, not the browser tab", async () => {
  const h = await setup();
  await h.rowButton().onclick();
  assert.equal(h.registrations.length, 0);
  assert.equal(h.document.getElementById("tabs").children.length, 0);
  assert.equal(h.closed, 0);
  assert.ok(h.messages.some(m => m.kind === "unregisterRegistration" && m.registrationId === "reg-17"));
});

test("a rejected row Unregister displays its error beside the clicked button", async () => {
  const h = await setup({ failRow: true });
  await h.rowButton().onclick();
  assert.equal(h.registrations.length, 1);
  const error = h.row().querySelector("[data-unregister-error]");
  assert.equal(error.attributes.role, "alert");
  assert.match(error.textContent, /RegistrationStale/);
  assert.equal(h.rowButton().disabled, false);
});

test("acknowledged but ineffective row Unregister is reported, not shown as success", async () => {
  const h = await setup({ ignoreRow: true });
  await h.rowButton().onclick();
  assert.match(h.row().querySelector("[data-unregister-error]").textContent, /still present/);
});

test("Unregister current tab removes the exact selected registration", async () => {
  const h = await setup();
  await h.document.getElementById("unregister").onclick();
  assert.equal(h.registrations.length, 0);
  assert.equal(h.document.getElementById("tabs").children.length, 0);
  assert.equal(h.document.getElementById("currentUnregisterError").textContent, "");
});

test("current tab failure is visible beside its button", async () => {
  const h = await setup({ failCurrent: true });
  await h.document.getElementById("unregister").onclick();
  assert.equal(h.registrations.length, 1);
  assert.match(h.document.getElementById("currentUnregisterError").textContent, /Tab unregister failed/);
});
