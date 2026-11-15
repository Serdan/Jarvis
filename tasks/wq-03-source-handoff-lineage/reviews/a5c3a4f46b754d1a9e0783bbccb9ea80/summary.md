# Independent frozen WQ-03 source-handoff review — ChangesRequested

- **Review:** `a5c3a4f46b754d1a9e0783bbccb9ea80`; reviewer `Reviewer 1`.
- **Exact frozen commit:** `4f0ac4df3f3f437501c8da054c262706ec3f5e64` on `refs/heads/jarvis-review/wq-03-source-handoff-lineage/a5c3a4f46b754d1a9e0783bbccb9ea80/r1`.
- **Baseline:** WQ-02-integrated main `e0ae297f42217a19cde29fb7729bf509a2448e53`.
- **Scope inspected:** `Server/WorkQueueContracts.fs`, `Client/Modules/WorkQueueGitLedger.fs`, `Client.Tests/WorkQueueGitLedgerTests.fs`; accepted source-continuity architecture §9; fresh isolated reviewer worktree anchored exactly to the frozen SHA.

## Verdict: ChangesRequested — 3 P1, 1 P2

1. **P1: successor does not start from pinned WIP source.** `ClaimNext` only runs remote-tip verification (`Client/Modules/WorkQueueGitLedger.fs:773-785`), not a pinned clean local successor checkout. The new two-clone test retains clone B's original HEAD and then claims `L2` on that stale clone (`Client.Tests/WorkQueueGitLedgerTests.fs:1059,1095`). See `findings/p1-stale-successor-checkout.md`.
2. **P1: unresolved mutating processes/pushes can bypass safe handoff.** `verifySourceHandoff` checks Git HEAD/status/ancestry/remote tip but never inspects mutating job or unknown-push journals. `MarkSourceRecoveryBlocked` is an optional caller-reported transition, not an independent barrier. A clean instant snapshot can requeue work while a prior process will mutate later. See `findings/p1-unreconciled-source-effects.md`.
3. **P1: WIP ref is mutable; no guaranteed protected immutable continuation.** The adapter only reads current `ls-remote` tip. The new test itself successfully force-rewrites the supposedly immutable `jarvis-wip` ref (`Client.Tests/WorkQueueGitLedgerTests.fs:1085,1094`). A later claim detects drift but cannot recover an overwritten/GC'd continuation. See `findings/p1-wip-ref-not-immutable.md`.
4. **P2: first-generation source identity preflight is absent.** Initial `SourceLineage` fields are merely nonblank; `verifySourceContinuation` returns unconditional success for no continuation, even with wrong repository ID/pinned base. An unusable first-gen source stage can consume a live claim. See `findings/p2-unverified-initial-source-lineage.md`.

## Verified positives

- One unfinished implementation stage per exact repository/task/target-ref/lineage is collision-rejected by `Contracts.publishStage` (`Server/WorkQueueContracts.fs:570-596`) under project CAS.
- `Contracts.yieldSource` checks launch UUID, exact source work and claim/stage/lineage generations, clear gate, no finalized/push state; advances WIP lineage/claim generation, records typed immutable operation receipt, requeues with fresh FIFO and terminalizes previous launch (`:1458-1527`).
- Missing remote WIP, dirty checkout, altered stale checkpoint payload and remote ref drift are rejected in the existing targeted negatives. Replay of the *identical* original operation from another clone returns the immutable receipt without double-requeue.
- Worker submitted no change to `OrchestrationApi`; reviewed commit has no authorized activation or production migration.

## Independent Release evidence

- **Client.Tests WorkQueueGitLedgerTests:** .NET 11, Release, fresh reviewer checkout: **23 passed / 0 failed**, actual `dotnet test Client.Tests/Client.Tests.fsproj --configuration Release --filter FullyQualifiedName~WorkQueueGitLedgerTests --verbosity minimal`.
- **Server.Tests WorkQueueContractTests:** .NET 11, Release, same frozen checkout: **77 passed / 0 failed**, `dotnet test Server.Tests/Server.Tests.fsproj --configuration Release --filter FullyQualifiedName~WorkQueueContractTests --verbosity minimal`.
- **Patch hygiene:** `git diff --check e0ae297f... 4f0ac4df...` exits 0.
- A broader Client.Tests Release run was also started, but no full-suite pass is claimed in this verdict. The scoped evidence is sufficient to diagnose the missing safety invariants: passing tests are not proof of a covered negative space.

**Acceptance requires correction and a new exact frozen independent review.** Do not integrate/activate this source-writing path based on the current passing tests. No hosted GitHub Actions, main push, deployment or Codex used.
