# Independent frozen review findings — WQ-07 on WQ-03
**Reviewer:** Reviewer 3  
**Request:** 1d4384f3a93149f0bd14a2acf195feda  
**Reviewed source:** 4f83130173d7758304fe7c0602897bdfc425dda6  
**Scope:** inert account-global admission CAS adapter and exact WQ-03 source-handoff composition; no production activation.

## 1. Provenance and merge integrity
- Confirmed `git rev-list --parents -n 1`: first parent e26a572b25f9ba829f1014f2b5ca36fd77900e85 (WQ-03 combined main), second parent eb1ec7c8360f99029728cea56ebf8bb78b3c0499 (independently Accepted WQ-07). Both `git merge-base --is-ancestor` checks returned 0.
- Frozen review metadata and registered worktree HEAD both equal exact 4f83130173d7758304fe7c0602897bdfc425dda6; worktree clean. Seven paths differ from first parent; no unrelated paths.
- `git show --remerge-diff` identifies one manual conflict in `Client/Modules/WorkQueueGitLedger.fs` (at the result planner near lines 1022-1064). Merged code preserves WQ-03 `sourceProof` and `validatedContent`, then appends WQ-07 `witnessChanges` to the same atomic `LedgerStore` transaction; neither safety check nor witness is dropped. `git diff --check` returned 0.

## 2. Account-global reservation and CAS invariants
- `Client/Modules/AccountCapacityLedger.fs:559-590` persists one token per launch with project GUID, exact ledger GUID/epoch, hashed approved fetch/push authority, role/version, original admission operation UUID. Capacity check and reserve run in the existing single account Git-CAS transaction, so competing projects cannot both occupy the last slot. Global occupied counts only Held tokens. The same launch cannot reserve a second token.
- Lines 592-606 preserve versioned resize CAS and reject shrinking below actual held occupancy.
- Lines 608-653 require an independently verified current project terminal proof that matches the original account token, project-ledger authority, role/launch/admission identity, exact terminal UUID and valid immutable Git OIDs. A release is a single idempotent CAS transition; a different release UUID cannot free again. Unknown Git transport outcomes remain uncertain, not success.

## 3. Project-witness integrity and C01 abort-only fencing
- `Client/Modules/WorkQueueGitLedger.fs:828-910` creates the immutable admission/C01 or terminal witness in the same Git operation as project-state update. `AbortUnboundProjectAdmission` competes with original admission on absent launch-ID; neither can win after the other has committed. Terminal witness requires the original admission decision. The source-file merge retains these exact atomic multi-path writes.
- `Client/Modules/AccountAdmissionAdapter.fs:40-59,63-221` independently reads the latest trusted WQ-02 project head, checks the exact original Git operation's path set and origin commit, compares immutable witness blob IDs/content to their original versions, and validates the terminal transition. Abort-only needs an untouched zero-claim/unregistered tombstone, no original project admission receipt, and the exact original abort UUID. Ordinary terminal requires original admission and terminal witnesses with ordered operations.
- Adapter lines 251-279 recheck project role/version/account identity and terminal proof before account release. Lines 281-342 hold the global token on missing/ambiguous project evidence and publish C01 before release. Lines 344-418 perform global-first/project-second admission; lost acknowledgment can replay the same original UUID, but cannot mint a second token or silently release.
- The project binding (ledger ID/epoch/approved URL hash), not a possibly reused project GUID or caller label, is matched against the account token. The same-GUID foreign-ledger regression exercises a second real Git remote.

## 4. WQ-03 fail-closed boundary preserved
- `WorkQueueGitLedger.fs:1025-1064` validates initial approved-source identity, anchored base/reachability, generation-specific clean detached checkout for ClaimNext, and WIP handoff evidence on YieldSource before any project ledger publication. The source-handoff branch then calls `verifySourceExternalEffectsAndRefProtection`, which intentionally returns RecoveryBlocked because authoritative mutating-job/original-push journaling and host-enforced WIP immutability/retention are still unavailable. No unsafe source handoff approval was introduced.
- `git grep -n AccountAdmissionAdapter -- Client` finds only the module declaration and fsproj inclusion. There is no live Scheduler call site; this is an inert implementation review, **not** launch/deployment authorization.

## 5. Independently executed evidence
- Exact local worktree `lead-wq07-post-wq03-review` (HEAD 4f831301...), .NET SDK **11.0.100**.
- Executed: `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter 'FullyQualifiedName~AccountAdmissionAdapter|FullyQualifiedName~AccountCapacityLedger|FullyQualifiedName~WorkQueueGitLedger' --logger 'console;verbosity=normal'`.
- Job 4d53eac2a5f04c4193a29f538c2e4bb4: **Completed exit 0; NUnit 51 discovered, 51 passed, 0 failed** (.NETCoreApp v11.0, Release outputs). Includes two-clone final-slot and resize races, same-UUID replay, lost admission/C01/release acknowledgments, foreign-ledger impersonation, immutable witness rewrite, no-claim proof rejection, stale source checkout/WIP drift, unknown jobs and source-yield fail-closed cases.
- `git diff --check e26a572b... 4f831301...`: exit 0. `git status --porcelain=v1`: empty.

## Verdict and residual gate
**ACCEPTED** for exact immutable integration of source **4f83130173d7758304fe7c0602897bdfc425dda6**, with both parents preserved. No new blocking correctness defect found in the inspected WQ-07 account/C01 CAS or WQ-03 conflict resolution.

**Explicit non-authorization:** This verdict does not certify or activate a live Scheduler, migration, deployment, main push or production source handoff. Host WIP-protection/retention and authoritative mutation/push-effect journals remain external blockers intentionally enforced fail-closed.