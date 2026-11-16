# P1 — Duplicate linked feature bypasses post-publication WQ02 revocation

**Frozen reviewed source:** `9afd7611e5d01878e276a139dc3821d01a21b133` (task `work-composition-queue-positive-single-cas`; review `84de6fb103bf4d42adab79df10ab15e6`). This is a distinct path from both prior P1 findings in `b1c9f640`, which the patch does address.

## Defect

`Server/WorkComposition.fs:209-249` validates graph structure but does not require `WorkUnitSpec.LinkedTaskId` to be unique. `WorkCompositionGitLedger.planOriginalStage` (`Client/Modules/WorkCompositionGitLedger.fs:204-243`) correctly refuses ambiguous associations *when the original WQ04 stage is born*. However, ordinary `Create` or `Revise` mutations can later introduce a second feature sharing the same canonical task.

After the original dependent stage has been held and legitimately released, `WorkCompositionGitLedger.queueRevocations` (`Client/Modules/WorkCompositionGitLedger.fs:275-367`) only calls `stageLinkStillCurrent` on the previously recorded *single* unit, plus `graphBlocked` for that original unit. A new linked feature and its unmet hard prerequisite do not alter the original unit's immutable spec/context, so both graph mutations commit without a WQ02 revocation. Thus a new graph-Blocked feature shares an **Available** original WQ02 planning stage on the same verified Git head. Default-off `MigrationEpoch.Frozen` prevents a real launch but does not satisfy canonical graph/stage agreement or future cutover safety.

## Executed independent reproduction

In isolated independent two-clone disposable `withClones` fixture, at exact frozen source:

1. Publish original WQ04 for `delivery-task` linked to `dependent`, stage born `Blocked/CompositionHold`; independently accept and integrate `prerequisite`; atomically `acceptAndRelease`. Stage becomes `Available`.
2. Clone A executes ordinary `WorkCompositionMutation.Create("duplicate-link", Feature, Some "foundation", spec "Duplicate link" (Some "delivery-task"))`.
3. Clone B executes ordinary `WorkCompositionMutation.Require("duplicate-link", "delivery")`. `delivery` has no current acceptance, so the new feature is graph-Blocked.
4. Read one final canonical project head in clone A. `WorkComposition.phase graph (Some queue) "duplicate-link"` is `Some Blocked`, but `queue.Works.[workId].State` is **Available**, not Blocked.

Appended a **temporary uncommitted reviewer-only** NUnit test to `Client.Tests/WorkCompositionQueueTransactionTests.fs`; ran locally:

```text
dotnet test Client.Tests/Client.Tests.fsproj --configuration Release --no-restore
  --filter FullyQualifiedName~DuplicateBlockedLinkedFeatureMustNotKeepOldWq02Available
  --logger "console;verbosity=normal"

Failed DuplicateBlockedLinkedFeatureMustNotKeepOldWq02Available
Expected: <Blocked>
But was:  <Available>
Total tests: 1; Failed: 1; net11.0; process exit 1
```

The scratch test was removed with `git restore`. Independent checkout is clean and remains at pinned `9afd7611e5d01878e276a139dc3821d01a21b133`; no source commit or production operation was made.

## Required fix

Enforce canonical uniqueness/ownership of every linked task across *all* accepted graph mutation paths, or atomically revoke/fail closed any old WQ02 stage when another linked feature appears (including `Create`, `Revise`, and subsequent `Require`). Ensure the original immutable association cannot be bypassed by the introduction of a second linked unit. Add a two-clone regression showing no `graph-Blocked / WQ02-Available` canonical head and explicit UUID replay/stale-head fencing. Keep original WQ04 role/source and WQ07 account admission **NO-GO**, Scheduler default-off and no live cutover.

**Severity:** P1; **requested verdict:** ChangesRequested, independent of the passing existing 19/19 scoped tests.