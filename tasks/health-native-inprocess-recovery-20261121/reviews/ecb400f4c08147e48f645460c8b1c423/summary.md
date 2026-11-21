# Independent exact-source review — Accepted

Review request: ecb400f4c08147e48f645460c8b1c423
Task: health-native-inprocess-recovery-20261121
Owner: Worker 18
Frozen candidate: `aab048c39bad208c058c419b142653a040ff82ab`
Base: `d702352ef705f9991ac1cb14dc15a13cce78aa44`
Prior ChangesRequested: review `8607e341622c48b59ae82a5c6a270df4` on `db01a9c9aecec1bf639eba921f3abaf88bea207c`.
Authoritative bounded adjudication: Adjudicator 1 checkpoint seq10078 (task-version-CAS ACCEPT, protocol-evidence NARROW).

## Source and requirements inspection
- Independently verified the exact frozen commit and clean, unchanged Worker 18 worktree; main remains at the specified base. Ten paths differ from base; the adjudicated corrective commit changes only four paths: `Client/Modules/ReviewWorkflow.fs`, `Client/Modules/OrchestrationApi.fs`, `Client/Modules/NativeHealth.fs`, and `Client.Tests/NativeHealthTests.fs`. `git diff --check` exit 0.
- Native Health stays default-off, periodically reads config and an authenticated server activity/clock snapshot, holds on missing/invalid activity or changed Git state, filters canonical ineligible states, and uses an existing direct Git recovery instead of browser execution, MCP calls, or a replacement launch. No server/SQLite or legacy BrowserSchedules source was modified.
- The original task-only interleaving is now fenced: `NativeHealth.recover` passes `ExpectedTaskVersion` through `OrchestrationApi.TaskApi.write` to `recoverAgentAssignmentWithTaskVersionAs`, which checks the exact task version *inside* `LegacyDirectWriterFence.transact` on retries alongside attempt/agent versions, lifecycle, owner and generation. The historical pre-versioned entrypoint and receipt normalization remain compatible.
- The new canonical task-only mutation test asserts rejection with unchanged attempt/agent/generation. Real Ready/Deferred/Blocked ledger state transitions are tested. Two independent fixture Git clients run concurrent native `checkOnce`/recovery with the same deterministic operation ID, verifying one logical generation advance and a receipt. The lost-ACK test injects failure *after* canonical publication while using the actual pending-intent journal and the native recovery adapter's original-ID status lookup/reconciliation. These are bounded deterministic adapter fixtures, not a real network outage.
- No concrete unfulfilled original task requirement or adjudicated correction found in the frozen source.

## Independently executed checks (frozen source, .NET 11 Release)
1. `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~NativeHealthTests --verbosity minimal` — exit 0, **21 passed, 0 failed, 0 skipped**; job `393a0952363c4b77b1df568514c574b2`.
2. `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~ClientConfig|FullyQualifiedName~BrowserSchedules' --verbosity minimal` — exit 0, **37 passed, 0 failed, 1 skipped**; job `5da1df43f14c4e1187faf01cda9f50c0`.
3. `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~NativeHealth|FullyQualifiedName~ReviewWorkflow|FullyQualifiedName~BrowserSchedules' --verbosity minimal` — exit 0, **195 passed, 0 failed, 1 skipped**, duration 2m50s; job `59c0a99d65b0498a9934078126dc2686`.
4. `git diff --check` — exit 0. Post-test HEAD matches candidate and the worktree has no tracked changes.

F# build warnings were nonfatal; the skipped legacy BrowserSchedules test is explicitly reported and is not counted as a pass.

## Disposition and limits
**Accepted** on the exact frozen candidate SHA for this source-only Native Health task and its adjudicated corrections. Acceptance is **not** an assertion of an independently integrated server `GetAgentActivity`/SQLite provider, deployed SignalR runtime, real service failure injection, live multi-client recovery, scheduler activation, or approval to integrate/cut over. Keep the native feature disabled and the existing Git ledger authoritative until separate integration and explicit Operator release gates are satisfied.