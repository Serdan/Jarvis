# Live DOM snapshots in the Jarvis browser bridge

**Status:** Implemented on `feature/browser-bridge`; not merged or deployed. Automated live Chrome for Testing verification has passed with the real MV3 extension paired to the actual `Client.BrowserBridge.Service` through an isolated test host. Human-operated extension-popup consent and the normal authenticated Jarvis MCP/server-to-client flow have not yet been verified.

## Why use it?

Many sites render useful content only after JavaScript hydration, fetches or events. `curl` and an HTTP response snapshot often contain only an application shell. Jarvis can capture the **current DOM** from an authorized, already-rendered browser tab, after a bounded readiness condition.

The operation is **provider-agnostic**. Jarvis does not know the target website's selectors or business rules; the agent supplies the readiness condition.

The extension advertises `domSnapshotV1` on pairing. A newer client paired to an older extension fails with `ExtensionOutdated` immediately for these commands; existing tab and JavaScript operations continue to work. Update the extension to use snapshots.

## Commands

`BrowserCaptureDom(tabRegistration, agentId, waitJson?, captureSelector?, timeoutMs?, deviceId?)`

Returns JSON with `status`, `registrationId`, `title`, sanitized `url`, `capturedAtUtc`, `waitKind`, `bytes`, and `html`. Inline markup is limited to **48 KiB of UTF-8** (or smaller when JSON expansion reaches the bounded frame limit). Oversized results are an explicit error, **not** silently truncated.

`BrowserExportDom(projectName, agentId, worktreeName?, filePath, tabRegistration, waitJson?, captureSelector?, timeoutMs?, deviceId?)`

Captures **up to 8 MiB** of UTF-8 markup into a project-relative file. The project/destination must exist and be writable; the destination file must **not already exist**. The extension serializes the DOM once, then serves bounded chunks from that frozen snapshot. JarvisClient validates chunk identities, verifies SHA-256, writes to a temporary file in the destination directory, atomically moves it into place, and releases the extension snapshot. Failed transfers remove temporary files.

Use `BrowserOpenTab` / `BrowserListTabs` to obtain an authorized `tabRegistration`. Existing manually registered tabs are also eligible for capture. Neither command grants access to unregistered tabs.

## Wait conditions

`waitJson` is a JSON-encoded object; omit it for immediate capture.

| Wait | `waitJson` | Meaning |
| --- | --- | --- |
| Immediate | `{"kind":"immediate"}` | Capture the current DOM now |
| Timer | `{"kind":"delay","milliseconds":2000}` | Wait 2 seconds before capture |
| Selector appears | `{"kind":"selector","selector":"#results","state":"exists"}` | Wait for an element to appear |
| Visible | `{"kind":"selector","selector":"#results","state":"visible"}` | Wait for an element with nonzero size and nonhidden computed style |
| Text appears | `{"kind":"selector","selector":"#results","state":"textContains","text":"Ready"}` | Wait for text in a selected element |
| Document event | `{"kind":"event","target":"document","name":"results:ready"}` | Listen for a **future** event on `document` |
| Window event | `{"kind":"event","target":"window","name":"load"}` | Listen for a **future** event on `window` |
| Element event | `{"kind":"event","target":"element","selector":"#grid","name":"data-loaded"}` | Wait for the element, then subscribe to its future event |

Selectors use standard CSS `querySelector`. For selector-state waits, the isolated script checks immediately, installs a `MutationObserver`, and supplements it with bounded visibility polling. Listeners and observers are detached at completion/timeout. Events which fire before the listener is registered **cannot** be detected retroactively. When possible, prefer a selector/text predicate that remains true.

A single **monotonic operation deadline** covers waiting, serialization, all chunk transfers, checksum verification, disk flush and final publication. The default is **30 seconds**, with an allowed range of **100 ms to 60 seconds**. Every chunk uses only the remaining deadline budget (never a fresh unbounded 10-second window); when time expires, the temporary file is deleted and no destination file is published. An explicit delay must be shorter than the timeout. A navigation during the wait, invalid selector, missing capture target, timeout or revoked tab registration produces an actionable error.

`captureSelector` optionally chooses a subtree, for example `#results`. If omitted, capture `document.documentElement.outerHTML`. This is serialized **live HTML** from the current DOM, *not* the original server response, not a screenshot, and not a network archive. It does not include closed shadow roots, canvas pixels, ephemeral JS state, or content inside cross-origin iframe documents.

## Example

1. Register a browser window in the extension, grant the intended site's host permission, and enable Jarvis-created tabs.
2. Call `BrowserOpenTab` with that window and the page URL.
3. Once `BrowserListTabs` reports the created tab as ready, call:

```json
{
  "agentId": "Browser Agent",
  "tabRegistration": "results-tab",
  "waitJson": "{\"kind\":\"selector\",\"selector\":\"#results\",\"state\":\"textContains\",\"text\":\"Ready\"}",
  "captureSelector": "#results",
  "timeoutMs": 30000
}
```

For large data, use `BrowserExportDom` with `projectName` and a new `filePath` such as `snapshots/results.html` (the folder must exist). Read the captured file through ordinary Jarvis project-file tools; no special browser runtime is needed after capture.

## Safety and limitations

The user's extension registration and explicit site host permission still apply. The bridge injects into a registered tab in the isolated `USER_SCRIPT` world and checks its identity before and after capture. For inline capture, the public MCP tool requires the `process:execute` OAuth scope. For file export, the public MCP tool requires **both** `workspace:write` and `process:execute`, as well as `workspace:read` for its project-scoped routing. These are checked by the server before dispatch, independently of the client's local browser permissions. Captures may contain personal data, tokens or other secrets present in the DOM: treat returned HTML and exported files as sensitive. Jarvis does not automatically commit, log or store snapshot HTML in Git.

The export buffer is ephemeral in the extension and limited to two snapshots, each up to 8 MiB, with a rolling two-minute idle expiration. Export uses **32 KiB chunks** and SHA-256 integrity checks. A disconnected extension, expired buffer, navigation or lost authorization aborts transfer and preserves no partial output file. The destination is never silently overwritten.

The fixture `browser-extension/tests/fixtures/dynamic-dom.html` demonstrates delayed content and a custom DOM event. Automated DOM simulation, loopback protocol regressions, and a **real Chromium extension ↔ native JarvisClient bridge** smoke test all pass. The latter uses synthetic consent in a disposable profile; the separate human-authorized popup and full Jarvis OAuth/MCP acceptance test remain unverified.

Tab closure is based on valid Jarvis ownership and authorization, **not whether page scripts can execute**. An owned `discarded` or `frozen` tab can be closed without waking or activating it, provided its registration generation, window, origin, and current host permission still match. Manually registered tabs remain non-closable. If a composite open-and-execute operation times out waiting for page readiness, it reports `TabNotReady; cleanup=closed`, `TabNotReady; cleanupFailed:<reason>`, or `TabNotReady; cleanup=retained` rather than hiding a failed cleanup.

## Disposable real-browser smoke test

Use an official Chrome for Testing executable, not the user's existing browser profile. With the repository's .NET 10 SDK installed, run either mode:

```sh
# Real Chromium extension against a minimal, test-only WebSocket peer
node browser-extension/tests/live-chromium-smoke.cjs /absolute/path/to/chrome-for-testing

# Real Chromium extension paired to the actual JarvisClient BrowserBridge.Service
dotnet build browser-extension/tests/live-client-host/LiveClientHost.csproj -c Release
node browser-extension/tests/live-chromium-smoke.cjs /absolute/path/to/chrome-for-testing --real-client
```

The native mode launches a small, test-only .NET process referencing the **same Client assembly** as JarvisClient. It uses the actual local `BrowserBridge.Service` authentication, registration, execution, read, chunk transfer, checksum and atomic file-export code. The host avoids the normal client's interactive OAuth login and TUI; it does **not** start a second production Jarvis session.

Both modes use a disposable Chromium profile, an HTTP fixture served exclusively on loopback, and a per-extension **Allow User Scripts** toggle enabled through Chrome's extension settings page. Window registration, new-tab opt-in and pairing are synthetic test setup inside the **disposable** extension background. No personal browser profile or real website is accessed.

The fixture generates over 150 KiB of rendered DOM after JavaScript hydration. The test verifies live USER_SCRIPT execution, selector/delay/custom-event waits, permission rejection for an unapproved origin, multi-chunk SHA-256 export, no overwrite of an existing exported file (native mode), refusal to close a manually registered tab, and safe cleanup of an agent-owned tab. Output includes test stage names, byte count, and checksum, never secrets or captured HTML. The harness removes its temporary browser profile on exit.

**Evidence boundary:** The native run proves real extension ↔ production JarvisClient browser-bridge code interoperability; its test host is not the full authenticated Jarvis server/MCP pipeline. Its registration is synthetic, **not a real user's consent through the extension popup**. Loading `popup.html` as an ordinary Chrome tab is correctly rejected with `Extension UI required`; the production sender restriction must remain intact. The explicit human-authorized, normally paired JarvisClient/browser acceptance remains separate and is not implied by this test. Nothing here authorizes integrating or deploying.
