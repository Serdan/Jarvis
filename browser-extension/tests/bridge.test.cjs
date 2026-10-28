const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const { webcrypto, createHmac } = require("node:crypto");

const source = fs.readFileSync(path.join(__dirname, "..", "background.js"), "utf8");
const tick = () => new Promise(resolve => setImmediate(resolve));

function createHarness(initialStorage = {}, initialSession = {}) {
  const storage = new Map(Object.entries(initialStorage));
  const session = new Map(Object.entries(initialSession));
  const tabs = new Map([
    [17, { id: 17, url: "https://example.test/chat/7", title: "Agent 7", status: "complete" }],
    [18, { id: 18, url: "https://example.test/other", title: "Unregistered", status: "complete" }]
  ]);
  const listeners = {};
  const sockets = [];
  const calls = [];
  const createdTabs = [];
  let nextCreatedId = 19;
  class FakeWebSocket {
    static OPEN = 1;
    static CONNECTING = 0;
    constructor(url) {
      this.url = url;
      this.readyState = 1;
      this.sent = [];
      sockets.push(this);
    }
    send(value) { this.sent.push(JSON.parse(value)); }
    close() {
      this.readyState = 3;
      this.onclose?.();
    }
  }
  const chrome = {
    storage: {
      local: {
        setAccessLevel: async () => {},
        get: async names => Object.fromEntries(names.filter(x => storage.has(x)).map(x => [x, storage.get(x)])),
        set: async obj => { for (const [k, v] of Object.entries(obj)) storage.set(k, v); },
        remove: async key => { storage.delete(key); }
      },
      session: {
        setAccessLevel: async () => {},
        get: async key => ({ [key]: session.get(key) }),
        set: async obj => { for (const [k, v] of Object.entries(obj)) session.set(k, v); }
      }
    },
    runtime: { id: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
      onMessage: { addListener: cb => { listeners.message = cb; } } },
    tabs: {
      create: async properties => {
        const tab = {
          id: nextCreatedId++, url: "about:blank", pendingUrl: properties.url,
          status: "loading", incognito: false, active: properties.active
        };
        tabs.set(tab.id, tab);
        createdTabs.push(tab);
        return tab;
      },
      remove: async id => { tabs.delete(id); },
      get: async id => {
        if (!tabs.has(id)) throw Error("Missing tab");
        return tabs.get(id);
      },
      onUpdated: { addListener: cb => { listeners.updated = cb; } },
      onRemoved: { addListener: cb => { listeners.removed = cb; } }
    },
    webNavigation: { onHistoryStateUpdated: { addListener: cb => { listeners.history = cb; } } },
    permissions: { contains: async () => true },
    alarms: {
      create: () => {},
      onAlarm: { addListener: cb => { listeners.alarm = cb; } }
    },
    userScripts: {
      execute: async ({ target, js, world }) => {
        calls.push({ target, world });
        const page = tabs.get(target.tabId);
        if (!page) throw new Error("Missing target");
        const result = await vm.runInNewContext(js[0].code, {
          location: { href: page.url, origin: new URL(page.url).origin }, document: { title: page.title }
        });
        return [{ frameId: 0, result }];
      }
    }
  };
  vm.runInNewContext(source, {
    chrome, crypto: webcrypto, TextEncoder, URL, WebSocket: FakeWebSocket,
    setTimeout, clearTimeout, setInterval: () => 1, clearInterval: () => {}
  });
  const message = (kind, extra = {}, sender = { id: chrome.runtime.id }) =>
    new Promise(resolve => listeners.message({ kind, ...extra }, sender, resolve));
  const deliver = async (socket, value) => {
    await socket.onmessage({ data: JSON.stringify({ version: 1, ...value }) });
    await tick(); await tick();
  };
  return { storage, session, tabs, createdTabs, sockets, calls, listeners, message, deliver, userScripts: chrome.userScripts };
}

test("registration requires a user action and unregistered tabs stay private", async () => {
  const h = createHarness();
  await tick(); await tick();
  let response = await h.message("getState");
  assert.equal(response.connected, false);
  assert.equal(response.registrations.length, 0);
  response = await h.message("register", { tabId: 17, alias: "agent-7" });
  assert.equal(response.ok, true);
  response = await h.message("getState");
  assert.equal(response.registrations.length, 1);
  assert.equal(response.registrations[0].alias, "agent-7");
  assert.equal(response.registrations[0].tabId, 17);
  assert.ok(!JSON.stringify(response.registrations).includes("Unregistered"));
  assert.equal((await h.message("register", { tabId: 18, alias: "agent-7" })).ok, false);
  assert.equal((await h.message("register", { tabId: 18, alias: "other" },
    { id: h.sockets[0]?.id || "other-extension", tab: { id: 1 } })).ok, false);
});

test("paired extension executes file script only on registered tab", async () => {
  const h = createHarness();
  await tick(); await tick();
  await h.message("register", { tabId: 17, alias: "agent-7" });
  const pairingKey = "abcdef1234".padEnd(64, "0");
  assert.equal((await h.message("pair", { port: 43822, secret: pairingKey })).ok, true);
  await tick(); await tick();
  assert.equal(h.sockets.length, 1);
  const ws = h.sockets[0];
  const nonce = "d".repeat(64);
  await h.deliver(ws, { kind: "challenge", nonce });
  const auth = ws.sent.find(m => m.kind === "authenticate");
  assert.equal(auth.proof, createHmac("sha256", pairingKey).update(nonce).digest("hex"));
  await h.deliver(ws, { kind: "ready", connectionId: "epoch-1" });
  const advertised = ws.sent.find(m => m.kind === "registrations");
  assert.equal(advertised.tabs.length, 1);
  assert.equal(advertised.tabs[0].alias, "agent-7");
  assert.ok(!JSON.stringify(advertised).includes("/other"));
  const tab = advertised.tabs[0];
  await h.deliver(ws, {
    kind: "executeJs", requestId: "req1", connectionId: "epoch-1",
    registrationId: tab.registrationId,
    registrationGeneration: tab.registrationGeneration,
    entrypoint: "execute",
    source: "async function execute(args) { return { title: document.title, value: args.value }; }",
    args: { value: 42 }
  });
  const response = ws.sent.find(m => m.kind === "result" && m.requestId === "req1");
  assert.deepEqual(JSON.parse(JSON.stringify(response.result)), { title: "Agent 7", value: 42 });
  assert.equal(h.calls[0].target.tabId, 17);
  assert.equal(h.calls[0].world, "USER_SCRIPT");

  await h.deliver(ws, {
    kind: "executeJs", requestId: "req2", connectionId: "epoch-1",
    registrationId: "unknown-registration", registrationGeneration: 1,
    source: "document.title", entrypoint: "expression", args: {}
  });
  assert.equal(ws.sent.find(m => m.requestId === "req2").kind, "error");
  assert.equal(h.calls.length, 1);
});

test("a late completion from a replaced connection is never delivered to the new socket", async () => {
  const h = createHarness();
  await tick(); await tick();
  await h.message("register", { tabId: 17, alias: "agent-7" });
  const pairingKey = "abcdef1234".padEnd(64, "0");
  await h.message("pair", { port: 43822, secret: pairingKey });
  await tick(); await tick();
  const first = h.sockets[0];
  await h.deliver(first, { kind: "challenge", nonce: "e".repeat(64) });
  await h.deliver(first, { kind: "ready", connectionId: "epoch-1" });
  const tab = first.sent.find(m => m.kind === "registrations").tabs[0];
  let completeExecution;
  h.calls.push("delayed-script");
  // Hold the page's JavaScript result until after reconnect.
  // Simulate it by dispatching while a delayed userScripts call is in flight.
  const pending = new Promise(resolve => { completeExecution = resolve; });
  const fakeExecute = async () => {
    await pending;
    return [{ result: { completed: true } }];
  };
  // The service worker shares the API mock; inject a one-off delay.
  h.userScripts.execute = fakeExecute;
  await h.deliver(first, {
    kind: "executeJs", requestId: "req-late", connectionId: "epoch-1",
    registrationId: tab.registrationId, registrationGeneration: tab.registrationGeneration,
    entrypoint: "execute", source: "async function execute() { return 1; }", args: {}
  });
  // Re-pairing closes the current socket, then opens a fresh one.
  await h.message("pair", { port: 43822, secret: pairingKey });
  await tick(); await tick();
  const second = h.sockets[1];
  await h.deliver(second, { kind: "challenge", nonce: "f".repeat(64) });
  await h.deliver(second, { kind: "ready", connectionId: "epoch-2" });
  completeExecution();
  await tick(); await tick();
  assert.equal(second.sent.some(m => m.requestId === "req-late"), false);
});

test("navigation permanently suspends registration until manual reauthorization", async () => {
  const h = createHarness();
  await tick(); await tick();
  await h.message("register", { tabId: 17, alias: "agent-7" });
  h.tabs.get(17).url = "https://example.test/chat/9";
  h.listeners.updated(17, { url: "https://example.test/chat/9" });
  await tick(); await tick();
  h.tabs.get(17).url = "https://example.test/chat/7";
  const state = await h.message("getState");
  assert.equal(state.registrations[0].availability, "needsReauthorization");
  await h.message("register", { tabId: 17, alias: "agent-7" });
  const restored = await h.message("getState");
  assert.equal(restored.registrations[0].availability, "ready");
  h.listeners.removed(17);
  await tick(); await tick();
  assert.equal((await h.message("getState")).registrations[0].availability, "needsReauthorization");
});

test("browser restart leaves old registrations unavailable without user reauthorization", async () => {
  const first = createHarness();
  await tick(); await tick();
  await first.message("register", { tabId: 17, alias: "agent-7" });
  const previous = first.storage.get("registrations");
  const oldEpoch = previous[0].sessionEpoch;
  // Model a browser restart: local storage survives but session storage does not.
  const second = createHarness({ registrations: JSON.parse(JSON.stringify(previous)) });
  await tick(); await tick();
  const state = await second.message("getState");
  assert.equal(state.registrations.length, 1);
  assert.equal(state.registrations[0].availability, "needsReauthorization");
  assert.notEqual(second.session.get("browserSessionEpoch"), oldEpoch);
  await second.message("register", { tabId: 17, alias: "agent-7" });
  assert.equal((await second.message("getState")).registrations[0].availability, "ready");
});

test("opening tabs requires extension opt-in and an existing manually registered origin", async () => {
  const h = createHarness();
  await tick(); await tick();
  const key = "deadbeef".padEnd(64, "0");
  await h.message("pair", { port: 43822, secret: key });
  await tick(); await tick();
  const ws = h.sockets[0];
  await h.deliver(ws, { kind: "challenge", nonce: "a".repeat(64) });
  await h.deliver(ws, { kind: "ready", connectionId: "epoch-1" });
  const request = (requestId, url, alias = "") => h.deliver(ws, {
    kind: "openTab", connectionId: "epoch-1", requestId, url, alias, active: false
  });
  await request("disabled", "https://example.test/chat/new");
  assert.equal(ws.sent.find(m => m.requestId === "disabled").error, "AgentCreatedTabsDisabled");
  await h.message("setAgentCreatedTabs", { allowed: true });
  await request("no-grant", "https://example.test/chat/new");
  assert.equal(ws.sent.find(m => m.requestId === "no-grant").error, "OriginNotRegisteredByUser");
  await h.message("register", { tabId: 17, alias: "original" });
  await request("other-origin", "https://another.test/chat");
  assert.equal(ws.sent.find(m => m.requestId === "other-origin").error, "OriginNotRegisteredByUser");
  await request("bad-scheme", "javascript:alert(1)");
  assert.equal(ws.sent.find(m => m.requestId === "bad-scheme").error, "InvalidTabUrl");
  assert.equal(h.createdTabs.length, 0);
});

test("agent-created tab is registered and controllable across same-origin navigation", async () => {
  const h = createHarness();
  await tick(); await tick();
  await h.message("register", { tabId: 17, alias: "original" });
  await h.message("setAgentCreatedTabs", { allowed: true });
  const key = "deadbeef".padEnd(64, "0");
  await h.message("pair", { port: 43822, secret: key });
  await tick(); await tick();
  const ws = h.sockets[0];
  await h.deliver(ws, { kind: "challenge", nonce: "b".repeat(64) });
  await h.deliver(ws, { kind: "ready", connectionId: "epoch-1" });
  const requestOpen = (requestId, url, alias) => h.deliver(ws, {
    kind: "openTab", connectionId: "epoch-1", requestId, url, alias, active: false
  });
  await requestOpen("new", "https://example.test/chat/new", "agent-new");
  await tick(); await tick();
  const opened = ws.sent.find(m => m.requestId === "new" && m.kind === "tabOpened");
  assert.ok(opened, "new tab must return an opaque registration ID");
  assert.equal(opened.tab.agentCreated, true);
  assert.equal(opened.tab.alias, "agent-new");
  assert.equal(opened.tab.availability, "loading");
  assert.equal(h.createdTabs[0].active, false);
  assert.equal(h.tabs.size, 3);
  await requestOpen("duplicate-alias", "https://example.test/another", "agent-new");
  assert.equal(ws.sent.find(m => m.requestId === "duplicate-alias").error, "AliasAlreadyRegistered");
  assert.equal(h.createdTabs.length, 1);

  const newTab = h.createdTabs[0];
  newTab.url = "https://example.test/chat/abc123";
  delete newTab.pendingUrl;
  newTab.status = "complete";
  newTab.title = "Agent new";
  h.listeners.updated(newTab.id, { url: newTab.url });
  await tick(); await tick();
  const advertised = ws.sent.filter(m => m.kind === "registrations").at(-1);
  const agentTab = advertised.tabs.find(t => t.registrationId === opened.tab.registrationId);
  assert.equal(agentTab.availability, "ready");
  assert.equal(agentTab.agentCreated, true);

  await h.deliver(ws, {
    kind: "executeJs", requestId: "run", connectionId: "epoch-1",
    registrationId: agentTab.registrationId,
    registrationGeneration: agentTab.registrationGeneration,
    entrypoint: "expression", source: "document.title", args: {}
  });
  const result = ws.sent.find(m => m.requestId === "run");
  assert.equal(result.kind, "result");
  assert.equal(result.result, "Agent new");

  newTab.url = "https://unrelated.test/";
  h.listeners.updated(newTab.id, { url: newTab.url });
  await tick(); await tick();
  await h.deliver(ws, {
    kind: "executeJs", requestId: "revoked", connectionId: "epoch-1",
    registrationId: agentTab.registrationId,
    registrationGeneration: agentTab.registrationGeneration,
    entrypoint: "expression", source: "document.title", args: {}
  });
  assert.equal(ws.sent.find(m => m.requestId === "revoked").kind, "error");
});

test("disabling automatic tab creation revokes access to all agent-created tabs", async () => {
  const h = createHarness();
  await tick(); await tick();
  await h.message("register", { tabId: 17, alias: "original" });
  await h.message("setAgentCreatedTabs", { allowed: true });
  const key = "deadbeef".padEnd(64, "0");
  await h.message("pair", { port: 43822, secret: key });
  await tick(); await tick();
  const ws = h.sockets[0];
  await h.deliver(ws, { kind: "challenge", nonce: "c".repeat(64) });
  await h.deliver(ws, { kind: "ready", connectionId: "epoch-1" });
  await h.deliver(ws, {
    kind: "openTab", requestId: "new", connectionId: "epoch-1",
    url: "https://example.test/chat/new", alias: "agent-auto", active: false
  });
  await tick(); await tick();
  const tab = ws.sent.find(m => m.requestId === "new").tab;
  assert.ok(tab);
  await h.message("setAgentCreatedTabs", { allowed: false });
  const listed = ws.sent.filter(m => m.kind === "registrations").at(-1).tabs;
  assert.equal(listed.find(r => r.registrationId === tab.registrationId).availability, "needsReauthorization");
  const state = await h.message("getState");
  assert.equal(state.allowAgentCreatedTabs, false);
  assert.equal(state.registrations.find(r => r.registrationId === tab.registrationId).availability, "needsReauthorization");
});

test("removing the last manually approved tab revokes the agent-created tab's authority", async () => {
  const h = createHarness();
  await tick(); await tick();
  await h.message("register", { tabId: 17, alias: "manual" });
  await h.message("setAgentCreatedTabs", { allowed: true });
  const key = "deadbeef".padEnd(64, "0");
  await h.message("pair", { port: 43822, secret: key });
  await tick(); await tick();
  const ws = h.sockets[0];
  await h.deliver(ws, { kind: "challenge", nonce: "1".repeat(64) });
  await h.deliver(ws, { kind: "ready", connectionId: "epoch-1" });
  await h.deliver(ws, {
    kind: "openTab", requestId: "opened", connectionId: "epoch-1",
    url: "https://example.test/chat/new", alias: "auto", active: false
  });
  await tick(); await tick();
  const opened = ws.sent.find(m => m.requestId === "opened" && m.kind === "tabOpened");
  assert.ok(opened);
  h.createdTabs[0].url = "https://example.test/chat/new";
  h.createdTabs[0].status = "complete";
  delete h.createdTabs[0].pendingUrl;
  await h.message("unregister", { tabId: 17 });
  const state = await h.message("getState");
  assert.equal(state.registrations.find(t => t.registrationId === opened.tab.registrationId).availability,
    "needsReauthorization");
  await h.deliver(ws, {
    kind: "openTab", requestId: "blocked", connectionId: "epoch-1",
    url: "https://example.test/chat/another", alias: "another", active: false
  });
  assert.equal(ws.sent.find(m => m.requestId === "blocked").error, "OriginNotRegisteredByUser");
  assert.equal(h.createdTabs.length, 1);
});
