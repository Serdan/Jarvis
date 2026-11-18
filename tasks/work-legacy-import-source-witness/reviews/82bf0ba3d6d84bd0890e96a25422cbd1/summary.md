# Independent Reviewer 3 verdict: Accepted — default-off C-to-B source-witness correction only

**Frozen:** `90f67128fd3a3bc97e64c8c55d5b0eaa9a1e01d2` (base `aa2e46ff2927b8e9fd0af25204377c299f2ec576`), review `82bf0ba3d6d84bd0890e96a25422cbd1`; previous ChangesRequested `49ac1b812b1d4d61a52335db1d64f9d7` at `4f562bcad458f0dcd8ac2cc1a438cd475673b363`.

## Reviewed correction and exact scope
- Inspected the previous finding and exact `4f562bca..90f67128` source/test delta. `checkEffectJournal` now rejects source-effect target substitution unless ledger ID, epoch, canonical repository ID and approved remote fingerprint equal the enclosing original project-ledger target; account/project/purpose and shape checks remain. The ledger target's canonical repository ID must equal the C2 fence trusted remote ID.
- The new `IWorkLegacyAuthenticatedSourceReader.ReadEffectAnchor` contract requires independently authenticated exact source-effect target identity, and `checkEffectJournal` requires equality to the journal target. It rejects a ref substitution even when the caller recomputes matching digest labels and `OriginalBarrier.ExactTargets`. The adapter has **no production implementation**.
- The new negative regression covers four self-consistent substitutions (ledger ID, epoch, approved fingerprint and source-effect Git ref) with recalculated digest/barrier but independently fixed anchor. Existing tests cover changed event content, UNKNOWN and duplicate effects, missing journal, stale C1, STOP/reinspection and two projects sharing a work label.
- Frozen base-to-head scope is exactly `Server/WorkLegacySourceWitness.fs`, `Server.Tests/WorkLegacySourceWitnessTests.fs`, one additive include in each F# project, and `docs/design/work-legacy-source-witness-w17-20261118.md`. No edits outside the leased source/tests and permitted evidence.

## Independently executed evidence
- `git diff --check aa2e46ff2927b8e9fd0af25204377c299f2ec576 90f67128fd3a3bc97e64c8c55d5b0eaa9a1e01d2`: exit 0.
- `dotnet --version`: SDK `11.0.100`.
- `dotnet test Server.Tests/Server.Tests.fsproj --configuration Release --verbosity minimal` on clean registered worktree at exactly `90f67128`: **495 passed, 0 failed, 0 skipped**, exit 0, `net11.0`. I independently executed this run; it was on the exact frozen author worktree, **not** a disposable archive (sandbox denies shell), and the subsequent `git status --short` was clean.
- `git rev-parse HEAD` still returned the exact frozen commit. Inspected implementation and regression source, previous finding, canonical task specification and Work evidence note. No new in-scope P1 issue established.

## Strict limits and migration HOLD
**Accepted only as a default-off, unhosted source-only validation/interface correction**, not as production authenticated ingress, remote source-effect/journal authority, independent pre-publication durability, real writer STOP, remote reader acceptance or C-to-B handover clearance. The synthetic test reader is not an independently authenticated external witness; future production reader and original writer must supply and independently validate actual remote/ref/commit, high-water, content, credential, client-quiescence and rollback-proof effect identity. Worker21 admission/STOP and Worker15/20/4 downstream gates remain independently blocked until their own accepted evidence and explicit operator GO. No integration, migration, dual writers, credentials/rules changes, Scheduler activation, restart/deployment, writer retirement, or cutover approved by this verdict.
