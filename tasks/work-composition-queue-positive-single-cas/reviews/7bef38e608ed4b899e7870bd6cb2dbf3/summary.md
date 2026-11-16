# Independent Reviewer 2 verdict — Accepted (r4 duplicate-linked-task correction only)

**Frozen review:** `7bef38e608ed4b899e7870bd6cb2dbf3`  
**Exact source:** `2dd803b6ad58544be346cabb40e40bc28e36abe9`  
**Parent:** `9afd7611e5d01878e276a139dc3821d01a21b133` (previous ChangesRequested source retained as exact ancestor).  
**Scope:** P1 post-publication duplicate `LinkedTaskId` failure, preserving prior same-CAS original hold and mutation revocations. **Not** an operational Work-migration GO.

## Independent code inspection

1. `Server/WorkComposition.fs:209-259` validates global canonical-task ownership before any mutation can publish. Every `WorkComposition.apply` path, including Create/Revise/Require, calls `validate` on the candidate after its graph-version check (`:362-369`). A second unit with the same `LinkedTaskId` returns `InvalidSpec`, not an accepted graph. The map is checked against the whole graph, not only newly born WQ04 stages. `Client/Modules/WorkCompositionGitLedger.fs:53-79,89-105` also uses `validate` during canonical graph rehydration; malformed loaded snapshots are refused.
2. `WorkCompositionGitLedger.transactWithVerifier` (`:372-406`) invokes that canonical apply and `queueRevocations` (`:275-367`) as one original `LedgerStore.transact` plan: graph/spec/parent/prerequisite-context changes or loss of prerequisite acceptance hold/revoke existing WQ02 eligibility atomically. A rejected candidate generates no graph/queue operation receipt.
3. The original WQ04 producer's `WorkQueueGitLedger` planning path (`:1042-1073`) gets `CompositionHold` from `WorkCompositionGitLedger.planOriginalStage` (`:201-244`) before constructing the stage record and submitting stage, provenance and immutable unit/spec link together in the *same* original Git CAS; a blocked graph never exposes an intermediate Available stage.
4. `WorkCompositionQueueTransaction.acceptAndRelease` (`:194-237`) maintains same-snapshot verified acceptance + WQ02 stage publication in one original CAS. Post-release ordinary graph Create cannot add a duplicate linked owner; Revise cannot attach the old task to a blocked second unit. An unrelated unlinked blocked unit legitimately coexists with the original Available WQ02 stage.

## Independent local .NET 11 Release execution (exact frozen worktree)

- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~WorkComposition --logger console;verbosity=normal`: **20/20 passed**, exit 0. Includes new `DuplicateTaskLinkAfterReleaseIsRejectedOnCreateAndReviseAcrossTwoClones`: duplicate Create rejected with zero receipts, real subsequent Create advances head, stale B UUID returns `StaleEntityVersion` with no receipt, original successful Require UUID replays `AlreadyApplied=true`, blocked unlinked feature's Revise on A/B rejects duplicate link with no new receipt; original WQ02 stage stays Available for its *unique* linked unit. Existing original hold/release and five revocation variants, race, replay and negative-origin checks also passed.
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~WorkComposition --logger console;verbosity=normal`: **18/18 passed**, exit 0. New direct graph Create/Revise duplicate rejection plus malformed snapshot `validate` rejection, as well as cycle/acceptance/gate negatives.
- Frozen commit lineage: `2dd803b6a` direct child of `9afd7611e`; `git merge-base --is-ancestor` exit 0; `git diff --check 9afd7611e..2dd803b6a` exit 0. Exactly three changed source/test files, no neighboring Worker1/Worker8 modules modified.

**Decision:** **Accepted** for the narrowly frozen r4 P1 duplicate `LinkedTaskId` correction and inherited same-CAS protections. No unresolved blocker found in this reviewed delta. Scope does **not** establish role/source/account credential admission or original trusted production lineage, WQ03/WQ07/WQ08/WQ09/WQ11 vertical readiness, Scheduler/Manager activation, legacy-Agent retirement, account/project migration, or live cutover. These remain explicit **NO-GO** independently of green tests. No hosted Actions, Codex or production writes used.
