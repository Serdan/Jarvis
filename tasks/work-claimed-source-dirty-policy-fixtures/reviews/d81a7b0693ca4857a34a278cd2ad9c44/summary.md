# Independent Reviewer 2 — ChangesRequested

**Review:** `d81a7b0693ca4857a34a278cd2ad9c44`  
**Task:** `work-claimed-source-dirty-policy-fixtures`  
**Frozen source:** `e2700710e671542834142448fa9673000d8c7e2a`  
**Base:** `bed92f3d2b39ef7c256a8d1955c48883af99cb60`

## Independent verification

The registered frozen checkout resolves to the exact pinned HEAD, with five changed files (new default-off policy, eight focused fixture cases, project inclusions, and documentation); `git diff --check` is clean. Local SDK: **11.0.100**. `dotnet build Client.Tests/Client.Tests.fsproj -c Release` succeeded (0 errors; 1 nonblocking existing FS0760 warning). `dotnet vstest Client.Tests/bin/Release/net11.0/Client.Tests.dll --TestCaseFilter:FullyQualifiedName~WorkQueueClaimBoundDirtySourcePolicyTests|FullyQualifiedName~WorkQueueReservationPermissionsTests|FullyQualifiedName~SourceEffectHostBindingTests` passed **26/26**, 0 failed, 0 skipped (35 seconds).

Positive and negative fixtures cover original WQ03 clean-at-claim rejection after intentional dirty edit; two-clone host-awaited file edit and scoped historical journal checkpoint; unproven test completion blocked; wrong credential, stale generation, foreign *different-path* valid checkout, moved HEAD, symlink and traversal, changed command/path, missing or uncertain journal, revoked grant and STOP; successor clean generation after a remote handoff. The code remains an internal, default-off inspection fixture; it enables no live source execution, public adapter, Scheduler or cutover.

## Blocking P1: original physical worktree identity is not bound across edits

`Client/Modules/WorkQueueClaimBoundDirtySourcePolicy.fs:130-182` checks the lexical claim path, generation-qualified name, remote, detached pinned HEAD, current Git-dir != current common-dir, and that *a* registered worktree exists there. `ReservationScopeGrant` (`Client/Modules/WorkQueueReservationPermissions.fs:29-54,323-339`) stores path/name/ref/SHA/repository ID but **not the original Git administration directory or common object-store identity**. `LedgerStore.sourceRepositoryIdentity` (`Client/Modules/LedgerStore.fs:1542-1571`) fingerprints the approved remote URLs, which are identical for separate clones of one remote.

A different local clone of the same approved remote could therefore replace the *original* linked worktree at the same absolute path and detached SHA with the same generation-qualified name. The current predicates would accept this replacement; the already-journaled awaited file edit could still be classified `EvidenceVerifiedForFixture`, although the effect applied to the prior physical checkout. The test of a same-name *different-path* foreign checkout does not cover this substitution. This is a source-code counterexample, **not** a live source-operation reproduction.

**Required:** pin original host-owned physical Git checkout identity at claim/grant time and verify that immutable identity on every post-edit inspection (e.g., stable original Git-dir and canonical common-dir/object store), accounting for legitimate two-clone remote verification. Add a disposable same-path different-clone replacement negative that fails closed despite identical remote/name/HEAD and old terminal journal proof. Preserve original clean claim, journal unknown-outcome and STOP/revocation guards.

**Verdict: ChangesRequested.** Do not integrate this frozen source as production source authorization. No live source job, hosted Actions, Codex or cutover was run.
