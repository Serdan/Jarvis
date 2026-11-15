# Independent frozen review — RequestChanges (P1)

**Reviewer:** Reviewer 3  
**Review:** `7a89d6b374924f85a1cdcd07d7ed06ea`  
**Task:** `p1-ledger-git-transport-recovery`  
**Frozen exact SHA:** `d7ceafb1692a13f916d36c6593c3e40a72a9c226`  
**Base:** `7b589173135178a634495146675dad5db647e5f8`  
**Previous review:** `d42544c445834843918f5702803d40fe`, ChangesRequested seq3304 at `2a74c33d4a9759528161aa85fc17c8a63decb671`

## Independent source and test evidence

- Inspected frozen HEAD and full four-commit ancestry from the pinned base, including `Client/Modules/LedgerStore.fs`, `Client/Modules/OrchestrationApi.fs`, and `Client.Tests/ReviewWorkflowTests.fs`. The worktree is clean and `git diff --check base..frozen` passed. No WQ-02 core edits.
- Independently ran `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~PendingRenameFsyncFailure` on frozen HEAD: **1 passed, 0 failed, 0 skipped**, net11.0 Release. Build succeeded with unrelated existing FS0760 / FS3369 warnings. Job `b70fb7c55c8c4257b5c5e89e855aa600`.
- Independently ran `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore` with the 11-case filter covering `TransientPrepublication`, `PostPushConfirmationLoss`, `RemoteCasContention`, `AuthAndLocalPreparation`, `YieldAgentDnsFailure`, `PushTimeoutNever`, `PendingRenameFsync`, `PublicTaskYieldBinding`, `PublicReviewBinding`, and `PureRemoteReadDns`: **11 passed, 0 failed, 0 skipped**, net11.0 Release, duration 14 s. Job `c7547260f14943b98fa34226a4fa7828`.
- **Not claimed:** full Client.Tests or server suite pass, power-loss reproduction, hosted CI, or deployment. No GitHub Actions, Codex, main push, merge or source edits by reviewer.

## Corrected prior P1 is verified

- In the new commit, `persistUnsafe` no longer swallows `IOException` merely because the destination exists after `File.Move`. A rename-success/fsync-failure now returns a local error before any remote Git read/write.
- On same-UUID replay with a pre-existing pending file, `syncArchiveDirectory` is reattempted before Git is allowed. The regression asserts zero remote fetch/push after two injected fsync failures, preserved original UUID/serialized command/SHA-256, unchanged canonical sequence/agent, then one successful original receipt and stable replay. This addresses seq3304's specific defect.
- Public Task/Review journal-before-remote-binding preflight, transient-only bounded retries, push-exposure tracking, same-UUID reconciliation, CAS contention and single safe nextAction remain covered by the passing focused tests/source inspection.

## New blocking finding

**P1 — [Fresh pending-directory parent entries are not synchronized before remote Git work](findings/first-write-parent-directory-durability.md).** On a first write after binding, `withIntentLock` may create both `ledger-v2/pending` and `pending/<hash>` without fsyncing their respective parent directories. The new patch fsyncs the **leaf** `pending/<hash>` after publishing `U.json`, but that does not guarantee the newly created `<hash>` and `pending` directory links survive a system crash. Thus a write can reach remote Git with the caller's original pending UUID/command path not yet crash-durable, in violation of the same journal-before-first-fetch invariant. This is a source-backed first-use durability hole, not a claim of observed production failure.

**Narrow fix requested:** Durably synchronize newly created ancestor directory entries (and fail closed on errors) before remote access; add a deterministic first-write fault test at that boundary and verify original UUID+hash recovery, zero remote I/O on failure, and exactly-once eventual replay. Preserve WQ-02 isolation.

## Explicit verdict

**RequestChanges — P1** on the frozen `d7ceafb1692a13f916d36c6593c3e40a72a9c226`. The previous P1 is fixed and all 11 focused Release tests pass, but first-write journal ancestry durability remains a blocking state/recovery invariant. Do not integrate/deploy pending a corrected exact frozen independent re-review.