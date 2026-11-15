# Independent P1 ledger-transport frozen review — Accepted

**Reviewer:** Reviewer 2  
**Review request:** `e95d6b97e9b74f3583ba5bc20f1ce0cf`  
**Exact frozen commit:** `c7c0ea883be9bb1fd3b2b1e54de991bae122e1c5`  
**Base:** `7b589173135178a634495146675dad5db647e5f8`  
**Prior rejected candidate:** `d7ceafb1692a13f916d36c6593c3e40a72a9c226`, ChangesRequested seq3328, review `7a89d6b374924f85a1cdcd07d7ed06ea`.  
**Verdict:** **Accepted** for the frozen P1 transport/durability/CAS source; no blocking finding identified.

## Correctness inspection

1. **First-write intent crash-durability before any remote fetch (previous P1).** `Client/Modules/OrchestrationApi.fs:605-641, 622-630, 823-891`: `persistUnsafe` creates the original UUID-bound command journal with `FileOptions.WriteThrough` and `Flush(true)`, uses a non-overwriting rename and fsync of the leaf journal, then `confirmPersisted` validates the persisted identity and invokes `syncPendingJournalAncestors`. On Unix this fsyncs `ledger-v2/pending` to persist the per-binding directory entry and `ledger-v2` to persist the `pending` entry. The order establishes the complete first-write ancestry prior to the first `executeLedgerWrite` remote preflight. Exceptions in either ancestor fsync are converted to fail-closed validation errors; the already-renamed original UUID+command remains present and subsequent same-UUID invocation re-fsyncs the leaf and both ancestors. Changed payload under that UUID is rejected. This directly closes the missing parent-directory durability invariant found in the prior review.

2. **Public Task/Review preflight and exact identity.** `ReviewApi.write` and `TaskApi.writeUsingContext` now resolve approved *local* trust binding for the journal, then verify the remote binding inside `PendingIntentJournal.executeLedgerWrite`. Thus DNS failure during initial authoritative binding fetch occurs after preserving the original UUID/serialized command and cannot spuriously publish an agent transition. `persistUnsafe` compares project/ledger/epoch, normalized type, immutable serialized command and SHA-256 before allowing replay. Pending intent only retires after verified success or a definitive prepublication rejection with no earlier pending attempt.

3. **CAS and uncertain publication.** `Client/Modules/LedgerStore.fs` scopes prepublication remote Git probes to bounded retries, never retries publication through that read helper, and instruments sanitized stage/UUID/class diagnostics without raw credential-bearing stderr. `transact` observes an exact existing operation receipt (request hash) before creating a new operation, makes a non-force Git push, and performs a fresh confirmation fetch. A potentially attempted push followed by unavailable confirmation returns `OutcomeUnknown` and requires same-UUID reconciliation rather than a blind second push. Competing writes are replayed against a fetched head. The command journal is retained until remote confirmation. Public diagnostics collapse duplicate `nextAction` fields and preserve the original caller UUID.

4. **Scope and source provenance.** `git show --stat` confirms the reviewed source is the immutable published review ref at `c7c0ea88`, and the final correction relative to `d7ceafb` changes only `Client/Modules/OrchestrationApi.fs` and `Client.Tests/ReviewWorkflowTests.fs`. Full candidate versus base changes only those and `Client/Modules/LedgerStore.fs`. Working tree clean; `git diff --check 7b589173..c7c0ea88` exit 0.

## Independently executed local Release evidence

- Runtime: `dotnet --version` = **11.0.100**; test assembly targets `net11.0`.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~FirstPendingParentFsyncFailurePreservesOriginalUuidUntilSameUuidReplay --verbosity minimal`: **1 passed, 0 failed, 0 skipped**, Release build successful.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter <OR of all 12 newly added P1 method names> --verbosity minimal`: **12 passed, 0 failed, 0 skipped**, ~15 s. These include DNS retry/recovery, no duplicate publication, post-push confirmation loss, two-clone remote CAS contention, auth/local preparation failure classification, YieldAgent state/checkpoint preservation, push timeout, post-rename leaf-fsync failure, first-write parent-fsync failure, public Task/Review binding DNS failure and pure-read diagnostics.

The first-write test injects a parent-directory fsync exception twice, asserts **zero** remote fetch/push, unchanged authoritative sequence and agent, unchanged original command/UUID/hash, changed-payload rejection, exactly one later receipt and successful already-applied replay. This is strong deterministic fail-closed coverage of the previously identified gap.

## Evidence boundaries / nonblocking observations

These tests simulate failures deterministically; they do **not** demonstrate a physical crash/power-loss recovery event. The new ancestor-fsync fault case runs on Unix and is skipped on Windows; Windows uses an existing `MoveFileExW(MOVEFILE_WRITE_THROUGH)` path and was not independently run here. I did **not** run or claim a full Client.Tests/Server.Tests suite or production/hosted verification. The worktree's source branch is ahead/behind its local `origin/main` tracking comparison; this verdict approves the pinned source only, not integration into current main. No hosted GitHub Actions, main push, deployment or Codex were used.

**Final finding:** No open P1/P2 blocking issue evidenced within the reviewed correction; accept exact `c7c0ea883be9bb1fd3b2b1e54de991bae122e1c5`.