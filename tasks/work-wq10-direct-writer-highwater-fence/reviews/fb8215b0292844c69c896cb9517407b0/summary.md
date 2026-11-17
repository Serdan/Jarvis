# Reviewer 2 — Accepted: WQ10 direct legacy writer replay fence

**Task:** `work-wq10-direct-writer-highwater-fence`
**Review:** `fb8215b0292844c69c896cb9517407b0`
**Exact frozen source:** `7e8a205a59c3dea2711c8b7594c0525bab70b6f1`
**Base:** `fa30f581c0a71880fa1a976b64a0f91740539957`
**Prior blocking review:** Reviewer 1 `e90bd79362d343e6a89e83a954547d25`, ChangesRequested seq 5505 on `0c23ac9bf212285bfbbb96d1fd5b25944a71f079`.

## Decision

**Accepted for the exact frozen correction only.** The P1 old-writer idempotent replay bypass is closed at all four prior upstream `LedgerStore.findApplied` fast returns, with the enabled host original-account/project epoch fence now evaluated **before** any prior success receipt can be returned.

## Independent code and Git verification

- Reviewed exact `0c23ac9..7e8a205` patch: only `Client/Modules/LegacyDirectWriterFence.fs`, `Client/Modules/ReviewWorkflow.fs`, and `Client.Tests/LegacyWriterProtocolFenceTests.fs` changed. The earlier direct side-effect source-ref publication guards remain in place.
- The new `LegacyDirectWriterFence.findAppliedForLegacyWrite` runs `preflightSourcePublication binding` and only then `LedgerStore.findApplied binding intent`. That preflight checks enabled host-scope parsing, scope protocol/authority validity, `sameProjectAuthority` before original project Git fetch, and `authorize` against original account phase and pinned account/project heads.
- All four `ReviewWorkflow` call sites use this helper: `submitIntegration` line 1239, `completeIntegration` line 1313, `submitBranchReview` line 3386, `replaceBranchReview` line 3437. No raw `LedgerStore.findApplied` direct call remains in that module.
- Clean registered checkout HEAD and both remote source branch and immutable `jarvis-review` ref independently resolve to exact `7e8a205a59c3dea2711c8b7594c0525bab70b6f1`. The source checkout remained clean after tests.
- `git diff --check fa30f581..7e8a205a`: exit 0.

## Independent local .NET 11 Release evidence

- `dotnet --version`: **11.0.100**.
- Explicit `dotnet build Client.Tests/Client.Tests.fsproj -c Release`: **exit 0; zero errors**, three unrelated pre-existing warnings.
- Actual NUnit VSTest execution `dotnet vstest Client.Tests/bin/Release/net11.0/Client.Tests.dll --TestCaseFilter:FullyQualifiedName~LegacyWriterProtocolFenceTests`: **11/11 passed**, zero skipped/failed. This includes the new two-original-clone regression exercising all four *same-UUID* replay paths: default-off and enabled `LegacyActive` preserve prior idempotent receipts; `Frozen` and `Preparing` deny old-writer replay; stale account epoch, forged project ledger authority, and missing enabled host binding fail closed; original project ledger head and managed review/integration refs remain unchanged.
- Explicit `dotnet build Server.Tests/Server.Tests.fsproj -c Release`: **exit 0; zero errors**, ten unrelated warnings.
- Actual `dotnet vstest Server.Tests/bin/Release/net11.0/Server.Tests.dll`: **294/294 passed**, zero skipped/failed.
- A broader optional adjacent Client run (ReviewWorkflowTests plus WorkQueueReviewIntegrationAdapterTests) was stopped before finishing and is **not** claimed as passing evidence.

## Boundary

This is **not** a public Work Migration/Scheduler/retirement/cutover GO. The synthetic frozen fixture does not prove original all-client pending intent journals, source-effect high-water, minimum connected/disconnected client protocol, or cross-account/project atomic freeze. No live freeze/cutover, real migration, hosted Actions, or Codex was performed.
