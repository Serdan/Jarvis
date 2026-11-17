# P1 — ConnectWork is missing from the authenticated MCP capability catalog

**Frozen source:** `6c291da9ab43217fcaa732952386ae85601c395d` (base `bed92f3d2b39ef7c256a8d1955c48883af99cb60`).

## Reproduction

In the exact clean source worktree `uuidv4-explicit-credentials-w2-20261117`, run:

```
dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore --logger "console;verbosity=minimal"
```

Actual .NET 11 Release result: **290 passed, 5 failed, 295 total**. Every failure is attributable to newly exposed `ConnectWork` not being registered in the manually maintained protocol/capability mappings:

1. `Server.Tests/McpAuthenticationTests.fs:191` — `write_tools_expose_optional_operation_ids` expected 67 public write methods but received 68.
2. `Server.Tests/ServiceTests.fs:616` — client-backed MCP `deviceId` count expected 159 but received 160.
3. `Server.Tests/ServiceTests.fs:542` — `command catalog matches AgentCommand union and MCP tools` fails due to missing alias.
4. `Server.Tests/ServiceTests.fs:1104-1130` — `mcp_tool_oauth_metadata_matches_command_permissions` throws `Unexpected server-local MCP tool: ConnectWork`.
5. `server ListCommands catalog matches public MCP tools` fails because `ConnectWork` is missing from published capability list.

## Root cause and requested correction

The new public `JarvisMcpTools.ConnectWork` method is real (`Server/McpTools.fs`), but `Server/McpTools.fs:393-396` still maps only `ClaimNextWork`, `CheckpointWork`, and `CompleteWork` to `WorkQueueWrite` in `ledgerAliasCapabilities`. Thus `ListCommands` and metadata/reporting do not match the actual MCP surface. Register `ConnectWork` as a `WorkQueueWrite` alias, update the server-side regression expectations/mappings for the additional public write tool, and assert `git:write`/mutating semantics and optional `operationId`/`deviceId` explicitly. Run `Server.Tests` Release green before resubmitting a new frozen review.

This is a genuine protocol consistency failure, not merely a test count adjustment: adding only `+1` to assertions without catalog registration leaves the actual capability list incorrect. No live Scheduler, credential cutover, or hosted CI is required to reproduce.
