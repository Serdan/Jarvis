// Disposable Chrome-for-Testing MV3 integration smoke test; no npm dependencies.
// Uses a fresh browser profile and a loopback fixture only. Synthetic opt-ins
// are sent from the extension background (not via a human clicking its popup).
// This is technical integration evidence, not human-operated consent acceptance.
// Usage: node browser-extension/tests/live-chromium-smoke.cjs /path/to/chrome
"use strict";
const assert = require("node:assert/strict");
const crypto = require("node:crypto");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const http = require("node:http");
const { spawn } = require("node:child_process");
const { once } = require("node:events");
const { setTimeout: sleep } = require("node:timers/promises");
const chromePath = process.argv[2];
if (!chromePath || !fs.existsSync(chromePath)) {
  console.error("Usage: node live-chromium-smoke.cjs /path/to/chrome-for-testing");
  process.exit(2);
}
const extensionPath = path.resolve(__dirname, "..");
const fixtureMarkup = '<!doctype html><html lang="en"><head><title>Jarvis E2E fixture</title></head><body>'
  + '<div id="app">Loading</div><script>setTimeout(() => {'
  + 'const p = document.createElement("p"); p.id = "results";'
  + 'p.textContent = "Hydrated Ω after JavaScript";'
  + 'document.querySelector("#app").replaceChildren(p);'
  + 'document.dispatchEvent(new Event("fixture:hydrated"));'
  + '}, 1800);</script></body></html>';
function frame(message) {
  const data = Buffer.from(JSON.stringify({ version: 2, ...message }));
  let header;
  if (data.length < 126) header = Buffer.from([129, data.length]);
  else if (data.length < 65536) {
    header = Buffer.alloc(4); header[0] = 129; header[1] = 126;
    header.writeUInt16BE(data.length, 2);
  } else {
    header = Buffer.alloc(10); header[0] = 129; header[1] = 127;
    header.writeBigUInt64BE(BigInt(data.length), 2);
  }
  return Buffer.concat([header, data]);
}
function parseFrames(buffer) {
  const messages = [];
  let offset = 0;
  while (buffer.length - offset >= 2) {
    const first = buffer[offset], second = buffer[offset + 1];
    const masked = !!(second & 128);
    let count = second & 127, pos = offset + 2;
    if (count === 126) {
      if (buffer.length - pos < 2) break;
      count = buffer.readUInt16BE(pos); pos += 2;
    } else if (count === 127) {
      if (buffer.length - pos < 8) break;
      count = Number(buffer.readBigUInt64BE(pos)); pos += 8;
    }
    if (count > 2 * 1024 * 1024) throw Error("Frame too large");
    if (buffer.length - pos < count + (masked ? 4 : 0)) break;
    const key = masked ? buffer.subarray(pos, pos + 4) : null;
    if (key) pos += 4;
    const data = Buffer.from(buffer.subarray(pos, pos + count));
    if (key) for (let i = 0; i < count; i++) data[i] ^= key[i % 4];
    if ((first & 15) === 1) messages.push(JSON.parse(data.toString("utf8")));
    offset = pos + count;
  }
  return { messages, remainder: buffer.subarray(offset) };
}
class Bridge {
  constructor() {
    this.secret = crypto.randomBytes(32).toString("hex");
    this.nonce = crypto.randomBytes(32).toString("hex");
    this.connectionId = crypto.randomBytes(12).toString("hex");
    this.received = [];
    this.waiters = [];
    this.socket = null;
    this.server = http.createServer((req, res) => {
      if (req.url?.startsWith("/fixture")) {
        res.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
        res.end(fixtureMarkup);
      } else res.writeHead(404).end();
    });
    this.server.on("upgrade", (req, socket, head) => {
      if (req.url !== "/" || !req.headers["sec-websocket-key"]) return socket.destroy();
      const accept = crypto.createHash("sha1")
        .update(req.headers["sec-websocket-key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")
        .digest("base64");
      socket.write("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\n"
        + "Connection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
      this.socket = socket;
      let pending = head;
      socket.on("data", bytes => {
        try {
          pending = Buffer.concat([pending, bytes]);
          const result = parseFrames(pending);
          pending = result.remainder;
          for (const msg of result.messages) this.onMessage(msg);
        } catch (e) { for (const w of this.waiters.splice(0)) w.reject(e); }
      });
      this.send({ kind: "challenge", nonce: this.nonce });
    });
  }
  async start() {
    this.server.listen(0, "127.0.0.1");
    await once(this.server, "listening");
    this.port = this.server.address().port;
  }
  onMessage(msg) {
    if (msg.kind === "authenticate") {
      const proof = crypto.createHmac("sha256", this.secret).update(this.nonce).digest("hex");
      assert.equal(msg.proof, proof, "HMAC pairing mismatch");
      this.send({ kind: "ready", connectionId: this.connectionId });
    }
    const index = this.waiters.findIndex(w => w.match(msg));
    if (index < 0) this.received.push(msg);
    else this.waiters.splice(index, 1)[0].resolve(msg);
  }
  send(msg) {
    assert.ok(this.socket, "Extension not connected");
    this.socket.write(frame(msg));
  }
  wait(match, timeout = 8000) {
    const index = this.received.findIndex(match);
    if (index >= 0) return Promise.resolve(this.received.splice(index, 1)[0]);
    return new Promise((resolve, reject) => {
      const w = { match,
        resolve: val => { clearTimeout(timer); resolve(val); },
        reject: err => { clearTimeout(timer); reject(err); } };
      const timer = setTimeout(() => {
        this.waiters = this.waiters.filter(x => x !== w);
        reject(Error("Timed out waiting for browser reply"));
      }, timeout);
      this.waiters.push(w);
    });
  }
  async request(message, timeout = 8000) {
    const requestId = crypto.randomBytes(8).toString("hex");
    const pending = this.wait(m => m.requestId === requestId, timeout);
    this.send({ connectionId: this.connectionId, requestId, ...message });
    const reply = await pending;
    if (reply.kind === "error") throw Error(message.kind + " failed: " + reply.error);
    return reply;
  }
  async stop() {
    this.socket?.destroy();
    await new Promise(r => this.server.close(r));
  }
}
class CDP {
  constructor(url) {
    this.url = url; this.nextId = 0; this.callbacks = new Map();
  }
  async connect() {
    this.socket = new WebSocket(this.url);
    await new Promise((resolve, reject) => {
      this.socket.onopen = resolve; this.socket.onerror = reject;
    });
    this.socket.onmessage = e => {
      const reply = JSON.parse(e.data);
      const cb = this.callbacks.get(reply.id);
      if (!cb) return;
      this.callbacks.delete(reply.id);
      if (reply.error) cb.reject(Error(JSON.stringify(reply.error)));
      else cb.resolve(reply.result);
    };
  }
  send(method, params = {}) {
    const id = ++this.nextId;
    return new Promise((resolve, reject) => {
      this.callbacks.set(id, { resolve, reject });
      this.socket.send(JSON.stringify({ id, method, params }));
    });
  }
  async eval(expression) {
    const v = await this.send("Runtime.evaluate",
      { expression, awaitPromise: true, returnByValue: true, userGesture: true });
    if (v.exceptionDetails) throw Error("Evaluation: "
      + (v.exceptionDetails.exception?.description || v.exceptionDetails.text));
    return v.result?.value;
  }
  close() { this.socket?.close(); }
}
async function until(fn, timeout = 10000) {
  const end = Date.now() + timeout;
  while (Date.now() < end) {
    const value = await fn();
    if (value) return value;
    await sleep(150);
  }
  throw Error("Timed out awaiting Chrome");
}
async function main() {
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), "jarvis-mv3-smoke-"));
  const bridge = new Bridge();
  const phases = [];
  let chrome, extension, page;
  try {
    await bridge.start();
    chrome = spawn(chromePath, [
      "--headless=new", "--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage",
      "--no-first-run", "--no-default-browser-check",
      "--disable-extensions-except=" + extensionPath,
      "--load-extension=" + extensionPath,
      "--user-data-dir=" + profile,
      "--remote-debugging-port=0",
      "http://127.0.0.1:" + bridge.port + "/fixture"
    ], { stdio: "ignore" });
    const portFile = path.join(profile, "DevToolsActivePort");
    const debugPort = await until(() =>
      fs.existsSync(portFile) ? +fs.readFileSync(portFile, "utf8").split("\n")[0] : 0);
    const endpoint = "http://127.0.0.1:" + debugPort;
    const targets = () => fetch(endpoint + "/json/list").then(x => x.json());
    let extensionId = null;
    await until(async () => {
      for (const item of (await targets()).filter(t =>
        t.type === "service_worker" && t.url?.endsWith("/background.js"))) {
        const client = new CDP(item.webSocketDebuggerUrl);
        try {
          await client.connect();
          if (await client.eval("chrome.runtime.getManifest().name")
              === "Jarvis Browser Bridge") {
            extension = client;
            extensionId = item.url.split("/")[2];
            return true;
          }
        } catch {}
        client.close();
      }
      return false;
    });
    phases.push("Live MV3 service worker");
    // Activate the *real browser UI* consent toggle in this disposable test
    // profile. Chrome 138+ does not expose userScripts until this is enabled.
    const settingsTarget = await (await fetch(endpoint + "/json/new?"
      + encodeURIComponent("chrome://extensions/?id=" + extensionId),
      { method: "PUT" })).json();
    const settings = new CDP(settingsTarget.webSocketDebuggerUrl);
    await settings.connect();
    const selector = 'document.querySelector("extensions-manager")?.shadowRoot'
      + '?.querySelector("extensions-detail-view")?.shadowRoot'
      + '?.querySelector("#allow-user-scripts")';
    await until(async () => settings.eval("!!(" + selector + ")"));
    const clicked = await settings.eval("(function(){const row=" + selector
      + "; const toggle=row?.shadowRoot?.querySelector('cr-toggle');"
      + " if (!toggle) return false; toggle.click(); return true;})()");
    assert.equal(clicked, true);
    await sleep(500);
    const enabled = await settings.eval("(function(){const row=" + selector
      + ";return {checked:row.checked,shadowChecked:"
      + "row.shadowRoot?.querySelector('cr-toggle')?.checked};})()");
    settings.close();
    const userScriptAvailability = await extension.eval("typeof chrome.userScripts");
    console.log("Isolated Allow User Scripts setting:", JSON.stringify({
      checked: enabled, availability: userScriptAvailability }));
    phases.push("Per-extension Allow User Scripts toggle clicked");
    // Chrome does not deliver runtime.sendMessage from a service worker to
    // its own onMessage handler. Use internal test setup in this disposable
    // profile; do not relax the production own-popup-only sender check.
    async function fromExtension(message) {
      if (message.kind === "registerWindow") {
        await extension.eval("registerWindow(" + message.windowId
          + "," + JSON.stringify(message.alias) + ")");
        return { ok: true };
      }
      if (message.kind === "setAgentCreatedTabs") {
        await extension.eval("(async () => { allowAgentCreatedTabs = "
          + JSON.stringify(message.allowed)
          + "; await chrome.storage.local.set({allowAgentCreatedTabs}); return true; })()");
        return { ok: true };
      }
      if (message.kind === "pair") {
        await extension.eval("(async () => { configuredPort = " + message.port
          + "; secret = " + JSON.stringify(message.secret)
          + "; await chrome.storage.local.set({port: configuredPort, secret});"
          + " await connect(); return true; })()");
        return { ok: true };
      }
      if (message.kind === "getState") {
        return extension.eval("(async () => { await refreshRegistrations(); return {ok: true,"
          + " registrations: registrations.map(r => ({...safeState(r), tabId:r.tabId})),"
          + " windows: registeredWindows.map(w => ({...windowState(w),windowId:w.windowId}))}; })()");
      }
      if (message.kind === "register") {
        await extension.eval("register(" + message.tabId
          + "," + JSON.stringify(message.alias) + ")");
        return { ok: true };
      }
      throw Error("Unsupported disposable test setup action");
    }
    const windows = await extension.eval("chrome.windows.getAll()");
    assert.ok(windows.length);
    const windowId = windows[0].id;
    await fromExtension({ kind: "registerWindow", windowId, alias: "e2e-window" });
    await fromExtension({ kind: "setAgentCreatedTabs", allowed: true });
    const localPermission = await extension.eval(
      "chrome.permissions.contains({origins:['http://127.0.0.1/*']})");
    assert.equal(localPermission, true);
    phases.push("Disposable window registration and opt-in");
    await fromExtension({ kind: "pair", port: bridge.port, secret: bridge.secret });
    await bridge.wait(m => m.kind === "capabilities"
      && m.features?.includes("domSnapshotV1"));
    await bridge.wait(m => m.kind === "windows"
      && m.windows?.some(x => x.alias === "e2e-window" && x.availability === "ready"));
    phases.push("Authenticated loopback pairing");
    const opened = await bridge.request({
      kind: "openTab", windowRegistration: "e2e-window",
      url: "http://127.0.0.1:" + bridge.port + "/fixture?agent",
      alias: "e2e-agent", active: false
    });
    assert.equal(opened.kind, "tabOpened");
    const tab = opened.tab;
    phases.push("Created tab");
    await until(async () => {
      const state = await fromExtension({ kind: "getState" });
      return state.registrations?.some(x =>
        x.registrationId === tab.registrationId && x.availability === "ready");
    });
    const script = "async function execute(args) { return { title: document.title, marker: args.marker }; }";
    const executed = await bridge.request({
      kind: "executeJs", registrationId: tab.registrationId,
      registrationGeneration: tab.registrationGeneration,
      entrypoint: "execute", source: script, args: { marker: "verified" }
    });
    assert.equal(executed.kind, "result");
    assert.equal(executed.result.marker, "verified");
    phases.push("Real USER_SCRIPT execution");
    const captured = await bridge.request({
      kind: "captureDom", registrationId: tab.registrationId,
      registrationGeneration: tab.registrationGeneration,
      timeoutMs: 5500, mode: "inline", captureSelector: "#results",
      wait: { kind: "selector", selector: "#results", state: "textContains", text: "Hydrated" }
    }, 7000);
    assert.equal(captured.kind, "snapshotReady");
    assert.match(captured.html, /Hydrated Ω after JavaScript/);
    phases.push("Post-hydration selector snapshot");
    const delay = await bridge.request({
      kind: "captureDom", registrationId: tab.registrationId,
      registrationGeneration: tab.registrationGeneration,
      timeoutMs: 2500, mode: "inline", captureSelector: "#results",
      wait: { kind: "delay", milliseconds: 180 }
    });
    assert.equal(delay.kind, "snapshotReady");
    phases.push("Delayed DOM snapshot");
    const pageTarget = await until(async () =>
      (await targets()).find(x => x.type === "page" && x.url.includes("/fixture?agent")));
    page = new CDP(pageTarget.webSocketDebuggerUrl);
    await page.connect();
    const futureEvent = bridge.request({
      kind: "captureDom", registrationId: tab.registrationId,
      registrationGeneration: tab.registrationGeneration,
      timeoutMs: 3000, mode: "inline", captureSelector: "#results",
      wait: { kind: "event", target: "document", name: "fixture:repeat" }
    }, 4500);
    await sleep(350);
    await page.eval("document.dispatchEvent(new Event('fixture:repeat'))");
    const eventCapture = await futureEvent;
    assert.equal(eventCapture.kind, "snapshotReady");
    phases.push("Custom DOM event snapshot");
    const ready = await bridge.request({
      kind: "captureDom", registrationId: tab.registrationId,
      registrationGeneration: tab.registrationGeneration,
      timeoutMs: 2500, mode: "file", captureSelector: "",
      wait: { kind: "immediate" }
    });
    assert.equal(ready.kind, "snapshotReady");
    const chunks = [];
    for (let offset = 0; offset < ready.bytes;) {
      const chunk = await bridge.request({
        kind: "snapshotChunk", snapshotId: ready.snapshotId, offset
      });
      assert.equal(chunk.offset, offset);
      const bytes = Buffer.from(chunk.data, "base64");
      chunks.push(bytes);
      offset += bytes.length;
    }
    const snapshot = Buffer.concat(chunks);
    assert.equal(snapshot.length, ready.bytes);
    assert.equal(crypto.createHash("sha256").update(snapshot).digest("hex"), ready.sha256);
    assert.match(snapshot.toString("utf8"), /Hydrated Ω after JavaScript/);
    bridge.send({ kind: "releaseSnapshot", connectionId: bridge.connectionId,
      snapshotId: ready.snapshotId });
    phases.push("Frozen chunked DOM SHA-256 transfer");
    const existing = (await extension.eval("chrome.tabs.query({windowId:"
      + windowId + "})")).find(x => x.url.includes("/fixture")
        && !x.url.includes("?agent"));
    assert.ok(existing);
    await fromExtension({ kind: "register", tabId: existing.id, alias: "manual-fixture" });
    const state = await fromExtension({ kind: "getState" });
    const manual = state.registrations.find(x => x.alias === "manual-fixture");
    assert.ok(manual);
    const manualId = crypto.randomBytes(8).toString("hex");
    const denied = bridge.wait(x => x.requestId === manualId);
    bridge.send({ kind: "closeTab", connectionId: bridge.connectionId,
      requestId: manualId, registrationId: manual.registrationId,
      registrationGeneration: manual.registrationGeneration });
    assert.equal((await denied).error, "TabNotAgentCreated");
    phases.push("Manual tab close refused");
    const closed = await bridge.request({
      kind: "closeTab", registrationId: tab.registrationId,
      registrationGeneration: tab.registrationGeneration
    });
    assert.equal(closed.kind, "tabClosed");
    phases.push("Agent tab safely closed");
    console.log(JSON.stringify({ status: "passed", steps: phases,
      snapshotBytes: snapshot.length, snapshotSha256: ready.sha256 }, null, 2));
  } finally {
    page?.close();
    extension?.close();
    chrome?.kill("SIGTERM");
    await bridge.stop();
    await sleep(400);
    fs.rmSync(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
  }
}
main().catch(e => { console.error("Live browser smoke test failed: " + e.stack); process.exitCode = 1; });
