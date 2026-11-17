# P1 — Task-spec source operations silently use main checkout instead of selected worktree

## Frozen scope
Review e47e41dea4a044c1b089974a615c8e18; exact source commit `36a7d5dc86ce1b2371f21ff46dbfb9bcfa2aef19` against `efe769e06b34451e007198a050d227947fd83410`.

## Evidence / call chain
1. `Client/SignalR/Client.fs:404-407` explicitly requires and passes the resolved source checkout to `TaskApi.writeForContext` for `ExportTaskSpec` and `ImportTaskSpec`.
2. Reviewed change in `Client/Modules/OrchestrationApi.fs:172-177` changes `LedgerBindingConfig.loadForContext` from `ResolvedProject.sourceRoot` to `ResolvedProject.mainRoot`, correctly centralizing the **ledger**. But `TaskApi.writeUsingContext` at :1066-1079 and :1123-1126 passes only that canonical-root binding to the export/import workflows; the selected `ResolvedProject.sourceRoot` is lost.
3. `Client/Modules/ReviewWorkflow.fs:859-875` calls `LedgerStore.exportRegisteredSpec binding ...`; `:718-724` and `:683-690` call `LedgerStore.readRegisteredSourceSpec binding ...` (including CAS retry). `Client/Modules/LedgerStore.fs:1146-1174` computes source file root from `binding.RepositoryPath` rather than the selected worktree; both helpers operate on **main**, irrespective of `P::W`.

## Impact
For an external `ProjectName = "P::W"`, AGENTS.md and source inspection use registered physical W, but a task-spec export writes `P/relative.html` rather than `W/relative.html`; an import reads `P/relative.html` rather than W. If same relative file exists with different content in main, an import can publish incorrect HTML into the canonical task specification with apparently valid main-branch source provenance. If the relative file exists only in W, the operation spuriously fails. This breaks the specified checkout/source/ledger separation and can make an operation inconsistent with the instructions it received.

## Required correction
Carry both typed identities through this path: canonical main-root `LedgerBinding` for trust/journal/transactions, and separately validated `ResolvedProject.sourceRoot` for task-spec source-file access/provenance. Keep the exact physical source root pinned and revalidated during CAS retries; retain symlink, registration, containment, collision and source-branch checks. Do not change V2 public command shape or create per-worktree ledgers. If safe source checkout support cannot be introduced immediately, fail closed on non-main `ImportTaskSpec`/`ExportTaskSpec` rather than silently using main.

Add end-to-end regression tests with main and two registered worktrees, distinct HTML at the same relative path, exercising both operations through `TaskApi.writeForContext`: selected worktree read/write and provenance, no main mutation, absent-file failure, and existing security checks. Current `Client.Tests/ReviewWorkflowTests.fs:1925-1978` uses only the canonical main checkout, so its passing result does not cover this.

## Independent checks
`dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore`: **393 passed, 0 failed, 0 skipped** (.NET 10 Client.Tests); focused `Name~registered_worktree`: **2 passed**. `git diff --check efe769e06b34451e007198a050d227947fd83410 36a7d5dc86ce1b2371f21ff46dbfb9bcfa2aef19`: clean. This finding is a source-level deterministic call-chain inspection, not a reproduced live mutation. No source edits, integration or deployment were performed.