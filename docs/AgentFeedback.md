# Agent Feedback and Operation Telemetry

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

No automatic deletion in the first implementation.

The schema includes timestamps so retention can be added later without migration difficulty. The database should be backed up with the rest of server state.

## Tasks

- [x] Add SQLite persistence and schema initialization.
- [x] Record privacy-minimal MCP operation telemetry.
- [x] Add server-local `Feedback` MCP tool.
- [x] Add safe implicit/explicit operation linkage.
- [x] Add `ListFeedback` and `GetFeedbackSummary`.
- [x] Add validation and bounded query limits.
- [x] Add storage and MCP tests.
- [ ] Deploy database directory, server update, and verify persistence.
- [ ] Commit and leave the repository clean.
