# Reviewer 2 — ChangesRequested (P1 physical checkout continuity)

**Review:** `c4d7b36a4f6b4ad8af5736fa391e4c5c`  
**Frozen source:** `626399e59a94cd5dacfc2f12fbdc2cdcabf09ec3`  
**Base:** `bed92f3d2b39ef7c256a8d1955c48883af99cb60`  
**Prior ChangesRequested source:** `e2700710e671542834142448fa9673000d8c7e2a`

## Independently verified fix

The original P1 **different-clone same-path** substitution is now denied. `WorkQueueReservationPermissions.fs:443-485` stores original Git admin/common/object-directory paths as a grant-digest-bound immutable witness in the **same winning Git CAS** as the WQ11 scope revision. `WorkQueueClaimBoundDirtySourcePolicy.fs:245-257,172-182` reads that original witness, rejects missing/mismatched proof, and compares all three physical Git paths on each inspection. The new two-clone same-path regression in `Client.Tests/WorkQueueClaimBoundDirtySourcePolicyTests.fs:299-327` preserves old journal evidence and rejects clone B at the original physical pathname; it passes.

Original WQ03 clean claim, credential/admission, stage/claim generation, source/target separation, strict edit route and anchored terminal journal, stale/foreign checkout, symlinks, revocation and STOP remain checked. The adapter is internal/default-off; test terminal evidence and production source execution remain NO-GO.

## Blocking related P1

The stored identity components are **paths**, not a per-instance checkout lifetime identity. An independent isolated temporary Git fixture removed and re-added a linked worktree from the **same original clone**, at the same pathname and detached SHA. It printed `admin_equal=true`, `common_equal=true`, `objects_equal=true`. Git deterministically reuses the same admin subdirectory name; the common and object directories naturally remain unchanged.

On this same-clone reincarnation, the policy's three comparisons, current detached HEAD, worktree registration, remote/source lineage, grant/credential and already-terminal historical FileEdit journal all remain satisfiable, though the originally edited checkout was destroyed and the recorded edit no longer applies to the replacement. Thus the broader physical claim-continuity invariant is still not established; the new two-clone negative does not exercise this case.

**Required:** add a host-owned immutable instance-distinguishing witness (not only reusable Git paths) that fails closed if a linked checkout is removed/recreated from the same clone, and add a disposable same-clone/same-path old-journal regression requiring Rejected or HostProofUnavailable. Preserve existing cross-clone defense, clean claim, journal UUID/digest and STOP negatives. Details: `findings/wq11-same-clone-checkout-reincarnation.md`.

## Independent local checks

SDK `11.0.100`. Exact frozen checkout HEAD and clean status confirmed; six changed files; `git diff --check bed92f3d..626399e5` clean. `dotnet build Client.Tests/Client.Tests.fsproj -c Release`: **succeeded**, 0 errors, 3 unrelated warnings. `dotnet vstest Client.Tests/bin/Release/net11.0/Client.Tests.dll --TestCaseFilter:FullyQualifiedName~WorkQueueClaimBoundDirtySourcePolicyTests|FullyQualifiedName~WorkQueueReservationPermissionsTests|FullyQualifiedName~SourceEffectHostBindingTests`: **27 passed, 0 failed, 0 skipped**. The same-clone reproduction used a temporary local repository and cleaned itself up; no Jarvis real source checkout or production route was touched.

**Decision: ChangesRequested (P1).** No live source execution, Scheduler/cutover, hosted Actions or Codex.
