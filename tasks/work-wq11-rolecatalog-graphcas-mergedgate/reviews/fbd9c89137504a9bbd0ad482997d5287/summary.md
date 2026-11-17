# Reviewer 3 — WQ11 merged-gate fixture correction

**Review:** `fbd9c89137504a9bbd0ad482997d5287`  
**Frozen commit:** `8b6dfcf03701cd90221bf806c7b807b54f5dad56`  
**Base:** `0e9a8d4ea958090c94af11952329565358fdd79b`  
**Disposition:** Accept this exact test-only regression correction; no production cutover, activation, or Scheduler authority is conferred.

## Independent inspection

- Examined the frozen diff and surrounding original WQ11 fixture flows. Only `Client.Tests/WorkQueueTwoProjectEphemeralSchedulerVerticalTests.fs` and `Client.Tests/WorkQueueWorkReplacementVerticalSliceTests.fs` changed (77 insertions, 12 deletions). No production adapter, authority, credential, or Scheduler source changed.
- The two-project fixture now creates the authenticated disposable WQ04 Reviewer and Integrator role records **before** capturing independently approved role-catalog proofs and subscription membership. No stale role-catalog checks are bypassed at host read.
- A new changed-catalog negative mutates Lead after approval and verifies `RoleCatalogApprovalChanged` / `CanonicalHeadsUnavailable`, zero executor calls, and unchanged account capacity ledger head. The existing LegacyActive no-go check still exercises the production gate.
- Synthetic blocked review/integration dependency stages are explicitly on inert queues with no approved subscriber, avoiding accidental runnable native launch candidates. The real integration authority rejection is separately asserted.
- Composition uses the already-established, exactly-one original WQ04 project root and its verified current graph version for CAS writes rather than resetting to version 0 or duplicating the root. The stale version-0 mutation is explicitly rejected with unchanged Git head and graph revision.
- The subsequent duplicate task-composition owner remains rejected by the canonical graph authority, and the test verifies the rejected write does not move the queue/Git head. Graph acceptance and WQ02 queue availability remain distinct.

## Independent test evidence

On clean frozen worktree `w15-wq11-mergedgate`, independently ran .NET 11 Release:

`dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter "FullyQualifiedName~WorkQueueTwoProjectEphemeralSchedulerVerticalTests|FullyQualifiedName~WorkQueueWorkReplacementVerticalSliceTests"`

**6 passed; 0 failed; 0 skipped**, exit code 0, duration 56 s, Jarvis job `6b1682f230994689bcdb0afe50925e9f`. `git diff --check 0e9a8d4..8b6dfcf` returned exit code 0; the registered source worktree was clean.

## Boundary

Acceptance is limited to the three specified merged-main fixture regressions and the included safety negatives. Worker14's separately owned composition diagnostic failure was not modified or adjudicated here. Worker10 owns the broader merged-main full gate, and Lead owns exact-commit integration. No live Scheduler, migration, host restart, hosted test, or deployment was authorized.