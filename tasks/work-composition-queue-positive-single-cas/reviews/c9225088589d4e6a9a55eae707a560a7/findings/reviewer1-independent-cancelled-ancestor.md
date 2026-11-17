# Reviewer 1 independent verification — cancelled-ancestor release blocker

Frozen reviewed commit: `9b6f471d5bba806e91beaf67392ba982c32b2cdd` (parent `2f652b1b7`). Exact delta is two files: `Client/Modules/WorkCompositionQueueTransaction.fs` and `Client.Tests/WorkCompositionQueueTransactionTests.fs`. Clean review worktree; no review edits.

## Source analysis

- The new guard calls `WorkCompositionGitLedger.graphBlocked nextGraph dependent` inside the original `LedgerStore.transact` acceptance/release callback, after applying independently verified prerequisite acceptance and checking structural coherence and all other prerequisites, but **before** creating the available WQ02 stage and publishing graph plus queue under one Git CAS.
- Canonical `graphBlocked` rejects cancellation of the unit or any ancestor (recursive parent traversal, fail-closed for missing nodes/cycles) and missing acceptance of hard prerequisites. The previous logic only checked the dependent cancellation and prerequisites; an already-cancelled ancestor could otherwise leave an unblocked dependent.
- Existing unchanged `load` and `originalPlanningOrigin` validate original WQ04 birth, immutable stage link, project/head/version/generation, original source-operation receipt, and historical task origin. The patch does not loosen UUID/idempotency, worktree/provenance, migration or host gating.
- New negative tests reproduce a cancelled parent after canonical stage birth; prove failed release leaves the Git head, sequence, operation, graph receipt, queue hold and ordering unchanged; and cover stale heads, reopen, recancel and stale immutable linkage. These are not synthetic retroactive links.

## Independent local .NET 11 Release verification

- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~WorkCompositionQueueTransactionTests`: **13 passed, 0 failed**.
- Same built Release binaries, `--filter FullyQualifiedName~WorkComposition`: **25 passed, 0 failed**.
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore`: **295 passed, 0 failed**.
- `git diff --check 9b6f471d5^ 9b6f471d5`: clean. Compiler warnings outside changed files only.

## Verdict and boundary

**Accepted** for the narrow P1 cancelled-ancestor same-CAS release failure. No blocking findings in this frozen delta. This is a **default-off, project-ledger rehearsal**, not permission to activate Scheduler, migrate ledgers, enable public host/provider send or assert fleet-wide legacy-writer fencing. Existing broader rollout dependencies remain unproven.