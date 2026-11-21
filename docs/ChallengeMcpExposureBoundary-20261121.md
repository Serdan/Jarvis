# Challenge MCP control-surface exposure: source-to-connector boundary (2026-11-21)

**Canonical task:** `challenge-canonical-mcp-control-surface-20261121` (pinned spec `aff6167838edad50f189b3f34b0195e40cf2886c`; Worker 21 attempt `b94f9ad5266d4ff19e11202611210021` generation 1).

**Scope:** read-only discovery and isolated Release verification. This document is not a Challenge implementation, deployment approval, cutover approval, or proof of the connected ChatGPT tool surface being fixed.

## Exact source and observations

- Inspected clean `main` at `d702352ef705f9991ac1cb14dc15a13cce78aa44` in registered isolated worktree `challenge-mcp-surface-worker21-20261121` (`refs/heads/work/challenge-mcp-surface-worker21-20261121`). Worktree was registered and bound to this canonical assignment before writing this report.
- `Server/McpTools.fs:373-397` maps two Challenge reads and nine Challenge writes from the existing typed protocol to the public catalog. `Server/McpTools.fs:2780-2980` implements **all eleven** as public `[<McpServerTool>]` methods: `ListChallenges`, `GetChallenge`, `CreateChallenge`, `SetChallengeSubscription`, `AssignChallengeCondition`, `SplitChallengeCondition`, `ResolveChallengeCondition`, `ReopenChallengeCondition`, `CancelChallenge`, `BlockOnChallenges`, `RequeueChallengeBlockedAttempt`. They forward typed `ChallengeReadCommand`/`ChallengeWriteCommand` requests through `McpToolHelpers.send`, retaining actor fields, CAS versions, optional operation IDs and existing errors. No replacement storage or direct Git mutation is present in these adapters.
- `Server/Program.fs:100-106` configures `.WithTools<JarvisMcpTools>()` at the MCP endpoint. `plugin/mcp.json` specifies `https://jarvis2.kehlet.dev/mcp`, with no per-tool allowlist in the manifest. `docs/PluginSubmission.md:1-3` identifies this as the publicly submitted remote-MCP plugin.
- The connected `ListCommands` response reports protocol **3.68** and the eleven Challenge command names. That is a **wire capability catalog**, not `tools/list` and not proof that the connected model can invoke each name.
- In this Worker 21 ChatGPT session the actual callable Jarvis connector catalog contains **130 tools**, and **zero of the eleven** Challenge tool names. `get_response_alerts` is also absent despite its in-repository adapter at `Server/McpTools.fs:2003-2004`. A read-only `rg -c 'McpServerTool\\(' Server/McpTools.fs` reports **174 annotated method lines** in the source. The mismatch is broader than Challenges; neither the count nor the ordering proves a specific ChatGPT hard cap.
- The source has exhaustive reflection/catalog/OAuth-metadata tests: `Server.Tests/ServiceTests.fs:468-545,1052-1120`. These test the source assembly, **not an authenticated deployed MCP `tools/list` response**.

## Actual isolated .NET 11 Release execution

| Command and target | OS exit | Cases |
|---|---:|---:|
| `dotnet test Server.Tests/Server.Tests.fsproj --configuration Release --filter FullyQualifiedName~mcp_tool_oauth_metadata_matches_command_permissions --logger 'console;verbosity=normal'` | 0 | 1 passed / 1 total |
| `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-build --no-restore --logger 'console;verbosity=minimal'` | 0 | 555 passed / 555 total, 0 skipped or failed |
| `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter 'FullyQualifiedName~OptionalAssigneeSubscriptionAndCASDispatchWithoutClaimOrNotifications|FullyQualifiedName~TwoDependentChallengesRequireExplicitRequeueWithoutLosingAttemptOrOwnership|FullyQualifiedName~ChallengeReadsDoNotActivateReadyAgentOrPublishCanonicalChanges' --logger 'console;verbosity=minimal'` | 0 | 3 passed / 3 total, 0 skipped or failed |

The first Server test is included in the later 555, not 556 independent cases. The client tests use disposable Git fixtures and cover subscription=false vs true inbox/triage, denial of unauthorized access, CAS version failures, idempotent operation IDs, condition resolution/requeue and no read-side activation. This evidence does **not** substitute for an end-to-end ChatGPT-connected MCP invocation.

## Boundary and smallest authorized next step

The eleven adapters are present, registered and source-tested. Do not duplicate them or mutate Worker 11's response-alert MCP lease or Worker 2's separate role-focused plugin catalogs. The remaining *unresolved evidence boundary* is between **the deployed server's authenticated MCP tool discovery** and **ChatGPT's published/connected plugin tool catalog**. Source inspection alone cannot distinguish deployed binary mismatch, platform publication/refresh, tool selection, or a tool-count limit.

1. **Owner: Lead / authorized operator for connector publication.** Obtain one read-only authenticated `tools/list` response directly from the live `https://jarvis2.kehlet.dev/mcp` under the relevant connection, recording whether the eleven exact method names appear. Do not send Challenge writes as part of this check.
2. If the deployed `tools/list` **includes** them, reconcile ChatGPT's connected/published plugin catalog (including Worker 2's role-focused catalog work); refresh/re-publish or select an authorized catalog as appropriate. Do not change source implementations to treat a publication mismatch.
3. If deployed `tools/list` **does not include** them, compare the deployed server binary/release to reviewed registered source and resolve the deployment/registration difference through independent review and explicit operator-controlled server release authorization. Do not deploy or restart as part of this task.
4. After a callable surface is verified and separately authorized, perform MCP end-to-end read/write tests **only against an isolated disposable ledger**: subscribe true, read inbox triage, unsubscribe/list visibility, cross-project/actor failure, stale CAS, duplicate resolution and exact-ID reconciliation, then blocked attempt requeue. Production Challenge state remains out of scope.

**Status at this checkpoint:** source-side registration was already implemented; documentation-only scope report prepared for independent review; ChatGPT exposure remains **unverified/unresolved**. No production Challenge mutation, live prompt send, schedule change, server/client deployment, or main-branch edit was performed.
