const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const { webcrypto, createHmac, createHash } = require("node:crypto");

const source = fs.readFileSync(path.join(__dirname, "..", "background.js"), "utf8");
const tick = () => new Promise(resolve => setImmediate(resolve));

function createHarness(initialStorage = {}, initialSession = {}) {
  const storage = new Map(Object.entries(initialStorage));
  const session = new Map(Object.entries(initialSession));
  const tabs = new Map([
    [17, { id: 17, url: "https://example.test/chat/7", title: "Agent 7", status: "complete", windowId: 1 }],
    [18, { id: 18, url: "https://example.test/other", title: "Unregistered", status: "complete", windowId: 1 }]
  ]);
  const listeners = {};
  const sockets = [];
  const calls = [];
  const createdTabs = [];
  const removedTabs = [];
  let userScriptHook = null;
  let postUserScriptHook = null;
  let nextCreatedId = 19;
  let hostPermissionGranted = true;
  let failNextPermissionQuery = false;
  let permissionHook = null;
  let windowQueryHook = null;
  let failNextTabQuery = false;
  let nextTabQueryHook = null;
  let failNextRegistryWrite = false;
  let holdNextRegistryWrite = false;
  let releaseRegistryWrite = null;
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
        set: async obj => {
          // Model Chrome's structured-clone snapshot, not the mutable live array.
          const saved = JSON.parse(JSON.stringify(obj));
          if (Object.hasOwn(saved, "registrations")) {
            if (failNextRegistryWrite) {
              failNextRegistryWrite = false;
              throw Error("Temporary storage write failure");
            }
            if (holdNextRegistryWrite) {
              holdNextRegistryWrite = false;
              await new Promise(resolve => { releaseRegistryWrite = resolve; });
              releaseRegistryWrite = null;
            }
          }
          for (const [k, v] of Object.entries(saved)) storage.set(k, v);
        },
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
          status: "loading", incognito: false, active: properties.active, windowId: properties.windowId
        };
        tabs.set(tab.id, tab);
        createdTabs.push(tab);
        return tab;
      },
      remove: async id => { removedTabs.push(id); tabs.delete(id); listeners.removed?.(id); },
      get: async id => {
        if (nextTabQueryHook) {
          const hook = nextTabQueryHook;
          nextTabQueryHook = null;
          await hook();
        }
        if (failNextTabQuery) {
          failNextTabQuery = false;
          throw Error("Temporary tabs API failure");
        }
        if (!tabs.has(id)) throw Error(`No tab with id: ${id}.`);
        // Chrome returns a snapshot; a previous get must not see later navigation.
        return { ...tabs.get(id) };
      },
      onUpdated: { addListener: cb => { listeners.updated = cb; } },
      onRemoved: { addListener: cb => { listeners.removed = cb; } }
    },
    windows: {
      get: async id => {
        if (windowQueryHook) await windowQueryHook();
        if (![1, 2].includes(id)) throw Error("Missing window");
        return { id, incognito: false };
      },
      onRemoved: { addListener: cb => { listeners.windowRemoved = cb; } }
    },
    webNavigation: { onHistoryStateUpdated: { addListener: cb => { listeners.history = cb; } } },
    permissions: { contains: async () => {
      if (permissionHook) {
        const hook = permissionHook;
        permissionHook = null;
        await hook();
      }
      if (failNextPermissionQuery) {
        failNextPermissionQuery = false;
        throw Error("Temporary permission API failure");
      }
      return hostPermissionGranted;
    } },
    alarms: {
      create: () => {},
      onAlarm: { addListener: cb => { listeners.alarm = cb; } }
    },
    userScripts: {
      execute: async ({ target, js, world }) => {
        if (userScriptHook) {
          const hook = userScriptHook;
          userScriptHook = null;
          await hook();
        }
        calls.push({ target, world });
        const page = tabs.get(target.tabId);
        if (!page) throw new Error("Missing target");
        const result = await vm.runInNewContext(js[0].code, {
          location: { href: page.url, origin: new URL(page.url).origin },
          document: {
            title: page.title,
            readyState: page.documentReadyState || "complete",
            querySelectorAll: () => Array.from({
              length: (page.userTurns || 0) + (page.userBubbles || 0)
            }, () => ({}))
          }
        });
        if (postUserScriptHook) {
          const hook = postUserScriptHook;
          postUserScriptHook = null;
          await hook();
        }
        return [{ frameId: 0, result }];
      }
    }
  };
  vm.runInNewContext(source, {
    chrome, crypto: webcrypto, TextEncoder, URL, WebSocket: FakeWebSocket,
    btoa: input => Buffer.from(input, "binary").toString("base64"),
    setTimeout, clearTimeout, setInterval: () => 1, clearInterval: () => {}
  });
  const message = (kind, extra = {}, sender = { id: chrome.runtime.id }) =>
    new Promise(resolve => listeners.message({ kind, ...extra }, sender, resolve));
  const deliver = async (socket, value) => {
    await socket.onmessage({ data: JSON.stringify({ version: 2, ...value }) });
    await tick(); await tick();
  };
  return { storage, session, tabs, createdTabs, removedTabs, sockets, calls, listeners, message, deliver,
    userScripts: chrome.userScripts,
    beforeNextUserScript: fn => { userScriptHook = fn; },
    afterNextUserScript: fn => { postUserScriptHook = fn; },
    setHostPermissionGranted: granted => { hostPermissionGranted = granted; },
    beforeNextPermissionCheck: fn => { permissionHook = fn; },
    onWindowQuery: fn => { windowQueryHook = fn; },
    failPermissionQueryOnce: () => { failNextPermissionQuery = true; },
    failTabQueryOnce: () => { failNextTabQuery = true; },
    beforeNextTabQuery: fn => { nextTabQueryHook = fn; },
    failRegistryWriteOnce: () => { failNextRegistryWrite = true; },
    delayNextRegistryWrite: () => { holdNextRegistryWrite = true; },
    hasDelayedRegistryWrite: () => Boolean(releaseRegistryWrite),
    releaseDelayedRegistryWrite: () => releaseRegistryWrite?.() };
}

async function windowPin(h) {
  const selected = (await h.message("getState")).windows.find(w => w.alias === "agents");
  return selected
    ? { windowRegistration: selected.windowRegistration, registrationGeneration: selected.registrationGeneration }
    : { windowRegistration: "agents", registrationGeneration: 1 };
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
  const capabilities = ws.sent.find(m => m.kind === "capabilities");
  assert.deepEqual(JSON.parse(JSON.stringify(capabilities.features)), ["domSnapshotV1"]);
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
  h.tabs.delete(17);
  h.listeners.removed(17);
  await tick(); await tick();
  assert.equal((await h.message("getState")).registrations.length, 0);
  assert.equal(h.storage.get("registrations").length, 0);
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

test("closing a registered tab removes its record and advertises the new registry", async () => {
  const h = createHarness();
  await tick(); await tick();
  await h.message("register", { tabId: 17, alias: "manual" });
  await h.message("pair", { port: 43822, secret: "a".repeat(64) });
  await tick(); await tick();
  const ws = h.sockets[0];
  await h.deliver(ws, { kind: "challenge", nonce: "b".repeat(64) });
  await h.deliver(ws, { kind: "ready", connectionId: "epoch-1" });
  h.tabs.delete(17);
  h.listeners.removed(17);
  await tick(); await tick();
  assert.equal((await h.message("getState")).registrations.length, 0);
  assert.equal(h.storage.get("registrations").length, 0);
  assert.equal(ws.sent.filter(m => m.kind === "registrations").at(-1).tabs.length, 0);
  assert.equal(h.tabs.has(18), true, "Unregistered tabs remain untouched");
});

test("cold startup prunes orphaned registrations even if previously blocked", async () => {
  const first = createHarness();
  await tick(); await tick();
  await first.message("register", { tabId: 17, alias: "closed" });
  await first.message("register", { tabId: 18, alias: "still-open" });
  const previous = JSON.parse(JSON.stringify(first.storage.get("registrations")));
  previous.find(r => r.tabId === 17).blocked = true;
  const second = createHarness({ registrations: previous });
  second.tabs.delete(17);
  await tick(); await tick();
  const state = await second.message("getState");
  assert.equal(state.registrations.length, 1);
  assert.equal(state.registrations[0].alias, "still-open");
  assert.equal(state.registrations[0].availability, "needsReauthorization");
  assert.equal(second.storage.get("registrations").length, 1);
});

test("user-closing a Jarvis-created tab frees its alias for a new tab", async () => {
  const h = createHarness();
  await tick(); await tick();
  await h.message("registerWindow", { windowId: 1, alias: "agents" });
  await h.message("setAgentCreatedTabs", { allowed: true });
  await h.message("pair", { port: 43822, secret: "c".repeat(64) });
  await tick(); await tick();
  const ws = h.sockets[0];
  await h.deliver(ws, { kind: "challenge", nonce: "d".repeat(64) });
  await h.deliver(ws, { kind: "ready", connectionId: "epoch-1" });
  const open = async requestId => h.deliver(ws, {
    kind: "openTab", requestId, connectionId: "epoch-1",
    url: "https://example.test/new", ...(await windowPin(h)), alias: "reusable"
  });
  await open("first");
  const first = ws.sent.find(m => m.requestId === "first" && m.kind === "tabOpened").tab;
  const tab = h.createdTabs[0];
  h.tabs.delete(tab.id);
  h.listeners.removed(tab.id);
  await tick(); await tick();
  assert.equal((await h.message("getState")).registrations.length, 0);
  await open("second");
  const second = ws.sent.find(m => m.requestId === "second" && m.kind === "tabOpened")?.tab;
  assert.ok(second, "Closed tab must not reserve its alias");
  assert.notEqual(second.registrationId, first.registrationId);
  assert.equal((await h.message("getState")).registrations.length, 1);
});

test("opening tabs requires registered window and explicit opt-in", async () => {
  const h = createHarness();
  await tick(); await tick();
  const key = "deadbeef".padEnd(64, "0");
  await h.message("pair", { port: 43822, secret: key });
  await tick(); await tick();
  const ws = h.sockets[0];
  await h.deliver(ws, { kind: "challenge", nonce: "a".repeat(64) });
  await h.deliver(ws, { kind: "ready", connectionId: "epoch-1" });
  const request = async (requestId, url, alias = "") => h.deliver(ws, {
    kind: "openTab", connectionId: "epoch-1", requestId, url, alias, ...(await windowPin(h)), active: false
  });
  await request("disabled", "https://example.test/chat/new");
  assert.equal(ws.sent.find(m => m.requestId === "disabled").error, "AgentCreatedTabsDisabled");
  await h.message("setAgentCreatedTabs", { allowed: true });
  await request("no-window", "https://example.test/chat/new");
  assert.equal(ws.sent.find(m => m.requestId === "no-window").error, "WindowNotRegistered");
  await h.message("registerWindow", { windowId: 1, alias: "agents" });
  await request("allowed", "https://another.test/chat");
  assert.equal(ws.sent.find(m => m.requestId === "allowed").kind, "tabOpened");
  await request("bad-scheme", "javascript:alert(1)");
  assert.equal(ws.sent.find(m => m.requestId === "bad-scheme").error, "InvalidTabUrl");
  assert.equal(h.createdTabs.length, 1);
  assert.equal(h.createdTabs[0].windowId, 1);
});

test("agent-created tab is registered and controllable across same-origin navigation", async () => {
  const h = createHarness();
  await tick(); await tick();
  await h.message("registerWindow", { windowId: 1, alias: "agents" });
  await h.message("setAgentCreatedTabs", { allowed: true });
  const key = "deadbeef".padEnd(64, "0");
  await h.message("pair", { port: 43822, secret: key });
  await tick(); await tick();
  const ws = h.sockets[0];
  await h.deliver(ws, { kind: "challenge", nonce: "b".repeat(64) });
  await h.deliver(ws, { kind: "ready", connectionId: "epoch-1" });
  const requestOpen = async (requestId, url, alias) => h.deliver(ws, {
    kind: "openTab", connectionId: "epoch-1", requestId, url, alias, ...(await windowPin(h)), active: false
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
  await h.message("registerWindow", { windowId: 1, alias: "agents" });
  await h.message("setAgentCreatedTabs", { allowed: true });
  const key = "deadbeef".padEnd(64, "0");
  await h.message("pair", { port: 43822, secret: key });
  await tick(); await tick();
  const ws = h.sockets[0];
  await h.deliver(ws, { kind: "challenge", nonce: "c".repeat(64) });
  await h.deliver(ws, { kind: "ready", connectionId: "epoch-1" });
  await h.deliver(ws, {
    kind: "openTab", requestId: "new", connectionId: "epoch-1",
    url: "https://example.test/chat/new", alias: "agent-auto", ...(await windowPin(h)), active: false
  });
  await tick(); await tick();
  const tab = ws.sent.find(m => m.requestId === "new").tab;
  assert.ok(tab);
  await h.message("setAgentCreatedTabs", { allowed: false });
  const listed = ws.sent.filter(m => m.kind === "registrations").at(-1).tabs;
  assert.equal(listed.some(r => r.registrationId === tab.registrationId), false);
  const state = await h.message("getState");
  assert.equal(state.allowAgentCreatedTabs, false);
  assert.equal(state.registrations.some(r => r.registrationId === tab.registrationId), false);
  assert.equal(h.tabs.has(h.createdTabs[0].id), true, "Browser tab survives authority removal");
});

test("revoking registered window revokes created tab authority", async () => {
  const h = createHarness();
  await tick(); await tick();
  await h.message("registerWindow", { windowId: 1, alias: "agents" });
  await h.message("setAgentCreatedTabs", { allowed: true });
  const key = "deadbeef".padEnd(64, "0");
  await h.message("pair", { port: 43822, secret: key });
  await tick(); await tick();
  const ws = h.sockets[0];
  await h.deliver(ws, { kind: "challenge", nonce: "1".repeat(64) });
  await h.deliver(ws, { kind: "ready", connectionId: "epoch-1" });
  await h.deliver(ws, {
    kind: "openTab", requestId: "opened", connectionId: "epoch-1",
    url: "https://example.test/chat/new", alias: "auto", ...(await windowPin(h)), active: false
  });
  await tick(); await tick();
  const opened = ws.sent.find(m => m.requestId === "opened" && m.kind === "tabOpened");
  assert.ok(opened);
  h.createdTabs[0].url = "https://example.test/chat/new";
  h.createdTabs[0].status = "complete";
  delete h.createdTabs[0].pendingUrl;
  await h.message("unregisterWindow", { windowId: 1 });
  const state = await h.message("getState");
  assert.equal(state.registrations.some(t => t.registrationId === opened.tab.registrationId), false);
  assert.equal(h.tabs.has(h.createdTabs[0].id), true);
  await h.deliver(ws, {
    kind: "openTab", requestId: "blocked", connectionId: "epoch-1",
    url: "https://example.test/chat/another", alias: "another", ...(await windowPin(h)), active: false
  });
  assert.equal(ws.sent.find(m => m.requestId === "blocked").error, "WindowNotRegistered");
  assert.equal(h.createdTabs.length, 1);
});

test("only agent-created tabs can be closed, preserving the window and manual tabs", async () => {
  const h = createHarness();
  await tick(); await tick();
  await h.message("registerWindow", { windowId: 1, alias: "agents" });
  await h.message("register", { tabId: 17, alias: "manual" });
  await h.message("setAgentCreatedTabs", { allowed: true });
  const key = "deadbeef".padEnd(64, "0");
  await h.message("pair", { port: 43822, secret: key });
  await tick(); await tick();
  const ws = h.sockets[0];
  await h.deliver(ws, { kind: "challenge", nonce: "f".repeat(64) });
  await h.deliver(ws, { kind: "ready", connectionId: "epoch-1" });
  assert.equal(ws.sent.find(m => m.kind === "windows").windows[0].alias, "agents");
  await h.deliver(ws, { kind: "openTab", requestId: "open", connectionId: "epoch-1",
    ...(await windowPin(h)), url: "https://example.test/chat/new", active: false });
  const created = ws.sent.find(m => m.requestId === "open").tab;
  const manual = (await h.message("getState")).registrations.find(r => r.alias === "manual");
  await h.deliver(ws, { kind: "closeTab", requestId: "not-ours", connectionId: "epoch-1",
    registrationId: manual.registrationId, registrationGeneration: manual.registrationGeneration });
  assert.equal(ws.sent.find(m => m.requestId === "not-ours").error, "TabNotAgentCreated");
  await h.deliver(ws, { kind: "closeTab", requestId: "close", connectionId: "epoch-1",
    registrationId: created.registrationId, registrationGeneration: created.registrationGeneration });
  assert.equal(ws.sent.find(m => m.requestId === "close").kind, "tabClosed");
  assert.equal(h.tabs.size, 2);
  assert.ok(h.tabs.has(17));
  assert.equal((await h.message("getState")).windows[0].alias, "agents");
  assert.equal((await h.message("getState")).registrations.length, 1);
});

async function createdTabForCloseRegression(url = "https://example.test/chat/new") {
  const h = createHarness();
  await tick(); await tick();
  await h.message("registerWindow", { windowId: 1, alias: "agents" });
  await h.message("register", { tabId: 17, alias: "manual" });
  await h.message("setAgentCreatedTabs", { allowed: true });
  await h.message("pair", { port: 43822, secret: "f".repeat(64) });
  await tick(); await tick();
  const ws = h.sockets[0];
  await h.deliver(ws, { kind: "challenge", nonce: "e".repeat(64) });
  await h.deliver(ws, { kind: "ready", connectionId: "epoch-1" });
  await h.deliver(ws, {
    kind: "openTab", requestId: "owned-open", connectionId: "epoch-1",
    url, alias: "owned", ...(await windowPin(h)), active: false
  });
  const registered = ws.sent.find(m => m.requestId === "owned-open" && m.kind === "tabOpened")?.tab;
  assert.ok(registered);
  const tab = h.createdTabs[0];
  tab.url = url;
  tab.status = "complete";
  delete tab.pendingUrl;
  const close = async (requestId, options = {}) => {
    await h.deliver(ws, {
      kind: "closeTab", requestId, connectionId: "epoch-1",
      registrationId: registered.registrationId,
      registrationGeneration: registered.registrationGeneration, ...options
    });
    return ws.sent.find(m => m.requestId === requestId);
  };
  return { h, ws, tab, registered, close };
}

// True launcher disposal evidence is historical, not authority to close later.
// Each transition happens while closeTab awaits its live page inspection.
for (const [label, mutate] of [
  ["same-origin conversation navigation", tab => { tab.url = "https://chatgpt.com/c/new-turn"; }],
  ["user turn on unchanged home route", tab => { tab.userTurns = 1; }],
  ["user bubble on unchanged home route", tab => { tab.userBubbles = 1; }]
]) {
  test("failed-launch disposal retains a tab after late " + label, async () => {
    const { h, tab, close } = await createdTabForCloseRegression("https://chatgpt.com/");
    const pageResult = { status: "notSubmitted", reason: "UnexpectedSendState",
      safeToDisposeCreatedTab: true };
    assert.equal(pageResult.safeToDisposeCreatedTab, true);
    h.beforeNextUserScript(() => mutate(tab));
    const response = await close("late-change-" + label.replaceAll(" ", "-"),
      { requireFreshHomeNoTurns: true });
    assert.equal(response.error, "CleanupSafetyChanged");
    assert.equal(h.tabs.has(tab.id), true, "New conversation must survive");
    assert.equal(h.removedTabs.includes(tab.id), false, "No remove call may occur");
    assert.equal(h.tabs.has(17), true, "Manually registered tab survives");
    assert.equal(h.tabs.has(18), true, "Unregistered tab survives");
    assert.equal(h.calls.length, 1, "Cleanup revalidates live page state");
  });
}

// A previous empty-home DOM snapshot never authorizes deletion if Chrome is
// already navigating (or starts navigating while the async probe is running).
// The harness snapshots tabs.get like Chrome, so the after-probe case requires
// a new tab query rather than observing mutation through a stale object.
for (const [label, phase, pending, probeCount] of [
  ["pending /c/ route before close admission", "before", true, 0],
  ["pending /c/ route during async DOM probe", "during", true, 1],
  ["pending /c/ route after DOM probe returns home", "after", true, 1],
  ["loading home without observable pending URL", "before", false, 0]
]) {
  test("failed-launch cleanup retains " + label, async () => {
    const { h, ws, tab, close } = await createdTabForCloseRegression("https://chatgpt.com/");
    const historicalPageResult = { status: "notSubmitted", reason: "UnexpectedSendState",
      safeToDisposeCreatedTab: true };
    assert.equal(historicalPageResult.safeToDisposeCreatedTab, true);
    const startNavigation = () => {
      // The committed URL and old document are intentionally still fresh home.
      tab.status = "loading";
      if (pending) tab.pendingUrl = "https://chatgpt.com/c/late-conversation";
      else delete tab.pendingUrl;
    };
    if (phase === "before") startNavigation();
    else if (phase === "during") h.beforeNextUserScript(startNavigation);
    else h.afterNextUserScript(startNavigation);
    const response = await close("pending-" + phase + "-" + (pending ? "route" : "unknown"),
      { requireFreshHomeNoTurns: true });
    assert.equal(response.error, "CleanupSafetyChanged");
    assert.equal(h.calls.length, probeCount,
      "Do not trust an old home DOM after a pending navigation");
    assert.equal(h.removedTabs.includes(tab.id), false, "Pending conversation must survive");
    assert.equal(h.tabs.has(tab.id), true);
    assert.equal(h.tabs.has(17), true, "Manually registered tab survives");
    assert.equal(h.tabs.has(18), true, "Unrelated tab survives");
    assert.equal(ws.sent.some(m => m.kind === "executeJs"), false,
      "Cleanup cannot replay a launcher after a definitive failure");
  });
}

test("failed-launch cleanup rejects a pending /c/ URL even if status reports complete", async () => {
  const { h, tab, close } = await createdTabForCloseRegression("https://chatgpt.com/");
  tab.pendingUrl = "https://chatgpt.com/c/not-yet-committed";
  // Contradictory Chrome observations must not grant deletion authority.
  assert.equal(tab.status, "complete");
  const response = await close("pending-complete", { requireFreshHomeNoTurns: true });
  assert.equal(response.error, "CleanupSafetyChanged");
  assert.equal(h.calls.length, 0);
  assert.equal(h.removedTabs.length, 0);
  assert.equal(h.tabs.has(tab.id), true);
});

test("failed-launch cleanup retains tab when post-probe navigation inspection fails", async () => {
  const { h, tab, close } = await createdTabForCloseRegression("https://chatgpt.com/");
  h.afterNextUserScript(() => h.failTabQueryOnce());
  const response = await close("post-probe-unverified", { requireFreshHomeNoTurns: true });
  assert.equal(response.error, "CleanupSafetyUnverified");
  assert.equal(h.calls.length, 1);
  assert.equal(h.removedTabs.length, 0);
  assert.equal(h.tabs.has(tab.id), true);
});

test("failed-launch disposal closes a still-fresh, zero-turn ChatGPT home tab", async () => {
  const { h, tab, close } = await createdTabForCloseRegression("https://chatgpt.com/");
  const response = await close("fresh-home", { requireFreshHomeNoTurns: true });
  assert.equal(response.kind, "tabClosed");
  assert.equal(h.tabs.has(tab.id), false);
  assert.deepEqual(h.removedTabs, [tab.id]);
  assert.equal(h.calls.length, 1, "Live DOM revalidation is mandatory");
  assert.equal(h.tabs.has(17), true);
  assert.equal(h.tabs.has(18), true);
});

test("pre-injection cleanup removes only uncommitted blank initial navigation", async () => {
  const { h, tab, close } = await createdTabForCloseRegression("https://chatgpt.com/");
  tab.url = "about:blank";
  tab.pendingUrl = "https://chatgpt.com/";
  tab.status = "loading";
  const response = await close("initial-blank", { requireFreshHomeNoTurns: true });
  assert.equal(response.kind, "tabClosed");
  assert.equal(h.tabs.has(tab.id), false);
  assert.equal(h.calls.length, 0, "No provider DOM yet exists");
});

test("pre-injection cleanup also disposes a genuinely blank generic initial navigation", async () => {
  const url = "https://example.test/chat/new";
  const { h, tab, close } = await createdTabForCloseRegression(url);
  tab.url = "about:blank";
  tab.pendingUrl = url;
  tab.status = "loading";
  const response = await close("generic-initial-blank", { requireFreshHomeNoTurns: true });
  assert.equal(response.kind, "tabClosed");
  assert.equal(h.tabs.has(tab.id), false);
  assert.equal(h.calls.length, 0);
  assert.equal(h.tabs.has(17), true);
  assert.equal(h.tabs.has(18), true);
});

test("failed-launch cleanup retains uninspectable and non-ChatGPT tabs", async () => {
  const a = await createdTabForCloseRegression("https://chatgpt.com/");
  a.tab.documentReadyState = "interactive";
  const result = await a.close("not-hydrated", { requireFreshHomeNoTurns: true });
  assert.equal(result.error, "CleanupSafetyUnverified");
  assert.equal(a.h.removedTabs.length, 0);
  const b = await createdTabForCloseRegression();
  const generic = await b.close("other-site", { requireFreshHomeNoTurns: true });
  assert.equal(generic.error, "CleanupSafetyUnverified");
  assert.equal(b.h.removedTabs.length, 0);
});

for (const lifecycleState of ["discarded", "frozen"]) {
  test("owned " + lifecycleState + " tab can be safely closed without waking it", async () => {
    const { h, tab, registered, close } = await createdTabForCloseRegression();
    tab[lifecycleState] = true;
    const status = await h.message("getState");
    assert.equal(status.registrations.find(r => r.registrationId === registered.registrationId).availability,
      lifecycleState);
    const response = await close("close-" + lifecycleState);
    assert.equal(response.kind, "tabClosed");
    assert.equal(h.tabs.has(tab.id), false);
    assert.equal(h.tabs.has(17), true, "Manually registered tab must survive");
    assert.equal(h.tabs.has(18), true, "Unregistered tab must survive");
    assert.equal((await h.message("getState")).windows[0].alias, "agents");
    assert.equal(h.calls.length, 0, "Closing may not execute a page script");
    assert.equal(tab[lifecycleState], true, "Closing must not wake/activate the tab");
  });
}

test("owned discarded/frozen close fails closed on stale generation, revoked origin or window", async () => {
  const { h, tab, registered, close } = await createdTabForCloseRegression();
  tab.discarded = true;
  assert.equal((await close("wrong-generation", {
    registrationGeneration: registered.registrationGeneration + 1
  })).error, "TabNotAgentCreated");
  assert.equal(h.tabs.has(tab.id), true);

  h.setHostPermissionGranted(false);
  assert.equal((await close("revoked-permission")).error, "RegistrationStale");
  assert.equal(h.tabs.has(tab.id), true, "Host permission revocation cannot authorize closure");
  h.setHostPermissionGranted(true);

  tab.windowId = 99;
  assert.equal((await close("moved-window")).error, "RegistrationStale");
  assert.equal(h.tabs.has(tab.id), true, "Moved tab cannot be closed");
});

test("closing an owned frozen tab refuses cross-origin navigation", async () => {
  const { h, tab, close } = await createdTabForCloseRegression();
  tab.frozen = true;
  tab.url = "https://unrelated.test/other";
  assert.equal((await close("cross-origin")).error, "RegistrationStale");
  assert.equal(h.tabs.has(tab.id), true);
});

test("closing a pending created tab requires its originally granted origin", async () => {
  const { h, tab, close } = await createdTabForCloseRegression();
  tab.url = "about:blank";
  tab.pendingUrl = "https://example.test/chat/new";
  tab.status = "loading";
  h.setHostPermissionGranted(false);
  assert.equal((await close("pending-revoked")).error, "RegistrationStale");
  assert.equal(h.tabs.has(tab.id), true);
});

async function snapshotHarness(dom = {}) {
  const h = createHarness();
  await tick(); await tick();
  await h.message("register", { tabId: 17, alias: "snapshot" });
  await h.message("pair", { port: 43822, secret: "a".repeat(64) });
  await tick(); await tick();
  const socket = h.sockets[0];
  await h.deliver(socket, { kind: "challenge", nonce: "b".repeat(64) });
  await h.deliver(socket, { kind: "ready", connectionId: "epoch-1" });
  const tab = socket.sent.find(m => m.kind === "registrations").tabs[0];
  const listeners = new Map();
  const mutations = new Set();
  const state = { html: dom.html || "<html><body><div id='app'></div></body></html>",
    node: null, url: "https://example.test/chat/7" };
  const document = {
    title: "Client-rendered page",
    readyState: "complete",
    get documentElement() { return { outerHTML: state.html }; },
    querySelector(selector) {
      if (dom.invalidSelector && selector === dom.invalidSelector) throw new SyntaxError("invalid CSS");
      return selector === "#results" ? state.node : null;
    },
    addEventListener(name, callback) {
      const entries = listeners.get(name) || new Set();
      entries.add(callback);
      listeners.set(name, entries);
    },
    removeEventListener(name, callback) { listeners.get(name)?.delete(callback); }
  };
  class Observer {
    constructor(callback) { this.callback = callback; }
    observe() { mutations.add(this.callback); }
    disconnect() { mutations.delete(this.callback); }
  }
  const update = (text = "Loaded Ω after hydration") => {
    state.node = { outerHTML: "<p id='results'>" + text + "</p>", textContent: text,
      getBoundingClientRect: () => ({width: 100,height:20}) };
    state.html = "<html><body><div id='app'>" + state.node.outerHTML + "</div></body></html>";
    for (const observe of mutations) observe();
  };
  const fire = name => { for(const listener of [...(listeners.get(name) || [])]) listener(); };
  h.userScripts.execute = async ({target,js,world}) => {
    assert.equal(target.tabId,17);
    assert.equal(world,"USER_SCRIPT");
    const location = { get href() { return state.url; }, get origin() { return new URL(state.url).origin; },
      get pathname() { return new URL(state.url).pathname; } };
    const result = await vm.runInNewContext(js[0].code, {
      document, location, window: document, Date, setTimeout, clearTimeout,
      setInterval, clearInterval, MutationObserver: Observer,
      getComputedStyle: () => ({ display:"block",visibility:"visible" })
    });
    return [{result}];
  };
  const capture = async (requestId, options = {}) => {
    await h.deliver(socket, {
      kind:"captureDom", requestId, connectionId:"epoch-1",
      registrationId: tab.registrationId, registrationGeneration:tab.registrationGeneration,
      mode: options.mode || "inline", captureSelector: options.captureSelector || "",
      timeoutMs:options.timeoutMs || 500, wait: options.wait || {kind:"immediate"}
    });
    return socket.sent.find(m => m.requestId === requestId);
  };
  const response = async (requestId, ms = 900) => {
    const deadline=Date.now()+ms;
    while(Date.now()<deadline){
      const value=socket.sent.find(m=>m.requestId===requestId);
      if(value) return value;
      await new Promise(resolve=>setTimeout(resolve,5));
    }
    throw new Error("Missing snapshot reply: "+requestId);
  };
  return { h, socket, tab, state, document, capture, response, update, fire, listeners, mutations };
}

test("DOM capture waits for dynamic hydration and returns the final rendered HTML", async () => {
  const x = await snapshotHarness();
  await x.capture("hydrated", {wait:{kind:"selector",selector:"#results",state:"textContains",text:"Ω"}});
  setTimeout(() => x.update(),35);
  const result = await x.response("hydrated");
  assert.equal(result.kind,"snapshotReady");
  assert.equal(result.waitKind,"selector");
  assert.match(result.html,/Loaded Ω after hydration/);
  assert.equal(result.bytes,new TextEncoder().encode(result.html).byteLength);
  assert.equal(x.mutations.size,0,"MutationObserver must be disconnected");
});

test("DOM capture supports a bounded event trigger and cleans up its listener", async () => {
  const x = await snapshotHarness();
  await x.capture("event-capture",{wait:{kind:"event",name:"page:ready",target:"document"}});
  x.update("results from custom event");
  x.fire("page:ready");
  const result = await x.response("event-capture");
  assert.equal(result.kind,"snapshotReady");
  assert.match(result.html,/results from custom event/);
  assert.equal(x.listeners.get("page:ready")?.size,0);
});

test("DOM capture waits a timer and can snapshot a selected subtree", async () => {
  const x = await snapshotHarness();
  await x.capture("timer",{wait:{kind:"delay",milliseconds:40},captureSelector:"#results"});
  setTimeout(() => x.update("subtree content"),15);
  const result = await x.response("timer");
  assert.equal(result.html,"<p id='results'>subtree content</p>");
  assert.equal(result.waitKind,"delay");
});

test("DOM capture times out waiting for missing event and discards listener", async () => {
  const x = await snapshotHarness();
  await x.capture("timeout",{wait:{kind:"event",name:"never",target:"document"},timeoutMs:110});
  const result = await x.response("timeout");
  assert.equal(result.kind,"error");
  assert.equal(result.error,"SnapshotTimeout");
  assert.equal(x.listeners.get("never")?.size,0);
});

test("DOM capture classifies navigation and invalid selector distinctly", async () => {
  const x = await snapshotHarness({invalidSelector:"###"});
  await x.capture("bad-selector",{wait:{kind:"selector",selector:"###",state:"exists"}});
  assert.equal((await x.response("bad-selector")).error,"InvalidSelector");
  await x.capture("navigation",{wait:{kind:"selector",selector:"#results",state:"exists"},timeoutMs:300});
  setTimeout(()=>{x.state.url="https://example.test/next";},35);
  assert.equal((await x.response("navigation")).error,"NavigationDuringCapture");
});

test("large DOM is captured once, then transferred as exact checksummed chunks", async () => {
  const x = await snapshotHarness({html:"<html><body>"+"🙂".repeat(20000)+"</body></html>"});
  await x.capture("inline-large");
  assert.match((await x.response("inline-large")).error,/SnapshotTooLargeForInline/);
  await x.capture("export-large",{mode:"file"});
  const ready = await x.response("export-large");
  assert.equal(ready.kind,"snapshotReady");
  assert.equal(ready.html,undefined);
  assert.ok(ready.bytes>48000);
  const chunks=[];
  for(let offset=0;offset<ready.bytes;){
    const reqId="chunk-"+offset;
    await x.h.deliver(x.socket,{kind:"snapshotChunk",requestId:reqId,connectionId:"epoch-1",
      snapshotId:ready.snapshotId,offset});
    const reply=await x.response(reqId);
    assert.equal(reply.kind,"snapshotChunk");
    assert.equal(reply.offset,offset);
    const part=Buffer.from(reply.data,"base64");
    chunks.push(part);
    offset+=part.length;
  }
  const assembled=Buffer.concat(chunks);
  assert.equal(assembled.length,ready.bytes);
  assert.equal(assembled.toString(),"<html><body>"+"🙂".repeat(20000)+"</body></html>");
  assert.equal(createHash("sha256").update(assembled).digest("hex"),ready.sha256);
  await x.h.deliver(x.socket,{kind:"releaseSnapshot",connectionId:"epoch-1",snapshotId:ready.snapshotId});
  await x.h.deliver(x.socket,{kind:"snapshotChunk",connectionId:"epoch-1",requestId:"released",
    snapshotId:ready.snapshotId,offset:0});
  assert.equal((await x.response("released")).error,"SnapshotExpired");
});

test("DOM snapshot respects tab registration and permission revocation", async () => {
  const x = await snapshotHarness();
  await x.h.deliver(x.socket,{kind:"captureDom",requestId:"unregistered",connectionId:"epoch-1",
    registrationId:"not-a-tab",registrationGeneration:1,timeoutMs:500,mode:"inline",
    captureSelector:"",wait:{kind:"immediate"}});
  assert.equal((await x.response("unregistered")).error,"RegistrationStale");
  await x.capture("authorized",{mode:"file"});
  const ready=await x.response("authorized");
  x.h.tabs.get(17).url="https://unapproved.test/";
  await x.h.deliver(x.socket,{kind:"snapshotChunk",requestId:"revoked",connectionId:"epoch-1",
    snapshotId:ready.snapshotId,offset:0});
  assert.equal((await x.response("revoked")).error,"SnapshotRegistrationChanged");
});

test("DOM event wait reports a same-origin SPA navigation rather than silent timeout", async () => {
  const x = await snapshotHarness();
  await x.capture("navigate-event",{wait:{kind:"event",name:"future",target:"document"},timeoutMs:300});
  setTimeout(()=>{x.state.url="https://example.test/new-route";},30);
  const result = await x.response("navigate-event");
  assert.equal(result.error,"NavigationDuringCapture");
  assert.equal(x.listeners.get("future")?.size,0);
});

test("snapshot subtree selector errors are reported without exposing DOM content", async () => {
  const x = await snapshotHarness({invalidSelector:"###"});
  await x.capture("invalid-subtree",{captureSelector:"###"});
  const result = await x.response("invalid-subtree");
  assert.equal(result.error,"InvalidSelector");
  assert.equal(JSON.stringify(result).includes("<html>"),false);
});

test("snapshot rejects unbounded conditions before executing page code", async () => {
  const x = await snapshotHarness();
  await x.capture("bad-wait",{wait:{kind:"delay",milliseconds:6000000}});
  const result=await x.response("bad-wait");
  assert.equal(result.error,"InvalidSnapshotOptions");
});

test("closed tab must stay removed from storage despite an older pending registration write", async () => {
  const h = createHarness();
  await tick(); await tick();
  assert.equal((await h.message("register", { tabId: 17, alias: "closed" })).ok, true);
  h.delayNextRegistryWrite();
  const adding = h.message("register", { tabId: 18, alias: "new" });
  for (let i = 0; i < 20 && !h.hasDelayedRegistryWrite(); i++) await tick();
  assert.equal(h.hasDelayedRegistryWrite(), true, "An older registration write must be pending");
  h.tabs.delete(17);
  h.listeners.removed(17);
  await tick(); await tick();
  assert.deepEqual(Array.from((await h.message("getState")).registrations, r => r.tabId), [18],
    "Removal must update the live registry even while a prior write is pending");
  h.releaseDelayedRegistryWrite();
  assert.equal((await adding).ok, true);
  for (let i = 0; i < 20; i++) await tick();
  assert.deepEqual(h.storage.get("registrations").map(r => r.tabId), [18],
    "Serial writes must leave the latest state durable, never resurrect the closed tab");
});

test("transient permissions API rejection preserves an existing tab registration", async () => {
  const h = createHarness();
  await tick(); await tick();
  assert.equal((await h.message("register", { tabId: 17, alias: "manual" })).ok, true);
  h.failPermissionQueryOnce();
  const state = await h.message("getState");
  assert.equal(h.tabs.has(17), true);
  assert.equal(state.registrations.length, 1);
  assert.equal(state.registrations[0].availability, "needsReauthorization");
  assert.equal(h.storage.get("registrations").length, 1);
  assert.equal((await h.message("getState")).registrations[0].availability, "ready",
    "A later successful permission check should restore availability");
});

test("transient tabs API rejection preserves an existing tab registration", async () => {
  const h = createHarness();
  await tick(); await tick();
  assert.equal((await h.message("register", { tabId: 17, alias: "manual" })).ok, true);
  h.failTabQueryOnce();
  const state = await h.message("getState");
  assert.equal(state.registrations.length, 1);
  assert.equal(state.registrations[0].availability, "needsReauthorization");
  assert.equal(h.storage.get("registrations").length, 1);
  assert.equal((await h.message("getState")).registrations[0].availability, "ready");
});

test("failed registry write does not poison later persistence", async () => {
  const h = createHarness();
  await tick(); await tick();
  h.failRegistryWriteOnce();
  const failed = await h.message("register", { tabId: 17, alias: "first" });
  assert.equal(failed.ok, false);
  const next = await h.message("register", { tabId: 18, alias: "second" });
  assert.equal(next.ok, true);
  assert.deepEqual(h.storage.get("registrations").map(r => r.tabId), [17, 18]);
});

test("opaque window identity rejects alias reassignment to another registered window", async () => {
  const h = createHarness();
  await tick(); await tick();
  await h.message("registerWindow", { windowId: 1, alias: "agents" });
  await h.message("setAgentCreatedTabs", { allowed: true });
  await h.message("pair", { port: 43822, secret: "b".repeat(64) });
  await tick(); await tick();
  const ws = h.sockets[0];
  await h.deliver(ws, { kind: "challenge", nonce: "d".repeat(64) });
  await h.deliver(ws, { kind: "ready", connectionId: "epoch-1" });
  const original = await windowPin(h);
  await h.message("registerWindow", { windowId: 1, alias: "old-agents" });
  await h.message("registerWindow", { windowId: 2, alias: "agents" });
  await h.deliver(ws, { kind: "openTab", requestId: "stale-alias", connectionId: "epoch-1",
    url: "https://example.test/chat", alias: "test", ...original });
  assert.equal(ws.sent.find(m => m.requestId === "stale-alias").error, "WindowRegistrationChanged");
  assert.equal(h.createdTabs.length, 0, "An alias reassignment must never open in window B");
});

test("window generation change during async permission check cannot create tab", async () => {
  const h = createHarness();
  await tick(); await tick();
  await h.message("registerWindow", { windowId: 1, alias: "agents" });
  await h.message("setAgentCreatedTabs", { allowed: true });
  await h.message("pair", { port: 43822, secret: "b".repeat(64) });
  await tick(); await tick();
  const ws = h.sockets[0];
  await h.deliver(ws, { kind: "challenge", nonce: "d".repeat(64) });
  await h.deliver(ws, { kind: "ready", connectionId: "epoch-1" });
  const original = await windowPin(h);
  h.beforeNextPermissionCheck(async () => {
    await h.message("registerWindow", { windowId: 1, alias: "agents" });
  });
  await h.deliver(ws, { kind: "openTab", requestId: "permission-race", connectionId: "epoch-1",
    url: "https://example.test/chat", alias: "test", ...original });
  assert.equal(ws.sent.find(m => m.requestId === "permission-race").error, "WindowRegistrationChanged");
  assert.equal(h.createdTabs.length, 0, "Generation must be rechecked after permission await");
});

test("revoking agent-created tabs during final window lookup cannot create tab", async () => {
  const h = createHarness();
  await tick(); await tick();
  await h.message("registerWindow", { windowId: 1, alias: "agents" });
  await h.message("setAgentCreatedTabs", { allowed: true });
  await h.message("pair", { port: 43822, secret: "b".repeat(64) });
  await tick(); await tick();
  const ws = h.sockets[0];
  await h.deliver(ws, { kind: "challenge", nonce: "d".repeat(64) });
  await h.deliver(ws, { kind: "ready", connectionId: "epoch-1" });
  const pin = await windowPin(h);
  let windowQueries = 0;
  h.onWindowQuery(async () => {
    if (++windowQueries !== 2) return;
    h.onWindowQuery(null);
    const revoked = await h.message("setAgentCreatedTabs", { allowed: false });
    assert.equal(revoked.ok, true);
  });
  await h.deliver(ws, { kind: "openTab", requestId: "revoked-during-lookup",
    connectionId: "epoch-1", url: "https://example.test/chat", alias: "must-not-open", ...pin });
  assert.equal(windowQueries, 2, "Revocation must happen in the final window lookup");
  assert.equal((await h.message("getState")).allowAgentCreatedTabs, false);
  assert.equal(ws.sent.find(m => m.requestId === "revoked-during-lookup")?.error,
    "AgentCreatedTabsDisabled");
  assert.equal(h.createdTabs.length, 0, "A revoked opt-in forbids tab creation");
});

test("revoking host permission during final window lookup prevents agent tab creation", async () => {
  const h = createHarness();
  await tick(); await tick();
  await h.message("registerWindow", { windowId: 1, alias: "agents" });
  await h.message("setAgentCreatedTabs", { allowed: true });
  await h.message("pair", { port: 43822, secret: "b".repeat(64) });
  await tick(); await tick();
  const ws = h.sockets[0];
  await h.deliver(ws, { kind: "challenge", nonce: "d".repeat(64) });
  await h.deliver(ws, { kind: "ready", connectionId: "epoch-1" });
  const pin = await windowPin(h);
  let windowQueries = 0;
  h.onWindowQuery(async () => {
    if (++windowQueries !== 2) return;
    h.onWindowQuery(null);
    h.setHostPermissionGranted(false);
  });
  await h.deliver(ws, { kind: "openTab", requestId: "host-revoked-during-lookup",
    connectionId: "epoch-1", url: "https://example.test/chat", alias: "must-not-open", ...pin });
  assert.equal(windowQueries, 2, "Host permission must be revoked after the first permission check");
  assert.equal(ws.sent.find(m => m.requestId === "host-revoked-during-lookup")?.error,
    "BrowserPermissionMissing");
  assert.equal(h.createdTabs.length, 0, "No new tab may bypass a revoked destination permission");
});

test("window re-registration during final permission check cannot create tab", async () => {
  const h = createHarness();
  await tick(); await tick();
  await h.message("registerWindow", { windowId: 1, alias: "agents" });
  await h.message("setAgentCreatedTabs", { allowed: true });
  await h.message("pair", { port: 43822, secret: "b".repeat(64) });
  await tick(); await tick();
  const ws = h.sockets[0];
  await h.deliver(ws, { kind: "challenge", nonce: "d".repeat(64) });
  await h.deliver(ws, { kind: "ready", connectionId: "epoch-1" });
  const pin = await windowPin(h);
  let windowQueries = 0;
  h.onWindowQuery(async () => {
    if (++windowQueries !== 2) return;
    h.onWindowQuery(null);
    h.beforeNextPermissionCheck(async () => {
      assert.equal((await h.message("registerWindow", { windowId: 1, alias: "agents" })).ok, true);
    });
  });
  await h.deliver(ws, { kind: "openTab", requestId: "window-changed-during-final-permission",
    connectionId: "epoch-1", url: "https://example.test/chat", alias: "must-not-open", ...pin });
  assert.equal(windowQueries, 2);
  assert.equal(ws.sent.find(m => m.requestId === "window-changed-during-final-permission")?.error,
    "WindowRegistrationChanged");
  assert.equal(h.createdTabs.length, 0, "Final async permission check must not leave window identity stale");
});

test("re-enabling opt-in during final window lookup does not authorize stale open", async () => {
  const h = createHarness();
  await tick(); await tick();
  await h.message("registerWindow", { windowId: 1, alias: "agents" });
  await h.message("setAgentCreatedTabs", { allowed: true });
  await h.message("pair", { port: 43822, secret: "b".repeat(64) });
  await tick(); await tick();
  const ws = h.sockets[0];
  await h.deliver(ws, { kind: "challenge", nonce: "d".repeat(64) });
  await h.deliver(ws, { kind: "ready", connectionId: "epoch-1" });
  const pin = await windowPin(h);
  let windowQueries = 0;
  h.onWindowQuery(async () => {
    if (++windowQueries !== 2) return;
    h.onWindowQuery(null);
    assert.equal((await h.message("setAgentCreatedTabs", { allowed: false })).ok, true);
    assert.equal((await h.message("setAgentCreatedTabs", { allowed: true })).ok, true);
  });
  await h.deliver(ws, { kind: "openTab", requestId: "stale-opt-in",
    connectionId: "epoch-1", url: "https://example.test/chat", alias: "must-not-open", ...pin });
  assert.equal(windowQueries, 2);
  assert.equal((await h.message("getState")).allowAgentCreatedTabs, true);
  assert.equal(ws.sent.find(m => m.requestId === "stale-opt-in")?.error,
    "AgentCreatedTabsAuthorizationChanged");
  assert.equal(h.createdTabs.length, 0, "Fresh opt-in must not revive the old request");
});

test("queued OpenTab cannot survive disable and re-enable of agent tab creation", async () => {
  const h = createHarness();
  await tick(); await tick();
  await h.message("registerWindow", { windowId: 1, alias: "agents" });
  await h.message("setAgentCreatedTabs", { allowed: true });
  await h.message("pair", { port: 43822, secret: "b".repeat(64) });
  await tick(); await tick();
  const ws = h.sockets[0];
  await h.deliver(ws, { kind: "challenge", nonce: "d".repeat(64) });
  await h.deliver(ws, { kind: "ready", connectionId: "epoch-1" });
  const pin = await windowPin(h);

  let releaseLookup;
  const heldLookup = new Promise(resolve => { releaseLookup = resolve; });
  let reportLookup;
  const reachedLookup = new Promise(resolve => { reportLookup = resolve; });
  let windowQueries = 0;
  h.onWindowQuery(async () => {
    if (++windowQueries !== 2) return;
    h.onWindowQuery(null);
    reportLookup();
    await heldLookup;
  });
  const first = h.deliver(ws, { kind: "openTab", requestId: "first-queued-open",
    connectionId: "epoch-1", url: "https://example.test/chat", alias: "first", ...pin });
  await reachedLookup;
  // This request is received under the old epoch, but awaits the first tab.
  await h.deliver(ws, { kind: "openTab", requestId: "second-queued-open",
    connectionId: "epoch-1", url: "https://example.test/chat", alias: "second", ...pin });
  assert.equal((await h.message("setAgentCreatedTabs", { allowed: false })).ok, true);
  assert.equal((await h.message("setAgentCreatedTabs", { allowed: true })).ok, true);
  releaseLookup();
  await first;
  await tick(); await tick();

  assert.equal(windowQueries, 2);
  assert.equal(ws.sent.find(m => m.requestId === "first-queued-open")?.error,
    "AgentCreatedTabsAuthorizationChanged");
  assert.equal(ws.sent.find(m => m.requestId === "second-queued-open")?.error,
    "AgentCreatedTabsAuthorizationChanged");
  assert.equal(h.createdTabs.length, 0, "Old queued request must never adopt fresh opt-in");
});

test("unpairing during final window lookup prevents any created tab", async () => {
  const h = createHarness();
  await tick(); await tick();
  await h.message("registerWindow", { windowId: 1, alias: "agents" });
  await h.message("setAgentCreatedTabs", { allowed: true });
  await h.message("pair", { port: 43822, secret: "b".repeat(64) });
  await tick(); await tick();
  const ws = h.sockets[0];
  await h.deliver(ws, { kind: "challenge", nonce: "d".repeat(64) });
  await h.deliver(ws, { kind: "ready", connectionId: "epoch-1" });
  const pin = await windowPin(h);
  let windowQueries = 0;
  h.onWindowQuery(async () => {
    if (++windowQueries !== 2) return;
    h.onWindowQuery(null);
    assert.equal((await h.message("unpair")).ok, true);
  });
  await h.deliver(ws, { kind: "openTab", requestId: "unpaired-during-lookup",
    connectionId: "epoch-1", url: "https://example.test/chat", alias: "must-not-open", ...pin });
  assert.equal(windowQueries, 2);
  assert.equal((await h.message("getState")).paired, false);
  assert.equal(h.createdTabs.length, 0, "Disconnected requests cannot create tabs");
  assert.equal(ws.sent.some(m => m.requestId === "unpaired-during-lookup" && m.kind === "tabOpened"), false);
});

test("manually re-registered created tab rejects pinned execution and retains user tab", async () => {
  const h = createHarness();
  await tick(); await tick();
  await h.message("registerWindow", { windowId: 1, alias: "agents" });
  await h.message("setAgentCreatedTabs", { allowed: true });
  await h.message("pair", { port: 43822, secret: "b".repeat(64) });
  await tick(); await tick();
  const ws = h.sockets[0];
  await h.deliver(ws, { kind: "challenge", nonce: "d".repeat(64) });
  await h.deliver(ws, { kind: "ready", connectionId: "epoch-1" });
  await h.deliver(ws, { kind: "openTab", requestId: "create", connectionId: "epoch-1",
    url: "https://example.test/chat", alias: "owned", ...(await windowPin(h)) });
  const created = ws.sent.find(m => m.requestId === "create" && m.kind === "tabOpened").tab;
  const tab = h.createdTabs[0];
  tab.url = "https://example.test/chat";
  tab.status = "complete";
  delete tab.pendingUrl;
  const adopted = await h.message("register", { tabId: tab.id, alias: "user-tab" });
  assert.equal(adopted.ok, true);
  const current = (await h.message("getState")).registrations.find(r => r.registrationId === created.registrationId);
  assert.equal(current.agentCreated, false);
  assert.ok(current.registrationGeneration > created.registrationGeneration);
  for (const [requestId, generation] of [["stale", created.registrationGeneration],
                                          ["provenance", current.registrationGeneration]]) {
    await h.deliver(ws, { kind: "executeJs", requestId, connectionId: "epoch-1",
      registrationId: created.registrationId, registrationGeneration: generation,
      requireAgentCreated: true, source: "document.title", entrypoint: "expression", args: {} });
    assert.equal(ws.sent.find(m => m.requestId === requestId).error, "RegistrationStale");
  }
  await h.deliver(ws, { kind: "closeTab", requestId: "close-stale", connectionId: "epoch-1",
    registrationId: created.registrationId, registrationGeneration: created.registrationGeneration });
  assert.equal(ws.sent.find(m => m.requestId === "close-stale").error, "TabNotAgentCreated");
  assert.equal(h.calls.length, 0, "A user-owned tab must never run the bootstrap");
  assert.equal(h.tabs.has(tab.id), true, "Ownership change forbids cleanup");
});

test("ownership changed during close preflight never removes the adopted tab", async () => {
  const { h, ws, tab, registered } = await createdTabForCloseRegression();
  h.beforeNextTabQuery(async () => {
    const result = await h.message("register", { tabId: tab.id, alias: "adopted" });
    assert.equal(result.ok, true);
  });
  await h.deliver(ws, { kind: "closeTab", requestId: "close-race", connectionId: "epoch-1",
    registrationId: registered.registrationId,
    registrationGeneration: registered.registrationGeneration });
  assert.equal(ws.sent.find(m => m.requestId === "close-race").error, "TabNotAgentCreated");
  assert.equal(h.tabs.has(tab.id), true, "User-adopted tab must not be removed");
  assert.equal(h.calls.length, 0);
});

test("adoption during execute permission checks fails before page injection", async () => {
  const h = createHarness();
  await tick(); await tick();
  await h.message("registerWindow", { windowId: 1, alias: "agents" });
  await h.message("setAgentCreatedTabs", { allowed: true });
  await h.message("pair", { port: 43822, secret: "b".repeat(64) });
  await tick(); await tick();
  const ws = h.sockets[0];
  await h.deliver(ws, { kind: "challenge", nonce: "d".repeat(64) });
  await h.deliver(ws, { kind: "ready", connectionId: "epoch-1" });
  await h.deliver(ws, { kind: "openTab", requestId: "create-execute", connectionId: "epoch-1",
    url: "https://example.test/chat", alias: "owned", ...(await windowPin(h)) });
  const opened = ws.sent.find(m => m.requestId === "create-execute" && m.kind === "tabOpened").tab;
  const tab = h.createdTabs[0];
  tab.url = "https://example.test/chat";
  tab.status = "complete";
  delete tab.pendingUrl;
  h.beforeNextTabQuery(async () => {
    const adopted = await h.message("register", { tabId: tab.id, alias: "operator-tab" });
    assert.equal(adopted.ok, true);
  });
  await h.deliver(ws, { kind: "executeJs", requestId: "execute-race",
    connectionId: "epoch-1", registrationId: opened.registrationId,
    registrationGeneration: opened.registrationGeneration, requireAgentCreated: true,
    source: "document.title", entrypoint: "expression", args: {} });
  assert.equal(ws.sent.find(m => m.requestId === "execute-race").error, "RegistrationStale");
  assert.equal(h.calls.length, 0, "Ownership mutation during assess must prevent injection");
  assert.equal(h.tabs.has(tab.id), true);
});

async function createdPageForExecutionRace() {
  const h = createHarness();
  await tick(); await tick();
  await h.message("registerWindow", { windowId: 1, alias: "agents" });
  await h.message("setAgentCreatedTabs", { allowed: true });
  await h.message("pair", { port: 43822, secret: "b".repeat(64) });
  await tick(); await tick();
  const ws = h.sockets[0];
  await h.deliver(ws, { kind: "challenge", nonce: "d".repeat(64) });
  await h.deliver(ws, { kind: "ready", connectionId: "epoch-1" });
  await h.deliver(ws, { kind: "openTab", requestId: "page-for-race", connectionId: "epoch-1",
    url: "https://example.test/chat", alias: "created-page", ...(await windowPin(h)) });
  const opened = ws.sent.find(m => m.requestId === "page-for-race" && m.kind === "tabOpened")?.tab;
  assert.ok(opened);
  const tab = h.createdTabs[0];
  tab.url = "https://example.test/chat";
  tab.status = "complete";
  delete tab.pendingUrl;
  return { h, ws, opened, tab };
}
async function requestPageAction({ h, ws, opened }, kind, requestId) {
  const request = { kind, requestId, connectionId: "epoch-1",
    registrationId: opened.registrationId, registrationGeneration: opened.registrationGeneration };
  if (kind === "executeJs") {
    Object.assign(request, { requireAgentCreated: true,
      source: "document.title", entrypoint: "expression", args: {} });
  } else {
    Object.assign(request, { mode: "inline", captureSelector: "",
      timeoutMs: 500, wait: { kind: "immediate" } });
  }
  await h.deliver(ws, request);
  return ws.sent.find(m => m.requestId === requestId);
}

test("OpenTab opt-out during first host permission check returns definite rejection", async () => {
  const h = createHarness();
  await tick(); await tick();
  await h.message("registerWindow", { windowId: 1, alias: "agents" });
  await h.message("setAgentCreatedTabs", { allowed: true });
  await h.message("pair", { port: 43822, secret: "b".repeat(64) });
  await tick(); await tick();
  const ws = h.sockets[0];
  await h.deliver(ws, { kind: "challenge", nonce: "d".repeat(64) });
  await h.deliver(ws, { kind: "ready", connectionId: "epoch-1" });
  h.beforeNextPermissionCheck(async () => {
    assert.equal((await h.message("setAgentCreatedTabs", { allowed: false })).ok, true);
  });
  await h.deliver(ws, { kind: "openTab", requestId: "preflight-revoked",
    connectionId: "epoch-1", url: "https://example.test/chat", alias: "must-not-open",
    ...(await windowPin(h)) });
  assert.equal(ws.sent.find(m => m.requestId === "preflight-revoked")?.error,
    "AgentCreatedTabsDisabled");
  assert.equal(h.createdTabs.length, 0);
});

for (const kind of ["executeJs", "captureDom"]) {
  for (const reenable of [false, true]) {
    test(kind + " rejects in-flight opt-out" + (reenable ? " and re-enable" : "") +
      " during assessment before injection", async () => {
      const x = await createdPageForExecutionRace();
      x.h.beforeNextPermissionCheck(async () => {
        assert.equal((await x.h.message("setAgentCreatedTabs", { allowed: false })).ok, true);
        if (reenable)
          assert.equal((await x.h.message("setAgentCreatedTabs", { allowed: true })).ok, true);
      });
      const response = await requestPageAction(x, kind, "assess-revoked");
      assert.equal(response?.error, reenable
        ? "AgentCreatedTabsAuthorizationChanged" : "AgentCreatedTabsDisabled");
      assert.equal(x.h.calls.length, 0, "No page code may execute after explicit opt-out");
    });
  }

  test(kind + " rechecks host permission after last window assessment", async () => {
    const x = await createdPageForExecutionRace();
    let lookups = 0;
    x.h.onWindowQuery(async () => {
      if (++lookups !== 3) return;
      x.h.onWindowQuery(null);
      x.h.setHostPermissionGranted(false);
    });
    const response = await requestPageAction(x, kind, "host-revoked");
    assert.equal(lookups, 3, "Permission revocation must occur during final preflight");
    assert.equal(response?.error, "BrowserPermissionMissing");
    assert.equal(x.h.calls.length, 0);
  });

  test(kind + " rejects window generation changed during last permission await", async () => {
    const x = await createdPageForExecutionRace();
    let lookups = 0;
    x.h.onWindowQuery(async () => {
      if (++lookups !== 3) return;
      x.h.onWindowQuery(null);
      x.h.beforeNextPermissionCheck(async () => {
        assert.equal((await x.h.message("registerWindow", { windowId: 1, alias: "agents" })).ok, true);
      });
    });
    const response = await requestPageAction(x, kind, "window-reregistered");
    assert.equal(lookups, 3);
    assert.equal(response?.error, "WindowRegistrationChanged");
    assert.equal(x.h.calls.length, 0);
  });
}

test("agent-created-tab opt-out does not revoke manually registered page execution", async () => {
  const h = createHarness();
  await tick(); await tick();
  await h.message("register", { tabId: 17, alias: "manual" });
  await h.message("pair", { port: 43822, secret: "b".repeat(64) });
  await tick(); await tick();
  const ws = h.sockets[0];
  await h.deliver(ws, { kind: "challenge", nonce: "d".repeat(64) });
  await h.deliver(ws, { kind: "ready", connectionId: "epoch-1" });
  const reg = (await h.message("getState")).registrations.find(r => r.alias === "manual");
  h.beforeNextPermissionCheck(async () => {
    assert.equal((await h.message("setAgentCreatedTabs", { allowed: false })).ok, true);
  });
  await h.deliver(ws, { kind: "executeJs", requestId: "manual-still-allowed",
    connectionId: "epoch-1", registrationId: reg.registrationId,
    registrationGeneration: reg.registrationGeneration,
    source: "document.title", entrypoint: "expression", args: {} });
  const response = ws.sent.find(m => m.requestId === "manual-still-allowed");
  assert.equal(response?.kind, "result");
  assert.equal(response?.result, "Agent 7");
  assert.equal(h.calls.length, 1);
});


test("permanently revoked agent registration disappears without closing its tab", async () => {
  const { h, tab, registered } = await createdTabForCloseRegression();
  tab.url = "https://unrelated.test/";
  h.listeners.updated(tab.id, { url: tab.url });
  await tick(); await tick();
  const state = await h.message("getState");
  assert.equal(state.registrations.some(r => r.registrationId === registered.registrationId), false);
  assert.equal(state.registrations.some(r => r.alias === "manual"), true);
  assert.equal(h.storage.get("registrations").some(r => r.alias === registered.alias), false);
  assert.equal(h.tabs.has(tab.id), true);
  assert.equal(h.removedTabs.length, 0);
  tab.url = "https://example.test/chat/new";
  assert.equal((await h.message("getState")).registrations.some(r =>
    r.registrationId === registered.registrationId), false);
});

test("temporary tabs API failure keeps agent-created registration", async () => {
  const { h, tab, registered } = await createdTabForCloseRegression();
  await h.message("unregister", { tabId: 17 });
  h.failTabQueryOnce();
  const state = await h.message("getState");
  const reg = state.registrations.find(r => r.registrationId === registered.registrationId);
  assert.ok(reg);
  assert.equal(reg.availability, "needsReauthorization");
  assert.equal((await h.message("getState")).registrations.find(r =>
    r.registrationId === registered.registrationId).availability, "ready");
  assert.equal(h.tabs.has(tab.id), true);
});

test("bulk cleanup removes only unavailable agent entries without closing browser tabs", async () => {
  const { h, tab, registered } = await createdTabForCloseRegression();
  h.setHostPermissionGranted(false);
  const before = await h.message("getState");
  assert.equal(before.registrations.find(r => r.registrationId === registered.registrationId).availability,
    "needsReauthorization");
  const result = await h.message("forgetUnavailableAgentRegistrations");
  assert.equal(result.ok, true);
  assert.equal(result.removed, 1);
  const after = await h.message("getState");
  assert.deepEqual(Array.from(after.registrations, r => r.alias), ["manual"]);
  assert.equal(h.tabs.has(tab.id), true);
  assert.equal(h.tabs.has(17), true);
  assert.equal(h.removedTabs.length, 0);
});

test("per-row unregister checks generation and cannot be invoked by a webpage", async () => {
  const h = createHarness();
  await tick(); await tick();
  await h.message("register", { tabId: 17, alias: "manual" });
  const reg = (await h.message("getState")).registrations[0];
  const args = { registrationId: reg.registrationId,
    registrationGeneration: reg.registrationGeneration };
  assert.equal((await h.message("unregisterRegistration",
    { ...args, registrationGeneration: args.registrationGeneration + 1 })).ok, false);
  assert.equal((await h.message("unregisterRegistration", args, { id: "page" })).ok, false);
  assert.equal((await h.message("unregisterRegistration", args)).ok, true);
  assert.equal((await h.message("getState")).registrations.length, 0);
  assert.equal(h.tabs.has(17), true);
  assert.equal(h.removedTabs.length, 0);
});

test("per-row Unregister succeeds when the tab already closed and registration was removed", async () => {
  const h = createHarness();
  await tick(); await tick();
  await h.message("register", { tabId: 17, alias: "manual" });
  const reg = (await h.message("getState")).registrations[0];
  const args = { registrationId: reg.registrationId, registrationGeneration: reg.registrationGeneration };
  // Chrome has already closed the tab; onRemoved revoked the registration.
  h.tabs.delete(17);
  h.listeners.removed(17);
  await tick(); await tick();
  assert.equal((await h.message("getState")).registrations.length, 0);
  assert.equal((await h.message("unregisterRegistration", args)).ok, true);
  assert.equal((await h.message("unregisterRegistration", args)).ok, true);
  assert.equal((await h.message("getState")).registrations.length, 0);
  // Invalid requests remain rejected even if no registration exists.
  assert.equal((await h.message("unregisterRegistration",
    { ...args, registrationGeneration: null })).ok, false);
});


test("browser restart prunes obsolete Jarvis-created registrations but preserves manual tabs", async () => {
  const first = createHarness();
  await tick(); await tick();
  await first.message("register", { tabId: 17, alias: "manual" });
  await first.message("registerWindow", { windowId: 1, alias: "agents" });
  await first.message("setAgentCreatedTabs", { allowed: true });
  await first.message("pair", { port: 43822, secret: "b".repeat(64) });
  await tick(); await tick();
  const ws = first.sockets[0];
  await first.deliver(ws, { kind: "challenge", nonce: "1".repeat(64) });
  await first.deliver(ws, { kind: "ready", connectionId: "epoch-1" });
  await first.deliver(ws, {
    kind: "openTab", requestId: "old", connectionId: "epoch-1",
    url: "https://example.test/chat/new", alias: "old-agent",
    ...(await windowPin(first))
  });
  const created = ws.sent.find(m => m.requestId === "old" && m.kind === "tabOpened").tab;
  const stored = {
    registrations: JSON.parse(JSON.stringify(first.storage.get("registrations"))),
    registeredWindows: JSON.parse(JSON.stringify(first.storage.get("registeredWindows"))),
    allowAgentCreatedTabs: true
  };
  const second = createHarness(stored); // new chrome.storage.session epoch
  second.tabs.set(first.createdTabs[0].id, {
    id: first.createdTabs[0].id, windowId: 1,
    url: "https://example.test/chat/new", status: "complete"
  });
  await tick(); await tick();
  const state = await second.message("getState");
  assert.deepEqual(Array.from(state.registrations, r => r.alias), ["manual"]);
  assert.equal(second.storage.get("registrations").length, 1);
  assert.equal(second.tabs.has(first.createdTabs[0].id), true,
    "Garbage-collect only extension authority, never the underlying browser tab");
  assert.equal(state.registrations.some(r => r.registrationId === created.registrationId), false);
});

test("adoption during asynchronous stale assessment cannot erase user-owned registration", async () => {
  const { h, tab, registered } = await createdTabForCloseRegression();
  await h.message("unregister", { tabId: 17 });
  tab.url = "https://unrelated.test/";
  h.beforeNextTabQuery(async () => {
    const adopted = await h.message("register", { tabId: tab.id, alias: "adopted" });
    assert.equal(adopted.ok, true);
  });
  const state = await h.message("getState");
  const current = state.registrations.find(r => r.registrationId === registered.registrationId);
  assert.ok(current, "Concurrent adopted registration must remain visible");
  assert.equal(current.agentCreated, false);
  assert.equal(current.alias, "adopted");
  assert.ok(current.registrationGeneration > registered.registrationGeneration);
  assert.equal(h.tabs.has(tab.id), true);
});
