# Reviewer 1 independent verdict: ChangesRequested (P1)

**Task:** `work-composition-queue-positive-single-cas`
**Review:** `b1c9f6403ac24e71ae40c9b7e1e401bc`
**Frozen review head:** `656e368045e6200f3838e0e6e1b5ab882b6a5718`
**Base:** `3458224ce3bff9141b05a7891148098db4900053`
**Independent checkout:** `review/reviewer1-composition-positive-656e`, clean at the frozen head.

## Independent evidence

- Inspected frozen `Client/Modules/WorkCompositionQueueTransaction.fs`, `WorkCompositionGitLedger.fs`, `WorkCompositionAcceptanceAdapter.fs`, `WorkQueueGitLedger.fs`, `WorkQueueReceiptProducer.fs`, canonical `Server/WorkComposition.fs`, and new two-clone NUnit fixtures.
- .NET SDK `11.0.100`. Restored the isolated checkout; `dotnet test Client.Tests/Client.Tests.fsproj --configuration Release --no-build --no-restore --filter FullyQualifiedName~WorkCompositionQueueTransactionTests --logger "console;verbosity=minimal"`: **3 passed / 0 failed / 0 skipped**.
- Broader `--filter FullyQualifiedName~WorkComposition`: **12 passed / 0 failed / 0 skipped**, net11.0 Release, duration 23s; independent rerun. No hosted Actions/Codex.
- Verified `git status --short --branch` clean on `review/reviewer1-composition-positive-656e`.
- The new positive fixture exercises original WQ04 receipt inspection, a frozen hold operation, independently accepted/integrated prerequisite, single `LedgerStore.transact` writing graph + WQ02 paths, UUID replay, and two-clone one-winner/stale-loser. These are legitimate **scoped rehearsal** results.

## Blocking findings

1. **P1: Spec/linked-task revisions leave stale WQ02 Available.** A valid `Revise` can move the dependent unit's `LinkedTaskId`; `WorkCompositionGitLedger.queueRevocations` matches installed stages by **post-mutation** task link and therefore misses the old stage. The transaction mutates the graph only. The optional correction seam also ignores dependent spec/link change. See `findings/linked-task-revision-bypasses-revoke.md`.
2. **P1 acceptance gap: initial original WQ04 stage is Available while graph is Blocked, until an optional second CAS hold.** The test itself asserts that state. No original producer-level atomic stage creation gate or approved WQ04 role/source lineage exists in this frozen patch; WQ07, WQ03 and WQ08/WQ09/WQ11 vertical acceptance are not proven. The author correctly calls the patch default-off and NO-GO. See `findings/initial-wq04-gap-and-admission-no-go.md`.

**Verdict:** **ChangesRequested** for this exact frozen review, notwithstanding 12/12 tests. Keep migration/Scheduler **NO-GO**, no live claims, no production cutover, and no claim of full positive writer acceptance. Fix same-CAS binding invalidation, protect the original creation path from a graph-Blocked/WQ02-Available head, obtain original role/source and account admission evidence, add negative two-clone fixtures, then submit a **new frozen** independent review. Review never modifies production source or deploys.

## Author-submitted scope note (retained)

Original diagnostic `3458224` was separately accepted only as NO-GO. This patch `656e3684` is a **default-off Frozen-only project planning rehearsal**. Its original CreateTask WQ04 proof is role-neutral with blank source fields. The author explicitly states original WQ04 role/source admission, WQ07 account admission and Scheduler remain NO-GO. This scope limitation is preserved rather than misrepresented as an end-to-end migration GO.
