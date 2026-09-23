# Privacy and Data Handling

This document describes Jarvis's data handling for the hosted MCP service and local Jarvis client.

## Authentication

Jarvis uses Auth0 for OAuth authentication.

The hosted server validates bearer tokens and uses the token's stable subject (`sub`) to associate an MCP request with the same user's connected JarvisClient. The model does not receive or supply a Jarvis session key.

JarvisClient uses native Authorization Code + PKCE. OAuth access and refresh tokens are held in client memory while the process runs. Jarvis does not intentionally persist OAuth tokens to disk. The local client persists a randomly generated device identifier so reconnects can identify the same installation; this identifier is not an authentication secret.

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

Jarvis does not intentionally persist project file contents or command results in a server-side database. Data necessarily exists in server memory while requests are being routed. Hosting, reverse-proxy, operating-system, or diagnostic infrastructure may retain normal operational metadata according to deployment configuration.

## Server-side state

The server keeps process-local, in-memory state required to route commands and diagnose connection state. This can include:

- the authenticated Auth0 subject;
- the local device identifier and device name;
- SignalR connection state and connection generation;
- client and protocol versions;
- registration, last-seen, and disconnect timestamps;
- the latest transport or dispatch failure;
- pending command-response correlation state.

The state is discarded when the server process is restarted unless hosting infrastructure independently records related operational logs.

## Client-side state

JarvisClient stores:

- the chosen workspace for the running process;
- a non-secret device identifier in the user's application-data directory;
- OAuth tokens in process memory;
- an in-memory audit log for sensitive command categories.

Audit entries can contain timestamps, command names, project names, affected paths, executable names/arguments, permission categories, and result summaries. The audit log is not intended to contain full project file contents or OAuth credentials.

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

_Last updated: 2026-09-24_
