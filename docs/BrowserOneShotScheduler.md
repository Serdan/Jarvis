# Persistent browser scheduling — implementation

**Status:** Implemented on `feature/browser-bridge`; not deployed or verified against live Edge/ChatGPT.

JarvisClient persists browser launch schedules in its local, user-private `browser-schedules.json` file. Scheduling is **generic**: it opens a new tab in a **user-registered window** on a site with browser host permission, executes the current ordinary project JavaScript file, and optionally closes the created tab. It knows nothing about ChatGPT agent identities or tasks. Immediate agent handoffs use `BrowserRunInNewTab`, not the scheduler.

## Commands

- `ScheduleBrowserExecution(scheduleId, runAfterSeconds, url, projectName, agentId, worktreeName?, scriptPath, argsJson?, alias?, active?, deviceId?, windowRegistration, closeTabAfterExecution?, repeatEverySeconds?)`
- `ListScheduledBrowserExecutions(agentId, deviceId?)`
- `CancelScheduledBrowserExecution(scheduleId, agentId, deviceId?)`

A script is a UTF-8 project-relative `.js` file whose entrypoint is `async function execute(args)`. At scheduling time Jarvis validates path and size; at due time the client re-resolves and re-reads the file under normal Jarvis worktree/symlink confinement, so future edits take effect.

Example delayed worker:

```json
{
  "scheduleId": "worker-3-next",
  "runAfterSeconds": 300,
  "windowRegistration": "agents",
  "url": "https://chatgpt.com/",
  "projectName": "Jarvis",
  "agentId": "Browser Agent",
  "worktreeName": "browser-bridge",
  "scriptPath": "browser-scripts/chatgpt/launchAgent.js",
  "argsJson": "{\"prompt\":\"You are Worker 3. Read your current Jarvis assignment and task, spec and checkpoint. Continue in this new session.\"}",
  "closeTabAfterExecution": true,
  "active": false
}
```

For a recurring hourly orchestrator, specify `repeatEverySeconds: 3600` and set `runAfterSeconds` to the desired initial delay. Without `repeatEverySeconds`, it is **one-shot**. Delay range is 5 seconds–30 days; recurring interval range is 60 seconds–30 days.

## Dispatch and recovery

At due time:

1. Persist the occurrence as **running** with a fresh attempt ID before performing browser effects.
2. Read the current project script and wait briefly for the extension to reconnect.
3. Call the same underlying open → ready → execute → optional close service as the immediate command.
4. Store the latest registration ID, submitted conversation URL if supplied by script, cleanup result, outcome and update time.
5. For one-shot actions, finish as completed/failed/outcomeUnknown or `suspended` (provider UI incompatibility). For recurring actions, advance to the next **future** interval after ordinary outcomes, but enter `suspended` immediately on a `UiIncompatible:` result. No further occurrences launch until the schedule is explicitly replaced.

After a client restart, a running one-shot is marked `outcomeUnknown`, **never replayed**. A running recurring occurrence is marked uncertain and skipped; the next future interval remains pending. Missed intervals are skipped rather than accumulated. There is no watchdog, strict singleton lease, or separate orchestration engine. The next orchestrator should read Jarvis activity and voluntarily exit if another is active.

Cancellation prevents **future** occurrences and cannot undo an already submitted ChatGPT turn. Replacing a pending schedule ID is allowed; replacing an actively executing occurrence is rejected. The scheduler admits at most 100 pending schedule IDs.

## Window authorization and tab cleanup

The user explicitly registers a browser **window** from the Edge/Chromium extension popup and independently grants host permission for the destination (e.g. `https://chatgpt.com`). **Allow Jarvis to open new tabs** must be enabled. Existing tabs within the selected window remain private. Auto-created tabs inherit an origin-bound registration only for that created tab.

Window handles are tied to their actual browser session. After a browser restart, window closure or revocation, the user must re-register/rebind the target. Old schedules that lack a `windowRegistration` are flagged for explicit reconfiguration, not silently retargeted to another window. The legacy manually registered same-origin tab anchor is no longer required.

`closeTabAfterExecution` defaults false for generic compatibility. The ChatGPT launcher convention supplies true: after a confirmed submission the client closes **only the new agent-created tab**. If it cannot confirm whether the page submitted its message, no automatic replay occurs and the tab is normally retained for inspection. A provider UI mismatch also retains its tab, reports the registration ID and sanitized structural observations, and suspends a recurring schedule until reconfigured. A cleanup error after successful submission does not convert the submission into a failure.

## Persistence and privacy

Schedules persist in the JarvisClient user's config directory with owner-private permissions and atomic writes. State schema is v2; v1 is accepted for migration and incompatible pending entries are marked as needing window reconfiguration. The private file stores full URL, project script path, prompt arguments, window target and due time. It is not stored on the public Jarvis server or committed to Git.

The list command reveals schedule IDs, due times, next/last status, window, script path, sanitized origin, last registration/attempt ID, and a conversation URL if returned by the project script. It **does not reveal the prompt arguments, scripts, cookies or DOM**. Treat conversation URLs as sensitive identifiers.

Successful JavaScript evaluation or UI submission is **not** proof the backend ChatGPT agent finished its assignment. The logical agent's work belongs to Jarvis assignments, Git commits, tasks and checkpoints.

## Tests and acceptance

```bash
dotnet test Jarvis.slnx --no-restore
node --test browser-extension/tests/bridge.test.cjs browser-extension/tests/chatgpt-launcher.test.cjs
```

The F# tests cover persistence, cancellation, no-replay, recurring restart and real loopback WebSocket framing. JavaScript tests cover extension consent/window/close behavior and simulated ChatGPT composer interaction. A live Edge/ChatGPT launch, new-session tool availability and post-close backend continuation have **not yet been exercised** on this branch.

See [ChatGPTAgentLauncher.md](ChatGPTAgentLauncher.md) for the UI contract and [BrowserBridgeSystem.md](BrowserBridgeSystem.md) for the bridge security model.
