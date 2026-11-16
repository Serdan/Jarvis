# Reviewer 2 independent verdict: ChangesRequested (P1)

**Task:** `work-composition-queue-positive-single-cas`
**Review:** `84de6fb103bf4d42adab79df10ab15e6`
**Frozen source:** `9afd7611e5d01878e276a139dc3821d01a21b133`
**Base:** `3458224ce3bff9141b05a7891148098db4900053`
**Independent clean checkout:** `review/reviewer2-composition-9afd-20261116`, same exact HEAD before and after scratch regression; no production change.

## Verified previous P1 corrections

1. **Original WQ04 non-waking birth (fixed in reviewed scope).** `WorkQueueCanonicalCasBinder.publishFreshPractical` routes `WorkCompositionGitLedger.planOriginalStage` into `WorkQueueGitLedger.publishCanonicalEvent`. The *winning original Git CAS* stages the WQ02 record and canonical WQ04 origin together with immutable `OriginalCompositionStageLink`. On unmet graph prerequisites the stage is committed `Blocked` + `CompositionHold` and no FIFO availability. The two-clone fixture asserts original producer paths and initial blocked state without optional second-CAS hold.
2. **Existing linked-task/spec/parent/prerequisite mutation revocation (fixed for the single original linked unit).** `WorkCompositionGitLedger.queueRevocations` now examines old and next graph, checks immutable unit/spec/context against the original stage link, and writes queue revocations in the same `LedgerStore.transact` as graph changes. Five parameterized cases (linked-task, spec, parent, hard-edge, ancestor-spec), cancellation/reopen, nonwaking dependency clear and unrelated-revision control pass.
3. **Positive accepted/integrated prerequisite release and two-clone races (verified scoped).** `acceptAndRelease` checks original WQ04 receipt/history and independent review/integration at a pinned head; one original Git CAS writes both graph acceptance and WQ02 release. Separate UUIDs racing from independent clones have one winner and stale loser, with same-UUID replay idempotent.

## Independent .NET 11 Release evidence

- On clean pinned checkout: `dotnet test Client.Tests/Client.Tests.fsproj --configuration Release --filter FullyQualifiedName~WorkComposition --logger "console;verbosity=minimal"` — **19 passed / 0 failed / 0 skipped**, net11.0, exit 0.
- Added a *temporary uncommitted* two-clone regression against the pinned code for a second linked feature sharing an already-released task. Its targeted .NET11 Release run **failed 1/1**: expected WQ02 `Blocked`, observed `Available` while new linked feature was graph-`Blocked`. Scratch test then removed; checkout clean and exact HEAD unchanged.
- Full independent reproduction and source lines: `findings/duplicate-linked-task-bypasses-revoke.md`.

## Blocking P1

**Duplicate `LinkedTaskId` introduced after original WQ04 publication escapes revocation.** `Server/WorkComposition.fs:209-249` does not enforce unique task-link ownership; `WorkCompositionGitLedger.queueRevocations:275-367` validates only its originally associated unit/context, not new conflicting feature links. Two ordinary graph CAS operations introducing a second feature linked to the existing task and an unmet hard prerequisite leave a verified original head with **graph-Blocked / WQ02-Available**. This reopens the fundamental consistency invariant even though the two *previously reported* paths have been repaired. Require a same-CAS fail-closed uniqueness check or revocation across every graph-mutation path, with a two-clone regression.

## GO / NO-GO

**Independent review verdict: ChangesRequested (P1)** on this exact frozen source. The original role/source selection, WQ07 account admission, WQ03 source effects, full WQ08/WQ09/WQ11 admission/vertical verification, and non-agent Scheduler readiness remain **NO-GO** and were not implied by 19 green scoped tests. Keep Scheduler default-off, legacy operations intact, no live Work claims or production migration/cutover. Worker should repair P1 on a fresh isolated source and request a **new** independent frozen review.