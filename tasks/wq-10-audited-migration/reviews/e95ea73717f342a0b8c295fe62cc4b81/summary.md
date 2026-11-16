# Reviewer 1 — WQ-10 frozen review: RequestChanges

**Frozen source:** `88863eead9c763e984bd34d3111812f67fd7ac2a`  
**Scope:** `tools/Wq10MigrationPreview/` only, 5 added files (916 lines), based on `289e32754b6e2bd59b157d51c3fd79a16e881e98`. This is the authorized **offline-only** preview track, not live migration or cutover.

## Independent verification

- `dotnet build tools/Wq10MigrationPreview/Wq10MigrationPreview.csproj -c Release --no-restore`: **pass**, 0 warnings/errors.
- `python3 -m unittest discover -s tools/Wq10MigrationPreview/tests -v`: **9/9 pass** on disposable Git remotes/two clones.
- `dotnet build Jarvis.slnx -c Release --no-restore`: **pass**, 13 existing F# warnings, 0 errors.
- Inspected full new CLI, README and tests; cross-checked account capacity state in `Client/Modules/AccountCapacityLedger.fs`, canonical project schema in `Server/WorkQueueContracts.fs` and `Client/Modules/WorkQueueGitLedger.fs`, plus accepted architecture's migration gates.
- Three additional disposable negative probes confirmed: omitted evidence inventories silently pass; account `Activated` with project `Frozen` silently passes; a stale `Ready` legacy assignment is displayed but not independently blocked. The first two are acceptance-significant and are detailed below. No live ledger, deployment, Manager, Scheduler or production branch was mutated.

## Findings requiring correction

1. **P1 — Missing evidence inventories silently default to empty:** `findings/omitted-evidence-inventories.md`. The three optional journal lists are coalesced with `?? []`, so incomplete inventory produces `PREVIEW_READY_FOR_REVIEW`. Require an explicit supplied inventory, including an explicit empty assertion.
2. **P1 — Partial migration phases do not block readiness:** `findings/partial-migration-phase.md`. Account `Activated` plus canonical project `Frozen` yields `PREVIEW_READY_FOR_REVIEW` with only a warning. Compare account and project **migration states**, not independent raw Git ledger identity epochs.

The implementation has a useful read-only foundation and correctly keeps actual activation/deployment disabled. Correct the two evidence gates, add minimal negative fixtures, and submit a new frozen commit for review. No global proof machinery or additional production mutation is requested.

**Verdict:** RequestChanges. Integration and operator authorization remain separate; none is granted by this review.
