# P1 — Original WQ04 review/integration graph authority is not revoked on graph mutation

Frozen source: `27ccbfed6252948aed645ebaa109a601af7a2b26`; claimed review: `5fd240db69f24a219e7cb75ac8c418d9`.

## Concrete source path

1. `Client/Modules/WorkCompositionGitLedger.fs:222-262` (`planOriginalStage`) now creates an immutable stage/spec/context link for **Planning, FrozenReview and Integration** in the ordinary original WQ04 project CAS.
2. A later ordinary `WorkCompositionGitLedger.execute` graph mutation goes through `transactWithVerifier -> queueRevocations` (`:379-505`). But `revocation` unconditionally returns `false, None` for `stage.Kind <> WorkKind.Planning || stage.QueueId <> "planning"` (around `:393-396`). Therefore original FrozenReview and Integration stages remain unchanged and claimed/live stages are not rejected when the task-linked Feature spec, parent, prerequisites, cancellation or its original `ContextDigest` changes.
3. This makes `stageLinkStillCurrent` false (`:194-207`) while an Available or Claimed review/integration stage remains in WQ02. `WorkQueueRoleAuthority.eligible` (`:626-638`) selects Available stages based on subscription without verifying the composition link. `WorkQueueIntegrationSourceAuthority.fs` verifies original operation/origin for WQ05 review and integration but does not re-verify the graph link. `WorkQueueDurableLaunchIntent.verifiedPlanning` does verify graph/source/role but exclusively for Planning; it is not a review/integration fence.

## Required correction

Extend graph mutation revocation and claimed-stage denial to **all newly original-composition-bound WQ04 stage kinds**, preserving the same winning project CAS. Add two-clone tests for review and integration Available->Blocked on relevant graph changes, claimed/live change denied, unrelated mutation unchanged, and duplicate/replay and wake cases. Preserve independent WQ07 account-global admission, STOP and default-off; no live Scheduler/cutover.

## Independent evidence

Verified source SHA and frozen review ref both equal `27ccbfed6252948aed645ebaa109a601af7a2b26`. Worktree clean; `git diff --check d356d14c46eef89af4522c05411f25dfd6c10065 27ccbfed6252948aed645ebaa109a601af7a2b26` exit 0. SDK `dotnet --version` = `11.0.100`.

Ran from exact frozen checkout:
`dotnet test Client.Tests/Client.Tests.fsproj --configuration Release --no-restore --filter "FullyQualifiedName~WorkQueueCanonicalCasBinderTests|FullyQualifiedName~WorkQueuePracticalAdmissionTests|FullyQualifiedName~WorkQueueIntegrationSourceAuthorityTests|FullyQualifiedName~WorkCompositionQueueTransactionTests|FullyQualifiedName~WorkQueueReleaseAcceptanceTests" --logger "console;verbosity=normal"`.

Result: exit 0, **40/40 passed**, NUnit on `net11.0`. This confirms covered positive/negative Planning WQ04 plus adjacent review/integration source paths, but test filters include no case that mutates composition while a real original review or integration stage exists. This uncovered P1 invariant remains unverified and violated by the actual mutation predicate. Requested verdict: **ChangesRequested**.