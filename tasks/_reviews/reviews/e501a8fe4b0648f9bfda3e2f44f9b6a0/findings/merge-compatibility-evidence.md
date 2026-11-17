# Independent merge-compatibility evidence — Reviewer 4

**Frozen head:** `a94241633c5a40111fccffe90a22e93c951ae1d0` (`integration/work-p1-stateful-accepted-20261117`). **G0 base:** `7712bc547c264873036975e30194da03a05cc0ba`. Source: accepted manual-grant device-binding `8d38fdf3792de24103f72ea7a6ff46472dc53efd` and accepted WQ04 original stage revocations `2670e6a2f10059ad80c35f1cd175b068d7b87e7b`.

## Independent verification

- `git log --format=%h %p %s -n 10 a94241633`: merge `e9a4c2085` joins G0 and exact `8d38fdf`; merge `a94241633` joins that result and exact `2670e6a`. Both accepted source commits are ancestors of the frozen head (individual `git merge-base --is-ancestor` exit 0).
- `git diff --stat 7712bc5 a94241633` shows combined change across Client, Server, Common and tests (42 files, 1,713 insertions, 193 deletions); `git diff --check 7712bc5 a94241633` exit 0; pinned integration worktree was clean before testing.
- `.NET 11 Release, Server.Tests`: `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore --no-dependencies --verbosity quiet` **317 passed / 0 failed / 0 skipped**, exit 0, job `f6b6cbfee25c4429b3925ab60178fe2e`.
- `.NET 11 Release, Client.Tests focused+adjacent`: `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter 'FullyQualifiedName~WQ04|FullyQualifiedName~WorkComposition|FullyQualifiedName~WorkQueueReleaseAcceptance|FullyQualifiedName~PermissionPolicy' --verbosity quiet` **65 passed / 0 failed / 0 skipped**, exit 0, job `bb3f3ea24f8749728bc2af5eda638a74`. One F# FS0760 style warning at `Client/Program.fs:188`, not a failure.

## Invariant inspection

1. **Device-bound manual grant:** `ManualGrantStore` stores CSPRNG UUIDv4 credential verifiers in RAM, binds each approval to owner, exact device and canonical Git project identity. `McpTools.sendWithSourceOperationId` rejects another selected/overridden device before any command, re-reads Health on the originally approved device, enforces original project ID, and then sends the manually authorized command to that same device. Pending/denied/unknown tokens and source-effect coauthorization fail closed. The separate local operator approval UI is not bypassed by FullTrust. The server tests include same-name two-device negative, device override and same-device positive, prospective creation binding, denied/pending, restart and scope negatives.
2. **Original WQ04 producer:** `WorkQueueCanonicalCasBinder` binds verified original source operation, versioned current enabled role subscribed to the stage queue and original graph stage link in the winning Git CAS. Replay requires same original, role and immutable graph link; legacy incomplete proof cannot silently become available. `WorkCompositionGitLedger.planOriginalStage` covers Planning, FrozenReview and Integration, and disallows missing/ambiguous feature context.
3. **Graph mutation safety:** `WorkCompositionGitLedger.queueRevocations` compares old/new graph and immutable stage link, blocks changed Available review/integration stages with a CompositionHold within the graph mutation CAS, refuses claimed/live stage rewrite, and preserves unrelated mutations. `WorkQueueGitLedger` recognizes original WQ04 review/integration CompositionHold without accepting unrelated stage kinds. Focused Client tests exercise two-clone same-CAS paths, spec/ancestor/cancel/retarget, dependency wake, claimed denial, unrelated replay.
4. **Scope/default-off:** no production Scheduler/cutover or networked tool actions executed. Project queue publication remains distinct from WQ07 account credit, STOP and actual launch authorization; manual grants remain ephemeral and separate from scheduled Work reservations.

## Finding

**No blocking merge-incompatibility or source regression identified within the reviewed scope.** The 65 client tests are a focused/adjacent slice, not a claimed full-client regression run. This verdict reviews the frozen merged source only, not authorization to activate a live scheduler or migrate persistence.
