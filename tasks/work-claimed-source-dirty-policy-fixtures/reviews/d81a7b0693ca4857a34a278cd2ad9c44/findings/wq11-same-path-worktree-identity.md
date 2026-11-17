# Blocking: same-path Git worktree replacement is not detected

**Verdict:** ChangesRequested  
**Frozen source:** `e2700710e671542834142448fa9673000d8c7e2a`  
**Severity:** P1 for the physical claim-continuity guarantee (default-off fixture; no production route currently consumes it)

## Evidence and counterexample

`Client/Modules/WorkQueueClaimBoundDirtySourcePolicy.fs:130-181` rechecks the immutable grant's lexical `SourceCheckoutPath`, generation-qualified basename, Git top-level, detached HEAD and `git worktree list --porcelain` entry. It only requires the checkout's `--absolute-git-dir` to differ from its *current* `--git-common-dir`; it does **not** compare either value to the Git administration directory/object-store identity at the original WQ03 claim or WQ11 grant. The grant record (`Client/Modules/WorkQueueReservationPermissions.fs:29-54, 323-339`) persists path/name/SHA/remote-derived repository ID, but no original Git-dir/common-dir identity. `LedgerStore.sourceRepositoryIdentity` (`Client/Modules/LedgerStore.fs:1542-1571`) produces its repository ID from the approved URLs, not the physical object store, while `validate` ensures the current configured remote matches those URLs (`LedgerStore.fs:511-529`).

Consequently, after one genuine awaited host file edit is journaled, a caller who controls that checkout can remove the original linked worktree and install a new linked worktree at the **identical absolute grant path** from a different local clone of the **same approved remote**, with the same detached pinned commit and generation-qualified basename. The configured remote, HEAD, registered linked-worktree predicate, ancestor-symlink check, source ref, immutable grant and historic UUID/hash/journal proof all still pass. `inspectDisposable` can then report `EvidenceVerifiedForFixture` for the replacement filesystem even though the recorded awaited edit targeted the removed checkout. The existing foreign-source test only changes to a different path; no same-path Git-admin/object-store replacement negative covers this.

This is a source-code-path counterexample, not a claim that a production source route was executed. The isolated shell reproduction was unavailable under the client's command policy, but all relevant predicates and persisted fields were inspected directly.

## Required correction

Pin sufficient **original physical checkout identity** in immutable claim/grant evidence (for example, original linked Git-dir plus canonical common-dir/object-store identity, with suitable stable host-owned verification), and compare current values on every post-edit inspection. Do not compare to the reviewing client clone's Git directory, because the two-clone proof deliberately allows a separate ledger reader to inspect the originally claimed physical worktree. Add a disposable negative that substitutes a different linked worktree **at the same approved path** from another clone of the same remote, preserves the pinned commit and journal record, and must return `Rejected` or `HostProofUnavailable`.

Keep the original WQ03 clean-at-claim rule, immutable WQ11 grant CAS, default-off flag, lack of public execution route, and fail-closed test-terminal status unchanged.

## Independent local checks

`git diff --check bed92f3d..e2700710` clean. `dotnet build Client.Tests/Client.Tests.fsproj -c Release` exit 0 (three existing warnings). `dotnet vstest ... --TestCaseFilter:FullyQualifiedName~WorkQueueClaimBoundDirtySourcePolicyTests`: 8/8 pass. Adjacent `WorkQueueReservationPermissionsTests|SourceEffectHostBindingTests|WorkQueueClaimBoundDirtySourcePolicyTests`: 26/26 pass. Passing tests do not exercise the same-path substitution case.
