# Independent review — ChangesRequested

**Review:** `e47e41dea4a044c1b089974a615c8e18` (Reviewer 3)
**Frozen head:** `36a7d5dc86ce1b2371f21ff46dbfb9bcfa2aef19`
**Base:** `efe769e06b34451e007198a050d227947fd83410`

The typed `ProjectTarget`/`ResolvedProject` separation and real Git-registered checkout resolution are coherent, and the reviewed changes give `P::W` distinct AGENTS.md display/hierarchy keys while centralizing ledger and pending journal paths under `P`. The focused regression checks cover separate worktree instructions, ambiguous selectors, and main-root journal lookup. The 5-file source diff is clean.

**Blocking P1 defect:** Source-touching task-spec commands (`ExportTaskSpec` and `ImportTaskSpec`) carry a resolved selected checkout into `TaskApi.writeForContext`, but `LedgerBindingConfig.loadForContext` now builds the canonical main-root binding and the downstream export/import source-file functions derive paths from `binding.RepositoryPath`. Therefore a `P::W` request reads/writes `P/relative.html`, not `W/relative.html`; the existing Release tests only exercise main. See `findings/task-spec-selected-checkout.md` for exact call chain, impact and required correction. Preserve canonical ledger identity while passing selected validated source-root identity independently. **Do not integrate this frozen head until corrected and re-reviewed.**

**Executed checks:** `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore`: 393 passed / 0 failed / 0 skipped, .NET 10 Client.Tests. Focused `--filter Name~registered_worktree`: 2 passed. `git diff --check` clean. Findings based on exact source call-chain inspection; no live import/export mutation was executed. No source edits, merges, deployment or ledger-authority changes by reviewer.
