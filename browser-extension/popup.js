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
    $("current").textContent = tab ? (tab.title || "(untitled)") + " — " + (tab.url || "") : "No selected tab";
    const current = state.registrations.find(r => r.tabId === tab?.id);
    $("alias").value = current?.alias || "";
    const container = $("tabs");
    container.replaceChildren();
    for (const registration of state.registrations) {
      const item = document.createElement("li");
      const name = document.createElement("strong");
      name.textContent = registration.alias || registration.registrationId;
      const info = document.createElement("small");
      info.textContent = " — " + registration.availability + " — " + registration.displayUrl;
      item.append(name, info);
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
$("unregister").onclick = () => action(() => call("unregister", {
  tabId: currentTab?.id
}));
render();
