# Durable execution scheduling

**Source/operational boundary (verified 2026-11-21):** The connected public command catalog reports protocol **3.68** and advertises `ScheduleExecution`, `ListScheduledExecutions`, `GetScheduledExecution` and `CancelScheduledExecution` alongside the legacy `ScheduleBrowserExecution` family. Tool advertisement is not an activation or deployment-acceptance receipt. The client-local schedule runner is distinct from both the account-wide deterministic agent Scheduler and the separate default-off Work Queue Scheduler. No unattended Manager/agent scheduling is authorized; the Operator stopped the old schedules and the model-based Manager is retired/Parked. The tracked Jarvis Manager heartbeat is disabled. This document describes implementation and failure behavior, **not** a live-run instruction.


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
  before re-arming; if none arrives for 30 minutes, one replacement is
  eligible only after fresh authoritative activity verification.

A historical **unactivated** Manager example uses `triggerKind="AfterAgentIdle"`,
`idleAgentId="Manager"`, and `idleSeconds=900`. Do not register, enable, or
reactivate it. Current `Client/Program.fs` deliberately supplies
`ActivityProofUnavailable` instead of independent proof of inactivity, so
this trigger must fail closed and cannot authorize an idle launch.

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

### Idle proof and no-replay limits

The generic scheduling state machine contains a 30-minute stale-attempt path
for `AfterAgentIdle`, but production `Client/Program.fs` deliberately
returns `ActivityProofUnavailable` for its independent verifier. Therefore
those code paths are **not evidence of a working, safe Manager heartbeat**.
The legacy `attributed_agent_activity` records in
`Server/Services/FeedbackStore.fs` are held in a disposable server SQLite
store. Missing/reset telemetry, local notification hints, an empty activity
result or a browser tab closing **cannot prove agent inactivity**. Never
use these as permission to start or replace an agent launch.

An authenticated verification failure must fail closed; a claimed one-shot
occurrence is never automatically replayed after a restart or uncertain
browser Send. At the pinned source, a recurring/idle `outcomeUnknown`
occurrence can still advance/re-arm, which is an **open safety defect**:
no replacement attempt may be treated as authorized merely because it is
due. Repeated `WindowUnavailable:needsReauthorization` retries and inability
to cancel a `suspended` schedule by the ordinary cancel path are also open.
The persisted-`suspended` restore fix is already in main at `9c2ae32e5`;
restore does **not** automatically re-enable suspended entries. In
`Client/Modules/BrowserSchedules.fs`, recurring `UiIncompatible:`
currently maps to a failed occurrence and advances to the next interval,
whereas a one-shot `UiIncompatible:` suspends. Do not describe recurring
UI incompatibility as terminal suspension until that behavior is integrated.

The exclusive file lock elects one executor for a *single local schedule
file*, not a global leader across different machines. Opt in to a single
Manager launch schedule per project rather than configuring duplicate
heartbeats on independent devices. Turning it off prevents future launches
without interrupting a job already in progress.
