# P1 — DOM capture/export MCP tools fail before dispatch due to incomplete authorization match

**Frozen source:** `b9dc25bc5340aa8cb043a75d4e47e69d0c3e9679`, merged protocol 3.63 browser bridge.

## Evidence

`Server/McpTools.fs:455–513`, `McpToolHelpers.requiredScope`, is a pattern match over `AgentCommand` but **does not include either `BrowserCaptureDomCommand _` or `BrowserExportDomCommand _`** in any branch. It has no wildcard branch.

`McpToolHelpers.send` at `Server/McpTools.fs:657–674` calculates `let scope = requiredScope command` *before* the authenticated user/scope check or `ClientService.SendCommandToUser` dispatch. The two new public MCP tools (lines 1845–1874) send precisely those missing cases.

An independent Release Server build emits the confirming diagnostic:

`Server/McpTools.fs(456,15): warning FS0025: Incomplete pattern matches ... 'BrowserCaptureDomCommand (_)' may indicate a case not covered.`

Thus both DOM read and file export tool calls fail with `MatchFailureException` before reaching the client, regardless of valid tab authorization, permission or browser state. The 127 passing Server tests do not exercise these tools through the public `McpToolHelpers.send` scope path; grep of `Server.Tests` shows no direct tests for either.

## Required change

Make `requiredScope` exhaustive, with `BrowserCaptureDomCommand` requiring at least `Auth.ProcessExecute` and `BrowserExportDomCommand` enforcing the declared `WorkspaceWrite` and `ProcessExecute` privileges (currently the helper returns a single scope; ensure server-side enforcement matches the two-scope MCP declaration rather than relying only on tool metadata or local client approval). Add routed Server regression tests invoking **both** actual public MCP tools with authenticated contexts; assert authorized requests reach the client and insufficient scopes are rejected normally, without exceptions. Treat FS0025 on security dispatchers as a blocking compiler warning.

This is independent of the already fixed client-side `AgentCommandInfo.detail` nonexhaustive match; that client-side fix is verified.

**Severity:** P1 — both central new DOM features are unusable through their published MCP API, with an authorization coverage gap.
