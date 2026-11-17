# Reviewer 1 — ChangesRequested on exact frozen 0c23ac9bf

**Task:** `work-wq10-direct-writer-highwater-fence`  
**Review:** `e90bd79362d343e6a89e83a954547d25`  
**Source:** `0c23ac9bf212285bfbbb96d1fd5b25944a71f079` (independent clean registered worktree, HEAD exact)  
**Base:** `fa30f581c0a71880fa1a976b64a0f91740539957`

## Decision

**ChangesRequested** — one blocking direct old-writer bypass of the post-freeze idempotent replay denial. See `findings/direct-idempotent-replay-fastpath.md`.

At `Client/Modules/ReviewWorkflow.fs:1239,1313,3386,3437`, the four `LedgerStore.findApplied` success fast paths return *before* the newly introduced preflight/transaction fence. This bypasses post-freeze denial on previously applied old-writer UUIDs, even though no new Git CAS or source-ref publication is proven to occur on replay. The new test only checks post-freeze duplicate UUID via `registerAgent`, whose implementation does run the fence. Guard these early returns and add concrete frozen/Preparing replay-negative tests for review submit/replace and integration submit/complete, retaining default-off/LegacyActive idempotency.

## Verified positive scope

- Exact diff: 8 files. New default-off `LegacyDirectWriterFence`; all direct `ReviewWorkflow` transaction sites and role/challenge transition writes routed through it; preflights before the three immutable source-ref publications.
- Original project/account Git authority verified afresh, no actor/kind/Manager label opt-out; stale epoch/heads are denied; optimistic project Git CAS retry rechecks the account epoch; historical reads and Work-native paths preserved.
- `inspectHighWater` reports the original project operation sequence but **explicitly Unknown** for all-client original intent journal, source-effect journal and minimum client protocol. Original cross-account/project freeze remains non-atomic; older disconnected binaries/raw ledger callers bypass the opt-in scope. No production fleet/cutover GO.
- `git diff --check` passed; independent review worktree clean with HEAD still at the exact reviewed SHA.

## Independent local test evidence

- `dotnet --version` = **11.0.100**.
- Explicit `dotnet build Client.Tests/Client.Tests.fsproj -c Release`: exit 0, 0 errors (three pre-existing style/compiler warnings).
- **Actual execution**: `dotnet vstest Client.Tests/bin/Release/net11.0/Client.Tests.dll --TestCaseFilter:FullyQualifiedName~LegacyWriterProtocolFenceTests --Logger:console;verbosity=normal`: **10/10 passed**, including two-clone project CAS/retry, Frozen direct Agent/role/challenge denial, old UUID for guarded register-agent, high-water Unknown and no orphan review ref.
- Explicit `dotnet build Server.Tests/Server.Tests.fsproj -c Release`: exit 0, 0 errors (ten unrelated warnings). Actual VSTest execution: **294/294 Server tests passed**.
- The `dotnet test ...` target in the fresh .NET11 checkout returned exit 0 *without building a test DLL or running cases*, so its success is **not** counted as evidence; explicit build + VSTest were used instead.
- A broad Client VSTest run was started; no result is asserted here without a completed result.

This review neither activates retirement/migration/Scheduler nor authorizes production freeze/cutover, hosted Actions or Codex. An updated implementation requires a newly frozen source commit and new independent review.