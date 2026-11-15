# Independent frozen review — RequestChanges (P1)

Reviewer: Reviewer 3 | Review: d42544c445834843918f5702803d40fe
Task: p1-ledger-git-transport-recovery | Frozen SHA: 2a74c33d4a9759528161aa85fc17c8a63decb671
Base: 7b589173135178a634495146675dad5db647e5f8 | Prior ChangesRequested: seq3260, SHA 5a24d3da479a2a934e3898e7add99858e394e565

## Independent source and Release evidence

- Verified exact git HEAD before and after test run; clean worktree, base ancestry and git diff --check PASS. Diff touches only Client/Modules/LedgerStore.fs, Client/Modules/OrchestrationApi.fs and Client.Tests/ReviewWorkflowTests.fs, without WQ-02 core edits.
- SDK 11.0.100. Independently ran dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore with filters PublicTaskYieldBindingDnsFailure, PublicReviewBindingDnsFailure, PureRemoteReadDnsFailure, TransientPrepublication, PostPushConfirmationLoss, RemoteCasContention, AuthAndLocalPreparation, YieldAgentDnsFailure, PushTimeoutNever; local job adde846dbeb349749f31a2062815c807: **10 passed, 0 failed** in net11.0 Release. Unrelated compiler warnings FS0760 and FS3369.
- Full Client.Tests Release suite is **not** established as passing. Prior review documented a bounded timeout/unrelated BrowserBridge failure at the prior SHA. No GH Actions, Codex, merge, deployment or source edits.

## Fixed prior P1/P2 findings

- Public Task and Review writes now inspect locally pinned binding and journal original UUID plus full command before the first remote fetch; they verify the trusted remote before canonical publication. Public Yield and Review Submit DNS fault tests assert unchanged canonical state, saved command/hash, changed-payload/stale-generation rejection, one checkpoint/receipt and identical replay.
- Prepublication transient reads are bounded; possible push goes through original UUID reconciliation, not blind push retry; CAS, timeout, auth and lifecycle tests remain passing.
- Public write errors normalize UUID and reduce nextAction to one policy. Confirmation loss selects reconcile-same-uuid, and pure reads select retry-read-after-reconnect. The previous contradictory-action defect is fixed in observed paths.

## Blocking new finding

**P1 — [Directory synchronization failure incorrectly swallowed after pending journal rename](findings/pending-rename-sync-failure.md).** The new Client/Modules/OrchestrationApi.fs:839-855 catch handles every IOException from durableArchiveMove when final filename exists. On Unix, durableArchiveMove first File.Move's the temp file and then fsyncs the directory (:611-620). If rename succeeds and fsync fails, the catch sees the destination exists and suppresses the error, so the public write can reach remote Git work without confirmed crash-durable UUID+command. This violates the before-first-fetch fail-closed durability guarantee. Source-proven counterexample, **not** a claim the selected DNS tests reproduced filesystem-crash conditions.

**Narrow fix:** Differentiate a pre-rename destination collision from a post-rename sync failure, or retry directory fsync and fail closed on any sync error; do not begin remote Git I/O until directory metadata durability succeeds. Add deterministic rename-success/fsync-failure negative test asserting no fetch/push and original UUID/hash recoverability.

## Decision

**RequestChanges (P1)** despite all 10 focused Release tests passing. The original transport issue is substantially corrected; the new fail-open durable journal path needs a narrow fix and a fresh exact frozen review. Do not merge or deploy.