# Jarvis Browser Bridge — System Specification

**Status:** Proposed system design (not implemented)  
**Scope:** Jarvis server, JarvisClient, Chromium extension, and project-owned JavaScript scripts  
**Initial implementation worktree:** `Jarvis::browser-bridge` (`feature/browser-bridge`)  
**Related task:** [Local browser bridge and MCP command routing](tasks/2026-10-26-browser-bridge-jarvis.html)

## 1. Purpose

Provide an authenticated Jarvis agent with a general-purpose way to inspect and manipulate **browser tabs manually registered by the user or newly created under an explicit extension opt-in**. The agent can execute JavaScript sourced from normal files in normal Jarvis projects, optionally passing JSON arguments and receiving a bounded result.

The browser bridge has **no knowledge of ChatGPT, agent identities, conversations, workflows, or task orchestration**. Those behaviors live in agent-authored project scripts and the existing Jarvis task system.

A motivating use case is an orchestrator using Jarvis's existing task tools to integrate an agent's work, then running a project script against a registered ChatGPT conversation tab to submit a continuation prompt. This is an application of the generic capability, not a built-in Jarvis workflow.

### Principles

1. **User-owned targets:** existing tabs may be registered only by the user in the extension. After a separate explicit opt-in, Jarvis may create and automatically register **new** tabs only on sites with a valid manually registered tab and browser host permission. No MCP operation can claim an arbitrary existing tab.
2. **Project-owned scripts:** scripts are regular `.js` files in Jarvis projects and Git worktrees; no Jarvis script database, dedicated script directory, or script registration.
3. **Browser-independent Jarvis API:** only the extension implements Chromium behavior. Jarvis passes typed commands and returns typed results.
4. **Explicit boundaries:** device authentication, browser-extension pairing, tab registration, and permission to execute are separate checks.
5. **Fail closed:** missing, navigated, frozen, discarded, disconnected, or ambiguously restored tabs never cause silent retargeting.
6. **No exactly-once fiction:** a timeout or disconnect can leave the result of a page action uncertain. Jarvis must never blindly replay it.

## 2. Architecture

```text
Agent / MCP caller
   |
   | BrowserListTabs / BrowserOpenTab / BrowserExecuteFile / BrowserExecuteJs
   v
Jarvis server (OAuth scopes, device routing, SignalR)
   |
   v
JarvisClient on selected device (F# / .NET)
   |  resolves project-relative scripts; checks local policy
   |  hosts authenticated WebSocket on 127.0.0.1
   v
Paired Chromium extension (Manifest V3 service worker)
   |  owns user-managed tab registry and browser permissions
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
| Extension service worker | Browser connection, user tab registry, URL/permission validation, script injection, tab lifecycle handling | Read project files, accept arbitrary registration from Jarvis, grant access to unregistered tabs |
| Extension UI | Pairing, register/rename/unregister tab, show availability and connection, revoke access | Automatically opt tabs into control without a user gesture |
| Jarvis project | JavaScript files, optional helper/build system, Git history, app-specific logic | Require changes to Jarvis or extension to target a new website |

The browser and its JarvisClient must be running on the **same machine**: the browser extension connects to that client over loopback, never to the public Jarvis server. Multiple devices may host independent clients and browser instances. Existing Jarvis `deviceId` selection applies. No mutable global “current tab” or “current agent conversation” exists at server level.

## 3. User experience

### Set up

1. Enable the browser bridge in JarvisClient's **user-level configuration**. Its listener is disabled by default.
2. Install the extension in a supported Chromium browser. Enable Chrome's required **Allow User Scripts** setting.
3. Pair that extension with the local JarvisClient through a user-approved pairing flow.
4. In the target tab, click the extension and choose **Register tab**, optionally supplying a stable alias such as `agent-7`.
5. Use Jarvis's browser tools with that alias or the returned opaque registration ID.
6. **Optional:** enable **Allow Jarvis to open new tabs** in the extension. This authorizes `BrowserOpenTab` only on the origins of existing, valid manually registered tabs. New tabs are registered automatically with a distinct agent-created provenance; the default is a background tab and at most 20 active agent-created tabs. Switching the option off invalidates auto-created registrations, but does not close their tabs.

Example extension registry:

| Alias | Bound page | Availability |
| --- | --- | --- |
| `orchestrator` | One explicitly selected conversation URL | ready |
| `agent-7` | Another selected conversation URL | background/ready |
| `agent-8` | Another selected conversation URL | discarded |
| `reference` | One selected documentation URL | needsReauthorization |

An alias is unique within the selected device's currently paired browser registrations; duplicate aliases are rejected or exposed as an explicit ambiguity error, never resolved by guesswork. Internally the stable opaque registration ID is preferred.

The extension UI shows the registered target, URL binding, whether browser permission exists, connection/paired state, and an obvious **Unregister** action. A registered tab can be manipulated while not selected, provided its JavaScript context is usable.

### Non-goals

No ChatGPT-specific MCP tools; no automatic discovery of ChatGPT agents; no unattended registration of all tabs; no remote browser process management; no CDP debugger; no general network interception; no file access from browser scripts; no headless Chromium service; and no guarantee that a web application will process requests in a background tab.

## 4. Manual registration and identity

Registration is an **explicit consent record**. Existing tabs can only be registered by a user gesture in the extension. The second, opt-in route permits Jarvis to **create** a new tab on an already manually approved origin and automatically register that newly created tab, without claiming any pre-existing tab. It is not a bookmark, an all-sites allowlist, or an inferred match. Pairing the extension alone does not register or create tabs.

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

- **Manually registered** tabs are bound to their exact page URL. **Agent-created** tabs are bound to the created tab ID and approved origin, so SPA navigation and assignment of a new conversation URL within that origin remain usable. Cross-origin navigation invalidates the registration.
- Reloading that URL in the same tab preserves the registration, but a new document must be rechecked before injection.
- A same-origin SPA route change, query change, or fragment change that changes the bound URL suspends execution until the user explicitly updates the binding. Watch `tabs.onUpdated`, `webNavigation` (including History API navigation), and inspect the target immediately before injection.
- Do not silently transfer a binding to a newly opened tab with the same URL or the same title.
- An extension service-worker restart is not a new browser session. Restore and validate existing registrations against the live tab IDs/URLs.
- Browser restart and session restoration require stronger continuity evidence than a matching URL alone. If continuity cannot be proven, retain the registration as **needsReauthorization**, and require the user to rebind it.
- Closing a tab, explicitly unregistering it, or losing the required browser permission must immediately prevent further execution; an in-flight operation must be canceled where possible and reported as indeterminate if cancellation cannot undo an already dispatched page effect.
- Registry data remains local to the extension; JarvisClient caches the connected extension's advertised registry only for its connection lifetime.
- The extension revalidates the registration's generation, tab ID, URL, required browser permission, and current document immediately before every execution. The client also validates the opaque registration against the current advertised catalog. Neither check replaces the other.

Never accept a script-supplied registration event as equivalent to the user clicking **Register tab**. Disallow `chrome://`, extension pages, privileged schemes, incognito/private contexts, and other restricted targets by default.

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

The extension requires `userScripts`, tab inspection/lifecycle capabilities, and host access for pages registered by the user. Favor permissions requested during registration for a specific site/origin instead of broad permanent all-sites host access. Browser host permissions are generally **origin-wide**, not per-tab: the extension's own per-tab registry provides the stricter authorization. A registration requires both browser permission and extension-level user consent. `activeTab` access alone should not be assumed sufficient for durable background automation.

The extension popup/options view supports:

- Pair/disconnect and diagnostic connection state.
- Register the currently selected tab and choose/rename a friendly alias.
- List registered tabs with availability and permission state.
- Unregister or reauthorize a tab, with immediate invalidation of outstanding handles.
- Display errors for disabled user scripts, missing host permission, unsupported pages, or disconnected JarvisClient.

Do not rely on unrestricted page-level `eval` or dynamically injecting scripts through `chrome.scripting.executeScript({func})` to handle arbitrary script source. The `userScripts` API is designed for user-provided code.

## 7. MCP command surface

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
  "url": "https://example.test/chat/new",
  "alias": "new-agent",
  "active": false,
  "deviceId": null
}
```

Creates a **new browser tab**, not a claim on any existing unregistered tab. This uses Chromium/Edge's `chrome.tabs.create({url, active})`, with `active=false` by default. It requires the paired extension's separate **Allow Jarvis to open new tabs** setting, an existing valid *manually registered* tab on the destination origin, and existing browser host permission for that site. The extension enforces a 20-active-agent-tab limit. Requests for non-HTTP(S) URLs, embedded credentials, ambiguous aliases, unapproved origins, or restricted pages fail before tab creation.

The extension automatically registers the newly created tab and returns a unique `registrationId`, alias, origin and availability, usually `loading`. Agents can poll `BrowserListTabs` until `ready`, then invoke `BrowserExecuteFile` or `BrowserExecuteJs`. The new tab remains origin-bound across same-origin page changes; manual registrations keep the stricter exact-URL binding. The extension labels agent-created registrations distinctly and allows the user to unregister them. Disabling the opt-in invalidates their access but does not close the tabs.

This is a side-effecting, non-idempotent operation: a network failure after creation can leave a new tab open even if the client did not receive its registration ID. **Do not automatically retry** when the result is uncertain. MCP authorization uses `process:execute` and the local execution confirmation policy; extension consent remains mandatory even under full trust.

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
- Registry: `registrations` snapshot, `tabRegistered`, `tabUpdated`, `tabUnregistered`.
- Invocation: `executeJs`, `result`, `error`, `cancel`.
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
3. **Extension:** user-initiated pairing + explicit tab registration + live browser host permission + exact target URL/identity validation on every invocation. No remote agent can change registry membership or broaden its page scope through MCP.

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
`BridgeDisabled`, `ExtensionDisconnected`, `PairingRequired`, `ScopeDenied`, `LocalConfirmationRequired`, `TabNotRegistered`, `AliasAmbiguous`, `RegistrationStale`, `NavigationMismatch`, `BrowserPermissionMissing`, `TabFrozen`, `TabDiscarded`, `TabMissing`, `ScriptNotFound`, `UnsafePath`, `InvalidUtf8`, `InvalidScript`, `ResultNotJson`, `InputTooLarge`, `OutputTooLarge`, `ExecutionError`, `Timeout`, `Canceled`, and `OutcomeUnknown`.

## 11. Motivating orchestration workflow (not Jarvis functionality)

An orchestrator can use existing Jarvis tools to inspect the agent queue and worktrees, review results, integrate branches, and issue new revisioned assignments. It can then choose a manually registered tab, or (if the user explicitly enabled agent-created tabs) use `BrowserOpenTab` to start a fresh same-origin conversation and receive its new registration ID. It calls a project script through `BrowserExecuteFile` to automate the selected application.

For example, a project script `chatgpt/sendMessage.js` may inspect the app's editor, send a message, and verify it is present in the conversation. Another project script could read conversation state or detect whether a response is complete. The scripts can be version controlled, revised when the web UI changes, and run independently from Jarvis's generic infrastructure.

A generic return value of `submitted: true` is **not** proof that the ChatGPT agent actually finished its turn or accepted its new task. The orchestrator must distinguish browser dispatch, visible message submission, agent response, and repository-backed task progress. Agent task ownership and assignment revisions continue to come from Jarvis's existing task APIs. No attempt is made to impersonate ChatGPT's internal session state or use an undocumented ChatGPT backend API.

## 12. Acceptance criteria

### Security and registration

- Fresh installation exposes no tabs; pairing by itself exposes no tabs.
- Agent tab creation is disabled by default; manual origin registration, browser host permission and separate explicit opt-in are required. An agent can never register a pre-existing tab.
- Only a user click in the extension can register, rename, reauthorize, or unregister a tab.
- Unregistered, renamed-away, wrong-device, ambiguous-alias, navigated, closed, and browser-restored-without-proof targets never execute.
- Browser host permission does not supersede per-tab registration.
- Invalid extension origin, bad pairing credentials, missing OAuth scope, or denied local permission are rejected; `--trust full` cannot manufacture a registration.
- Revoking a registration blocks subsequent calls, including a delayed reply from an earlier generation.

### Execution and lifecycle

- A registered loaded background tab executes without being focused.
- A new tab opens in the background by default, receives a distinct registration ID, remains usable across same-origin SPA navigation, and becomes unavailable on cross-origin navigation or when the opt-in is revoked.
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

1. **Jarvis foundation (current task):** shared commands, scoped MCP routes, local loopback transport and pairing, project-file execution, registry-aware mock extension, tests, schema and diagnostics. No real browser extension is required to complete this stage.
2. **Chromium extension:** Manifest V3 UI, permissions, persistent manual registrations, connection/pairing, lifecycle handling, actual `chrome.userScripts.execute`, real-browser tests.
3. **Integrated acceptance:** run both parts against registered real browser tabs, verify security and lifecycle cases, then coordinate deployment/release. No automated execution against unregistered tabs.
4. **Optional convenience:** explicit wake policy, DOM/screenshot/wait operations, project-side script packaging, Firefox adapter, richer browser diagnostics. All remain generic; no ChatGPT-specific server logic.

## 14. External API references

- [Chrome userScripts API](https://developer.chrome.com/docs/extensions/reference/api/userScripts) — arbitrary user-supplied scripts, execution world, and Chrome 138+ user setting.
- [Chrome extension service-worker lifecycle](https://developer.chrome.com/docs/extensions/develop/concepts/service-workers/lifecycle) — nonpersistent workers and WebSocket lifetime handling.
- [Chrome tabs API](https://developer.chrome.com/docs/extensions/reference/api/tabs) — tab identities, frozen/discarded states and lifecycle fields.
- [Chrome permissions API](https://developer.chrome.com/docs/extensions/reference/api/permissions) — optional host access and user gestures.
- [Chrome webNavigation API](https://developer.chrome.com/docs/extensions/reference/api/webNavigation) — navigation/History API events and document identity.
