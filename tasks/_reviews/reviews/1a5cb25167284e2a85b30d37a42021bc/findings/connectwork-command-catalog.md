# P1 — ConnectWork omitted from canonical MCP command catalog

**Frozen source:** `6c291da9ab43217fcaa732952386ae85601c395d` (base `bed92f3d2b39ef7c256a8d1955c48883af99cb60`).

## Evidence

1. `Server/McpTools.fs:2935-2950` adds the exported `ConnectWork` MCP method, accepting an explicit model-supplied `credential` and forwarding `FirstConnect`.
2. `Server/McpTools.fs:393-396` builds the public WorkQueueWrite aliases for `ListCommands` using only `ClaimNextWork`, `CheckpointWork` and `CompleteWork`. **`ConnectWork` is absent.** The internal `WorkQueueWrite` wire capability is filtered out in `Server/McpTools.fs:401-410`, so `ListCommands` cannot advertise the new public method.
3. Independent `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore` on this frozen checkout exited **1**:
   - `Server.Tests/McpAuthenticationTests.fs:191-195` expects 67 write tools but observes **68** after the addition; its explicit Work list also omits `ConnectWork`.
   - `Server.Tests/ServiceTests.fs:606-616` expects 159 client-backed MCP tools but observes **160**.
   - `Server.Tests/ServiceTests.fs:528-529, 1102-1105` retains the old five public Work aliases and fails catalog-to-MCP parity.
4. Checkout is clean, `git rev-parse HEAD` matches the pinned review source and `git diff --check bed92f3d... 6c291da9...` exits **0**.

## Impact

The ordinary MCP endpoint exists but command discovery omits it; authoritative tool inventory/catalog parity is broken and the server Release suite is red. Clients which discover supported tools through `ListCommands` will not discover `ConnectWork`. This is a release-blocking protocol/documentation drift, not a credential cryptography failure.

## Required fix before acceptance

- Register `ConnectWork` in `Server/McpTools.fs`'s WorkQueueWrite aliases, preserving the OAuth `git:write` capability and default-off Work-write semantics.
- Update server test inventories, count assertions, and metadata/command-catalog parity to cover **all four** Work-write aliases, especially explicit `credential`, optional `operationId`, optional `deviceId`, and no legacy-agent ownership.
- Run the full local .NET 11 Release `Server.Tests` suite green, keep the source frozen for a new independent review; do not enable live launch or Work writes.

The focused credential and adjacent client suites independently passed (6/6 and 26/26); this finding is confined to the server tool surface/catalog and its tests.