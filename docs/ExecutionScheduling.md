# Durable execution scheduling

JarvisClient persists **one-off, recurring, and agent-idle** actions in the
existing client-local `browser-schedules.json` store (schema v3; v1/v2 are
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
State changes use restrictive file permissions and an atomic, crash-durable
replace: write and fsync the temporary file, atomically rename, then fsync
the containing directory on Unix (Windows uses MoveFileEx with
MOVEFILE_WRITE_THROUGH). If durability fails, no occurrence is dispatched.
This assumes a local filesystem honoring fsync and atomic rename; NFS and
network filesystems are not supported.

A process-exclusive lifetime file lock (`browser-schedules.json.owner.lock`)
is acquired before state reads and held until scheduling stops. A second
JarvisClient sharing the folder remains connected but has scheduling
disabled: it cannot overwrite claims or run a due occurrence. After a
process crash, the OS releases the lock, and the next owner loads the
persisted attempt and applies no-replay/uncertain-outcome recovery.

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

Actual agent-attributed tool invocations persist a monotonic
user/project/agent activity watermark in the server's SQLite database
before remote command dispatch, alongside best-effort SignalR hints.
The registered, authenticated client can query `GetAgentActivity`
without activating or modifying the target agent. `observe_agent`,
job polling and other read-only observations do not reset the Manager clock.
The watermark survives server restarts and is authoritative even when the
scheduler missed notifications while disconnected.

Before every due `AfterAgentIdle` execution, JarvisClient makes a fresh
authenticated server roundtrip. An offline client, failed query, revoked
registration or absent verifier fails closed, preserving the pending
occurrence and an `AgentActivityUnverified` diagnostic. On reconnect,
a fresh query reconciles missed remote activity and postpones the deadline.
Waiting schedules also query for a new Manager activity episode, even if
the notification hint was lost. The query never invokes the target's inbox.
Separate idle schedules for the same project and Manager do not overlap
while a previous occurrence is running within this store.

The exclusive file lock elects one executor for a *single local schedule
file*, not a global leader across different machines. Opt in to a single
Manager launch schedule per project rather than configuring duplicate
heartbeats on independent devices. Turning it off prevents future launches
without interrupting a job already in progress.
