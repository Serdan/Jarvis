# Reviewer 2 — Accepted (independent WQ11 P1 checkout reincarnation re-review)

**Review:** `e6f2b712b34a45e08bdad8fa7b24190e`  
**Frozen source:** `d8a2f1f23dd867732f5f3c534a7b78309ae1ebd5`  
**Base:** `bed92f3d2b39ef7c256a8d1955c48883af99cb60`  
**Previous ChangesRequested:** `c4d7b36a4f6b4ad8af5736fa391e4c5c`, seq 5575, frozen `626399e59a94cd5dacfc2f12fbdc2cdcabf09ec3`.

## Independent source review

The previous same-clone/same-path linked checkout reincarnation P1 is addressed. `WorkQueueReservationPermissions.sourceCheckoutInstanceDigest` creates a one-time 32-byte `RandomNumberGenerator.GetBytes` nonce, stored as 64 lower-hex characters in the linked worktree's Git administration directory rather than the checkout/source tree. Creation uses `FileMode.CreateNew`; subsequent verification is read-only, rejects missing/malformed/linked marker and enforces 0600 on Unix. The nonce's SHA256 digest—not its contents—is persisted.

`reviseWithSourceAt` first verifies original WQ03 clean checkout/lineage, then creates the per-launch/claim-generation `SourceCheckoutClaimInstanceWitness` only on the first source grant. It writes that original witness, the revision-specific v2 `SourceCheckoutGitIdentityWitness` (including grant digest and instance digest), scope grant, and revision history within the same `LedgerStore.transact` Git CAS. Later revisions must match the original claim-generation witness exactly and cannot retrofit absent witness or rekey an existing claim; recreated checkout with missing marker fails before ledger mutation.

`WorkQueueClaimBoundDirtySourcePolicy.validate` matches current grant bytes to immutable history, checks the revision-specific witness against the grant SHA256 and the independent original claim witness, then `verifyPhysical` compares Git admin/common/object-store directories and the *current* marker digest before any historical FileEdit journal evidence is considered. Same clone, same path and HEAD can reuse all Git paths, but a removed linked checkout loses the marker and is rejected rather than inheriting old receipt evidence. Retained credential/admission SHA256 binding, claim generation, source/ref/HEAD verification, revocation/STOP, original scoped file edit UUID/request digest and host journal remain enforced.

## Independent execution

- Local frozen checkout HEAD matches `d8a2f1f23dd867732f5f3c534a7b78309ae1ebd5` and remains clean after tests.
- `git diff --check bed92f3d2b39ef7c256a8d1955c48883af99cb60 d8a2f1f23dd867732f5f3c534a7b78309ae1ebd5`: clean.
- Correction relative to 626399e5 touches only three implementation/test files (227 insertions, 10 deletions).
- SDK `dotnet --version`: **11.0.100**.
- Actual `dotnet build Client.Tests/Client.Tests.fsproj -c Release --nologo`: **succeeded**, 0 errors, 3 unrelated pre-existing warnings.
- Actual `dotnet vstest Client.Tests/bin/Release/net11.0/Client.Tests.dll --TestCaseFilter:FullyQualifiedName~WorkQueueClaimBoundDirtySourcePolicyTests|FullyQualifiedName~WorkQueueReservationPermissionsTests|FullyQualifiedName~SourceEffectHostBindingTests`: **29 passed, 0 failed, 0 skipped**.
- The new disposable `same_clone_same_path_checkout_reincarnation_cannot_reuse_original_host_edit` test proves Git admin/common/object paths and HEAD are identical after removal/re-addition, the original marker is absent, regrant fails with original scope revision unchanged, and old FileEdit journal/credential are rejected. `unchanged_checkout_renewal_preserves_immutable_claim_instance_and_edit_evidence` proves a matching renewal retains the original anchor and remains valid. Original second-clone negative, dirty-path journal positives/negatives, generation, credential, remote handoff, and STOP tests remain passing.

**Verdict: Accepted** for this independent frozen WQ11 correction. The policy remains internal, default-off and NO-GO for real source execution, Scheduler and cutover; no hosted Actions or Codex used. This verdict does not authorize a live route.