# Independent review verdict: ChangesRequested

**Review:** `9e1ca7540e114f13a82d9b4394cab721`, Reviewer 1, exact frozen `e2830bc9a158f83f4a467d9f68ac45903f69c70d` (do not amend). Two parents independently verified via `git show -s`: Reviewer 2 Accepted r4 `2dd803b6ad58544be346cabb40e40bc28e36abe9` (Accepted review `7bef38e608ed4b899e7870bd6cb2dbf3`, decision seq 5249), plus then-current remote main `04b90e0cc43f8939f2c0722ea03d085f3244925b`.

## Preserved invariants

- `git show --remerge-diff` identifies one physical textual merge conflict in `Client/Modules/WorkCompositionGitLedger.fs`, resolved by retaining r4 `FsToolkit.ErrorHandling`, `OriginalCompositionStageLink` and immutable WQ04 bindings alongside main `CompositionViewPage`/snapshot-pinned `loadOverviewPage`, UTF-8 byte limit and stale-cursor denial. `Client/Client.fsproj` preserves both integration entries.
- `Server/WorkComposition.fs:209-230,362-369`: `WorkComposition.validate` performs **global** duplicate `LinkedTaskId` check on all graph mutations (Create, Revise, Require included). `Client.Tests/WorkCompositionQueueTransactionTests.fs:370-454` checks duplicate after Available release with two-clone stale-head, rejected UUID and no mixed graph/queue write. The transaction and regression test files have no changed bytes relative to Accepted `2dd803b6`.
- `Client/Modules/WorkQueueCanonicalCasBinder.fs:301-305` invokes `planOriginalStage` in the producer; `WorkQueueGitLedger.fs:1108-1139` commits the immutable link and initial non-waking `CompositionHold` in the **same original project Git CAS**. `WorkCompositionGitLedger.fs:374-497` performs graph and WQ02 revocation changes atomically on ordinary graph mutation, rejecting absent immutable binding or non-Frozen/live queue.
- Main pagination preserved; corrected stress fixture `WorkCompositionLedgerTests.fs` uses legal unique task IDs and separately checks synthetic oversize transport rejection. WQ12 `BlockedCompositionAndInvalidProjectMembershipNeverPrepareAnOriginal` now composes before producing the WQ04 stage, preserving the blocked/without-credit negative. `WorkQueueDurableLaunchIntent.fs:197-201` remains fail closed.
- `git diff --check` against **both** parents returned exit 0; exact worktree `git status --porcelain` empty. No hosted Actions, Codex, Scheduler, live provider or production cutover exercised.

## Independent local .NET 11 Release results

1. `dotnet --version`: **11.0.100**.
2. Focused `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~WorkCompositionQueueTransactionTests|FullyQualifiedName~WorkCompositionLedgerTests|FullyQualifiedName~WorkCompositionQueueBinderTests|FullyQualifiedName~WorkQueueGitLedgerTests|FullyQualifiedName~WorkQueueCanonicalCasBinderTests|FullyQualifiedName~WorkQueueDurableLaunchIntentTests' --verbosity minimal`: **67/67 passed**, exit 0 (2m17s).
3. Full `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore --verbosity minimal`: **295/295 passed**, exit 0 (9 seconds test duration).
4. Broader `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter 'FullyQualifiedName~WorkComposition|FullyQualifiedName~WorkQueue' --verbosity minimal`: **251 passed, 1 failed, total 252; exit 1** (4m55s). The failure must not be hidden behind the focused green count.

## Required change

See `findings/wq11-retroactive-link-fixture.md`: `Client.Tests/WorkQueueWorkReplacementVerticalSliceTests.fs:123-153` produces `dependent-task` WQ04 stage before the graph, then retroactively adds a `LinkedTaskId` in ordinary composition mutation. That stage has no original immutable WQ04 composition link; `queueRevocations` correctly rejects it at line 152 with `InvalidBinding "WQ02 stage lacks immutable original composition binding; reconcile before graph mutation."` This retained WQ11 fixture was not reconciled as the separate WQ12 fixture was. Fix the fixture or explicitly assert fail-closed legacy behavior, preserving real provenance enforcement and negative claim/read assertions. Rerun broad Client (including WQ11), focused two-clone cases, Server, Git checks on a **new exact frozen review**. No production GO.

**Decision: ChangesRequested** solely because the exact merged source does not pass the broader relevant Client Release suite, despite correct core r4 and pagination logic.
