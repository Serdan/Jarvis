# Jarvis Browser Bridge — Chromium extension

A generic local browser bridge. Jarvis **does not** know anything about ChatGPT, agents, or websites; it only executes JavaScript in tabs that the user explicitly registers here.

## Installation (Chrome / Chromium 138+)

1. In JarvisClient's user-level config, enable the listener:
   \`JarvisClient config set browser-port 43822\`
   Restart JarvisClient. Alternatively start it with \`--browser-port 43822\`. The listener binds to \`127.0.0.1\`, not the public network.
2. Run \`JarvisClient browser pair\` **locally**. It prints the randomly generated pairing secret. Do not put this secret in Git, chat messages or agent-facing scripts.
3. Open \`chrome://extensions\`, enable Developer mode to load unpacked code, then select **Load unpacked** and choose this \`browser-extension\` folder.
4. In the extension's details page, enable **Allow User Scripts**. Chrome 138+ requires this for runtime-provided scripts.
5. Pin/click the extension icon, enter port and pairing secret, and click **Pair / connect**. Pairing is with the JarvisClient on this same machine.
6. Open the desired page, click the extension, supply a unique optional alias (e.g. \`agent-7\`), and choose **Register tab**. Approve its site permission when prompted.

Only tabs registered in the extension appear to \`BrowserListTabs\`. A browser host permission does not by itself register any tab. Use **Unregister current tab** to revoke one tab or **Revoke pairing** to disconnect the extension.

Registration persists through ordinary reloads, MV3 service worker restarts, and tab discards. It remains unavailable when the tab is discarded/frozen. Navigation to a different URL, closing a tab, or browser restart invalidates the binding until manually reauthorized in the extension. It is never silently matched by URL to a different tab.

## Jarvis commands

The client sends JavaScript source to the extension, not file paths. Scripts can live in **any** ordinary Jarvis project or its Git worktrees, and changes are picked up on every invocation.

\`BrowserListTabs(deviceId?)\` lists registered aliases, opaque identifiers and availability.

\`BrowserExecuteFile(projectName, scriptPath, tabRegistration, argsJson?, timeoutMs?, deviceId?)\` executes a UTF-8 \`.js\` file (64 KiB max), with an optional JSON argument string (64 KiB max) and JSON result (256 KiB max including envelope). Default timeout: 15 s; maximum 30 s.

Example project file, at \`Userscripts/inspect/title.js\`:

\`\`\`javascript
async function execute(args) {
  return { title: document.title, marker: args.marker };
}
\`\`\`

Example MCP arguments:

\`\`\`json
{
  "projectName": "Userscripts",
  "scriptPath": "inspect/title.js",
  "tabRegistration": "agent-7",
  "argsJson": "{\"marker\":\"test\"}"
}
\`\`\`

\`BrowserExecuteJs(tabRegistration, code, argsJson?, timeoutMs?, deviceId?)\` is available for **expression** evaluation, such as \`document.title\`. For multi-statement or reusable logic, use a file with an \`execute(args)\` entrypoint. There is no module resolver or persistent injected state in MVP.

## Permissions and limits

Jarvis MCP calls require \`workspace:read\` to list tabs, and \`process:execute\` (plus \`workspace:read\` to load a project file) to execute code. In default \`partial\` trust, JarvisClient prompts for script execution; \`full\` trust skips that client prompt but does **not** override extension pairing, user registration, or Chrome permission checks.

The listener is **off by default** until a browser port is configured. Revoke the key with \`JarvisClient browser revoke\`, then restart the client. Disable the listener with \`JarvisClient config unset browser-port\`, then restart.

Script execution can change authenticated webpages and submit irreversible actions. An operation that times out or loses its connection may already have taken effect. Jarvis never automatically retries such an operation. A successful JavaScript result is not proof that the web application completed a background task.

## Development and tests

- \`node --check browser-extension/background.js\`
- \`node --test browser-extension/tests/bridge.test.cjs\`
- \`dotnet test Jarvis.slnx\`

The Node tests simulate Chromium APIs and a webpage. The F# tests use a real loopback WebSocket connection. **Real Chromium integration testing is still required** before release (the initial development machine may not have a Chromium executable). See \`docs/BrowserBridgeSystem.md\` for the full design and acceptance requirements.
