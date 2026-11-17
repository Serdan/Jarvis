# P1: same-clone same-path worktree reincarnation reuses the pinned identity

**Review:** c4d7b36a4f6b4ad8af5736fa391e4c5c  
**Frozen source:** 626399e59a94cd5dacfc2f12fbdc2cdcabf09ec3  
**Verdict:** ChangesRequested  
**Scope:** default-off WQ11 claim-bound dirty-source fixture, not a live source route

## Verified fix and remaining bypass

The requested **different-clone** same-path replacement is correctly rejected: the new immutable, grant-digest-bound companion source-identity witness is created in the same `LedgerStore.transact` CAS as the scope revision (`Client/Modules/WorkQueueReservationPermissions.fs:443-485`), and later inspections read the original witness and compare Git administration/common/object-store paths (`Client/Modules/WorkQueueClaimBoundDirtySourcePolicy.fs:245-257,172-182`). The new two-clone regression (`Client.Tests/WorkQueueClaimBoundDirtySourcePolicyTests.fs:299-327`) passed in the independent Release suite.

However, those three fields are **directory path strings**, not a unique lifetime identity of the physical linked checkout. Git can remove a linked worktree and later recreate a new linked worktree from the *same original clone* at the same absolute pathname and detached commit, reusing every one of those directory paths.

An independent disposable local Python/subprocess Git probe created a temporary repository and linked worktree, recorded the three exact `git rev-parse` values, ran `git worktree remove --force <path>`, then `git worktree add --detach <same-path> HEAD`. The output was:

```text
admin_equal=true
common_equal=true
objects_equal=true
```

This was an isolated temporary repository, automatically cleaned; no Jarvis production checkout or source job was touched. The existing `verifyPhysical` checks inspect current path, registered detached checkout, approved remote/ref and original HEAD; all remain possible under same-clone same-path recreation. The original host-owned terminal file-edit journal is historical and remains unchanged. Consequently, the original edit can be credited to a new checkout instance even though the old instance and its dirty edit disappeared. No new source-effect UUID, credential or grant change is needed. This is the same physical-continuity failure class as the previously reported cross-clone P1.

## Required correction

Bind an **instance-distinguishing, host-owned physical checkout witness** at the original grant/claim, and verify it after every edit before relying on historical journal evidence. The witness must change or become unavailable if the linked worktree was removed and recreated, **even from the same clone, at the same path and same detached SHA**. Preserve the newly added CAS-bound Git admin/common/object-store checks, WQ03 clean-at-claim, scoped journal UUID/hash, stale/foreign/STOP/revocation negatives, and default-off/NO-GO behavior. Add a disposable full-policy same-clone same-path reincarnation test that leaves the old journal and credential intact and requires Rejected or HostProofUnavailable.

## Independent checks

`git diff --check bed92f3d..626399e5` clean; exact checkout HEAD and branch clean. SDK 11.0.100. `dotnet build Client.Tests/Client.Tests.fsproj -c Release` succeeded with 0 errors and 3 unrelated warnings. `dotnet vstest Client.Tests/bin/Release/net11.0/Client.Tests.dll --TestCaseFilter:FullyQualifiedName~WorkQueueClaimBoundDirtySourcePolicyTests|FullyQualifiedName~WorkQueueReservationPermissionsTests|FullyQualifiedName~SourceEffectHostBindingTests` passed **27/27**; the current tests do not exercise same-clone reincarnation.

**P1: ChangesRequested.** No live source execution, hosted Actions, Codex, Scheduler or cutover.
