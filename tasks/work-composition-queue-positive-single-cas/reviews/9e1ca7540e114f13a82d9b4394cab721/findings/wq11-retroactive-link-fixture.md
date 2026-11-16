# P1: WQ11 vertical-slice regression fixture still assumes retroactive task-to-composition attachment

Frozen review: `9e1ca7540e114f13a82d9b4394cab721` on exact merge `e2830bc9a158f83f4a467d9f68ac45903f69c70d` (parents `2dd803b6ad58544be346cabb40e40bc28e36abe9`, `04b90e0cc43f8939f2c0722ea03d085f3244925b`).

**Independent reproduction:** .NET 11.0.100 Release `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter 'FullyQualifiedName~WorkComposition|FullyQualifiedName~WorkQueue' --verbosity minimal` exited 1: **251 passed / 1 failed / 252 total** (4m55s). The sole failing test is `Client.Tests.WorkQueueWorkReplacementVerticalSliceTests.PinnedCompositionAndWq02ViewExposeMissingWorkToSchedulerBindings` at `Client.Tests/WorkQueueWorkReplacementVerticalSliceTests.fs:152`. Actual:
```
InvalidBinding "WQ02 stage lacks immutable original composition binding; reconcile before graph mutation."
```

**Cause:** In the WQ11 fixture, lines 123-124 invoke `produce` to create canonical WQ04 task stages first. Lines 145-153 then issue ordinary `WorkCompositionGitLedger.execute` graph Create/Require mutations, including a feature `LinkedTaskId = Some "dependent-task"`. The stage was born without a graph owner or `records/work-composition/stage-links/<stage>.json`, and `queueRevocations` correctly rejects retroactive provenance minting. This is consistent with the accepted WQ04 default-off fail-closed invariant; do **not** weaken `WorkCompositionGitLedger.queueRevocations` or grant a caller-supplied association to make the test green.

**Required correction:** Reconcile this preserved WQ11 read/claim vertical-slice fixture with the canonical one-CAS WQ04 lifecycle. For legally linked graph units, compose before the original stage is produced and assert the blocked non-waking `CompositionHold` at birth; otherwise keep the test's intentionally unbound WQ02 stage *unlinked* and separately prove queue availability is not execution authority. Preserve the existing missing Scheduler/claim binding negative assertions, add an explicit failed retroactive-link assertion if useful, and rerun the affected Client Release suites plus Server full. Any source correction needs a new exact frozen commit/review; do not rewrite this reviewed SHA.

**Impact:** This is a test/merge compatibility failure, not evidence that runtime admitted an unauthorized stage. The broadened run is red and therefore **ChangesRequested**; no production Scheduler/migration/cutover GO.
