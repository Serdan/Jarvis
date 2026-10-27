// Jarvis Browser Bridge - Manifest V3 service worker.
// Only the extension popup may create, update, or remove registrations.
// The public MCP server has no browser access without this paired local bridge.
const PROTOCOL = 1;
const REGISTRY_KEY = "registrations";
const SESSION_KEY = "browserSessionEpoch";
const encoder = new TextEncoder();

let socket = null;
let connectionId = null;
let connected = false;
let heartbeat = null;
let retry = null;
let inflight = 0;
let registrations = [];
let sessionEpoch = null;
let configuredPort = 43822;
let secret = "";
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
    const persistent = await chrome.storage.local.get(["port", "secret", REGISTRY_KEY]);
    configuredPort = persistent.port || 43822;
    secret = persistent.secret || "";
    registrations = Array.isArray(persistent[REGISTRY_KEY]) ? persistent[REGISTRY_KEY] : [];
    const session = await chrome.storage.session.get(SESSION_KEY);
    sessionEpoch = session[SESSION_KEY] || randomId();
    if (!session[SESSION_KEY]) await chrome.storage.session.set({ [SESSION_KEY]: sessionEpoch });
    await refreshRegistrations();
  })();
  return initPromise;
}
function safeState(r) {
  return {
    registrationId: r.registrationId,
    alias: r.alias,
    displayUrl: (() => {
      try { const url = new URL(r.boundUrl); return url.origin + url.pathname; }
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
async function assess(reg) {
  if (reg.blocked || reg.sessionEpoch !== sessionEpoch) return "needsReauthorization";
  try {
    const tab = await chrome.tabs.get(reg.tabId);
    if (tab.url !== reg.boundUrl) {
      reg.blocked = true;
      await persist();
      return "needsReauthorization";
    }
    if (!isAllowedUrl(tab.url)) return "needsReauthorization";
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
        await advertise();
      } else if (message.kind === "executeJs" && connected) {
        void execute(message);
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
    "if (location.href !== " + JSON.stringify(registration.boundUrl) +
    ") throw new Error('NavigationMismatch');\n" +
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
        registrations: registrations.map(r => ({ ...safeState(r), tabId: r.tabId })) };
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
    if (r.tabId === tabId && (removed || (url && r.boundUrl !== url))) {
      r.blocked = true;
      changed = true;
    }
  }
  if (changed) await persist();
  if (registrations.some(r => r.tabId === tabId)) await advertise();
}
chrome.tabs.onUpdated.addListener((tabId, changeInfo) => {
  void observeNavigation(tabId, changeInfo.url);
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
