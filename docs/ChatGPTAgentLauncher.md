# ChatGPT Ephemeral Agent Launcher — Design Specification

**Status:** Proposed; specification only, not implemented or deployed  
**Worktree:** `Jarvis::browser-bridge` (`feature/browser-bridge`)  
**Depends on:** [Browser bridge](BrowserBridgeSystem.md), [implemented one-shot scheduler](BrowserOneShotScheduler.md)  
**Scope:** Project-owned ChatGPT UI automation; minimal generic browser-bridge and scheduler changes needed to support reliable launch, tab cleanup, and optional recurring execution.

## 1. Objective

Use an authenticated Edge/Chromium tab as a **short-lived launcher** for fresh ChatGPT agent conversations. A JarvisClient schedule or an existing agent creates a tab, navigates to the chosen ChatGPT entry URL, enters and submits a bootstrap prompt through the real web UI, captures the resulting conversation URL, and closes the tab. ChatGPT execution is expected to continue server-side after tab closure; no browser polling for the agent's later progress is required.

The logical worker/orchestrator is durable in **Jarvis tasks, assignments, Git history, specifications, and checkpoints**, not in any particular ChatGPT conversation. Fresh sessions reconstruct that ground truth, minimizing drift and dependence on long-running conversations. The browser is a launcher and optional diagnostic viewer, not the runtime, orchestrator, or task database.

This is primarily a **project-owned script** on the generic browser bridge, not a ChatGPT-specific MCP tool or ChatGPT API client.

### Invariants

1. **UI only:** interact with the DOM and normal user interface; do not send requests to internal ChatGPT APIs, forge `conversation`/`prepare` messages, or depend on private network payload schemas.
2. **One submission per launch:** after the script may have clicked Send, failures/timeouts are outcome-uncertain; never blindly replay a prompt or retry a launch with a new tab.
3. **Disposable tabs:** close only tabs created for this launch by Jarvis, never user-registered or unrelated tabs. Closing a tab is not a request to cancel the running ChatGPT conversation.
4. **Separation of responsibilities:** scheduler knows only due time, site URL, project file, arguments, and optional cleanup; the ChatGPT script knows DOM selectors and submission semantics; agents know tasks and assignments.
5. **No new agent orchestration state machine:** Jarvis remains Git/task/activity-driven. The launcher cannot infer successful task completion from successful prompt submission.
6. **Security stays layered:** ChatGPT app authorization, Jarvis MCP permissions, JarvisClient trust, extension pairing, browser host permissions, and manual origin anchor are independent. No automation bypasses consent dialogs.
7. **Fresh bootstraps are self-contained:** the prompt specifies role and how to find current Jarvis truth, not a copied snapshot of potentially stale instructions.

## 2. Existing implementation and required additions

### Already implemented in the browser-bridge worktree

- Local authenticated Chromium/Edge MV3 extension paired to JarvisClient.
- Explicit manual registration and host permission for a site; separate **Allow Jarvis to open new tabs** extension opt-in.
- `BrowserOpenTab` creates an agent-owned background tab only on an approved/manual-registration origin. Agent-created registrations remain origin-bound during same-site SPA navigation.
- `BrowserExecuteFile` runs a real `.js` file from an ordinary Jarvis project; exported contract is `async function execute(args)`.
- `ScheduleBrowserExecution` runs an **one-shot** delayed open-tab + execute-file action; schedule persisted locally through client restarts; no automatic replay after an uncertain dispatch. `ListScheduledBrowserExecutions` and `CancelScheduledBrowserExecution` exist.
- A limit of **20 currently registered agent-created tabs** exists; without tab cleanup, frequent launches eventually hit that limit.

### New work required by this specification

1. A project-owned, reusable `chatgpt/launchAgent.js` UI automation script and tests/fixtures. This path is illustrative; no special Jarvis directory is required.
2. **Agent-created tab close capability** in the generic extension bridge, and schedule-level post-execution cleanup. Design as `BrowserCloseTab(tabRegistration, deviceId?)` or an equivalent typed extension close request. Do not inject `window.close()` as a substitute.
3. A durable **launch result** with submission status, optional resulting conversation URL, cleanup outcome, and schedule/attempt correlation, without persisting prompt contents in activity/log listings.
4. Optional **recurring fixed-interval schedules** for an orchestrator or other role; existing one-shot behavior remains the default.
5. Documentation and regression tests spanning UI changes, restart, duplicate dispatch, permissions, registration capacity, and live Edge.

Do **not** mark these additions as present until implemented and verified. No deployment, merge, or rollout is implied by accepting this spec.

## 3. Architecture and ownership

```text
Existing agent / human operator
    | create / update / cancel one-shot or optional recurring schedule
    v
JarvisClient local scheduler (persistent JSON)
    | due action: choose URL, project/script, JSON args
    v
Authenticated local browser bridge
    | BrowserOpenTab (user-approved origin; auto-registration)
    v
Fresh Edge/Chromium tab -> ChatGPT frontend
    | BrowserExecuteFile: project-owned ChatGPT launcher
    |   find composer -> insert prompt -> wait for Send -> click
    |   observe launch acknowledgement + capture conversation URL
    v
JarvisClient: persist launch metadata, best-effort close agent-owned tab
    |
    +-> new ChatGPT conversation continues on backend
              | first steps: invoke Jarvis, read assignment/spec/task
              v
         Task work, Git commits, checkpoints, activity
```

**Jarvis server** remains stateless and routes existing MCP calls; it must not host browser sessions, store persistent scheduler state, or know how ChatGPT works. **JarvisClient** owns local schedule persistence, execution orchestration, tab cleanup, and structured metadata. **Extension** enforces pairing and tab authorization and executes close requests. **Project-owned script** owns selectors, insertion, click, and UI confirmation. **Agent bootstrap text** owns role instructions and reconciliation with Jarvis state.

## 4. Configuration and launch request

A launch is expressible as a normal scheduled browser execution whose script/arguments are ChatGPT-specific. No new ChatGPT MCP endpoint.

Illustrative one-shot invocation using the existing command:

```json
{
  "scheduleId": "worker-3-next",
  "runAfterSeconds": 300,
  "url": "https://chatgpt.com/",
  "projectName": "Userscripts",
  "scriptPath": "chatgpt/launchAgent.js",
  "argsJson": "{\"role\":\"worker\",\"agentId\":\"3\",\"projectName\":\"Wayfold\",\"prompt\":\"You are Worker 3. Read the latest Jarvis assignment, relevant AGENTS.md, task and spec. Follow current instructions and continue the assigned work; do not assume previous conversation state. End with the required checkpoint/Next line.\"}",
  "active": false
}
```

The payload is an *example*, not an instruction to hard-code a specific project, worker, prompt, role, gizmo ID, or account. The launcher accepts all agent-specific strings from arguments. One launch has one explicit message; arbitrary strings supplied as arguments must never be evaluated as JavaScript.

Recommended argument contract:

| Field | Required | Meaning |
| --- | --- | --- |
| `prompt` | Yes | Exact bootstrap text to submit; nonempty, bounded, may contain newlines |
| `role` / `agentId` / `projectName` | No | Caller-owned metadata for logs/correlation; **not** alternative sources of instructions |
| `expectedOrigin` | No | Defaults to `https://chatgpt.com`; reject unexpected origins |
| `uiReadyTimeoutMs` | No | Bounded wait for textbox/controls after hydration |
| `submitTimeoutMs` | No | Bounded wait for evidence of a submitted turn |
| `postSubmitWaitMs` | No | Small bounded grace interval before returning, for SPA navigation to settle |
| `debug` | No | Opt-in diagnostics; do not leak draft text by default |

Scheduling parameters, **not** script arguments, control `active`, `closeTabAfterExecution`, device selection, repeat interval, and cleanup on failure.

For ChatGPT **Projects**, a supported, explicitly chosen ChatGPT landing URL should be passed as `url`. It must be a known user-selected entry URL that opens a fresh conversation in the intended project. For other contexts, `https://chatgpt.com/` may suffice. Do not infer project identity from a display name, fabricate project URLs, or assume the selected GPT/Project context follows from a prior conversation. When a project, model, or thinking-effort selection is required, UI automation must verify or explicitly select it using discoverable controls. The launch must fail if the required context cannot be established; do not silently send in the wrong context.

### Model and thinking effort

Prefer the account's already configured UI defaults when the caller does not specify them. If supplied, a future explicit `model` or `thinkingEffort` option must be implemented as actual UI interaction + verification, not by forging backend parameters or assuming a saved setting. The initial launcher may omit this feature. Unsupported requested settings fail clearly instead of falling back silently.

## 5. ChatGPT UI automation contract

### 5.1 Tab readiness

- Open a **new** agent-created tab on the already approved `chatgpt.com` origin. Do not claim existing unregistered tabs.
- Await a ready registration/document; the extension rechecks current origin and identity before execution.
- The script must wait for React/ProseMirror hydration and composer availability with a bounded deadline; merely finding a DOM node before hydration is insufficient.
- Support background tabs by default, but test behavior under Chromium background throttling. Configurable `active=true` exists for diagnostics or an identified site limitation; don't steal focus unconditionally.
- Reject ChatGPT login, challenge, error, suspended account, wrong project/GPT, permission prompts, or otherwise blocked UI states when detectable. Report actionable categories; do not attempt to bypass a challenge.

### 5.2 Stable element selection

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

After the click, wait a bounded interval for **positive UI evidence** that the turn was accepted. Examples to verify in live Edge:
- A new ChatGPT conversation route becomes visible in `location.href`, including project/GPT-specific route shapes (not exclusively `/c/<id>`).
- A new user-message turn appears in the conversation and the composer has transitioned to a submitted/empty or generating state.
- Other observable stable UI transition proven to indicate submission, not merely a button click.

A changing URL alone can mean routing without submission, so route change is **not** proof unless accompanied by an appropriate post-submit state. Similarly, the editor clearing alone should not be treated as the only proof. The implementation should define an evidence precedence and test it against live ChatGPT.

On success return:

```json
{
  "status": "submitted",
  "conversationUrl": "https://chatgpt.com/c/illustrative-id",
  "evidence": ["newUserTurn", "conversationRoute"],
  "submittedAtUtc": "2026-10-30T00:00:00Z"
}
```

The URL above is illustrative; use the actual observed route without constructing one. A valid same-origin URL is recorded for later human reopening; strip fragments, credentials, and sensitive query parameters when storing or displaying it. If submission is confirmed but no stable conversation URL is available, return `submitted` with `conversationUrl: null` and an evidence category. Do not turn that into a second submission.

If success cannot be proven after a possible click, return `outcomeUnknown` with sanitized evidence/error; never silently classify as a definitive failure.

### 5.6 Timeouts and result boundary

A browser script runs within the existing bridge's timeout limits (15 s default; 30 s maximum); UI waits + post-submission grace must fit inside the overall execution bound. Distinguish:
- `notSubmitted`: definitively failed before any possible send.
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
- Validate session epoch, exact tab ID, registration generation, and provenance immediately before closing; never close a manually registered tab, arbitrary tab ID, or URL-matched replacement.
- Reject absent/disconnected/revoked/mismatched registrations with typed errors. “Tab already closed” may be an idempotent cleanup success **only when identity is provable**.
- Closing unregisters the agent-created tab and promptly releases its capacity slot.
- The manually registered ChatGPT origin anchor remains open and unaffected.
- Audit only operation metadata. Separate close authorization from script execution and preserve the extension's own consent boundary.
- No mechanism for an agent to enumerate and close unrelated browser tabs.

Extend generic scheduled execution with optional `closeTabAfterExecution` (false for legacy caller compatibility; **true for the ChatGPT agent-launching convention**) and optional debug `keepTabOpen`. The close action is managed by the client/extension, never by the page script, and should use the exact registration ID returned by the immediately preceding open.

**Default launcher behavior:** after confirmed submission and a short, bounded grace period, perform best-effort tab close. For a definitive pre-submit failure, close the owned tab unless a diagnostic flag requests preservation. For an outcome-unknown post-click, **do not auto-resubmit**; prefer retaining the tab for inspection rather than risking interruption of an in-flight submission, with its registration/URL available for manual cleanup. A later safe retention policy may be added only with explicit semantics.

If close fails after confirmed submission, report `submitted + cleanupFailed` and retain enough metadata to clean up later. Do not change successful submission into a failed/duplicated launch. Normal successful hourly launches must not accumulate toward the 20-tab cap.

No request to close a ChatGPT tab should be interpreted as asking ChatGPT to cancel backend execution. This behavior is a user-observed product property; we are not implementing a background-execution service in Jarvis.

## 8. Scheduling semantics

### 8.1 Existing one-shot scheduling

Retain existing `ScheduleBrowserExecution(scheduleId, runAfterSeconds, url, projectName, scriptPath, argsJson?, alias?, active?, deviceId?)`, validation, private on-disk persistence, one-shot dispatch and explicit `outcomeUnknown` on restart after claiming work. The script is re-read at execution time. One-shot work is **never automatically replayed** if it may have submitted a message.

### 8.2 Optional fixed-interval recurring schedules (new)

Add an explicit repeat mode (e.g. `repeatEverySeconds`) without changing one-shot semantics. Example proposal:

```json
{
  "scheduleId": "hourly-orchestrator",
  "runAfterSeconds": 3600,
  "repeatEverySeconds": 3600,
  "url": "https://chatgpt.com/",
  "projectName": "Userscripts",
  "scriptPath": "chatgpt/launchAgent.js",
  "argsJson": "{\"role\":\"orchestrator\",\"prompt\":\"Start a fresh orchestration pass. Read Jarvis activity and task state first; if an orchestrator is already active, exit without taking work.\"}",
  "closeTabAfterExecution": true,
  "active": false
}
```

Required semantics:
- Fixed-interval **launch attempts**, not recurring completion claims. `runAfterSeconds` sets first due time. Repeated schedule remains until explicitly canceled/changed.
- Persistent schedule definition, next due UTC, last attempt ID/outcome/URL and bounded recent history. No database; JarvisClient user-private state file remains the source of scheduling truth.
- Once due, persist an attempt as claimed before any browser effect. Each occurrence gets a unique attempt ID; completion records whether submission was confirmed and whether cleanup succeeded.
- On success, failure or uncertain outcome, continue at the **next scheduled interval**, except an explicitly disabled/canceled schedule. Never retry the same uncertain occurrence.
- After client downtime, **skip missed occurrences** and schedule the next future interval; do not launch a backlog of hourly orchestrators. If a due attempt was interrupted, record it as uncertain and advance past it.
- No overlapping **local dispatches of the same schedule ID**. If an invocation is still running at its next interval, skip that interval instead of queueing another copy.
- Repeated attempts must use fresh tabs and unique/generated aliases. Do not reuse a previous conversation or registration.
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
2. Extension is paired to the local JarvisClient, site host permission granted, a live **manual ChatGPT origin anchor** registered, and **Allow Jarvis to open new tabs** enabled. This does **not** authorize arbitrary existing tabs or origins.
3. An MCP caller must possess the necessary workspace/browser execution permissions. ChatGPT itself may ask the user to approve individual Jarvis operations; client-side full trust does not change ChatGPT's app permission behavior.
4. A persistent ChatGPT app-specific permission setting, if the workspace exposes it, is the intended route for unattended operation; the launcher must not simulate clicks on permission approval, account recovery, security challenges, or other consent dialogs.
5. The origin, requested context, composer and clicked Send control must be verified on the **agent-created** tab. No stale alias or URL match is sufficient authorization.
6. Local schedule JSON contains prompts/arguments: enforce owner-private permissions, atomic writes, path/symlink validation, and bounded inputs as in the current scheduler.
7. Keep ChatGPT-specific selectors and troubleshooting in normal project code, not in the public Jarvis server or generic bridge.

## 11. Failures and expected outcome policy

| Scenario | Launch classification | Recovery |
| --- | --- | --- |
| No pairing/manual origin anchor/extension opt-in | `notSubmitted` | Operator resolves consent/configuration |
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

1. **Script and fixtures:** create the reusable project-owned launcher with semantic selectors, ProseMirror insertion, single-click guard, UI acknowledgement contract, structured results and DOM-mutation tests. Include a dedicated freshly created chat fixture and a project-context fixture.
2. **Generic tab close:** add extension protocol + client command/service, agent-owned identity/revocation checks, tab-limit release, and tests. Keep manual tab registration controls unchanged.
3. **Schedule launch result and cleanup:** add `closeTabAfterExecution` and safe result metadata to the generic scheduler without changing existing one-shot guarantees. Existing consumers with no close option should behave exactly as before.
4. **Optional repeat:** implement fixed-interval schedule persistence, per-occurrence journal, skip-missed behavior, restart semantics, edit/cancel/list and tests. No orchestrator-specific code.
5. **Manual real Edge verification:** pair the extension, register a ChatGPT tab, enable agent-created tabs, and perform one actual fresh-conversation launch with a deliberately harmless prompt, confirm the returned URL and post-close backend continuation. Verify fresh project context/tool availability and permission behavior separately. Test a repeated schedule long enough to establish that tabs do not accumulate.
6. **Only after verification:** update usage docs, review and integrate coordinated extension/client/server changes, deploy through the normal Jarvis release sequence if authorized.

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
- Only freshly agent-created registered tabs can be closed; manual anchors, wrong-generation IDs, navigated/reused tab IDs, unregistered tabs and unrelated tabs cannot be closed.
- Open → execute → confirmed submission → close releases one of 20 agent-tab slots; repeated cycles do not accumulate. On close error, success of the submission remains recorded.
- Existing one-shot schedules keep their previous behavior when new optional fields are omitted.
- Interrupted submission is not replayed; client shutdown/restart marks claimed occurrence uncertain.
- Recurring schedule fires on interval, is persistent across restart, skips missed intervals, never overlaps a local invocation for the same ID, and continues after a failed/uncertain attempt at its next interval. Cancel prevents future launches.
- Schedule listing and logs show metadata but not prompts, network tokens, or ChatGPT private payloads.

**Manual end-to-end (Edge / real ChatGPT)**
- With an authenticated session and normal extension consent, a fresh ChatGPT conversation receives the exact bootstrap prompt via UI, a usable conversation URL is recorded, and its agent-created tab closes while the manually registered anchor remains.
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
- **ChatGPT specifics remain in project script**; generic browser bridge adds only tab-close and scheduler capabilities.
- **No automatic retry after possible Send**, no exactly-once promises, no orchestration leases, and no spoofing of user permission controls.

## 15. Explicitly deferred

- Native ChatGPT API integration or synthesis of private network payloads.
- Parsing `usage` frames to enforce account budget, credit or model-rate limits.
- Automated selection of models/thinking effort until robust UI selectors are tested.
- Agent-side concurrency leases / strict singleton enforcement.
- Automatic cleanup of uncertain in-flight tabs without a proven safe policy.
- Automated detection of *agent task completion* from ChatGPT UI.
