# Jarvis browser bridge — Chromium/Edge extension

The extension pairs with a **local JarvisClient** over authenticated loopback WebSocket and executes ordinary project-owned JavaScript in registered tabs. Jarvis's server knows no website-specific automation. **The registered-window and disposable-tab workflow is implemented on `feature/browser-bridge` but has not been deployed or verified in live Edge yet.**

## Local setup

1. Enable JarvisClient browser listener: `JarvisClient config set browser-port 43822` (or `--browser-port 43822`) and restart the client. It binds to `127.0.0.1` only.
2. Run `JarvisClient browser pair` locally to get the private pairing key.
3. Load the unpacked `browser-extension` folder in `edge://extensions` or `chrome://extensions` (Developer mode). Enable **Allow User Scripts** in the extension details.
4. Open the extension popup. Enter the local port/key and pair.
5. In the browser window that should receive agent launches, click **Register / reauthorize window**, optionally naming it `agents`. This grants **placement**, not access to its existing tabs.
6. Under **Site to authorize for new tabs**, enter the origin you want agents to automate (for example `https://example.com`) and click **Grant site permission**. Browser host permission is independent of window registration.
7. Enable **Allow Jarvis to open new tabs**. New tabs open in the chosen window and are automatically registered. Existing tabs in the window remain inaccessible unless explicitly registered separately.

The extension accepts only registered windows; neither Jarvis nor a webpage may register an arbitrary window. A browser restart or closed window invalidates the window registration until you reauthorize it from the popup. Moving a created tab outside its registered window suspends script execution.

Manually registering a **tab** is still supported for existing-page inspections, and those registrations are bound to the exact URL. A manual tab is **not required** for creating agent tabs. **Unregister window** blocks new launches and revokes the permissions derived from that window, but does not close its tabs. **Unregister current tab** applies only to that tab.

## Browser commands

The current Jarvis protocol requires the caller's `agentId`. Commands that read or write project files also accept an optional registered `worktreeName` alongside `projectName`; pass the worktree name separately instead of encoding it into a project alias.

| MCP command | Purpose |
| --- | --- |
| `BrowserListWindows(agentId, deviceId?)` | Lists only user-registered windows, with alias, ID and availability |
| `BrowserListTabs(agentId, deviceId?)` | Lists only manually registered and Jarvis-created tabs |
| `BrowserOpenTab(url, agentId, windowRegistration, alias?, active?, deviceId?)` | Opens an agent-created tab in the selected window, default background |
| `BrowserExecuteFile(projectName, agentId, worktreeName?, scriptPath, tabRegistration, argsJson?, timeoutMs?, deviceId?)` | Runs a regular project-relative UTF-8 `.js` file in a registered tab |
| `BrowserCaptureDom(tabRegistration, agentId, waitJson?, captureSelector?, timeoutMs?, deviceId?)` | Capture current post-JavaScript DOM, optionally after a timer, selector condition or DOM event |
| `BrowserExportDom(projectName, agentId, worktreeName?, filePath, tabRegistration, waitJson?, captureSelector?, timeoutMs?, deviceId?)` | Stream a large DOM snapshot into a new project file, with SHA-256 and atomic write |
| `BrowserCloseTab(tabRegistration, agentId, deviceId?)` | Closes **only** a Jarvis-created tab with matching identity |
| `BrowserRunInNewTab(windowRegistration, url, projectName, agentId, worktreeName?, scriptPath, argsJson?, active?, closeTabAfterExecution?, timeoutMs?, deviceId?)` | One-call open → ready → execute → optional close, for immediate agent handoffs |
| `ScheduleBrowserExecution(scheduleId, runAfterSeconds, url, projectName, agentId, worktreeName?, scriptPath, windowRegistration, argsJson?, closeTabAfterExecution?, repeatEverySeconds?, ...)` | One-shot or recurring future invocation of the same workflow |
| `ListScheduledBrowserExecutions(agentId)` / `CancelScheduledBrowserExecution(scheduleId, agentId)` | Inspect/cancel local future launches |

`BrowserOpenTab` requires the separate new-tab opt-in, registered window, and destination host permission. Extension caps active Jarvis-created registrations at 20. Agent-created tabs remain authorized across same-origin SPA navigation but not cross-origin navigation. Never retry a timed-out open or execution blindly, because the page may already have been modified.

`BrowserRunInNewTab` and scheduling **do not run a ChatGPT backend API**. They execute the project script in the actual UI. Generic scripts use `async function execute(args)`. The composite operation returns structured script result + registration + cleanup status. If a script indicates `outcomeUnknown` or `uiIncompatible`, the created tab is retained for investigation rather than automatically resubmitting. Returned errors include the created tab registration ID where known. A definitive `notSubmitted` result may close its unused tab. A provider script reporting `uiIncompatible` returns an actionable error with stage, reason, sanitized structural diagnostics and the tab registration; recurring schedules enter `suspended` and do not launch again until explicitly replaced/reconfigured. The ChatGPT script waits for an actual conversation URL within one 27-second default deadline before reporting success.

## Capture dynamic web pages

An agent can use the browser bridge to obtain **rendered HTML** when `curl` only returns an application shell. Open a permitted tab (or explicitly register an existing one), then call `BrowserCaptureDom`. The capture can wait for an explicit delay, a selector to appear or contain text, or a future DOM event. For example:

```json
{
  "agentId": "Browser Agent",
  "tabRegistration": "results-tab",
  "waitJson": "{\"kind\":\"selector\",\"selector\":\"#results\",\"state\":\"exists\"}",
  "captureSelector": "#results"
}
```

Small captures return inline HTML. For pages exceeding the inline bound, call `BrowserExportDom` with an explicit `projectName` and new `filePath`; it transfers up to 8 MiB in 32 KiB chunks, verifies a checksum and writes the file atomically without overwriting. No HTML is logged or automatically committed.

See [Live DOM snapshots](../docs/BrowserDomSnapshots.md) for all wait forms, timeout behavior, export details and the test fixture. The extension advertises a snapshot capability; an older extension produces an explicit update-required error instead of an unexplained timeout.

## Example provider script: ChatGPT agent launcher

A reusable DOM-based launcher is included in this repository at:

```text
browser-scripts/chatgpt/launchAgent.js
```

Use it through a normal Jarvis project selector (for example `Jarvis::browser-bridge` when working in this development worktree). Its main argument is `prompt`; an optional overall UI timeout is supported. The entry URL is fixed to `https://chatgpt.com/`, and the script rejects any different starting URL (including an existing `/c/...` conversation). It selects **Chat** mode first (switching away from **Work** if needed), then selects the contenteditable ProseMirror editor, inserts text through normal editing behavior, waits for the enabled **Send** button (which replaces **Start voice**), clicks once, and checks UI evidence of submission. It does not rely on ChatGPT's private network API.

Example command arguments:

```json
{
  "windowRegistration": "agents",
  "url": "https://chatgpt.com/",
  "projectName": "Jarvis",
  "agentId": "Browser Agent",
  "worktreeName": "browser-bridge",
  "scriptPath": "browser-scripts/chatgpt/launchAgent.js",
  "argsJson": "{\"prompt\":\"You are Worker 3. Read your current Jarvis assignment and AGENTS.md, task, spec, and checkpoint. Continue the assigned work in this fresh session.\"}",
  "active": false,
  "closeTabAfterExecution": true
}
```

The logical agent is persistent **in Jarvis**, not the ChatGPT tab. The new chat continues backend execution after the temporary tab closes, as observed by the operator. A successful launch means the bootstrap message was submitted, **not** that the agent completed the assignment. Retrieve progress through Jarvis task/activity tools, not UI polling.

When a specific ChatGPT Project/GPT or model is required, start from a user-verified new-conversation URL and inspect the chosen context in the UI before using it unattended. Automatic model/thinking-effort selection is not implemented.

## Delayed and recurring launches

JarvisClient saves browser schedules in user-private `browser-schedules.json`. `ScheduleBrowserExecution` requires `windowRegistration`. Pre-existing entries created before window support must be reconfigured; they are never silently retargeted.

Omit `repeatEverySeconds` for one-shot scheduling. For hourly orchestration, set `repeatEverySeconds: 3600` and choose a first `runAfterSeconds` delay. The scheduler skips missed intervals after client downtime, and does not retry a claimed occurrence if the client is interrupted. It records the most recent attempt ID, submission URL if reported, cleanup state, and last outcome. A fresh orchestrator should inspect Jarvis activity before beginning work to avoid overlap; no automatic singleton lease is imposed.

Immediate orchestrator handoff **does not need a schedule**: call `BrowserRunInNewTab`.

## Security and limits

- MCP needs `workspace:read` for project scripts/read actions and `process:execute` for browser effects; the Jarvis local permission policy still applies.
- ChatGPT's **own plugin permission prompts are separate**. This launcher does not click or bypass approval, login, CAPTCHA, or security controls.
- Pairing and extension registries remain local. Cookies and tokens are not exposed to Jarvis. Scheduled prompt arguments are stored locally in a user-private JSON file and omitted from list/audit output.
- Script input maximum 64 KiB, JSON args maximum 64 KiB, result maximum 256 KiB, script timeout maximum 30 seconds. Prompt has a lower explicit size limit in the ChatGPT script.
- Only the extension UI registers windows or existing tabs. MCP cannot adopt preexisting tabs, switch to an arbitrary active window, or close a registered browser window.
- Versioned bridge protocol is **v2**; new extension and JarvisClient must be installed together.

## Tests

```bash
dotnet test Jarvis.slnx --no-restore
node --test browser-extension/tests/bridge.test.cjs browser-extension/tests/chatgpt-launcher.test.cjs
```

The extension uses simulated Chromium APIs in Node tests; the F# client tests exercise a real loopback WebSocket. **A live Edge/ChatGPT acceptance run remains necessary** before deploying to normal use. See `docs/BrowserBridgeSystem.md` and `docs/ChatGPTAgentLauncher.md` for the detailed specifications.
