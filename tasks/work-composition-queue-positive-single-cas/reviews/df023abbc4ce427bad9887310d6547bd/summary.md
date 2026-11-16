# Worker 12 exact-source review: WQ11 fixture compatibility

## Frozen boundary and scope

- **Review:** `df023abbc4ce427bad9887310d6547bd` — independent review requested, not yet Accepted.
- **Exact source:** `9602a85acde4679efd6f555aceb00c20ff67a196`, branch `refs/heads/fix/composition-wq11-fixture-w12-20261116` (pushed; no main push).
- **Parent:** frozen `e2830bc9a158f83f4a467d9f68ac45903f69c70d` (Reviewer 1's older review `9e1ca7540e114f13a82d9b4394cab721`, still Claimed with decision 0 when last verified; its ChangesRequested operation belongs to that reviewer and must be reconciled separately). Its two exact parents are Reviewer 2 Accepted `2dd803b6ad58544be346cabb40e40bc28e36abe9` and then-main `04b90e0cc43f8939f2c0722ea03d085f3244925b`.
- **Changed file only:** `Client.Tests/WorkQueueWorkReplacementVerticalSliceTests.fs` (19 additions, 3 deletions). No production source, compile-project file or other worker-owned module changed.

## Root cause and corrected proof

The preserved WQ11 fixture created original `dependent-task` WQ04 planning stage before a composition graph existed, then attempted to attach the existing stage retroactively via `LinkedTaskId = Some "dependent-task"`. The stage had no immutable original WQ04 stage-link record. Canonical `queueRevocations` correctly refused that invented provenance with `InvalidBinding`, causing the prior broad Client Release suite to fail. This was fixture incompatibility, **not an unauthorized runtime admission**.

This correction keeps the already-created planning stage intentionally **unbound** (`Dependent` graph unit has `LinkedTaskId = None`) and adds a new explicit negative: graph `Revise` trying to attach the original stage later must fail with an immutable-original-binding error and leave the verified Git head unchanged. Its separate graph hard prerequisite remains Blocked. Existing WQ11 proof retains the deliberately unbound queue's Available projection while `ClaimEnabled = false`, `ReadOnly = true`, `ClaimNext = RejectedMissingBinding` with `Applied = false`, no Scheduler dispatch, zero account capacity tokens, and no wake/executor. Queue availability without a composition binding is **not execution authority**.

Immutable WQ04 original links, default-off CompositionHold birth paths, duplicate `LinkedTaskId` validation, graph-mutation CAS revocation, accepted composition pagination and production modules remain untouched. The positive same-CAS lifecycle belongs to the existing two-parent source; this fixture does not replace it or assert a new production positive.

## Real local .NET 11 Release evidence

- SDK `dotnet --version`: **11.0.100**. Fresh checkout required `dotnet restore Client.Tests/Client.Tests.fsproj --ignore-failed-sources` and Server.Tests restore. An earlier `dotnet test --no-restore` returned 0 without an assembly, so it was explicitly **discarded as invalid evidence**.
- `dotnet build Client.Tests/Client.Tests.fsproj -c Release --no-restore --verbosity minimal`: **exit 0**, 0 errors, 3 existing non-blocking warnings, real `Client.Tests.dll` produced.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~WorkQueueWorkReplacementVerticalSliceTests' --logger 'console;verbosity=minimal'`: **1/1 passed**, exit 0.
- Focused two-clone WQ11/WQ04/WQ02/ledger/launch filter (WorkQueueWorkReplacementVerticalSliceTests, WorkCompositionQueueTransactionTests, WorkCompositionLedgerTests, WorkCompositionQueueBinderTests, WorkQueueGitLedgerTests, WorkQueueCanonicalCasBinderTests, WorkQueueDurableLaunchIntentTests): **68/68 passed**, exit 0, 2m09s.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~WorkComposition|FullyQualifiedName~WorkQueue' --logger 'console;verbosity=minimal'`: **252/252 passed**, exit 0, 4m31s; closes the exact 251/252 regression reported on e283.
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore --logger 'console;verbosity=minimal'`: **295/295 passed**, exit 0.
- `git diff --check e2830bc9 HEAD`: exit 0. `git merge-base --is-ancestor` for `2dd803b6` and `e2830bc9` against HEAD: both exit 0. Worktree status clean.

## Decision boundary for the independent reviewer

Review the exact **9602a85** source and classify only this fixture correction. Do not carry forward the old unfinalized `e283` review as Accepted. No production Scheduler registration, live Work claim, real provider send, WQ10 cutover, old-writer fleet fence, hosted GitHub Actions, Codex, or main integration was performed. **Production GO remains denied.** Lead may nonforce integrate only after independent Accepted verdict and merged-source verification.
