# Privacy and Data Handling

This document describes Jarvis's data handling for the hosted MCP service and local Jarvis client.

## Authentication

Jarvis uses Auth0 for OAuth authentication.

The hosted server validates bearer tokens and uses the token's stable subject (`sub`) to associate an MCP request with the same user's connected JarvisClient. The model does not receive or supply a Jarvis session key.

JarvisClient uses native Authorization Code + PKCE. OAuth access tokens are held only in memory. The native client persists its OAuth refresh token separately from general settings in `oauth-refresh.json` under the user configuration directory, with owner-only file permissions on Linux/macOS, to enable silent reauthentication after restart. Rotated refresh tokens replace the cached token atomically. This is a local credential and must not be shared or backed up into source control. The local client persists a randomly generated device identifier so reconnects can identify the same installation; this identifier is not an authentication secret.

Auth0 and the infrastructure hosting Auth0 may process account and authentication information according to their own service terms and privacy practices.

## Workspace access

JarvisClient can access files and folders inside the workspace selected by the user. Project-relative path and symlink checks prevent structured file operations from escaping the selected project root.

Depending on the tool and local permission decision, Jarvis may:

- list projects, directories, and file metadata;
- search file names and contents;
- read files;
- write or patch files;
- run bounded local processes;
- inspect git status and diffs;
- create local git commits;
- start, inspect, or cancel local jobs.

## Data routed through the server

The hosted server forwards MCP commands to the authenticated user's connected JarvisClient and forwards results back to the authenticated MCP caller.

Results can contain project file contents, search matches, file paths, command output, git output, and error information required to complete the user's request.

Jarvis does not intentionally persist project file contents, command arguments, command results, file paths, stdout/stderr, or commit messages in its server-side database. Data necessarily exists in server memory while requests are being routed. Hosting, reverse-proxy, operating-system, or diagnostic infrastructure may retain normal operational metadata according to deployment configuration.

Jarvis persists coarse operation telemetry for authenticated MCP tool calls. Operation records can include:

- a generated operation identifier;
- the authenticated Auth0 subject;
- tool and optional project names;
- start/completion timestamps and duration;
- success/failure and a coarse error category;
- server, client, and protocol versions.

Jarvis also provides an explicit agent-feedback tool. Feedback text is persisted only when an agent deliberately submits it and can include a summary, optional details, and an optional workaround. Feedback may be linked to a recent operation belonging to the same authenticated user.

This persisted state is stored in the Jarvis server's SQLite database. It is not intended for advertising or behavioral profiling.

Server-side operation telemetry and connection diagnostics are kept for 30 days by default. Cleanup runs at startup and hourly. Old operation records are deleted without deleting linked feedback (the link is cleared). Explicitly submitted feedback has no automatic expiry; backup retention is separate.


## Server-side state

The server keeps process-local, in-memory state required to route commands and diagnose connection state. This can include:

- the authenticated Auth0 subject;
- the local device identifier and device name;
- SignalR connection state and connection generation;
- client and protocol versions;
- registration, last-seen, and disconnect timestamps;
- the latest transport or dispatch failure;
- pending command-response correlation state.

Most live routing state is discarded when the server process is restarted.

In addition, the server persistently stores:

- the coarse operation telemetry described above;
- explicitly submitted agent feedback;
- links between feedback and operations where applicable;
- bounded SignalR connection lifecycle metadata for diagnostics, including connect/register/disconnect timestamps, connection/device identifiers, generation, client/protocol versions, connection duration, and disconnect exception type/message.

Connection diagnostics are scoped to the authenticated user and are exposed through the read-only `GetConnectionDiagnostics` MCP tool so connection failures can be investigated after the local client reconnects.

The server does not automatically persist raw command payloads, project contents, file paths, command arguments, stdout/stderr, or command results as part of this telemetry.

## Client-side state

JarvisClient stores:

- the chosen workspace for the running process;
- a non-secret device identifier in the user's application-data directory;
- OAuth access tokens in process memory and refresh credentials in the local owner-only credential cache;
- an in-memory audit log for sensitive command categories;
- local activity history in append-only JSONL files under `Jarvis/activity` in the user's local application-data directory.

Audit entries can contain timestamps, command names, project names, affected paths, executable names/arguments, permission categories, and result summaries. The audit log is not intended to contain full project file contents or OAuth credentials.

Activity logs persist visible metadata: timestamps, project and command names, compact activity messages, full reasons/details, lifecycle status, duration, result summaries, and retained failure detail. These fields can include paths, executable arguments, or informational `Message` text; activity logs do not serialize full command payloads or raw stdout/stderr. Logs rotate by local calendar day and at 10 MiB, and files older than 14 days are pruned. The latest 500 activities are restored on launch, with unfinished commands marked `Interrupted`. Persistence is best-effort.

The authenticated MCP caller can read this history through `GetClientActivity` while the client is connected. Detailed activity remains stored on the client rather than in the server's telemetry database. `Message` adds a persisted local informational entry without a permission prompt; it does not modify project files.

## Local permissions

Read-only tools can inspect the workspace selected by the user. File writes, patches, process execution, project tasks, job control, and git commits are additionally governed by JarvisClient's local permission policy.

Local approvals are independent of OAuth scopes. Possessing an OAuth scope permits the MCP caller to request an operation; it does not override a local Jarvis permission denial.

Spawned processes do not inherit environment variables whose names look credential-bearing unless the user explicitly allowlists them at client startup. Project-owned configuration cannot modify that allowlist.

Closing JarvisClient immediately removes the active local execution path for that device.

## Data sharing

Jarvis returns command results only through the authenticated MCP request path associated with the authenticated user. Jarvis does not intentionally sell project data or use project contents for advertising or behavioral profiling.

Data may be processed by infrastructure providers as necessary to provide authentication, hosting, networking, and the user's chosen MCP/AI service.

## Security

Users should expose only workspaces they are comfortable making available to their authenticated Jarvis integration. OAuth credentials must not be placed in prompts, project files, tool arguments, or logs.

## Contact

Privacy questions can be sent to admin@kehlet.dev.

_Last updated: 2026-10-06_


## Optional local browser bridge

When explicitly enabled, JarvisClient hosts a loopback-only WebSocket endpoint for a separately installed browser extension. The extension must complete a local challenge-response using a per-installation pairing secret and access to pre-existing browser tabs requires the user to **manually register each tab**. A separate extension opt-in permits agents to create and automatically register new tabs on sites with a valid manual registration and browser host permission. Registrations, selected browser tab identities, and the pairing credential are stored by the extension in browser-local storage; the client stores the pairing secret in a user-private local file. Jarvis does not store these browser registrations on the server.

The authenticated MCP caller can request a list of registered tab aliases, redacted URLs and availability states. Script execution requests can include inline JavaScript or the contents of a project-relative script file, and JSON arguments. Execution results can include DOM and page data. The Jarvis server forwards this material between authenticated MCP caller and selected local client (and corresponding result) through its existing command routing; it is present in transit and in process memory, and can be exposed to the MCP/AI service chosen by the user. The extension executes the supplied script in its authorized tab and returns bounded JSON results. Ordinary Jarvis telemetry does not intentionally persist JavaScript source, script arguments, DOM data, browser output or credentials.

Browser host permissions alone are insufficient for access: the extension also requires an explicit user-managed registration, which can be revoked at any time. A registered tab may contain a logged-in account; arbitrary scripts can read and modify data accessible to that page and submit authenticated actions. Only register tabs and run scripts you trust. Tab identity and current URL are rechecked before injection. A page-derived script result is untrusted content. Browser extensions can be disabled or removed through Chromium's extension manager, and the browser connection can be disabled with `JarvisClient config unset browser-port` followed by restarting JarvisClient. The local pairing credential can be revoked with `JarvisClient browser revoke` followed by a client restart.


### Agent-created browser tabs

By default, Jarvis cannot create browser tabs. After explicitly enabling **Allow Jarvis to open new tabs** in the extension, authorized MCP callers can request that Edge/Chromium create a new background tab on a site already authorized by a valid manually registered tab. The new tab receives an independent registration ID. It remains usable across navigation within the approved origin but never acquires access to an existing unregistered tab. The extension limits active agent-created tabs to 20. Disabling the setting revokes access to those auto-created tabs, without closing them. Navigating an auto-created tab to another origin invalidates its registration. As with any web automation, creation can complete even if the response is lost; Jarvis reports uncertainty and does not automatically retry.

## Client-local scheduled browser execution

When the browser bridge is enabled, the user may schedule one-shot browser actions. The client stores their due times, full destination URLs, project/script paths, and JSON arguments in a user-private local file named browser-schedules.json. The browser extension does not receive script content or JSON arguments until execution time. Only sanitized schedule metadata is exposed by the listing command; the full arguments and page results are not placed in normal activity logging or the public Jarvis server's persistent storage. On restarting the client, pending items remain eligible to run; an action already claimed for execution is marked uncertain and never automatically retried. Remove/cancel a pending action via CancelScheduledBrowserExecution; actions already dispatched cannot be recalled.
