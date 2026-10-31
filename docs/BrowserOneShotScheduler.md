# Persistent one-shot browser scheduling

JarvisClient can persist a **single future browser action** and execute it once. This enables an ephemeral orchestrator to schedule a fresh browser session after its current ChatGPT session finishes. The scheduler itself has no ChatGPT-specific code, agent knowledge, watchdog, lease or recurring trigger.

## Command surface

- ScheduleBrowserExecution(scheduleId, runAfterSeconds, url, projectName, scriptPath, argsJson?, alias?, active?, deviceId?)
- ListScheduledBrowserExecutions(deviceId?)
- CancelScheduledBrowserExecution(scheduleId, deviceId?)

Example:

    {
      "scheduleId": "orchestrator",
      "runAfterSeconds": 600,
      "url": "https://chatgpt.com/",
      "projectName": "Userscripts",
      "scriptPath": "chatgpt/startOrchestrator.js",
      "argsJson": "{\"role\":\"orchestrator\"}",
      "active": false
    }

Omit the tab alias for repeated orchestrator cycles: the extension generates a fresh unique alias for each new tab. Reusing a fixed alias while the previous tab remains registered will fail. Keep a manually registered tab on the destination origin as the authorization anchor; the extension opt-in does not authorize unrelated origins.

The JavaScript file must be a regular UTF-8 .js file in a registered Jarvis project or Git worktree. The file defines an asynchronous execute(args) function. The project script handles all application-specific browser interactions; the generic scheduler knows only when to open a tab and execute the file.

At schedule time the client validates the target project path and checks that the file exists. At execution time it **re-resolves and re-reads** the file under existing project/worktree path and symlink constraints, picking up subsequent project commits/edits. The JSON arguments are preserved until execution.

The scheduler accepts a 5-second to 30-day delay and at most 100 pending actions. Re-scheduling an ID replaces its **pending** entry. Running actions cannot be replaced or canceled. Each action is one-shot. Scheduling a successor is entirely the active agent's responsibility; there is no self-repeating schedule.

## Execution

At the due time, JarvisClient:

1. Atomically marks the action running **on disk before dispatch**.
2. Reads the current project script.
3. Waits briefly for a paired browser extension to reconnect, if needed.
4. Uses BrowserOpenTab to open a new background tab (or a foreground tab only if explicitly requested).
5. Waits for the new registration to become ready, with a bounded timeout.
6. Uses BrowserExecuteFile's underlying execution protocol with the newly created registration ID and preserved JSON arguments.
7. Records completed, failed or outcomeUnknown, with the created registration ID when available.

To allow the new tab, the user must already have the Jarvis browser bridge paired, have **manually registered a tab on the destination origin**, granted browser host permission, and switched on **Allow Jarvis to open new tabs** in the Edge/Chromium extension. No scheduled action can bypass that consent.

Scripts should return after submitting or verifying their own browser action; a completed browser script does not prove a new ChatGPT session will successfully finish its orchestration work.

## Persistence, privacy, recovery

Actions live in a **user-private file**, alongside JarvisClient's ordinary user-level configuration:

    browser-schedules.json

The file contains the full URL, project/script path, due time, alias, and JSON arguments. On Linux/macOS the state file is written with user-only read/write permissions. It is **not** committed to Git and **not** stored in the public Jarvis server. It is intentionally not protected from local processes running as the same OS user.

ListScheduledBrowserExecutions reveals only IDs, due times, statuses, project/script paths, origin, alias and a bounded error category. It does not expose full URLs, stored arguments, JavaScript source or page results.

Completed/canceled/failed/uncertain history is capped at the 100 most recently updated terminal entries. At client startup, pending actions remain pending and will fire when due. A previously running action becomes outcomeUnknown and is **never automatically retried**: it might already have created a tab or submitted a message. A canceled/completed/failed action does not run again. No watchdog or fallback orchestrator is created.

If the orchestration chain breaks, the human operator can start a new orchestrator. This is by design.

## Tests

- dotnet test Client.Tests/Client.Tests.fsproj --filter FullyQualifiedName~BrowserSchedulesTests
- dotnet test Jarvis.slnx
- node --test browser-extension/tests/bridge.test.cjs

Real Edge end-to-end testing of the browser extension and script behavior remains a separate acceptance requirement.

## Proposed replacement: registered-window launch targets (not implemented)

The sections above document the **implemented** one-shot scheduler and its current origin-anchor requirement. The **new target** replaces that requirement with a **user-registered browser window** plus separately granted destination-site host permission. A manually registered same-origin tab is no longer needed to create fresh tabs. Existing unregistered tabs in the registered window remain inaccessible.

An orchestrator uses **`BrowserRunInNewTab`** to launch a new agent **immediately**, in one operation: open a tab in the designated window, execute a normal project-owned ChatGPT UI script, capture the result, and close only the created tab. **A schedule is not needed for immediate agent resumption.**

For deliberate delayed worker continuation, extend the current `ScheduleBrowserExecution` with `windowRegistration` and optional `closeTabAfterExecution`. The delayed run invokes exactly the same generic open/execute/close service as the immediate call. The scheduler must not silently pick the current window; old persisted schedules with no window target require explicit reconfiguration. Window closure, revocation or browser restart makes the binding unavailable until the user re-registers/rebinds.

For optional recurring orchestration, a fixed interval such as `repeatEverySeconds: 3600` causes **new launch attempts**, with one claimed attempt at a time, no backlog after downtime and no replay of uncertain submissions. The newly started orchestrator checks Jarvis activity for an already active orchestrator instead of using leases or hard locks.

Illustrative future delayed launch (fields unavailable today):

```json
{
  "scheduleId": "worker-3-next",
  "runAfterSeconds": 300,
  "windowRegistration": "agents",
  "url": "https://chatgpt.com/",
  "projectName": "Userscripts",
  "scriptPath": "chatgpt/launchAgent.js",
  "argsJson": "{\"role\":\"worker\",\"prompt\":\"Read your current Jarvis assignment and continue in this fresh session.\"}",
  "closeTabAfterExecution": true,
  "active": false
}
```

The extension must enforce agent-created tab provenance before closing and preserve the registered window and all existing tabs. See [ChatGPT Ephemeral Agent Launcher](ChatGPTAgentLauncher.md) for the full immediate workflow, bootstrap contract, DOM selectors, result semantics, and acceptance tests.

**No registered-window functionality, tab closure, one-call launcher or recurrence has been implemented in this worktree yet.**
