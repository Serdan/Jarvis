# Independent P1 follow-up re-review — auto-promotion and cross-project notification routing

**Verdict: Accept.** Prior two P1 blocking findings in review `35714cc484b644ea811e621b512151fd` are corrected at frozen source `c7115472eb0cda5009cfc940265bd818a42f5b2f`. Previous frozen source `2349cb9c1e5b9ccdd15817224146882534039988`; pinned task base `28fd0c3d2fa887a8cd7289e07cb65b476de351f9`; branch `feature/auto-promote-tool-jobs`.

## Fix 1 — pending verified-ledger health no longer authorizes unverified destination routing

- `Server/Services/ClientService.fs`: `ProjectNotificationRouting.resolveHealth` recognizes a `PendingJob` and polls the **same** origin device, with original initiating actor, project, typed job ID/status/terminal response checks and a bounded deadline. It cannot treat an accepted `Running` handle as a valid ledger. Device connection ID and session generation are rechecked immediately before dispatch.
- For read-only failed health, server can reject unhealthy candidate and check next eligible device. **Once mutation is dispatched it is never retried on a different device**. Destination `PendingJob` binds `DeviceId` to that verified device, rather than later polling an implicit default.
- `Server/McpTools.fs` source-agent validation also polls its original `DeviceId` via `SendCommandToUser`, rather than routing a newly generated poll to whatever device currently owns the source project.
- New `Server.Tests/ServiceTests.fs` actual `ClientService` routed multi-device fixtures assert pending unhealthy first candidate rejects, successful pending verification uses exact original candidate/actor, and a pending destination returns bound device handle and does not repeat a notification.

## Fix 2 — source/destination state evidence preserved with pending cross-project writes

- `Server/McpTools.fs` no longer prematurely merges a verified source list with a `null` unknown destination `AgentStateChanges`. Instead it constructs a private `CrossProjectNotificationCommand` carrying the already-verified source mutations and activation receipt.
- Client normalizes the internal envelope to the ordinary TaskWrite command **before execution**; a terminal `combineCrossProjectNotification` response is persisted in the original bounded client-owned tool job. The initial pending response correctly keeps unknown effects `null`. The terminal poll returns the full combined typed response without replaying ledger writes.
- `Client/Modules/JobManager.fs` uses `PreserveEvidence` for queued cancellation, exception, dispatch failure, and completed response, so verified source effects remain observable even if destination never started or failed.
- Added Client regressions exercise terminal success/error, repeated polling/idempotent replay, and cancellation before execution preserving source activation; Server tests assert unknown pending changes and exact device. Internal envelope is intentionally excluded from the public tool command catalog; MCP authorization and normal client permission policy remain applied as `TaskWrite`.
- Prior independently reproduced `NullReferenceException` from `mergeStateEffects` no longer occurs for a pending destination; the client-owned result now holds the complete source evidence on terminal paths.

## Independent verification

- Full **Client Release 559/559 passed**, job `d6bd34a7fa014e6bbf037c4bbf8c3075`, exit 0.
- Full **Server Release 133/133 passed**, job `4adb08f2de524e22af711abf9345e190`, exit 0.
- Exact frozen `git rev-parse HEAD` matches `c7115472...`, worktree clean. `git diff --check 28fd0c3d..c7115472` exit 0, `git merge-tree --write-tree main c7115472` conflict-free exit 0, resulting tree `496e544d9f7a61c63fbb6dac6443a29f50fd570c`.
- The original broader job registry, SignalR bounded callback ingress and typed polling behavior were previously inspected. This focused re-review examined the precise new production fixes and negative regressions.
- No new blocking defect found on frozen correction. Remaining deployment concern: protocol **3.64** requires coordinated server and client upgrades and human approval, with integration compatibility tests after merging other branches.

**Verdict: Accept.** Technical review only. No integration or deployment performed or authorized.
