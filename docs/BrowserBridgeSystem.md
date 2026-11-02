# Jarvis Browser Bridge — System Specification

**Status:** Browser bridge, registered-window launch, safe tab close, and one-shot/recurring scheduling implemented on this unmerged worktree; **live Edge acceptance and deployment pending**
**Scope:** Jarvis server, JarvisClient, Chromium extension, and project-owned JavaScript scripts  
**Initial implementation worktree:** `Jarvis::browser-bridge` (`feature/browser-bridge`)  
**Related task:** [Local browser bridge and MCP command routing](tasks/2026-10-26-browser-bridge-jarvis.html)

## 1. Purpose

Provide an authenticated Jarvis agent with a general-purpose way to inspect and manipulate **tabs explicitly registered by the user or created by Jarvis in a user-registered browser window**. The agent can execute JavaScript sourced from normal files in normal Jarvis projects, optionally passing JSON arguments and receiving a bounded result.

The browser bridge has **no knowledge of ChatGPT, agent identities, conversations, workflows, or task orchestration**. Those behaviors live in agent-authored project scripts and the existing Jarvis task system.

A motivating use case is an orchestrator starting a **new ChatGPT session** for a logical worker: Jarvis creates a disposable tab in a user-registered browser window, runs a project-owned UI script to submit the bootstrap message, and closes only that new tab. The agent reconstructs its assignment from Jarvis. This is an application of the generic capability, not a built-in Jarvis workflow. See [ChatGPT Agent Launcher](ChatGPTAgentLauncher.md).

### Principles

1. **User-owned targets and launch windows:** the user registers an existing **browser window** as an allowed destination and separately grants browser host permission for sites Jarvis may automate. Jarvis may create and automatically register **new tabs only in that registered window**. Existing tabs remain inaccessible unless individually registered by the user. No MCP operation can claim an arbitrary existing tab.
2. **Project-owned scripts:** scripts are regular `.js` files in Jarvis projects and Git worktrees; no Jarvis script database, dedicated script directory, or script registration.
3. **Browser-independent Jarvis API:** only the extension implements Chromium behavior. Jarvis passes typed commands and returns typed results.
4. **Explicit boundaries:** device authentication, extension pairing, user window registration, site host permission, tab registration, and permission to execute are separate checks.
5. **Fail closed:** missing, navigated, frozen, discarded, disconnected, or ambiguously restored tabs never cause silent retargeting.
6. **No exactly-once fiction:** a timeout or disconnect can leave the result of a page action uncertain. Jarvis must never blindly replay it.

## 2. Architecture

```text
Agent / MCP caller
   |
   | BrowserListWindows / BrowserOpenTab / BrowserRunInNewTab / BrowserExecuteFile / BrowserCloseTab
   v
Jarvis server (OAuth scopes, device routing, SignalR)
   |
   v
JarvisClient on selected device (F# / .NET)
   |  resolves project-relative scripts; checks local policy
   |  hosts authenticated WebSocket on 127.0.0.1
   v
Paired Chromium extension (Manifest V3 service worker)
   |  owns user window/tab registries and browser permissions
   |  validates registration against real tab and URL
   v
Chrome userScripts.execute() -> explicitly registered tab
   |
   v
JSON-compatible result -> extension -> client -> server -> agent
```

### Responsibilities

| Component | Owns | Must not do |
| --- | --- | --- |
| Jarvis server | OAuth authorization, authenticated MCP routing, per-conversation device selection, request correlation | Know browser tabs or store scripts/registrations |
| JarvisClient | Loopback listener, extension pairing, permission policy, script-file reading, protocol correlation, bounds and diagnostics | Enumerate browser tabs independently or bypass extension consent |
| Extension service worker | Browser connection, user-registered windows and tabs, host permission checks, creation/closure of agent-created tabs, script injection | Read project files, let Jarvis register windows or claim existing tabs |
| Extension UI | Pairing, explicit register/rename/unregister **window or existing tab**, site permissions, availability and revocation | Opt existing tabs or windows into control without a user gesture |
| Jarvis project | JavaScript files, optional helper/build system, Git history, app-specific logic | Require changes to Jarvis or extension to target a new website |

The browser and its JarvisClient must be running on the **same machine**: the browser extension connects to that client over loopback, never to the public Jarvis server. Multiple devices may host independent clients and browser instances. Existing Jarvis `deviceId` selection applies. No mutable global “current tab” or “current agent conversation” exists at server level.

## 3. User experience

### Set up

1. Enable the browser bridge in JarvisClient's **user-level configuration**. Its listener is disabled by default.
2. Install the extension in a supported Chromium browser. Enable Chrome's required **Allow User Scripts** setting.
3. Pair that extension with the local JarvisClient through a user-approved pairing flow.
4. In the intended Edge/Chromium window, open the extension and choose **Register window**; name it (for example `agents`) and receive a window registration ID. This action **does not register any existing tab** in the window.
5. In the extension UI, grant browser host permission for destinations to automate (for example `https://chatgpt.com/*`). Window registration determines **where** to open tabs; host permission determines **which origins** scripts can access. Neither alone authorizes access to existing tabs.
6. Enable the separate **Allow Jarvis to open new tabs** opt-in. `BrowserOpenTab` or `BrowserRunInNewTab` requires both the target window registration and the destination site's host permission. New tabs are automatically registered with agent-created provenance, open in the background by default, and count toward the 20-active-agent-tab cap until closed.
7. Optionally register a particular existing tab with **Register tab** to use ordinary `BrowserExecuteFile` against that exact URL; this is independent of window registration and not required to launch new tabs.

Example extension registries:

| Window alias | Binding | Availability |
| --- | --- | --- |
| `agents` | User-selected browser window | ready |
| `debug` | Another user-selected window | closed / needsReauthorization |

| Tab alias | Binding | Availability |
| --- | --- | --- |
| `agent-launch-42` | New tab created in `agents`, origin-bound | background/ready |
| `reference` | Manually registered documentation tab, exact URL | ready |

Window aliases and tab aliases are distinct namespaces, each unique per paired browser profile/device. Prefer opaque IDs. No implicit global 'current window' or arbitrary active-window fallback: an ambiguous or missing window target fails. The extension UI shows window bindings and origin host permissions separately from tab registrations, and provides explicit **Unregister window** and **Unregister tab** actions. A registered tab can be manipulated while not selected, provided its JavaScript context is usable.

### Non-goals

No ChatGPT-specific MCP tools; no automatic discovery of ChatGPT agents; no unattended registration of all tabs; no remote browser process management; no CDP debugger; no general network interception; no file access from browser scripts; no headless Chromium service; and no guarantee that a web application will process requests in a background tab.

## 4. Window and tab registration and identity

Registration is an **explicit consent record**. The user can register a browser **window** as a location where Jarvis may open new tabs. Window registration never exposes or registers its preexisting tabs. Existing individual tabs can separately be registered through a user gesture in the extension and remain exact-URL-bound. After opt-in, Jarvis may create a tab in a registered window **only if the destination's browser host permission is already approved**; the extension auto-registers only that newly created tab. Pairing the extension alone grants neither window access nor site access.

### Window record (new)

An opaque `windowRegistration` (or unique window alias) identifies a particular browser window on the paired extension/device. Store `windowId`, profile/session epoch, registration generation, friendly alias and current availability in extension-local storage. Browser window IDs may be reused: validate the live window and generation on every create; window close or browser restart invalidates the registration until the user explicitly registers or reauthorizes a window. A service-worker restart within the same browser session may restore the binding if the identity remains verifiable. Never auto-bind a restored/new window by matching name, URL or position.

Only extension UI gestures may create, rename, reauthorize or revoke window registrations; MCP calls cannot register or silently switch windows. Unregistering the window blocks further new-tab creation and any further script injection into tabs whose authorization depends on that window; it does **not** silently close existing user or agent tabs. Close of a formerly created tab may need manual intervention after revocation.

### Tab records (existing + agent-created)

Persist the following locally using extension storage:

```json
{
  "registrationId": "opaque-random-id",
  "alias": "agent-7",
  "boundUrl": "https://example.test/conversation/123",
  "profileInstanceId": "opaque-browser-profile-id",
  "browserSessionId": "opaque-session-epoch",
  "tabId": 421,
  "registrationGeneration": 7,
  "createdAt": "2026-10-27T00:00:00Z",
  "state": "ready"
}
```

This is an illustrative internal record, not a mandate to send all fields to Jarvis. Persist the binding metadata locally; send only what an authorized Jarvis request needs. Query parameters and fragments may contain sensitive tokens and must be redacted from logs and human-facing diagnostics by default.

**Binding rules:**

- **Manually registered** tabs are bound to their exact page URL. **Agent-created** tabs are bound to the created tab ID, its registered parent window, and approved origin, so SPA navigation and assignment of a new conversation URL within that origin remain usable. Cross-origin navigation invalidates the registration.
- Reloading that URL in the same tab preserves the registration, but a new document must be rechecked before injection.
- A same-origin SPA route change, query change, or fragment change that changes the bound URL suspends execution until the user explicitly updates the binding. Watch `tabs.onUpdated`, `webNavigation` (including History API navigation), and inspect the target immediately before injection.
- Do not silently transfer a binding to a newly opened tab with the same URL or the same title.
- An extension service-worker restart is not a new browser session. Restore and validate existing registrations against the live tab IDs/URLs.
- Browser restart and session restoration require stronger continuity evidence than a matching URL alone. If continuity cannot be proven, retain the registration as **needsReauthorization**, and require the user to rebind it.
- Closing a tab, explicitly unregistering it, or losing the required browser permission must immediately prevent further execution; an in-flight operation must be canceled where possible and reported as indeterminate if cancellation cannot undo an already dispatched page effect.
- Registry data remains local to the extension; JarvisClient caches the connected extension's advertised registry only for its connection lifetime.
- The extension revalidates the registration's generation, tab ID, URL, required browser permission, and current document immediately before every execution. For agent-created tabs it also validates that the parent window is still registered and the tab remains in it; moving the tab to another window revokes execution until user intervention. The client also validates the opaque registration against the current advertised catalog. Neither check replaces the other.

Never accept a script-supplied registration event as equivalent to the user clicking **Register window** or **Register tab**. Disallow `chrome://`, extension pages, privileged schemes, incognito/private contexts, and other restricted targets by default.

## 5. Browser lifecycle and availability

A registered tab is not necessarily a runnable tab. Model **registration**, **permission**, **availability**, and **visibility** independently.

| State | Expected behavior |
| --- | --- |
| Loaded, foreground | Execute if registered and authorized |
| Loaded, background | Execute without selecting the tab; page timers/rendering may be throttled |
| Frozen | Do not claim execution succeeded; report unavailable/resume required |
| Discarded | DOM and JS context no longer exist; report reload required |
| Loading/navigation | Wait within a bounded operation if the original binding remains valid, otherwise reject |
| Closed/missing | Mark unavailable; do not search for a replacement |
| Bound URL changed | Mark needsReauthorization until explicit user action |
| Browser/bridge disconnected | Return disconnected; do not queue page mutations for later blind replay |

**MVP wake policy:** `never`. Merely listing registrations never wakes a page. An unavailable target produces a typed result.

**Future opt-in wake policy:** `ifAllowed` (per call and subject to an extension-side preference) may activate/reload a registered tab, potentially stealing focus and losing unsaved in-memory state. It must revalidate the binding after load, respect a timeout, and be explicit in operation results. Chrome does not guarantee indefinite execution or rendering in inactive tabs; there is no “always active” promise.

The extension itself is browser-wide, not attached to a page. Manifest V3 service workers can be suspended; maintain durable registry state in extension storage, reconnect to JarvisClient as needed, and correlate operations only while a connection is alive. WebSocket heartbeats may help keep the worker active but are not a correctness guarantee.

## 6. Browser extension requirements

Target Chromium Manifest V3, initially **Chrome/Chromium 138+** (also test supported Edge builds); this version includes `chrome.userScripts.execute()` and the per-extension **Allow User Scripts** UI. Use `chrome.userScripts.execute({target:{tabId},js:[{code}],world:"USER_SCRIPT"})` for dynamically supplied script text. The script executes in an isolated user-script world with DOM access; do not default to `MAIN` world. Promise results must be awaited.

The extension requires `userScripts`, tab/window inspection and lifecycle capabilities, and host access for sites explicitly approved by the user. The extension UI must support requesting/revoking an intended site's optional host permission without requiring a manually registered anchor tab. Avoid broad permanent all-sites host access. Browser host permissions are generally **origin-wide**, not per-tab: the extension's own per-tab registry provides the stricter authorization. A registration requires both browser permission and extension-level user consent. `activeTab` access alone should not be assumed sufficient for durable background automation.

The extension popup/options view supports:

- Pair/disconnect and diagnostic connection state.
- Register/rename/unregister the **current window**; list window aliases/IDs and availability.
- Grant/revoke **site host permissions** separately from choosing a window.
- Optionally register the currently selected **existing tab** for exact-URL inspection or execution.
- List/revoke manual and agent-created tab registrations with provenance and availability.
- On revocation, invalidate outstanding operations and prevent scripts in unauthorized tabs.
- Display errors for disabled user scripts, missing host permission, unsupported pages, or disconnected JarvisClient.

Do not rely on unrestricted page-level `eval` or dynamically injecting scripts through `chrome.scripting.executeScript({func})` to handle arbitrary script source. The `userScripts` API is designed for user-provided code.

## 7. MCP command surface

### BrowserListWindows (new, read-only)

`BrowserListWindows(deviceId?)` returns **only user-registered windows**, each with opaque `windowRegistration`, alias, availability and its paired device/profile context (no enumeration of unrelated windows or tabs). Window creation/registration remains extension-UI-only. The caller must choose the returned registration ID or unique alias when opening a tab; never silently use the active window.

### BrowserListTabs

```json
{ "deviceId": null }
```

Returns only registered tabs—manual and explicitly opted-in agent-created—with provenance, e.g.:

```json
{
  "tabs": [
    {
      "registrationId": "opaque-id",
      "alias": "agent-7",
      "displayUrl": "https://example.test/conversation/123",
      "origin": "https://example.test",
      "availability": "ready",
      "visibility": "background",
      "registrationGeneration": 7,
      "agentCreated": false
    }
  ]
}
```

Sensitive URL components are omitted or redacted. A disconnected extension may yield a distinct connection error rather than an empty list.

### BrowserOpenTab (opt-in, effectful)

```json
{
  "windowRegistration": "agents",
  "url": "https://example.test/chat/new",
  "alias": "new-agent",
  "active": false,
  "deviceId": null
}
```

Creates a **new browser tab inside the explicitly registered window**, not a claim on any existing tab. The extension calls `chrome.tabs.create({windowId, url, active})` with `active=false` by default. It requires the extension's **Allow Jarvis to open new tabs** opt-in, a live registered `windowRegistration`, and previously granted browser host permission for that destination origin; no manually registered tab on that origin is required. The extension enforces a 20-active-agent-tab limit. Requests for non-HTTP(S) URLs, embedded credentials, stale window registrations, unapproved host permissions, ambiguous aliases, or restricted pages fail before creation.

The extension automatically registers the new tab with its parent `windowRegistration`, tab ID, generation, origin and agent-created provenance; it returns a unique `registrationId` and availability, usually `loading`. Agents can wait/poll until ready, then invoke `BrowserExecuteFile` or `BrowserExecuteJs`. Same-origin SPA navigation is allowed for new tabs; cross-origin navigation or moving a tab outside its registered window invalidates authorization. Existing manually registered tabs remain exact-URL-bound. Disabling the new-tab opt-in invalidates future creation and any still-dependent agent-created registration; it does not close tabs.

This is a side-effecting, non-idempotent operation: a network failure after creation can leave a new tab open even if the client did not receive its registration ID. **Do not automatically retry** when the result is uncertain. MCP authorization uses `process:execute` and the local execution confirmation policy; extension consent remains mandatory even under full trust.


### BrowserCloseTab (new, effectful)

```json
{ "tabRegistration": "new-agent", "deviceId": null }
```

Closes **only a tab created by Jarvis in a registered window**, using the exact opaque auto-created registration. The extension validates agent-created provenance, session/profile, window identity, tab ID and generation immediately before `chrome.tabs.remove(tabId)`. It **never** closes a manually registered tab, all tabs in a window, the registered window itself, or an unrelated tab inferred from URL/title. It unregisters the closed tab and releases its capacity slot. Close is independently authorized and audited. A close failure after a successful browser script must not cause a second script execution. A tab whose registration became stale is not closed by guesswork.

### BrowserRunInNewTab (new, preferred immediate workflow)

```json
{
  "windowRegistration": "agents",
  "url": "https://chatgpt.com/",
  "projectName": "Userscripts",
  "scriptPath": "chatgpt/launchAgent.js",
  "argsJson": "{\"prompt\":\"Start a new session and read current Jarvis assignment.\"}",
  "active": false,
  "closeTabAfterExecution": true,
  "deviceId": null
}
```

This **generic** composite command is the preferred single call for an orchestrator that wants to launch a fresh agent immediately. JarvisClient performs `open → await ready → execute project script → capture result → close created tab`, using the same extension validation as the separate tools. The page JavaScript **does not** need privileged window/tab APIs and cannot choose or close arbitrary browser tabs.

The client distinguishes `notSubmitted`, `submitted`, and `outcomeUnknown` **when the project script exposes those semantics**; for a generic script, use completed/failed/uncertain execution results without pretending it submitted ChatGPT content. After confirmed completion, close the newly created tab when `closeTabAfterExecution` is true. On a definitive pre-dispatch failure, cleanup is safe; after an ambiguous send, preserve the tab for inspection unless an explicit separate close is requested. Report script outcome and cleanup outcome separately. Never automatically repeat a possible page-side effect.

`closeTabAfterExecution` defaults **false** for compatibility with existing generic operations, but the ChatGPT launcher supplies **true**. A successful submission is **not** a completed Jarvis task.

The delayed scheduler's due-time callback should reuse the same underlying open/execute/close service rather than reimplementing a second browser launcher. `ScheduleBrowserExecution` gains `windowRegistration` and optional `closeTabAfterExecution`; new schedules must name an authorized window. Existing persisted schedules without a window target must surface a clear reconfiguration-needed state, not silently select the active window.

### BrowserExecuteFile (primary)

```json
{
  "projectName": "Userscripts",
  "scriptPath": "chatgpt/sendMessage.js",
  "tabRegistration": "agent-7",
  "args": { "message": "Check your current Jarvis assignment." },
  "timeoutMs": 15000,
  "deviceId": null
}
```

Resolve `projectName` and `scriptPath` through the existing Jarvis workspace/project/worktree resolution and symlink confinement, including selectors like `Userscripts::feature-x`. Only a **project-relative regular UTF-8 `.js` file** is permitted: reject absolute paths, traversal, symlink escapes, unavailable projects, directories, invalid UTF-8, or exceeded size limits. No dedicated script path conventions.

Read an immutable snapshot for each call, hash it (SHA-256 for traceability), and send source text plus arguments; the extension receives **no filesystem path it can resolve itself**. Re-read on each invocation so an agent's edits take effect. A script does not have to be committed, although Git is encouraged.

Define the JavaScript file contract as a self-contained entrypoint:

```javascript
async function execute(args) {
  return { title: document.title, requested: args.action };
}
```

The extension invokes `execute(args)` in the authorized tab using a controlled wrapper, awaiting its result. Arguments are JSON values serialized safely into that wrapper, never string-concatenated from unescaped user input. The wrapper validates that an entrypoint exists and returns only a JSON-compatible value. No ESM `import` or `require` resolution is performed by Jarvis in the MVP; reusable helpers can be built/bundled as part of the ordinary project build. Every execution is a new injection, not a permanent function installed in the tab.

### BrowserExecuteJs (optional exploratory tool)

```json
{
  "tabRegistration": "agent-7",
  "code": "document.title",
  "deviceId": null
}
```

Uses the same pairing, registration, script execution, permission, and resource limits as file-based execution. Does not expose a bypass around registration.

### Common result

```json
{
  "executionId": "opaque-request-id",
  "registrationId": "opaque-id",
  "status": "completed",
  "result": { "title": "Example", "requested": "inspect" },
  "durationMs": 42,
  "scriptSha256": "sha256:...",
  "mayHaveAppliedSideEffects": true
}
```

Result types include success, rejected, executionError, unavailable, timeout, canceled, and **outcomeUnknown** after a transport failure occurring after dispatch. An explicit `null` is distinct from an unsupported/non-JSON return. Never return a silently truncated result.

## 8. Local WebSocket wire contract

JarvisClient hosts a versioned WebSocket endpoint on `127.0.0.1` only (proposed configurable port **43822**, disabled by default). The extension connects outward to the listener. Both directions support asynchronous request/reply and events.

Message envelope (illustrative; the protocol implementation will publish the final JSON schema):

```json
{
  "version": 1,
  "kind": "executeJs",
  "requestId": "req-42",
  "connectionId": "connection-epoch",
  "registrationId": "opaque-id",
  "registrationGeneration": 7,
  "payload": {
    "source": "async function execute(args) { return document.title; }",
    "args": {},
    "timeoutMs": 15000
  }
}
```

Responses use the same `requestId`, `connectionId` and registration generation with either `result` or structured `error`. Define these message kinds:

- Handshake: `hello`, `challenge`, `authenticate`, `ready`, `protocolError`.
- Registry: `registrations` snapshot, `tabRegistered`, `tabUpdated`, `tabUnregistered`; **new** `windows` snapshot, `windowRegistered`, `windowUpdated`, `windowUnregistered`.
- Invocation: `executeJs`, `openTab`, **new** `closeTab`, `result`, `error`, `cancel`. New-tab creation carries the opaque window registration and is revalidated in the extension. The generic composite run is orchestrated by JarvisClient, not a new page-executed script privilege.
- Connection: `ping` and `pong`.

All frames carry an explicit version and bounded length. A new authenticated connection gets a new connection epoch. The client drops responses with a mismatched epoch or unknown request ID, rejects duplicate in-flight IDs, and closes/clears pending operations on disconnect.

Default proposed limits (final values can be tuned with evidence):

| Resource | Initial limit |
| --- | ---: |
| Script source (UTF-8 bytes) | 64 KiB |
| Serialized arguments | 64 KiB |
| Serialized result | 256 KiB |
| Default execution timeout | 15 s |
| Maximum execution timeout | 30 s |
| Concurrent executions per extension | 4 |
| Bridge listener | Loopback only, disabled by default |

Also bound the full frame/envelope and existing MCP result ceiling, and enforce limits before buffering arbitrarily large frames. No silent truncation. Cancellation is best-effort after injection; it cannot undo page effects.

## 9. Authentication, authorization, and privacy

### Transport pairing

Loopback binding is not authentication: local websites or untrusted local processes may attempt to connect. Reject unexpected WebSocket `Origin` and ensure the extension identity matches the explicitly paired installation, but do **not** rely on `Origin` alone (non-browser clients can forge it).

A local JarvisClient pairing action displays a one-time, short-lived user-mediated challenge for the extension. Complete a challenge-response and establish a cryptographically strong, per-paired-extension secret stored in appropriate per-user local storage; subsequent connections authenticate with fresh nonces, not a reusable credential embedded in a URL. Rate-limit failures, enforce expiry, and support revocation. Never place pairing secrets in MCP results, activity, command arguments, source control, page code, or logs.

### Enforcement layers

1. **MCP caller:** authenticated via existing OAuth, routed only to its selected Jarvis device. Define browser-list/inspect and browser-execute permissions (recommended distinct OAuth scopes `browser:read` and `browser:execute`; coordinate Auth0/API registration and deployment). `BrowserExecuteFile` additionally needs project `workspace:read`.
2. **JarvisClient:** existing local trust/confirmation policy applies; executing JS is classified as an effectful operation, not read-only. In `partial` mode it should require local approval unless an explicit scoped session grant has been issued; `full` may suppress Jarvis prompts but cannot bypass paired-extension and registered-tab checks.
3. **Extension:** user-initiated pairing + explicit **window registration** for new tabs (or individual tab registration for existing tabs) + live browser host permission + exact window/tab identity and URL/origin checks. No remote agent can create window registrations, adopt existing tabs, change registry membership, or grant site access through MCP.

Scripts can read and change anything available to their page's permitted DOM context, potentially submitting authenticated actions and exposing sensitive data. Isolated execution worlds do **not** make arbitrary JS safe. Treat scripts as trusted local executable code, do not run project files from untrusted sources without review, and avoid giving them privileged extension APIs. Page-derived results are untrusted content; the agent must not treat page text as higher-priority instructions.

Do not persist script source, arguments, DOM output, returned page content, full sensitive URLs, or pairing secrets in normal telemetry. Log only metadata such as command, device, safe registration alias/ID, script project/path/hash when appropriate, duration, outcome, and error category. Update `docs/privacy.md` for this data flow before deployment.

## 10. Idempotency, errors and recovery

A general-purpose browser script may send a message, purchase an item, submit a form, or mutate page state. It is **not idempotent by default**.

- Never automatically replay browser script execution after timeout, disconnect, extension restart, or agent retry.
- Distinguish **not dispatched** from **dispatched but outcome unknown**. Even canceled or timed-out scripts might already have affected the page.
- Return `executionId` and enough metadata for agents to inspect the page and reconcile their application-level state before retrying.
- Do not create a server-side queue of deferred browser mutations. Future event-driven orchestration may call the browser tool, but Jarvis does not schedule a hidden replay.
- If the browser tab is frozen/discarded, return a typed unavailable result; do not report “delivered” or “completed.”
- The extension reports successful JavaScript evaluation, **not** proof that a remote web application's business action was committed. App-specific scripts own assertions and completion checks.

Define stable error codes including:
`BridgeDisabled`, `ExtensionDisconnected`, `PairingRequired`, `ScopeDenied`, `LocalConfirmationRequired`, **`WindowNotRegistered`, `WindowUnavailable`, `WindowRegistrationStale`, `WindowAmbiguous`**, `TabNotRegistered`, `AliasAmbiguous`, `RegistrationStale`, `NavigationMismatch`, `BrowserPermissionMissing`, `TabFrozen`, `TabDiscarded`, `TabMissing`, `ScriptNotFound`, `UnsafePath`, `InvalidUtf8`, `InvalidScript`, `ResultNotJson`, `InputTooLarge`, `OutputTooLarge`, `ExecutionError`, `Timeout`, `Canceled`, and `OutcomeUnknown`.

## 11. Motivating orchestration workflow (generic bridge, ChatGPT-specific project script)

An orchestrator can use existing Jarvis tools to inspect the agent queue and worktrees, review results, integrate branches, and issue new assignments. For a worker resumption, it calls **`BrowserRunInNewTab` once**, supplying a user-registered browser-window handle, the ChatGPT URL, a project-owned UI script path, and a bootstrap prompt identifying the logical worker. JarvisClient opens the tab **in that registered window**, executes the script, records the launch outcome and closes the newly created tab. The new ChatGPT session reconstructs its task from Jarvis. Existing tabs are untouched, and no scheduled job is necessary for an immediate handoff.

For example, a project script `chatgpt/launchAgent.js` may inspect the app's editor, enter the prompt, click Send once, and verify submission. The script can be version-controlled and revised when the UI changes. The existing generic `BrowserExecuteFile` still works against separately manually registered tabs for other applications. A **delayed** worker continuation or hourly orchestrator may invoke the very same generic composite service through the scheduler. See [ChatGPT Ephemeral Agent Launcher](ChatGPTAgentLauncher.md).

A generic return value of `submitted: true` is **not** proof that the ChatGPT agent actually finished its turn or accepted its new task. The orchestrator must distinguish browser dispatch, visible message submission, agent response, and repository-backed task progress. Agent task ownership and assignment revisions continue to come from Jarvis's existing task APIs. No attempt is made to impersonate ChatGPT's internal session state or use an undocumented ChatGPT backend API.

## 12. Acceptance criteria

### Security and registration

- Fresh installation exposes no tabs; pairing by itself exposes no tabs.
- Agent tab creation is disabled by default; a **user-registered window**, destination browser host permission and separate explicit new-tab opt-in are required. An agent can never register a pre-existing tab.
- Only a user gesture in the extension can register, rename, reauthorize or unregister a **window** or a manually registered tab. Window registration does not expose existing tabs.
- Unregistered, renamed-away, wrong-device, ambiguous-alias, navigated, closed, and browser-restored-without-proof targets never execute.
- Browser host permission does not register existing tabs or authorize a window. The auto-created tab's own registration is derived only from creation in a still-registered window; manually registered tabs still require explicit tab registration.
- Invalid extension origin, bad pairing credentials, missing OAuth scope, or denied local permission are rejected; `--trust full` cannot manufacture a registration.
- Revoking a registration blocks subsequent calls, including a delayed reply from an earlier generation.

### Execution and lifecycle

- A registered loaded background tab executes without being focused.
- A new tab opens **inside the selected registered window** in the background by default, receives a distinct auto-registration, remains usable across same-origin SPA navigation, and becomes unavailable on cross-origin navigation, window revocation/move, or opt-in revocation.
- Revoking/closing a window or restarting the browser cannot silently retarget or restore a window registration by matching its name, last-used URL or numeric ID.
- The generic `BrowserRunInNewTab` performs open/execute/optional close in one caller-visible operation; closing only the new tab releases capacity. Never close a window or its existing tabs.
- A registered frozen/discarded tab returns the correct availability error without a default wake-up.
- Reloading the same tab/URL and restarting the MV3 service worker preserve valid registration; browser restart with uncertain tab identity does not silently rebind.
- `BrowserExecuteFile` reads scripts from an ordinary project and from a registered Git worktree, without extra registration or relocation.
- Script changes are read on the next invocation; invalid/missing/escaping/oversized files fail before injection.
- Self-contained async `execute(args)` is awaited and its JSON result returned intact, respecting output budgets.
- Concurrent/out-of-order responses, disconnects, timeouts, cancellation, duplicate IDs and multiple devices cannot cross-route results.
- An ambiguous post-dispatch failure is surfaced as `OutcomeUnknown`, without replay.

### Tests and release

- Headless client/server tests include a **real loopback WebSocket** simulated-extension implementation, covering pairing, registry, execution, and adverse lifecycles.
- Browser integration tests run in a real supported Chromium build against controllable pages and verify background execution, SPA navigation, reload, freeze/discard, and reauthorization.
- Run focused client/common/server tests and solution build; review permissions, dependency footprint, telemetry and threat model before deployment.
- Update public MCP capability metadata, browser extension setup instructions, privacy docs and version negotiation; only then release compatible server/client/extension versions.

## 13. Implementation phases

1. **Implemented on branch (not deployed):** generic MCP bridge, pairing, manual tab registry, registered browser windows, project-file execution, window-targeted agent tabs, safe tab close, immediate open/execute/close workflow and one-shot/recurring scheduler. See runtime extension README for current operational commands.
2. **Implemented window-target work:** extension Register/Unregister window UI and local window registry, site host-permission UI, `BrowserListWindows`, `BrowserOpenTab(windowRegistration, ...)`, generation/window checks, and `BrowserCloseTab` with agent-owned-only enforcement.
3. **Implemented immediate workflow:** `BrowserRunInNewTab`, shared open/execute/close service, launch outcome and cleanup reporting; scheduled execution takes `windowRegistration` and uses that same service.
4. **Project UI script implemented; live test pending:** ChatGPT launcher at `browser-scripts/chatgpt/launchAgent.js` and Node DOM tests. Real Edge open → submit → close remains an acceptance requirement.
5. **Recurrence implemented; other conveniences deferred:** fixed-interval scheduler with skipped missed occurrences is implemented. Wake policy and screenshots remain deferred. No ChatGPT-specific server logic.
6. **Review and release separately** once verified. No deployment or merge is implied by the spec.

## One-shot delayed browser execution

In addition to direct browser commands, JarvisClient implements a generic one-shot scheduler. ScheduleBrowserExecution records a future URL, ordinary project script path, JSON arguments and runAfterSeconds delay in private user-level storage. When due, it opens a newly authorized tab and runs the current project file. Pending actions can be listed/canceled/replaced by ID. A claimed action is not automatically replayed after client restart or browser failure. An orchestrator may use this to create its next ephemeral session, but no special orchestrator role, lease, watchdog, or recurrence exists in Jarvis. See [BrowserOneShotScheduler.md](BrowserOneShotScheduler.md).


### Registered-window launch workflow (new target)

The target behavior for starting a fresh logical agent is **one immediate composite call**, not a chain of agent-visible browser tool calls:

```text
Orchestrator -> BrowserRunInNewTab(windowRegistration, URL, JS file, bootstrap args)
               -> extension opens a new tab in that registered window
               -> client executes the project-owned ChatGPT DOM script
               -> extension closes only the newly created tab
               -> new ChatGPT conversation continues server-side
```

The registered window is a destination, **not permission to inspect every tab inside it**. A new tab receives its own origin-bound auto-registration; pre-existing tabs remain unregistered. A separate app-specific script may automate ChatGPT, while the bridge stays generic. If the caller wants a delayed launch, the current one-shot scheduler and planned recurring scheduling call the same open/execute/close service. See [ChatGPTAgentLauncher.md](ChatGPTAgentLauncher.md). Window registration, tab closure, and this composite operation are implemented on the feature branch; live Edge verification and deployment are pending.

## 14. External API references

- [Chrome userScripts API](https://developer.chrome.com/docs/extensions/reference/api/userScripts) — arbitrary user-supplied scripts, execution world, and Chrome 138+ user setting.
- [Chrome extension service-worker lifecycle](https://developer.chrome.com/docs/extensions/develop/concepts/service-workers/lifecycle) — nonpersistent workers and WebSocket lifetime handling.
- [Chrome tabs API](https://developer.chrome.com/docs/extensions/reference/api/tabs) — tab identities, frozen/discarded states and lifecycle fields.
- [Chrome permissions API](https://developer.chrome.com/docs/extensions/reference/api/permissions) — optional host access and user gestures.
- [Chrome webNavigation API](https://developer.chrome.com/docs/extensions/reference/api/webNavigation) — navigation/History API events and document identity.
