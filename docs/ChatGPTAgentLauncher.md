# ChatGPT Ephemeral Agent Launcher — Design Specification

**Status:** Browser Bridge deployed and live-tested with Edge/ChatGPT on 2026-11-13; one-command readiness and acknowledgement reliability accepted, integrated and live-tested. Thinking-effort enforcement is a separate independently reviewed addition.
**Implementation:** `browser-scripts/chatgpt/launchAgent.js` and `Client/Modules/BrowserBridge.fs`
**Depends on:** [Browser bridge](BrowserBridgeSystem.md), [implemented one-shot scheduler](BrowserOneShotScheduler.md)
**Scope:** User-registered browser-window targets, the implemented immediate open/execute/optional-close operation, project-owned ChatGPT UI automation, and optional scheduling.

## 1. Objective

Use an authenticated Edge/Chromium tab as a **short-lived launcher** for fresh ChatGPT agent conversations. The user explicitly registers a **browser window** in the extension. An orchestrator invokes one generic Jarvis browser-script workflow, or a JarvisClient schedule invokes that same workflow: it creates a tab **in the registered window**, navigates to the chosen ChatGPT entry URL, enters and submits a bootstrap prompt through the real web UI, captures the resulting conversation URL, and closes only that created tab. ChatGPT execution is expected to continue server-side after tab closure; no browser polling for the agent's later progress is required.

The logical worker/orchestrator is durable in **Jarvis tasks, assignments, Git history, specifications, and checkpoints**, not in any particular ChatGPT conversation. Fresh sessions reconstruct that ground truth, minimizing drift and dependence on long-running conversations. The browser is a launcher and optional diagnostic viewer, not the runtime, orchestrator, or task database.

This is primarily a **project-owned script** on the generic browser bridge, not a ChatGPT-specific MCP tool or ChatGPT API client.

### Invariants

1. **UI only:** interact with the DOM and normal user interface; do not send requests to internal ChatGPT APIs, forge `conversation`/`prepare` messages, or depend on private network payload schemas.
2. **One submission per launch:** after the script may have clicked Send, failures/timeouts are outcome-uncertain; never blindly replay a prompt or retry a launch with a new tab.
3. **Disposable tabs:** close only tabs created for this launch by Jarvis, never user-registered or unrelated tabs. Closing a tab is not a request to cancel the running ChatGPT conversation.
4. **Separation of responsibilities:** the orchestrator chooses the logical agent and calls one generic immediate launcher; the optional scheduler chooses **when** to call the same launcher; the browser extension owns registered windows/new-tab lifecycle; the project script knows DOM selectors and submission semantics; agents know tasks and assignments.
5. **No new agent orchestration state machine:** Jarvis remains Git/task/activity-driven. The launcher cannot infer successful task completion from successful prompt submission.
6. **Security stays layered:** ChatGPT app authorization, Jarvis MCP permissions, JarvisClient trust, extension pairing, **user window registration**, and browser host permissions are independent. Registering a window is not permission to inspect its existing tabs. No automation bypasses consent dialogs.
7. **Fresh bootstraps are self-contained:** the prompt specifies role and how to find current Jarvis truth, not a copied snapshot of potentially stale instructions.

## 2. Existing implementation and required additions

### Initial browser-bridge implementation

- Local authenticated Chromium/Edge MV3 extension paired to JarvisClient.
- Explicit manual **tab** registration and host permission for the corresponding site; separate **Allow Jarvis to open new tabs** extension opt-in. Window registration is implemented later on this branch.
- Current `BrowserOpenTab` creates an agent-owned background tab only on an origin with an existing manually registered tab. The new design **replaces that origin-anchor requirement** with an explicit window registration plus host permission; it is implemented on this branch.
- `BrowserExecuteFile` runs a real `.js` file from an ordinary Jarvis project; exported contract is `async function execute(args)`.
- `ScheduleBrowserExecution` runs an **one-shot** delayed open-tab + execute-file action; schedule persisted locally through client restarts; no automatic replay after an uncertain dispatch. `ListScheduledBrowserExecutions` and `CancelScheduledBrowserExecution` exist.
- A limit of **20 currently registered agent-created tabs** exists; without tab cleanup, frequent launches eventually hit that limit.

### Implementation deliverables (completed on this branch unless explicitly deferred)

1. A project-owned, reusable `chatgpt/launchAgent.js` UI automation script and tests/fixtures. This path is illustrative; no special Jarvis directory is required.
2. **User-registered browser windows:** extension UI for Register/Unregister window and user-approved site host permissions. New tabs are created only inside a specified live registered window; no manually registered tab on the destination origin is needed.
3. **Generic immediate workflow:** `BrowserRunInNewTab(windowRegistration, url, projectName, scriptPath, argsJson?, active?, closeTabAfterExecution?, deviceId?)` performs open → wait → execute script → close in one call, so an orchestrator doesn't have to coordinate three separate MCP operations.
4. **Agent-created tab close capability** via `BrowserCloseTab(tabRegistration, deviceId?)` and safe cleanup. Do not inject `window.close()` as a substitute.
5. A durable **launch result** with submission status, optional conversation URL, cleanup outcome, and attempt correlation, without persisting prompt contents in activity/log listings.
6. Existing one-shot scheduling gains `windowRegistration`, uses the shared immediate workflow, and optional **recurring fixed-interval schedules** may launch later. Scheduling is not needed for immediate orchestrator handoffs.
7. Documentation and regression tests spanning UI changes, window revocation/restart, duplicate dispatch, permissions, registration capacity, and live Edge.

The registered-window workflow, immediate composite operation, safe tab-close and recurrence are deployed. Live Edge testing on 2026-11-13 successfully sent one greeting and observed a response; that test also exposed a tab-loading race and false-negative submission acknowledgement, addressed by isolated regression fixes.

## 3. Architecture and ownership

```text
User: extension popup -> Register window "agents" + approve chatgpt.com host access
                                    |
Orchestrator / worker / human        |  explicit window destination
    | BrowserRunInNewTab(windowRegistration, URL, script, bootstrap args)
    | (or schedule same operation for later)
    v
Jarvis server -> JarvisClient (one generic composite workflow)
    | extension opens new tab in chosen, still-registered window
    v
New Edge/Chromium tab -> ChatGPT frontend
    | BrowserExecuteFile: project-owned ChatGPT UI launcher
    |   find composer -> insert prompt -> wait for Send -> click
    |   observe submission acknowledgement + capture conversation URL
    v
JarvisClient: record launch metadata, close **only new agent-owned tab**
    |
    +-> new ChatGPT backend conversation continues independently
              | fresh agent: invoke Jarvis, read assignment/spec/task
              v
         Task work, Git commits, checkpoints, activity
```

The **registered window** is a placement/consent target, not a registered collection of tabs. The extension must never expose preexisting tabs merely because the window was registered. **Browser host permission** independently authorizes the destination site. The currently implemented manually registered *tab* remains supported for generic inspection, but it is **not** required for new ChatGPT launches in this new design.

**Orchestrator** chooses the logical agent and performs **one immediate browser workflow call**, with no need to create a schedule for an immediate resume. **Jarvis server** stays stateless and routes existing MCP calls; no browser sessions or tasks are stored there. **JarvisClient** orchestrates open/execute/close, retries only safe readiness polls, and owns optional local schedules and sanitized results. **Extension** validates live window registration, host permissions and tab provenance. **Project-owned script** owns selectors, insertion, click, and UI confirmation. **Agent bootstrap text** owns role instructions and reconciliation with Jarvis state.

A new browser window registration is identified by opaque ID or unique alias per paired device/profile; its actual Chromium window ID and browser-session epoch are private extension state. On close/restart, it becomes unavailable until explicitly re-registered. Jarvis must never silently select another window, infer the window from an active tab, or migrate a stale schedule to a different window.

## 4. Configuration and launch request

A launch is primarily an **immediate** `BrowserRunInNewTab` call from the orchestrator, with no ChatGPT-specific MCP endpoint. The same generic service can be triggered later by `ScheduleBrowserExecution` for worker self-continuation or recurring orchestration. Both operations require a **user-registered window** and permission for the destination site.

Example **implemented immediate command**:

```json
{
  "windowRegistration": "agents",
  "url": "https://chatgpt.com/",
  "projectName": "Jarvis",
  "scriptPath": "browser-scripts/chatgpt/launchAgent.js",
  "argsJson": "{\"role\":\"worker\",\"agentId\":\"3\",\"projectName\":\"Wayfold\",\"prompt\":\"You are Worker 3. Read the latest Jarvis assignment, relevant AGENTS.md, task and spec. Follow current instructions and continue the assigned work; do not assume previous conversation state. End with the required checkpoint/Next line.\"}",
  "active": false,
  "closeTabAfterExecution": true
}
```

The payload is an *example*, not an instruction to hard-code a specific project, worker, prompt, role, gizmo ID, or account. The launcher accepts all agent-specific strings from arguments. One launch has one explicit message; arbitrary strings supplied as arguments must never be evaluated as JavaScript.

Recommended argument contract:

| Field | Required | Meaning |
| --- | --- | --- |
| `prompt` | Yes | Exact bootstrap text to submit; nonempty, bounded, may contain newlines |
| `role` / `agentId` / `projectName` | No | Caller-owned metadata for logs/correlation; **not** alternative sources of instructions |
| `timeoutMs` | No | One overall UI deadline for hydration, insertion, submission confirmation and conversation URL capture (defaults to 27 s; capped at 27 s) |
| `thinkingEffort` | No | Explicit `Instant`, `Medium`, `High`, or `Extra High`; select and verify before sending, or fail closed; omission preserves the UI default |
| `preflightOnly` | No | When `true`, exercise mode, draft and effort preflight but never insert or send; returns `notSubmitted` / `PreflightOnly` with optional verified effort |
| `debug` | No | Reserved for future diagnostics; the current script always returns only sanitized structural counts on compatibility errors |

Jarvis browser-workflow parameters, **not** ChatGPT script arguments, control `windowRegistration`, `active`, `closeTabAfterExecution`, and device selection. Only **scheduled** calls have delay and repeat interval. An orchestrator normally invokes the immediate workflow, rather than scheduling a zero-delay job.

**Fixed ChatGPT entry point:** `https://chatgpt.com/`. The ChatGPT-specific launcher does not accept alternate origins, Project URLs, GPT URLs, or existing conversation URLs. The generic bridge remains URL-agnostic, but callers of this script always supply the fixed URL. The script rejects an unexpected starting URL before typing. Explicit model selection, project navigation and alternate entry points require a separate feature if desired.

### Model and thinking effort

The launcher supports an explicit `thinkingEffort` argument: `Instant`, `Medium`, `High`, or `Extra High` (case, whitespace and hyphen variants are accepted). If omitted, the launch **does not change** the account's current effort. For unattended agents where quality matters, the orchestrator should explicitly pass `"thinkingEffort":"Extra High"` rather than rely on a persistent ChatGPT preference. The script does not select a different model; that remains a separate feature.

After selecting **Chat** and confirming an empty composer, the launcher checks the unique `button[aria-label="Select ChatGPT model"]` and its rendered "Thinking effort" label. If different from the requested value, it opens the model picker, locates the visible `[role="menuitem"][aria-label="Power"][data-reasoning-slider="true"]` slider, and uses its advertised **ArrowLeft/ArrowRight** keyboard control. The currently observed UI exposes the four slider values `0..3` in the same order as the four labels above. Each step verifies the numerical slider value **and** rendered label; an unexpected range, unavailable/locked control, changed UI, or failed transition is an explicit **pre-send** incompatibility. New effort-specific polling has shorter per-stage deadlines so a failed selection is reported before the overall bridge execution timeout. The picker is closed via Escape only if still open, because clicking its trigger after the slider auto-closes can reopen it. The requested effort is checked again immediately before Send.

The existing-draft guard runs **before any effort change** and again before insertion. No prompt is sent when a requested effort cannot be verified; there is no silent downgrade, guessed selection, private ChatGPT API call, or backend parameter forgery. On confirmed submission, `scriptResult.thinkingEffort` reports the explicitly verified label; it is omitted if the caller did not request an effort. For safe live verification without a conversation, pass `preflightOnly: true`; the script may adjust the requested effort but will not enter a prompt or click Send, returning `notSubmitted` / `PreflightOnly` and the verified `thinkingEffort`.

## 5. ChatGPT UI automation contract

### 5.1 Tab readiness

- Open a **new** agent-created tab **in the specified user-registered browser window** on the separately approved `chatgpt.com` origin. Do not claim, enumerate, or alter existing unregistered tabs.
- Await a ready registration/document; the extension rechecks current origin and identity before execution. A subsequent explicit `TabUnavailable:loading` from the extension's **pre-injection check** is safe to retry within the 30-second readiness budget. Never retry an uncertain or post-injection failure.
- The script must wait for React/ProseMirror hydration and composer availability with a bounded deadline; merely finding a DOM node before hydration is insufficient.
- Support background tabs by default, but test behavior under Chromium background throttling. Configurable `active=true` exists for diagnostics or an identified site limitation; don't steal focus unconditionally.
- Reject ChatGPT login, challenge, error, suspended account, wrong project/GPT, permission prompts, or otherwise blocked UI states when detectable. Report actionable categories; do not attempt to bypass a challenge.

### 5.2 Chat/Work home-mode selection

The `https://chatgpt.com/` homepage may remember **Work** as its selected mode. Before interacting with the composer, locate the unique `button[type="button"][aria-pressed]` buttons whose visible text is exactly **Chat** and **Work**. If Chat is not pressed, click Chat once and wait within the existing overall UI deadline for Chat `aria-pressed="true"` and Work `aria-pressed="false"`. Do not depend on generated CSS classes or button order. If the controls are missing or the transition does not complete, return `notSubmitted` without entering the prompt. If Chat is already selected, do not click it.

### 5.3 Stable element selection

Prefer accessible/semantic DOM attributes over generated CSS class names.

Known composer structure observed in Edge:

```html
<div data-composer-input-layout="single-line">
  <div contenteditable="true" role="textbox"
       aria-label="Ask ChatGPT" class="ProseMirror"></div>
</div>
```

Use a selector equivalent to:

```js
'[data-composer-input-layout] [contenteditable="true"][role="textbox"]'
```

Avoid generated classes such as `RichTextInput-*` and `composer-*`.

Known submit control, **only when the composer is populated**:

```html
<button type="submit" aria-label="Send">...</button>
```

Use:

```js
'button[type="submit"][aria-label="Send"]:not(:disabled)'
```

**Important observed behavior:** ChatGPT changes the composer action from **Start voice** (empty) to **Send** (nonempty). The launcher must look up the Send element **after insertion** and wait for it; it must not cache or click the voice control. The presence and enabled state of Send provide evidence that the frontend recognized editable content.

Allow narrowly scoped, ordered fallback selectors only when empirically verified and covered by tests. An unexpected DOM must fail with `ComposerUnavailable` or `SendUnavailable`, not guess a button.

### 5.3 Text insertion through ProseMirror

- Focus the editable element.
- Use normal editing behavior, initially `document.execCommand("insertText", false, prompt)`, not direct assignment to `textContent`, `innerHTML` or a synthetic input event alone. The deprecated `execCommand` remains useful in contenteditable and must be tested against ChatGPT's editor state.
- Verify exact resulting editor text, accounting for newline/paragraph representation rather than merely checking `includes` on an arbitrary HTML node.
- Require the composer to be empty for a fresh launch; do **not** append to unexpected draft text or overwrite a preexisting draft silently. A fresh tab should normally have an empty composer, but ChatGPT's `prepare`/draft restoration behavior makes this a real condition.
- After insertion, re-query Send until it is present and enabled. A transient disabled state should be polled within the deadline.
- Do not log or print prompt text or draft DOM by default. Unexpected edits/autocomplete should fail verification.
- Multi-paragraph prompts, Unicode, quotation marks, Markdown, and long inputs within documented size limits require explicit tests.

### 5.4 Submit exactly once

- Click the **current** Send button using the page UI. Do not synthesize HTTP requests or manually generate ChatGPT conversation IDs/message IDs.
- Maintain an in-script `submitAttempted` flag. Once Send may have been clicked, no retries of Send, alternate selector clicks, or fresh-tab relaunches are permitted for that attempt.
- Readiness polls are safe to retry; any possible message dispatch is not.
- Don't submit by synthesized Enter, which can be interpreted as a newline or rejected depending on editor/event semantics.
- If the script cannot determine whether the click was dispatched or accepted, return an **uncertain submission** result instead of `notSubmitted`.

### 5.5 Launch acknowledgement and conversation URL

After the click, wait within the **single overall UI deadline** for positive evidence that the turn was accepted. Current ChatGPT user turns are marked by `[data-user-message-bubble]` (the old `[data-message-author-role="user"]` may not exist). Match the newly added bubble's rendered text against the submitted prompt; do not infer success from a sidebar title or URL alone. This remains valid when model generation has already finished. Examples to verify in live Edge:
- A new ChatGPT conversation route becomes visible in `location.href`, including project/GPT-specific route shapes (not exclusively `/c/<id>`).
- A new user-message turn appears in the conversation and the composer has transitioned to a submitted/empty or generating state.
- Other observable stable UI transition proven to indicate submission, not merely a button click.

A changing URL alone can mean routing without submission, so route change is **not** proof unless accompanied by an appropriate post-submit state. Similarly, the editor clearing alone should not be treated as the only proof. The implementation should define an evidence precedence and test it against live ChatGPT.

After submission evidence, **continue waiting for the actual server-issued `/c/<conversation-UUID>` URL** before returning `submitted` or allowing automatic tab closure. ChatGPT can first navigate to an optimistic `/c/local-chatgpt:...` route (percent-encoded in the address bar); that temporary route is **not** proof of a durable conversation. An acknowledged user turn without a stable route is `outcomeUnknown` (`ConversationUrlUnavailable`) and retains the tab; never invent a URL or resubmit. On success return:

```json
{
  "status": "submitted",
  "conversationUrl": "https://chatgpt.com/c/illustrative-id",
  "evidence": ["newUserTurn", "matchingUserBubble", "conversationRoute"],
  "submittedAtUtc": "2026-10-30T00:00:00Z"
}
```

The URL above is illustrative; use the actual observed route without constructing one. A valid same-origin URL is recorded for later human reopening; strip fragments, credentials, and sensitive query parameters when storing or displaying it. If submission appears confirmed but no stable conversation URL is available before the overall deadline, return `outcomeUnknown` and retain the created tab for inspection. Do not turn this into a second submission.

If success cannot be proven after a possible click, return `outcomeUnknown` with sanitized evidence/error; never silently classify as a definitive failure.

### 5.6 Timeouts and result boundary

The ChatGPT UI script uses one deadline across all UI phases (27 s default, capped at 27 s), fitting the generic bridge's default 30 s execution limit. When overriding bridge timeouts, configure the script's `timeoutMs` lower than the bridge timeout. Distinguish:
- `notSubmitted`: definitively failed before any possible send, unrelated to a missing UI contract (e.g. an existing unsent draft).
- `uiIncompatible`: the page failed to provide required controls or their expected state, or raised an unexpected pre-send DOM error. No send occurred. Preserve the created tab, report sanitized stage/reason/selector counts, and do not automatically retry.
- `submitted`: observed accepted turn, regardless of later agent outcome.
- `outcomeUnknown`: clicked/possibly clicked but acceptance not conclusively observed.

A launcher ending successfully means the **bootstrap was submitted**, not the agent has started work, completed work, called Jarvis, or satisfied its assigned task. Do not keep the tab open to watch reasoning, tool calls, or frontend event streams.

## 6. Agent bootstrap and ground-truth reconstruction

The bootstrap text is a simple instruction addressed to a **new** ChatGPT session. It must establish:
- Logical role (worker, orchestrator, reviewer, etc.), and stable agent identifier if relevant.
- Jarvis project/target scope, but not a copied large task snapshot.
- First action: inspect fresh Jarvis assignment, task state, current relevant `AGENTS.md` hierarchy, repository spec, and latest checkpoint; handle missing/changed assignments explicitly.
- Work according to the project's current instructions and repository state; do not trust memories of previous sessions as authority.
- How/when to checkpoint or schedule a successor, and whether the role is permitted to do so.
- Expected review integration workflow and any required completion convention, sourced from current instructions rather than duplicated in a launcher.

If Jarvis is unavailable, or the necessary tools are not exposed in this conversation, the agent must not invent task state or claim progress. ChatGPT app permissions may still present approval controls; the browser launcher cannot grant them. A workspace-level or app-specific permission configuration is a prerequisite for fully unattended runs, but separate high-risk confirmations may remain.

A user/bootstrap may request that a worker schedules its own **one-shot continuation** at a logical stopping point. It should checkpoint *before* arranging continuation where practical; choose a stable schedule ID or unique continuation ID deliberately to avoid duplicate successor launches. Self-continuation is opt-in instructions/policy, not implicit scheduler behavior.

## 7. Tab lifecycle and close operation

The current 20-agent-tab cap makes tab cleanup a requirement, not an optional optimization.

Introduce a **generic, explicit** close operation in extension and JarvisClient (either surfaced as `BrowserCloseTab` MCP or internal scheduler-close with an equivalent authenticated protocol). Preferred public command:

```text
BrowserCloseTab(tabRegistration, deviceId?)
```

Contract:
- Only accept a currently registered tab marked **agentCreated=true** on the paired extension.
- Validate parent window registration, session epoch, exact tab ID, registration generation, and provenance immediately before closing; never close a manually registered tab, arbitrary tab ID, or URL-matched replacement.
- Reject absent/disconnected/revoked/mismatched registrations with typed errors. “Tab already closed” may be an idempotent cleanup success **only when identity is provable**.
- Closing unregisters the agent-created tab and promptly releases its capacity slot.
- The user-registered **browser window** remains open, and all preexisting tabs in it remain untouched. Closing a created tab does not revoke the window registration.
- Audit only operation metadata. Separate close authorization from script execution and preserve the extension's own consent boundary.
- No mechanism for an agent to enumerate and close unrelated browser tabs.

The **immediate** generic `BrowserRunInNewTab` command and the delayed scheduler both expose optional `closeTabAfterExecution` (false when omitted for legacy generic callers; **true for this ChatGPT launching convention**) and a debug option to keep the tab open. JarvisClient manages cleanup using the exact registration returned by its own open; the page script never invokes privileged tab/window APIs. Both call paths share the same implementation, not duplicated open/execute/close logic.

**Default launcher behavior:** after confirmed submission and a short, bounded grace period, perform best-effort tab close. For a definitive non-compatibility pre-submit failure, close the owned tab; for `uiIncompatible`, preserve it for inspection. A later human may close the retained tab explicitly. For an outcome-unknown post-click, **do not auto-resubmit**; prefer retaining the tab for inspection rather than risking interruption of an in-flight submission, with its registration/URL available for manual cleanup. A later safe retention policy may be added only with explicit semantics.

If close fails after confirmed submission, report `submitted + cleanupFailed` and retain enough metadata to clean up later. Do not change successful submission into a failed/duplicated launch. Normal successful hourly launches must not accumulate toward the 20-tab cap.

No request to close a ChatGPT tab should be interpreted as asking ChatGPT to cancel backend execution. This behavior is a user-observed product property; we are not implementing a background-execution service in Jarvis.

## 8. Scheduling semantics

### 8.1 Existing one-shot scheduling

Retain the existing one-shot scheduling semantics and private on-disk persistence, but extend the command to `ScheduleBrowserExecution(scheduleId, runAfterSeconds, windowRegistration, url, projectName, scriptPath, argsJson?, alias?, active?, closeTabAfterExecution?, deviceId?)`. Scheduled execution uses the same generic `BrowserRunInNewTab` service as immediate launches. The target is the **explicit registered window**, not a tab-origin anchor; site host permissions remain required. Preexisting persisted schedules without a window target must be flagged for explicit reconfiguration, never silently moved into an active window. One-shot work is **never automatically replayed** after a possible message submission.

**Immediate orchestrator resumption does not require scheduling:** `BrowserRunInNewTab` opens the next agent session now. The scheduler exists for deliberate delayed worker continuations and fixed-interval orchestration.

### 8.2 Optional fixed-interval recurring schedules (new)

Add an explicit repeat mode (e.g. `repeatEverySeconds`) without changing one-shot semantics. Example proposal:

```json
{
  "scheduleId": "hourly-orchestrator",
  "runAfterSeconds": 3600,
  "repeatEverySeconds": 3600,
  "windowRegistration": "agents",
  "url": "https://chatgpt.com/",
  "projectName": "Jarvis",
  "scriptPath": "browser-scripts/chatgpt/launchAgent.js",
  "argsJson": "{\"role\":\"orchestrator\",\"prompt\":\"Start a fresh orchestration pass. Read Jarvis activity and task state first; if an orchestrator is already active, exit without taking work.\"}",
  "closeTabAfterExecution": true,
  "active": false
}
```

Required semantics:
- Fixed-interval **launch attempts**, not recurring completion claims. `runAfterSeconds` sets first due time. Repeated schedule remains until explicitly canceled/changed.
- Persistent schedule definition, next due UTC, last attempt ID/outcome/URL and bounded recent history. No database; JarvisClient user-private state file remains the source of scheduling truth.
- Once due, persist an attempt as claimed before any browser effect. Each occurrence gets a unique attempt ID; completion records whether submission was confirmed and whether cleanup succeeded.
- On ordinary success, failure or uncertain outcome, continue at the **next scheduled interval** without replaying the same occurrence. If the provider script returns `uiIncompatible`, mark the recurring schedule `suspended` and stop future launches until a human explicitly reconfigures it. Keep the created tab and its registration ID.
- After client downtime, **skip missed occurrences** and schedule the next future interval; do not launch a backlog of hourly orchestrators. If a due attempt was interrupted, record it as uncertain and advance past it.
- No overlapping **local dispatches of the same schedule ID**. If an invocation is still running at its next interval, skip that interval instead of queueing another copy.
- Repeated attempts must target the explicitly selected, **currently registered window**, use fresh tabs with unique/generated tab aliases, and never reuse a previous conversation. If that window was closed/revoked or the browser restarted, fail/report until the user explicitly re-registers/rebinds; do not choose a substitute window.
- Cancellation prevents future occurrences; it does not undo a submitted ChatGPT run or retroactively revoke one already dispatched. Editing/replacing an active schedule is atomic between occurrences and may be rejected while an occurrence is currently being claimed.
- Define sensible min/max interval bounds and capacity limits; do not interpret `repeatEverySeconds` omitted as an implicit repeat.
- Display status as schedule enabled/disabled, next due, and **last launch outcome** separately. “Recurring” is not a terminal launch status.

This introduces no leases, distributed locks, watchdogs, background polling of chat, or special orchestrator authority logic. A recurring orchestrator **must inspect Jarvis's recent activity/current task assignments** before doing substantial work and voluntarily exit when it sees another orchestrator plausibly active. That is an advisory concurrency check, **not an exactly-once or globally exclusive guarantee**. The sole operator accepts the residual race.

### 8.3 Worker continuation versus recurring orchestrator

- **Worker:** normally one-shot self-continuation after creating a checkpoint and before ending the current conversation; no new session unless the worker deliberately schedules it.
- **Orchestrator:** optional recurring hourly fixed-interval launch regardless of previous session status, with agent-side recent-activity check to avoid collisions.
- **Reviewer:** same generic primitives as workers; no hardcoded role rules in JarvisClient.

## 9. Observability, privacy and capacity

The scheduler/list view should expose safe metadata: schedule ID, occurrence/attempt ID, due/start/end times, device, script path, status (notSubmitted/submitted/outcomeUnknown), sanitized conversation URL if available, registration ID (if still live), cleanup status, and bounded error category.

Keep **distinct states** for (a) browser launch, (b) ChatGPT submission, and (c) work completion. Jarvis activity and task checkpoints provide (c); the launcher does not.

Do not persist or emit prompt text, full draft, cookies, tokens, browser DOM dumps, ChatGPT internal request/response bodies, or account credits in general activity logs. The existing local scheduler necessarily persists `argsJson` privately for future execution; list and audit outputs remain redacted. The conversation URL may reveal sensitive identifiers and should be visible only through authorized Jarvis operations, without query/fragment secrets.

The observed frontend messages `prepare`, `conversation` and `usage` are **diagnostic research subjects**, not launch dependencies. `usage` might reveal model limits, reset timestamps or credit balance; do not treat its schema as an API contract and do not launch/stop schedules based on these values without separate implementation and validation. Browser tabs need not remain open to poll for usage data.

For diagnosis, expose optional coarse timings (tab ready, composer ready, text accepted, Send ready, submit confirmation, tab close) and sanitized failure codes. No frontend streaming/polling is required after launch.

## 10. Security and permission boundaries

1. The ChatGPT browser session must already be authenticated as the intended account. UI launcher must not collect or store ChatGPT credentials.
2. Extension is paired to the local JarvisClient, a **specific live browser window was registered by the user**, the destination site's host permission was granted separately, and **Allow Jarvis to open new tabs** is enabled. Registering a window does **not** authorize arbitrary existing tabs or unpermitted origins.
3. An MCP caller must possess the necessary workspace/browser execution permissions. ChatGPT itself may ask the user to approve individual Jarvis operations; client-side full trust does not change ChatGPT's app permission behavior.
4. A persistent ChatGPT app-specific permission setting, if the workspace exposes it, is the intended route for unattended operation; the launcher must not simulate clicks on permission approval, account recovery, security challenges, or other consent dialogs.
5. The origin, requested context, composer and clicked Send control must be verified on the **agent-created** tab. No stale alias or URL match is sufficient authorization.
6. Local schedule JSON contains prompts/arguments: enforce owner-private permissions, atomic writes, path/symlink validation, and bounded inputs as in the current scheduler.
7. Keep ChatGPT-specific selectors and troubleshooting in normal project code, not in the public Jarvis server or generic bridge.

## 11. Failures and expected outcome policy

| Scenario | Launch classification | Recovery |
| --- | --- | --- |
| No pairing/registered window/host permission/extension opt-in | `notSubmitted` | Operator resolves consent/configuration; no alternate-window fallback |
| Registered window closed, moved, or stale after browser restart | `notSubmitted` | Explicitly re-register/rebind window; do not target another window |
| Page fails to load; login/challenge/incorrect project | `notSubmitted` | Report typed reason; no automatic bypass |
| Composer missing or contains restored draft | `notSubmitted` | Leave draft unchanged; report/diagnose |
| Text insertion fails exact verification; Send never enabled | `notSubmitted` | No click; safe to inspect/fix script |
| Send clicked, UI reports new turn and route | `submitted` | Record URL; close owned tab |
| Send clicked, no conclusive acknowledgement before timeout | `outcomeUnknown` | No replay; preserve evidence and preferably tab |
| Client/extension disconnect during or after click | `outcomeUnknown` | Do not automatically retry |
| Tab close fails following confirmed submission | `submitted` + cleanup error | Manual or bounded safe cleanup |
| Client restarts after claiming one-shot occurrence | `outcomeUnknown` | Never automatically replay that occurrence |
| Client restarts during recurring occurrence | Occurrence `outcomeUnknown` | Skip it; next future interval remains scheduled |
| ChatGPT rate limits the run or later agent hits permissions | Submission may still be `submitted` | Agent/task activity shows actual outcome; launcher does not claim completion |
| Orchestrator already working when next one launches | Submission `submitted` | Fresh agent checks Jarvis activity and exits |

**No silent duplicate sends, no permission bypasses, and no claiming task success from launch success.**

## 12. Implementation boundaries and sequence

1. **Register windows in extension:** add user-gesture Register/Unregister window, window alias/ID validation and lifecycle, explicit destination host permission UI, and no adoption of any preexisting tab. Preserve existing manual-tab functionality.
2. **Generic open/execute/close:** add safe `BrowserCloseTab`, then `BrowserRunInNewTab` that owns the whole immediate workflow with one MCP call; keep all page-specific behavior in project JavaScript. Verify window selection, site permission, identity and tab-limit release.
3. **Script and fixtures:** create the reusable ChatGPT UI launcher with semantic selectors, ProseMirror insertion, single-click guard, submission acknowledgement, structured results and DOM tests.
4. **Scheduled reuse:** add `windowRegistration`, `closeTabAfterExecution` and safe launch metadata to one-shot schedules, and reuse the same generic workflow. Existing schedules missing a window must require explicit reconfiguration, never silent retargeting.
5. **Optional recurrence:** implement fixed-interval schedule persistence, per-occurrence journal, skip-missed behavior, restart semantics, edit/cancel/list and tests. No orchestrator-specific code.
6. **Manual real Edge verification:** pair extension, register a **browser window**, grant `chatgpt.com` host permission and enable agent-created tabs. From an orchestrator, invoke `BrowserRunInNewTab` once with a harmless new-session prompt; verify correct target window, URL, tab close, and post-close continuation. Confirm context/tool availability separately; exercise delayed/recurring launches as needed.
7. **Only after verification:** review and integrate coordinated changes, then deploy through the normal release sequence if authorized.

No new task database, browser automation engine, direct HTTP ChatGPT integration, API token extraction, generic page credential handling, or Codex-through-Jarvis flow.

## 13. Acceptance criteria

**Unit / simulation tests**
- The script waits through delayed hydration, finds the current ProseMirror textbox, inserts multiline Unicode text via editing operations, verifies exact content, waits for **Send** to replace **Start voice**, and clicks Send once.
- Empty, pre-populated/restored, replaced, disabled, loading, duplicate, and misleading composer/button fixtures fail safely. The script does not target arbitrary page submit buttons.
- Verified post-submit transition yields `submitted` and actual same-origin conversation URL. Click without acknowledgement yields `outcomeUnknown`; no second click.
- ChatGPT UI remains independent of hashed CSS class names and internal `prepare`/`conversation` network messages.
- Correct project context is enforced when requested; missing/wrong context does not submit.
- URL sanitization, prompt redaction, validation, bounded JSON and timeout behavior are covered.

**Bridge/scheduler tests**
- Registering one window permits **only new-tab creation in that window**; it does not register or expose any existing tab. A different window, a closed/reused window ID, stale generation, or unapproved site is rejected.
- Only freshly agent-created registered tabs can be closed; manually registered tabs, wrong-generation IDs, navigated/reused tab IDs, unregistered tabs and unrelated tabs cannot be closed.
- Open → execute → confirmed submission → close releases one of 20 agent-tab slots; repeated cycles do not accumulate. On close error, success of the submission remains recorded.
- Existing one-shot timing and no-replay behavior is preserved; stored schedules without a window target require explicit reconfiguration, not a guessed active window.
- An orchestrator issues **one** immediate `BrowserRunInNewTab` call that opens in the chosen registered window, executes the script and closes its own created tab; scheduler uses exactly the same underlying service.
- Interrupted submission is not replayed; client shutdown/restart marks claimed occurrence uncertain.
- Recurring schedule fires on interval, is persistent across restart, skips missed intervals, never overlaps a local invocation for the same ID, and continues after a failed/uncertain attempt at its next interval. Cancel prevents future launches.
- Schedule listing and logs show metadata but not prompts, network tokens, or ChatGPT private payloads.

**Manual end-to-end (Edge / real ChatGPT)**
- With an authenticated session and normal extension consent, a window is manually registered without registering any existing tabs. An orchestrator immediately starts a fresh ChatGPT conversation through one browser workflow call, the exact bootstrap prompt is submitted via UI, a usable conversation URL is recorded, and **only its new tab closes**; the registered window and all existing tabs remain.
- The resulting agent can access Jarvis in the chosen project/context and reconstruct its task and checkpoint. If ChatGPT asks for MCP approval, the documented limitation is observed, not bypassed.
- A successful run is not marked as a completed Jarvis task until the agent actually checkpoints/completes it.
- Repeated hourly launches can run without a buildup of agent-created tabs, and the new orchestrator performs an activity check before assuming ownership.
- All changes are committed and independently reviewed before integration; no automatic deployment as a side effect of writing this spec.

## 14. Resolved design decisions

- **Prefer UI automation** over reverse-engineering ChatGPT's private `conversation` request: the frontend owns the evolving protocol, while our script uses accessible controls.
- **Close tab shortly after confirmed submission**, rather than keeping a frontend connected through agent execution.
- **No backend-liveness probe** from the launcher: agents may work long after tab closure; activity/task state is inspected by other agents/operators, not by browser polling.
- **Persistent logical agents, ephemeral ChatGPT sessions**: fresh instructions from current Jarvis/project truth on each run.
- **Workers may schedule one-shot continuations**; a recurring orchestrator may run hourly even when another appears active, and must itself check current activity.
- **ChatGPT specifics remain in project script**; the generic browser bridge gains a registered-window target, safe tab close and a single immediate open/execute/close command, reused by the scheduler.
- **Window registration is distinct from individual tab registration**; site host permission remains an independent requirement, but a manually registered same-origin tab is no longer required to launch new tabs.
- **Orchestrator handoffs are immediate** through the generic workflow; schedules are for intentional delayed/recurring launches.
- **No automatic retry after possible Send**, no exactly-once promises, no orchestration leases, and no spoofing of user permission controls.

## 15. Explicitly deferred

- Native ChatGPT API integration or synthesis of private network payloads.
- Parsing `usage` frames to enforce account budget, credit or model-rate limits.
- Automated selection of models/thinking effort until robust UI selectors are tested.
- Agent-side concurrency leases / strict singleton enforcement.
- Automatic cleanup of uncertain in-flight tabs without a proven safe policy.
- Automated detection of *agent task completion* from ChatGPT UI.
