// Jarvis Browser Bridge - Manifest V3 service worker.
// Only the extension popup may create, update, or remove registrations.
// The public MCP server has no browser access without this paired local bridge.
const PROTOCOL = 2;
const REGISTRY_KEY = "registrations";
const WINDOW_KEY = "registeredWindows";
const SESSION_KEY = "browserSessionEpoch";
const OPEN_TABS_KEY = "allowAgentCreatedTabs";
const MAX_AGENT_TABS = 20;
const MAX_SNAPSHOT_BYTES = 8 * 1024 * 1024;
const MAX_INLINE_SNAPSHOT_BYTES = 48 * 1024;
const SNAPSHOT_CHUNK_BYTES = 32 * 1024;
const snapshots = new Map();
const encoder = new TextEncoder();

let socket = null;
let connectionId = null;
let connected = false;
let heartbeat = null;
let retry = null;
let inflight = 0;
let registrations = [];
let registeredWindows = [];
let sessionEpoch = null;
let configuredPort = 43822;
let secret = "";
let allowAgentCreatedTabs = false;
let openQueue = Promise.resolve();
let initPromise = null;

function randomId() {
  return Array.from(crypto.getRandomValues(new Uint8Array(16)), x => x.toString(16).padStart(2, "0")).join("");
}
function isAllowedUrl(value) {
  try {
    const url = new URL(value);
    return (url.protocol === "http:" || url.protocol === "https:") &&
      Boolean(url.hostname);
  } catch { return false; }
}
function bytes(value) { return encoder.encode(value).byteLength; }
function send(payload) {
  if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ version: PROTOCOL, ...payload }));
}
async function hmacHex(key, message) {
  const imported = await crypto.subtle.importKey(
    "raw", encoder.encode(key), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const signature = new Uint8Array(await crypto.subtle.sign("HMAC", imported, encoder.encode(message)));
  return Array.from(signature, x => x.toString(16).padStart(2, "0")).join("");
}
async function initialize() {
  if (initPromise) return initPromise;
  initPromise = (async () => {
    // Keep pairing credentials and browser registrations inaccessible to page content scripts.
    await chrome.storage.local.setAccessLevel?.({ accessLevel: "TRUSTED_CONTEXTS" });
    await chrome.storage.session.setAccessLevel?.({ accessLevel: "TRUSTED_CONTEXTS" });
    const persistent = await chrome.storage.local.get(["port", "secret", REGISTRY_KEY, WINDOW_KEY, OPEN_TABS_KEY]);
    configuredPort = persistent.port || 43822;
    secret = persistent.secret || "";
    allowAgentCreatedTabs = persistent[OPEN_TABS_KEY] === true;
    registrations = Array.isArray(persistent[REGISTRY_KEY]) ? persistent[REGISTRY_KEY] : [];
    registeredWindows = Array.isArray(persistent[WINDOW_KEY]) ? persistent[WINDOW_KEY] : [];
    const session = await chrome.storage.session.get(SESSION_KEY);
    sessionEpoch = session[SESSION_KEY] || randomId();
    if (!session[SESSION_KEY]) await chrome.storage.session.set({ [SESSION_KEY]: sessionEpoch });
    await refreshWindows();
    await refreshRegistrations();
  })();
  return initPromise;
}
function safeState(r) {
  return {
    registrationId: r.registrationId,
    alias: r.alias,
    agentCreated: r.agentCreated === true,
    windowRegistration: r.agentCreated ? r.windowRegistration : undefined,
    displayUrl: (() => {
      try { const url = new URL(r.currentUrl || r.boundUrl); return url.origin + url.pathname; }
      catch { return "(unavailable)"; }
    })(),
    origin: (() => { try { return new URL(r.boundUrl).origin; } catch { return ""; } })(),
    registrationGeneration: r.registrationGeneration,
    availability: r.availability
  };
}
async function persist() {
  await chrome.storage.local.set({ [REGISTRY_KEY]: registrations });
}
function windowState(w) {
  return { windowRegistration: w.registrationId, alias: w.alias,
    availability: w.availability, registrationGeneration: w.generation };
}
async function assessWindow(w) {
  if (w.blocked || w.sessionEpoch !== sessionEpoch) return "needsReauthorization";
  try {
    const window = await chrome.windows.get(w.windowId);
    return window?.incognito ? "needsReauthorization" : "ready";
  } catch { return "missing"; }
}
async function refreshWindows() {
  for (const w of registeredWindows) w.availability = await assessWindow(w);
}
async function validWindow(id) {
  const matches = registeredWindows.filter(w => w.registrationId === id || w.alias === id);
  if (matches.length !== 1) return null;
  const w = matches[0];
  if (await assessWindow(w) !== "ready") return null;
  return w;
}
async function persistWindows() {
  await chrome.storage.local.set({ [WINDOW_KEY]: registeredWindows });
}
async function advertiseWindows() {
  await refreshWindows();
  if (connected) send({ kind: "windows", windows: registeredWindows.map(windowState) });
}

async function assess(reg) {
  if (reg.blocked || reg.sessionEpoch !== sessionEpoch) return "needsReauthorization";
  if (reg.agentCreated &&
      (!allowAgentCreatedTabs || !reg.windowRegistration ||
       !(await validWindow(reg.windowRegistration)))) {
    reg.blocked = true;
    await persist();
    return "needsReauthorization";
  }
  try {
    const tab = await chrome.tabs.get(reg.tabId);
    const pendingInitialNavigation = reg.agentCreated && tab.status === "loading" &&
      tab.pendingUrl === reg.boundUrl &&
      (!isAllowedUrl(tab.url) || tab.url === "about:blank");
    if (pendingInitialNavigation) return "loading";

    if (!isAllowedUrl(tab.url) ||
        (reg.agentCreated
          ? new URL(tab.url).origin !== reg.boundOrigin
          : tab.url !== reg.boundUrl)) {
      reg.blocked = true;
      await persist();
      return "needsReauthorization";
    }
    if (reg.agentCreated && tab.windowId !== (await validWindow(reg.windowRegistration))?.windowId) {
      reg.blocked = true;
      await persist();
      return "needsReauthorization";
    }
    if (reg.agentCreated) reg.currentUrl = tab.url;
    if (tab.discarded) return "discarded";
    if (tab.frozen) return "frozen";
    if (tab.status === "loading") return "loading";
    const origin = new URL(tab.url).origin + "/*";
    if (!(await chrome.permissions.contains({ origins: [origin] }))) return "needsReauthorization";
    return "ready";
  } catch { return "missing"; }
}
async function refreshRegistrations() {
  for (const reg of registrations) reg.availability = await assess(reg);
}
async function advertise() {
  await refreshRegistrations();
  // Registry includes no unregistered tab metadata.
  if (connected) send({ kind: "registrations", tabs: registrations.map(safeState) });
}
function clearConnection() {
  snapshots.clear();
  connected = false;
  connectionId = null;
  if (heartbeat) clearInterval(heartbeat);
  heartbeat = null;
  if (retry) clearTimeout(retry);
  retry = null;
}
function reconnectSoon() {
  if (retry || !secret) return;
  retry = setTimeout(() => { retry = null; connect(); }, 5000);
}
async function connect() {
  await initialize();
  if (!secret || (socket && [WebSocket.OPEN, WebSocket.CONNECTING].includes(socket.readyState))) return;
  clearConnection();
  const ws = new WebSocket("ws://127.0.0.1:" + configuredPort + "/");
  socket = ws;
  ws.onmessage = async event => {
    try {
      const message = JSON.parse(event.data);
      if (message.version !== PROTOCOL || socket !== ws) return;
      if (message.kind === "challenge") {
        if (typeof message.nonce !== "string" || message.nonce.length !== 64) return;
        send({ kind: "authenticate", proof: await hmacHex(secret, message.nonce) });
      } else if (message.kind === "ready") {
        connectionId = message.connectionId;
        connected = true;
        if (heartbeat) clearInterval(heartbeat);
        heartbeat = setInterval(() => send({ kind: "ping" }), 20000);
        send({kind:"capabilities", features:["domSnapshotV1"]});
        await advertise();
        await advertiseWindows();
      } else if (message.kind === "executeJs" && connected) {
        void execute(message);
      } else if (message.kind === "captureDom" && connected) {
        void captureDom(message);
      } else if (message.kind === "snapshotChunk" && connected) {
        void readSnapshotChunk(message);
      } else if (message.kind === "releaseSnapshot" && connected) {
        if (message.connectionId === connectionId) snapshots.delete(message.snapshotId);
      } else if (message.kind === "openTab" && connected) {
        // Serialize creations to enforce alias uniqueness and the tab quota.
        openQueue = openQueue.then(() => openNewTab(message)).catch(() => {});
      } else if (message.kind === "closeTab" && connected) {
        openQueue = openQueue.then(() => closeCreatedTab(message)).catch(() => {});
      } else if (message.kind === "ping") {
        send({ kind: "pong" });
      }
    } catch {
      // Do not log source text, arguments, pairing material or page data.
    }
  };
  ws.onclose = () => {
    if (socket !== ws) return;
    socket = null;
    clearConnection();
    reconnectSoon();
  };
  ws.onerror = () => { try { ws.close(); } catch {} };
}
async function execute(message) {
  const { requestId, registrationId, registrationGeneration, source, args } = message;
  const requestSocket = socket;
  const requestConnection = connectionId;
  // Never deliver an old page operation's result through a newly paired connection.
  function respond(payload) {
    if (socket === requestSocket && connected && connectionId === requestConnection) send(payload);
  }
  function error(code) {
    respond({ kind: "error", requestId, connectionId: requestConnection, registrationId,
      registrationGeneration, status: "executionError", error: code });
  }
  if (message.connectionId !== connectionId) return error("ConnectionEpochMismatch");
  if (typeof source !== "string" || bytes(source) > 65536 ||
      bytes(JSON.stringify(args)) > 65536) return error("InputTooLarge");
  if (inflight >= 4) return error("BridgeBusy");
  const registration = registrations.find(r => r.registrationId === registrationId);
  if (!registration || registration.registrationGeneration !== registrationGeneration) {
    return error("RegistrationStale");
  }
  const state = await assess(registration);
  if (state !== "ready") return error("TabUnavailable:" + state);
  let api;
  try { api = chrome.userScripts; } catch { return error("AllowUserScriptsDisabled"); }
  if (typeof api?.execute !== "function") return error("AllowUserScriptsDisabled");
  const invocationArguments = JSON.stringify(args ?? {});
  // Trusted project code lives in the isolated USER_SCRIPT world, not extension globals.
  // The URL check also runs inside the frame to prevent navigation races.
  const wrapper =
    "(async () => {\n" +
    (registration.agentCreated ?
      "if (location.origin !== " + JSON.stringify(registration.boundOrigin) +
        ") throw new Error('NavigationMismatch');\n" :
      "if (location.href !== " + JSON.stringify(registration.boundUrl) +
        ") throw new Error('NavigationMismatch');\n") +
    (message.entrypoint === "expression" ?
      "return await (" + source + ");\n" :
      source + "\n" +
      "if (typeof execute !== 'function') throw new Error('MissingExecuteEntrypoint');\n" +
      "return await execute(" + invocationArguments + ");\n") +
    "})()";
  if (bytes(wrapper) > 140000) return error("InputTooLarge");
  inflight++;
  try {
    const results = await api.execute({
      target: { tabId: registration.tabId },
      js: [{ code: wrapper }],
      world: "USER_SCRIPT"
    });
    const first = results?.[0];
    if (first?.error) return error("ScriptError:" + first.error.slice(0, 300));
    // Recheck URL and registration after execution. Side effects can have occurred.
    if (registration !== registrations.find(r => r.registrationId === registrationId) ||
        registration.registrationGeneration !== registrationGeneration ||
        await assess(registration) !== "ready") {
      return error("OutcomeUnknown:RegistrationChangedDuringExecution");
    }
    const value = first?.result === undefined ? null : first.result;
    const serialized = JSON.stringify(value);
    if (serialized === undefined) return error("ResultNotJson");
    if (bytes(serialized) > 220000) return error("OutputTooLarge");
    respond({ kind: "result", requestId, connectionId: requestConnection, registrationId,
      registrationGeneration, status: "completed", result: value });
  } catch (e) {
    error("ScriptError:" + String(e.message || e).slice(0, 300));
  } finally { inflight--; }
}

// Runs inside the registered page's isolated USER_SCRIPT world. Capture is a single
// point-in-time DOM serialization after a bounded, explicit readiness condition.
async function capturePage(options) {
  const initialUrl = location.href;
  const deadline = Date.now() + options.timeoutMs;
  const ensureSameDocument = () => {
    if (location.href !== initialUrl) throw new Error("NavigationDuringCapture");
  };
  const remaining = () => Math.max(0, deadline - Date.now());
  const fail = reason => { throw new Error(reason); };
  const checkSelector = (selector, state, text) => {
    const element = document.querySelector(selector);
    if (!element) return null;
    if (state === "textContains" && !element.textContent?.includes(text)) return null;
    if (state === "visible") {
      const style = getComputedStyle(element);
      const bounds = element.getBoundingClientRect();
      if (style.display === "none" || style.visibility === "hidden" ||
          bounds.width === 0 || bounds.height === 0) return null;
    }
    return element;
  };
  const waitForSelector = (selector, state, text) => new Promise((resolve, reject) => {
    let done = false;
    let observer;
    let interval;
    let timeout;
    function finish(error, value) {
      if (done) return;
      done = true;
      observer?.disconnect();
      clearTimeout(timeout);
      clearInterval(interval);
      if (error) reject(error);
      else resolve(value);
    }
    function check() {
      try {
        ensureSameDocument();
        const element = checkSelector(selector, state, text);
        if (element) finish(null, element);
      } catch (error) {
        finish(new Error(error.name === "SyntaxError" ? "InvalidSelector" : error.message));
      }
    }
    try {
      observer = new MutationObserver(check);
      observer.observe(document.documentElement || document,
        { subtree: true, childList: true, characterData: true, attributes: true });
      // Polling supplements mutation notifications for visibility changes driven by CSS.
      interval = setInterval(check, 150);
      timeout = setTimeout(() => finish(new Error("SnapshotTimeout")), remaining());
      check(); // Recheck after subscribing, eliminating the observer-install race.
    } catch {
      finish(new Error("SnapshotDomError"));
    }
  });
  const waitForEvent = (target, name) => new Promise((resolve, reject) => {
    let done = false;
    let timeout;
    let navigationCheck;
    const listener = () => finish(null);
    function finish(error) {
      if (done) return;
      done = true;
      clearTimeout(timeout);
      clearInterval(navigationCheck);
      target.removeEventListener(name, listener);
      if (error) reject(error);
      else resolve();
    }
    if (!remaining()) return reject(new Error("SnapshotTimeout"));
    target.addEventListener(name, listener, { once: true });
    navigationCheck = setInterval(() => {
      try { ensureSameDocument(); }
      catch { finish(new Error("NavigationDuringCapture")); }
    }, 100);
    timeout = setTimeout(() => finish(new Error("SnapshotTimeout")), remaining());
  });
  try {
    const wait = options.wait;
    if (wait.kind === "delay") {
      if (wait.milliseconds > remaining()) fail("SnapshotTimeout");
      await new Promise(resolve => setTimeout(resolve, wait.milliseconds));
    } else if (wait.kind === "selector") {
      await waitForSelector(wait.selector, wait.state, wait.text);
    } else if (wait.kind === "event") {
      const target = wait.target === "window" ? window :
        wait.target === "document" ? document :
        await waitForSelector(wait.selector, "exists", "");
      await waitForEvent(target, wait.name);
    }
    ensureSameDocument();
    if (remaining() === 0) fail("SnapshotTimeout");
    const selected = options.captureSelector
      ? document.querySelector(options.captureSelector)
      : document.documentElement;
    if (!selected) fail("SnapshotTargetNotFound");
    const html = selected.outerHTML;
    if (typeof html !== "string") fail("SnapshotDomError");
    if (html.length > 8 * 1024 * 1024) fail("SnapshotTooLarge");
    return {
      html,
      title: document.title,
      url: location.origin + location.pathname,
      capturedAtUtc: new Date().toISOString(),
      waitKind: wait.kind
    };
  } catch (e) {
    const recognized = new Set(["SnapshotTimeout", "NavigationDuringCapture",
      "InvalidSelector", "SnapshotTargetNotFound", "SnapshotDomError", "SnapshotTooLarge"]);
    return { failure: e?.name === "SyntaxError" ? "InvalidSelector" :
      recognized.has(e?.message) ? e.message : "SnapshotDomError" };
  }
}

function validSnapshotOptions(message) {
  if (!Number.isInteger(message.timeoutMs) || message.timeoutMs < 100 ||
      message.timeoutMs > 60000) return false;
  if (message.mode !== "inline" && message.mode !== "file") return false;
  if (message.captureSelector !== "" &&
      (typeof message.captureSelector !== "string" || message.captureSelector.length > 512)) return false;
  const wait = message.wait;
  if (!wait || typeof wait !== "object" || Array.isArray(wait)) return false;
  if (wait.kind === "immediate") return true;
  if (wait.kind === "delay")
    return Number.isInteger(wait.milliseconds) && wait.milliseconds >= 0 &&
      wait.milliseconds < message.timeoutMs;
  if (wait.kind === "selector")
    return typeof wait.selector === "string" && wait.selector.length > 0 &&
      wait.selector.length <= 512 && ["exists", "visible", "textContains"].includes(wait.state) &&
      (wait.state !== "textContains" ||
        (typeof wait.text === "string" && wait.text.length > 0 && wait.text.length <= 256));
  if (wait.kind === "event")
    return typeof wait.name === "string" && /^[A-Za-z][A-Za-z0-9_.:-]{0,127}$/.test(wait.name) &&
      ["document", "window", "element"].includes(wait.target) &&
      (wait.target !== "element" ||
        (typeof wait.selector === "string" && wait.selector.length > 0 && wait.selector.length <= 512));
  return false;
}
function purgeSnapshots() {
  for (const [id, item] of snapshots)
    if (Date.now() - item.createdAt > 120000) snapshots.delete(id);
}
async function captureDom(message) {
  const { requestId, registrationId, registrationGeneration } = message;
  const requestSocket = socket;
  const requestConnection = connectionId;
  const respond = payload => {
    if (socket === requestSocket && connected && connectionId === requestConnection)
      send({ kind: "snapshotReady", requestId, connectionId: requestConnection,
        registrationId, registrationGeneration, ...payload });
  };
  const reject = code => respond({ kind: "error", error: code });
  if (message.connectionId !== requestConnection) return reject("ConnectionEpochMismatch");
  if (!validSnapshotOptions(message)) return reject("InvalidSnapshotOptions");
  if (inflight >= 4) return reject("BridgeBusy");
  const reg = registrations.find(r => r.registrationId === registrationId);
  if (!reg || reg.registrationGeneration !== registrationGeneration)
    return reject("RegistrationStale");
  if (await assess(reg) !== "ready") return reject("TabUnavailable");
  const api = chrome.userScripts;
  if (typeof api?.execute !== "function") return reject("AllowUserScriptsDisabled");
  const options = { timeoutMs: message.timeoutMs, wait: message.wait,
    captureSelector: message.captureSelector };
  const identityCheck = reg.agentCreated
    ? "location.origin === " + JSON.stringify(reg.boundOrigin)
    : "location.href === " + JSON.stringify(reg.boundUrl);
  const source = "(async () => { if (!(" + identityCheck +
    ")) return {failure:'NavigationDuringCapture'}; return (" +
    capturePage.toString() + ")(" + JSON.stringify(options) + "); })()";
  inflight++;
  try {
    const results = await api.execute({ target: { tabId: reg.tabId },
      js: [{ code: source }], world: "USER_SCRIPT" });
    const first = results?.[0];
    if (first?.error) return reject("SnapshotScriptError");
    if (reg !== registrations.find(r => r.registrationId === registrationId) ||
        reg.registrationGeneration !== registrationGeneration ||
        await assess(reg) !== "ready") return reject("SnapshotRegistrationChanged");
    const result = first?.result;
    if (!result || typeof result !== "object") return reject("SnapshotResultInvalid");
    if (typeof result.failure === "string") return reject(result.failure);
    if (typeof result.html !== "string" ||
        typeof result.url !== "string" || typeof result.title !== "string")
      return reject("SnapshotResultInvalid");
    const data = encoder.encode(result.html);
    if (data.byteLength > MAX_SNAPSHOT_BYTES) return reject("SnapshotTooLarge");
    const metadata = { status: "completed", bytes: data.byteLength,
      title: result.title.slice(0, 512), url: result.url.slice(0, 4096),
      capturedAtUtc: result.capturedAtUtc, waitKind: result.waitKind };
    if (message.mode === "inline") {
      const response = { ...metadata, html: result.html };
      if (data.byteLength > MAX_INLINE_SNAPSHOT_BYTES ||
          bytes(JSON.stringify(response)) > 180000)
        return reject("SnapshotTooLargeForInline: request file output");
      return respond(response);
    }
    purgeSnapshots();
    if (snapshots.size >= 2) return reject("SnapshotCapacityReached");
    const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", data));
    const sha256 = Array.from(digest, x => x.toString(16).padStart(2, "0")).join("");
    const snapshotId = randomId();
    snapshots.set(snapshotId, { data, registrationId, registrationGeneration,
      connectionId: requestConnection, createdAt: Date.now() });
    respond({ ...metadata, snapshotId, sha256 });
  } catch {
    reject("SnapshotScriptError");
  } finally {
    inflight--;
  }
}

async function readSnapshotChunk(message) {
  const { requestId, snapshotId } = message;
  const requestSocket = socket;
  const requestConnection = connectionId;
  const respond = payload => {
    if (socket === requestSocket && connected && connectionId === requestConnection)
      send({ requestId, connectionId: requestConnection, ...payload });
  };
  const reject = error => respond({ kind: "error", error });
  if (message.connectionId !== requestConnection) return reject("ConnectionEpochMismatch");
  purgeSnapshots();
  const entry = snapshots.get(snapshotId);
  if (!entry || entry.connectionId !== requestConnection) return reject("SnapshotExpired");
  const reg = registrations.find(r => r.registrationId === entry.registrationId);
  if (!reg || reg.registrationGeneration !== entry.registrationGeneration ||
      await assess(reg) !== "ready") {
    snapshots.delete(snapshotId);
    return reject("SnapshotRegistrationChanged");
  }
  if (!Number.isInteger(message.offset) || message.offset < 0 ||
      message.offset >= entry.data.byteLength || message.offset > MAX_SNAPSHOT_BYTES)
    return reject("InvalidSnapshotOffset");
  const part = entry.data.subarray(message.offset,
    Math.min(entry.data.byteLength, message.offset + SNAPSHOT_CHUNK_BYTES));
  // Keep an active transfer alive while retaining a bounded idle expiration.
  entry.createdAt = Date.now();
  let binary = "";
  for (const value of part) binary += String.fromCharCode(value);
  respond({ kind: "snapshotChunk", snapshotId, registrationId: entry.registrationId,
    registrationGeneration: entry.registrationGeneration,
    offset: message.offset, data: btoa(binary) });
}

async function openNewTab(message) {
  const { requestId } = message;
  const requestSocket = socket;
  const requestConnection = connectionId;
  const respond = payload => {
    if (socket === requestSocket && connected && connectionId === requestConnection) {
      send({ requestId, connectionId: requestConnection, ...payload });
    }
  };
  function reject(error) { respond({ kind: "error", error }); }

  if (message.connectionId !== requestConnection) return reject("ConnectionEpochMismatch");
  if (!allowAgentCreatedTabs) return reject("AgentCreatedTabsDisabled");
  if (typeof message.url !== "string" || message.url.length > 2048 || !isAllowedUrl(message.url)) {
    return reject("InvalidTabUrl");
  }
  const target = new URL(message.url);
  if (target.username || target.password) return reject("InvalidTabUrl");
  const alias = message.alias || ("agent-" + randomId().slice(0, 12));
  if (typeof alias !== "string" || alias.length > 128 || /[\u0000-\u001f]/.test(alias)) {
    return reject("InvalidTabAlias");
  }
  if (registrations.some(r => r.alias === alias)) return reject("AliasAlreadyRegistered");
  if (registrations.filter(r => r.agentCreated && !r.blocked && r.sessionEpoch === sessionEpoch).length >= MAX_AGENT_TABS) {
    return reject("AgentTabLimitReached");
  }
  const window = await validWindow(message.windowRegistration);
  if (!window) return reject("WindowNotRegistered");
  let tabCreated = false;
  try {
    const permission = await chrome.permissions.contains({ origins: [target.origin + "/*"] });
    if (!permission) return reject("BrowserPermissionMissing");
    if (socket !== requestSocket || !connected || connectionId !== requestConnection ||
        !allowAgentCreatedTabs || !(await validWindow(window.registrationId))) return;

    // Creating the tab is the irreversible operation; a transport failure afterwards
    // is reported as indeterminate and must not be blindly retried by Jarvis.
    const tab = await chrome.tabs.create({ windowId: window.windowId, url: target.href, active: message.active === true });
    tabCreated = true;
    if (!Number.isInteger(tab?.id) || tab.incognito || tab.windowId !== window.windowId) {
      if (Number.isInteger(tab?.id)) await chrome.tabs.remove(tab.id);
      return reject("InvalidCreatedTab");
    }
    const registration = {
      registrationId: randomId(), alias,
      agentCreated: true, tabId: tab.id, windowRegistration: window.registrationId,
      boundUrl: target.href, boundOrigin: target.origin,
      currentUrl: target.href, sessionEpoch, registrationGeneration: 1,
      blocked: false, availability: "loading"
    };
    registrations.push(registration);
    await persist();
    await advertise();
    respond({ kind: "tabOpened", tab: safeState(registration) });
  } catch (e) {
    const reason = String(e?.message || e).slice(0, 160);
    // Once tabs.create succeeded, a storage or connection failure cannot prove that
    // the operation did not happen. Require reconciliation before retrying.
    reject((tabCreated ? "OutcomeUnknown:" : "TabCreateFailed:") + reason);
  }
}

async function closeCreatedTab(message) {
  const { requestId, registrationId, registrationGeneration } = message;
  const requestSocket = socket;
  const requestConnection = connectionId;
  const respond = payload => {
    if (socket === requestSocket && connected && connectionId === requestConnection) {
      send({ requestId, connectionId: requestConnection,
        registrationId, registrationGeneration, ...payload });
    }
  };
  const reject = error => respond({ kind: "error", error });
  if (message.connectionId !== requestConnection) return reject("ConnectionEpochMismatch");
  const reg = registrations.find(r => r.registrationId === registrationId);
  if (!reg || !reg.agentCreated || reg.registrationGeneration !== registrationGeneration)
    return reject("TabNotAgentCreated");
  const availability = await assess(reg);
  if (availability !== "ready" && availability !== "loading")
    return reject("RegistrationStale");
  const parent = await validWindow(reg.windowRegistration);
  if (!parent) return reject("WindowNotRegistered");
  let tab;
  try { tab = await chrome.tabs.get(reg.tabId); } catch { return reject("TabMissing"); }
  if (tab.windowId !== parent.windowId) return reject("WindowMismatch");
  try {
    await chrome.tabs.remove(reg.tabId);
    registrations = registrations.filter(r => r !== reg);
    await persist();
    await advertise();
    respond({ kind: "tabClosed" });
  } catch { reject("OutcomeUnknown:TabCloseFailed"); }
}

async function registerWindow(windowId, alias) {
  if (!Number.isInteger(windowId) || typeof alias !== "string" ||
      alias.length > 128 || /[\u0000-\u001f]/.test(alias))
    throw new Error("Invalid window registration");
  const window = await chrome.windows.get(windowId);
  if (!window || window.incognito) throw new Error("Cannot register this window");
  if (alias && registeredWindows.some(w => w.alias === alias && w.windowId !== windowId))
    throw new Error("Window alias already registered");
  const old = registeredWindows.find(w => w.windowId === windowId);
  if (old) {
    old.alias = alias; old.generation++; old.blocked = false;
    old.sessionEpoch = sessionEpoch;
  } else {
    registeredWindows.push({ registrationId: randomId(), alias, windowId,
      sessionEpoch, generation: 1, blocked: false, availability: "ready" });
  }
  await persistWindows();
  await advertiseWindows();
}
async function unregisterWindow(windowId) {
  registeredWindows = registeredWindows.filter(w => w.windowId !== windowId);
  for (const r of registrations) if (r.agentCreated &&
    !registeredWindows.some(w => w.registrationId === r.windowRegistration)) r.blocked = true;
  await persistWindows(); await persist(); await advertiseWindows(); await advertise();
}

async function register(tabId, alias) {
  if (!Number.isInteger(tabId)) throw new Error("No active tab selected");
  if (typeof alias !== "string" || alias.length > 128 || /[\u0000-\u001f]/.test(alias)) {
    throw new Error("Invalid tab alias");
  }
  const tab = await chrome.tabs.get(tabId);
  if (!isAllowedUrl(tab.url)) throw new Error("Cannot register a restricted tab");
  const origin = new URL(tab.url).origin + "/*";
  if (!(await chrome.permissions.contains({ origins: [origin] }))) {
    throw new Error("Grant site permission using the extension popup first");
  }
  if (alias && registrations.some(r => r.alias === alias && r.tabId !== tabId)) {
    throw new Error("Alias already registered");
  }
  const old = registrations.find(r => r.tabId === tabId);
  if (old) {
    old.alias = alias;
    old.boundUrl = tab.url;
    old.registrationGeneration++;
    old.sessionEpoch = sessionEpoch;
    old.agentCreated = false;
    delete old.boundOrigin;
    delete old.currentUrl;
    old.blocked = false;
  } else {
    registrations.push({
      registrationId: randomId(), alias, boundUrl: tab.url, tabId,
      sessionEpoch, registrationGeneration: 1, blocked: false, availability: "ready"
    });
  }
  await persist();
  await advertise();
}
async function unregister(tabId) {
  registrations = registrations.filter(r => r.tabId !== tabId);
  await persist();
  await advertise();
}
chrome.runtime.onMessage.addListener((message, sender, respond) => {
  // Message origin must be our own extension UI, never a webpage/content script.
  if (sender.id !== chrome.runtime.id || sender.tab) {
    respond({ ok: false, error: "Extension UI required" });
    return;
  }
  (async () => {
    await initialize();
    if (message.kind === "getState") {
      await refreshRegistrations();
      return { connected, paired: Boolean(secret), port: configuredPort,
        allowAgentCreatedTabs,
        registrations: registrations.map(r => ({ ...safeState(r), tabId: r.tabId })),
        windows: registeredWindows.map(w => ({ ...windowState(w), windowId: w.windowId })) };
    }
    if (message.kind === "pair") {
      if (!Number.isInteger(message.port) || message.port < 1024 || message.port > 65535 ||
          !/^[a-f0-9]{64}$/.test(message.secret || "")) throw new Error("Invalid port or pairing key");
      if (socket) { socket.close(); socket = null; }
      configuredPort = message.port;
      secret = message.secret;
      await chrome.storage.local.set({ port: configuredPort, secret });
      void connect();
      return {};
    }
    if (message.kind === "unpair") {
      secret = "";
      await chrome.storage.local.remove("secret");
      if (socket) socket.close();
      socket = null;
      clearConnection();
      return {};
    }
    if (message.kind === "setAgentCreatedTabs") {
      if (typeof message.allowed !== "boolean") throw new Error("Invalid setting");
      allowAgentCreatedTabs = message.allowed;
      await chrome.storage.local.set({ [OPEN_TABS_KEY]: allowAgentCreatedTabs });
      if (!allowAgentCreatedTabs) {
        for (const r of registrations) if (r.agentCreated) r.blocked = true;
        await persist();
      }
      await advertise();
      return {};
    }
    if (message.kind === "registerWindow") {
      await registerWindow(message.windowId, message.alias || "");
      return {};
    }
    if (message.kind === "unregisterWindow") {
      await unregisterWindow(message.windowId);
      return {};
    }
    if (message.kind === "register") {
      await register(message.tabId, message.alias || "");
      return {};
    }
    if (message.kind === "unregister") {
      await unregister(message.tabId);
      return {};
    }
    throw new Error("Unknown extension operation");
  })().then(data => respond({ ok: true, ...data }),
            e => respond({ ok: false, error: String(e.message || e) }));
  return true;
});
async function observeNavigation(tabId, url, removed = false) {
  let changed = false;
  for (const r of registrations) {
    if (r.tabId === tabId &&
        (removed || (url && (r.agentCreated ?
          !isAllowedUrl(url) || new URL(url).origin !== r.boundOrigin : r.boundUrl !== url)))) {
      r.blocked = true;
      changed = true;
    }
  }
  if (changed) await persist();
  if (registrations.some(r => r.tabId === tabId)) await advertise();
}
chrome.tabs.onUpdated.addListener((tabId, changeInfo) => {
  void observeNavigation(tabId, changeInfo.url);
  if (changeInfo.windowId !== undefined) void advertise();
});
chrome.windows.onRemoved.addListener(windowId => {
  for (const w of registeredWindows) if (w.windowId === windowId) w.blocked = true;
  void persistWindows(); void advertiseWindows(); void advertise();
});
chrome.tabs.onRemoved.addListener((tabId) => {
  void observeNavigation(tabId, null, true);
});
chrome.webNavigation?.onHistoryStateUpdated?.addListener((details) => {
  if (details.frameId === 0) void observeNavigation(details.tabId, details.url);
});
chrome.alarms.create("jarvis-reconnect", { periodInMinutes: 0.5 });
chrome.alarms.onAlarm.addListener(alarm => {
  if (alarm.name === "jarvis-reconnect" && !connected) void connect();
});
void initialize().then(connect);
