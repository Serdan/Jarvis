# Agent Feedback and Operation Telemetry

## Multiple Jarvis clients and conversation routing

Jarvis retains independent server-side sessions for each authenticated user and persistent device ID. The first registered device is the process-local fallback default. `ListDevices` exposes registered devices, connections, capabilities, and this conversation's selected device. `GetConnectionDiagnostics` includes the global fallback and conversation selection. Device discovery and selection are always scoped to the OAuth user.

The MCP SDK call-tool filter reads the documented `_meta["openai/session"]` string for each `tools/call` request. `SelectDevice(deviceId)` associates that conversation with a device using the `(authenticated user, openai/session)` key. Other conversations under the same OAuth account retain their selections. If `openai/session` is absent, `SelectDevice` fails instead of mutating another agent's routing. Commands without session metadata and without an explicit override use the process-local fallback device.

All 33 client-backed MCP tools also expose an optional `deviceId` argument that overrides conversation selection **for that call only**. Requests can alternatively carry `_meta["jarvis/deviceId"]` for explicit routing; the tool argument takes precedence. Invalid or unknown device targets must never silently redirect to another device. Commands aimed at a disconnected device fail rather than falling back to another connected computer.

The conversation mapping and global fallback are currently server-memory state: they reset on server restart. A persistent user-preferred fallback may be implemented separately, but must not introduce cross-conversation mutable routing. Client software does not need new device-selection logic; the change is on the server and the MCP tool schemas.


## Resolution workflow

Feedback records default to `Open` and expose `Status`, `ResolvedAt`, `ResolutionCommit`, and `ResolutionNotes` in `ListFeedback` and the new `GetFeedback(feedbackId)` MCP tool. Use `UpdateFeedback(feedbackId, status, resolutionCommit, resolutionNotes)` to set `Open`, `InProgress`, `Resolved`, or `WontFix` (case-insensitive). Closing records sets a resolution timestamp and stores optional commit and notes. Reopening clears the resolution details. Every lookup and update is scoped to the authenticated user, without requiring a connected client.

The SQLite migration is additive and preserves existing feedback with the default `Open` state. Updating feedback requires the `workspace:read` OAuth scope and no local client approval. Resolution metadata follows the feedback retention policy independently of expiring operation telemetry.


## Goal

Give agents a first-class, low-friction way to report problems, limitations, useful workarounds, and positive ergonomics while they are using Jarvis.

Feedback is server-owned. It must not be dispatched to the local client and must never require local approval.

## Storage

Use one SQLite database on the Jarvis server.

Production default:

```text
/var/lib/jarvis/jarvis.db
```

The path can be overridden for development/tests.

SQLite runs in WAL mode with foreign keys enabled.

## Privacy model

Automatic operation telemetry is deliberately coarse. Do not persist:

- command payloads;
- command arguments;
- paths;
- file contents;
- stdout/stderr;
- commit messages;
- MCP request bodies;
- command results.

Automatic operation rows contain only identifiers and coarse execution metadata.

Free-form text is stored only when an agent explicitly submits feedback.

## Operations

Every normal Jarvis MCP tool invocation receives a server-generated operation ID.

Persist:

- operation ID;
- creation/completion timestamps;
- authenticated user ID;
- MCP tool name;
- project name when it is already a first-class tool parameter;
- success/failure;
- coarse error category;
- elapsed milliseconds;
- server version;
- connected client version and protocol version when available.

Operations are recorded around the server MCP boundary, not in the local client.

A failed dispatch is still an operation.

## Feedback

Feedback fields:

- feedback ID;
- timestamp;
- authenticated user ID;
- optional project name;
- optional tool name;
- category;
- severity;
- summary;
- optional details;
- optional workaround;
- optional linked operation ID.

Categories:

- `ToolFailure`
- `ToolLimitation`
- `Ergonomics`
- `MissingCapability`
- `Documentation`
- `Positive`
- `Other`

Severities:

- `Info`
- `Friction`
- `Blocking`

Feedback is append-only through the MCP surface.

## MCP surface

### `Feedback`

Server-local write operation. It does not mutate a project and does not require local approval.

Inputs:

- optional `projectName`;
- optional `toolName`;
- `category`;
- `severity`;
- `summary`;
- optional `details`;
- optional `workaround`;
- optional `operationId`.

Returns the created feedback ID and timestamp.

If `operationId` is omitted, Jarvis may link the feedback to the authenticated user's most recent operation when the supplied tool/project context agrees. It must not guess across unrelated context.

### `ListFeedback`

Read-only server-local query for recent feedback. Supports bounded filtering by tool, project, category, severity, and limit.

### `GetFeedbackSummary`

Read-only aggregate query returning counts grouped by tool/category/severity plus common workaround presence and linked-operation failure counts. No generated evaluative ranking.

## Operation linkage

Explicit `operationId` always wins.

For implicit linkage:

1. Find the authenticated user's most recent completed operation.
2. Require it to be recent (maximum 10 minutes).
3. If `toolName` is supplied, it must match.
4. If `projectName` is supplied, it must match.
5. Otherwise leave `operationId` null.

Feedback must never be linked to another user.

## Retention

The optional server SQLite store is not a canonical command receipt or project ledger.
Routine age-based retention removes up to 128 expired operation rows and 128
connection events at startup/hourly (30-day horizon); linked feedback survives
normal operation expiry. Explicit feedback and attributed activity rows are
never deleted by quota maintenance.

The default limits remain 16 MiB of SQLite database pages and 32 MiB combined
main DB, WAL, SHM and rollback journal files. The constructor's smaller
test/host-supplied budget also reduces the main page ceiling to at most half
the aggregate limit. These are write-admission limits, not a filesystem quota:
an already admitted SQLite transaction can temporarily grow its sidecars.

- At 80% of either limit, new expendable operation/connection posts are shed.
  Explicit feedback/activity writes remain subject to the hard limits and
  activity proof still fails closed on missed or late writes.
- At the hard limits, new write admission fails with `TelemetryQuotaExceeded`.
  Existing history and feedback reads use read-only SQLite without initializing
  a missing database. The authenticated connection diagnostic response always
  includes process-local device state, with `historyUnavailable` populated if
  persisted events cannot be read.
- Each minute, the existing single physical worker can attempt one bounded
  pressure pass. It checkpoints WAL, deletes at most 128 oldest connection
  events and 128 oldest unlinked operations, then checkpoints again. When
  live pages fit below the recovery watermark, transactional VACUUM can reclaim
  physical pages. Admission checks filesystem free space and refuses a legacy
  main file over one aggregate budget or a total over twice that budget.
  The SQLite busy timeout is one second and a two-second VM progress budget
  bounds SQL work; an uninterruptible I/O stall retains the single worker.
  Expendable writes resume below both 65% watermarks.
- `storageStatus` reports coarse pressure/maintenance state; server-local
  warnings record precise aggregate/live-page and protected feedback footprint
  where SQLite dbstat is available. A blocked checkpoint or incomplete pass
  retries on a later maintenance turn. Protected-only saturation, corrupt or
  unsupported legacy files, or inadequate free space require an operator
  decision. Do not delete SQLite sidecars or feedback to clear the error.

For recovery, see [diagnostic quota recovery](diagnostic-quota-recovery.md).

## Tasks

- [x] Add SQLite persistence and schema initialization.
- [x] Record privacy-minimal MCP operation telemetry.
- [x] Add server-local `Feedback` MCP tool.
- [x] Add safe implicit/explicit operation linkage.
- [x] Add `ListFeedback` and `GetFeedbackSummary`.
- [x] Add validation and bounded query limits.
- [x] Add storage and MCP tests.
- [x] Deploy database directory, server update, and verify persistence.
- [x] Commit and leave the repository clean.

## Verification

- Full solution build: 0 warnings, 0 errors.
- Client tests: 108/108 passed.
- Server tests: 25/25 passed.
- Production service uses `StateDirectory=jarvis`, `JarvisDatabasePath=/var/lib/jarvis/jarvis.db`, and a state-directory bundle extraction path.
- Production SQLite database is owned by the `jarvis` service account.
- Live MCP calls create operation rows and finalize them with success/failure and duration.
