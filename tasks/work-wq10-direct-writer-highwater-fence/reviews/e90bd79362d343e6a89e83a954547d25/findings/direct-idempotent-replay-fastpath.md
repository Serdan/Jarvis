# P1 — Four legacy idempotency fast paths bypass the post-freeze fence

**Frozen source:** `0c23ac9bf212285bfbbb96d1fd5b25944a71f079`, reviewed independently in `review/reviewer1-direct-writer-fence-0c23-20261117` (HEAD pinned and clean before tests).

## Finding

`Client/Modules/ReviewWorkflow.fs` directly calls `LedgerStore.findApplied binding action` and returns `Ok applied` **before** either `LegacyDirectWriterFence.preflightSourcePublication` or `LegacyDirectWriterFence.transact` on four old-writer entrypoints:

- `submitIntegration` around line **1239**.
- `completeIntegration` around line **1313**.
- `submitBranchReview` around line **3386**.
- `replaceBranchReview` around line **3437**.

The new `LegacyDirectWriterFence.transactWithScope` deliberately fetches and authorizes **before** calling `LedgerStore.transact`, specifically so an already-applied UUID is denied after Frozen/Preparing. These four upstream short-circuits skip that protection entirely. A legacy operation first applied while account phase is `LegacyActive` will return the prior success when replayed with the same operation UUID after the original account moves to `Frozen` with the enabled host fence, instead of reporting `OldWriterFenced`. The current same-UUID regression only calls `registerAgent`, which does reach the guard; it cannot establish replay denial in these four paths. Malformed or stale enabled host binding is likewise never inspected on the fast-return path.

This does **not** demonstrate a new Git CAS or source-ref publication on the replay itself; the defect is a false successful old-writer operation after exclusion, violating the advertised direct fence guarantee and potentially allowing consumers to continue based on an old success receipt.

## Required correction

Ensure the enabled host direct-writer fence is evaluated **before every `findApplied` short-circuit**, not only before the eventual publication/transaction. Preserve exact default-off and LegacyActive idempotent behavior. Add two-original-clone disposable .NET 11 Release negative tests for post-freeze same-UUID replay of all four concrete operations, stale/malformed enabled binding and absence of new original Git/ref effects. The immutable source-ref publication preflights already present must remain.

Keep public migration/cutover NO-GO: neither this fix nor the synthetic test epochs can attest the original all-client pending intent journal, source-effect high-water, connected/disconnected client protocol, or account/project cross-Git atomic freeze.