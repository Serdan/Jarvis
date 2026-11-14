# Durable execution scheduling

JarvisClient persists **one-off, recurring, and agent-idle** actions in the
existing per-device `browser-schedules.json` store (schema v3; v1/v2 are
read automatically). One scheduler runs regardless of whether the browser
extension is enabled. The operator can list, inspect, replace or cancel a
schedule via `schedule_execution`, `list_scheduled_executions`,
`get_scheduled_execution` and `cancel_scheduled_execution`.

## Triggers

- `Once`: supply `runAfterSeconds` (0–30 days) or `runAtUtc`
  (future within 30 days).
- `Every`: supply `repeatEverySeconds` (60 seconds–30 days); optional
  initial `runAfterSeconds`. After each attempt the next due occurrence
  is computed from the original interval, skipping missed ticks.
- `AfterAgentIdle`: supply `idleAgentId` and `idleSeconds`
  (60 seconds–30 days). The first deadline is last attributed activity or
  schedule creation, plus the interval. Any observed activity extends the
  deadline. After launch, the schedule waits for subsequent agent activity
  before re-arming; it does not recursively launch itself.

An example Manager heartbeat is `triggerKind="AfterAgentIdle"`,
`idleAgentId="Manager"`, `idleSeconds=900`, with the appropriate project
and either a `RunCommand` action or a `BrowserScript` action that launches
the Manager. Each project has its own Manager schedule. Canceling it stops
future Manager launches.

## Actions

`RunCommand` uses the normal project-scoped `run_command` execution,
permission confirmation, TUI activity and audit trail at each due execution.
Supply `executable`, `args` and optional `workingDirectory`,
`timeoutSeconds` and `maxOutputBytes`. Ordinary `run_command` restrictions
remain in force, including denied shell executables.

`BrowserScript` uses the pre-existing browser bridge and registered browser
window. Supply `windowRegistration`, `url`, `scriptPath` (a project-local
`.js` file), and optionally `argsJson`, `alias`, `active`,
`closeTabAfterExecution`. An unavailable bridge reports a failed occurrence,
not silent success. Deferred actions are reauthorized when they run.

## Durability and status

Schedules have stable IDs (1–64 alphanumeric, hyphen or underscore).
State changes use restrictive file permissions and an atomic, fsynced replace.
A claim is persisted with an attempt ID **before** execution; a late result
cannot overwrite a later attempt. Completed once-only executions are never
replayed after restart, and missed recurring ticks are skipped. Up to 100
pending/idle schedules and eight concurrent running actions are accepted.

`list`/`get` expose due time, trigger, action type, attempt ID, error,
status and bounded result summary; they do not expose command arguments or
browser script JSON. Canceling a running schedule blocks future occurrences
but does not kill its already running command.

For Manager idle schedules, a running attempt without any activity for
30 minutes can become stale and be relaunched; the status records uncertain
outcome and the old attempt's eventual result cannot override the new one.
The absence of any event after a completed Manager launch leaves the schedule
waiting until the Manager is observed again.

Actual `agentId`-attributed tool invocations produce authenticated
cross-device SignalR activity hints; `observe_agent`, job polling and
cross-project observer notifications do not reset the clock. Hints are
best-effort for connected devices, not a canonical ledger timestamp. The
scheduler remains device-local and is not a distributed leader election:
do not configure duplicate Manager heartbeats on multiple devices.
