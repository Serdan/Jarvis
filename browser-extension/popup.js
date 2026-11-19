const $ = id => document.getElementById(id);
let currentTab;

async function call(kind, extras = {}) {
  const response = await chrome.runtime.sendMessage({ kind, ...extras });
  if (!response?.ok) throw new Error(response?.error || "Browser extension unavailable");
  return response;
}
async function render() {
  try {
    const state = await call("getState");
    const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
    currentTab = tab;
    $("status").textContent = state.connected ? "Connected to JarvisClient" :
      state.paired ? "Paired; currently disconnected" : "Not paired";
    $("port").value = String(state.port);
    $("allowAgentTabs").checked = state.allowAgentCreatedTabs === true;
    const window = await chrome.windows.getCurrent();
    $("currentWindow").textContent = "Window " + window.id;
    $("windowAlias").value = state.windows.find(w => w.windowId === window.id)?.alias || "";
    const windowList = $("windows");
    windowList.replaceChildren();
    for (const entry of state.windows) {
      const item = document.createElement("li");
      item.textContent = (entry.alias || entry.windowRegistration) + " — " + entry.availability;
      windowList.append(item);
    }
    $("current").textContent = tab ? (tab.title || "(untitled)") + " — " + (tab.url || "") : "No selected tab";
    const current = state.registrations.find(r => r.tabId === tab?.id);
    $("alias").value = current?.alias || "";
    const container = $("tabs");
    container.replaceChildren();
    $("forgetUnavailable").disabled = !state.registrations.some(r =>
      r.agentCreated && r.availability === "needsReauthorization");
    for (const registration of state.registrations) {
      const item = document.createElement("li");
      const name = document.createElement("strong");
      name.textContent = (registration.alias || registration.registrationId) + (registration.agentCreated ? " (created by Jarvis)" : " (manual)");
      const info = document.createElement("small");
      info.textContent = " — " + registration.availability + " — " + registration.displayUrl;
      const forget = document.createElement("button");
      forget.textContent = "Unregister";
      forget.title = "Remove only this extension registration; leave its browser tab open";
      forget.onclick = async () => {
        forget.disabled = true;
        item.querySelector('[data-unregister-error]')?.remove();
        try {
          await call("unregisterRegistration", {
            registrationId: registration.registrationId,
            registrationGeneration: registration.registrationGeneration
          });
          // Verify the registration is gone; a response alone is not UI proof.
          const latest = await call("getState");
          if (latest.registrations.some(r => r.registrationId === registration.registrationId))
            throw new Error("Registration still present after Unregister");
          await render();
        } catch (e) {
          const error = document.createElement("small");
          error.setAttribute("role", "alert");
          error.setAttribute("data-unregister-error", "");
          error.textContent = "Unregister failed: " + (e.message || String(e));
          item.append(error);
        } finally {
          forget.disabled = false;
        }
      };
      item.append(name, info, forget);
      container.append(item);
    }
    $("error").textContent = "";
  } catch (e) { $("error").textContent = e.message; }
}
async function action(fn) {
  try { await fn(); await render(); } catch (e) { $("error").textContent = e.message; }
}
$("pair").onclick = () => action(() => call("pair", {
  port: Number($("port").value), secret: $("secret").value.trim()
}).then(() => { $("secret").value = ""; }));
$("disconnect").onclick = () => action(() => call("unpair"));
$("register").onclick = () => action(async () => {
  if (!currentTab?.url || !/^https?:/.test(currentTab.url))
    throw new Error("This tab cannot be registered");
  const origin = new URL(currentTab.url).origin + "/*";
  const granted = await chrome.permissions.request({ origins: [origin] });
  if (!granted) throw new Error("Browser host permission denied");
  await call("register", { tabId: currentTab.id, alias: $("alias").value.trim() });
});
$("unregister").onclick = async () => {
  const error = $("currentUnregisterError");
  error.textContent = "";
  const button = $("unregister");
  button.disabled = true;
  try {
    const tabId = currentTab?.id;
    if (!Number.isInteger(tabId)) throw new Error("No current tab selected");
    await call("unregister", { tabId });
    const latest = await call("getState");
    if (latest.registrations.some(r => r.tabId === tabId))
      throw new Error("Current tab remains registered");
    await render();
  } catch (e) {
    error.textContent = "Unregister failed: " + (e.message || String(e));
  } finally {
    button.disabled = false;
  }
};
$("forgetUnavailable").onclick = () => action(() => call("forgetUnavailableAgentRegistrations"));
$("registerWindow").onclick = () => action(async () => {
  const window = await chrome.windows.getCurrent();
  await call("registerWindow", { windowId: window.id, alias: $("windowAlias").value.trim() });
});
$("unregisterWindow").onclick = () => action(async () => {
  const window = await chrome.windows.getCurrent();
  await call("unregisterWindow", { windowId: window.id });
});
$("grantOrigin").onclick = () => action(async () => {
  const url = new URL($("siteOrigin").value);
  if (!["https:", "http:"].includes(url.protocol) || url.username || url.password) throw Error("Invalid site origin");
  const granted = await chrome.permissions.request({ origins: [url.origin + "/*"] });
  if (!granted) throw Error("Site permission denied");
});
$("allowAgentTabs").onchange = () => action(() => call("setAgentCreatedTabs", {
  allowed: $("allowAgentTabs").checked
}));
render();
