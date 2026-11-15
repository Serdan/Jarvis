# P1 — Public WQ-02 execute retries reject the original operation ID because a new clock value changes the request hash

**Exact frozen source:** `683357d7c1432541abcfa59fbbf483feab49a371`  
**Review:** `5f8a9c41276e4d42a4f43c568f03e2cd`  
**Reviewer:** Reviewer 2  
**Severity:** P1 / ChangesRequested — violates original-operation receipt and exactly-once retry guarantees of this canonical Git foundation.  
**Evidence:** Independent executable .NET 11 reproduction against two disposable Git clones and the frozen Release assemblies, plus source trace. No source changes, hosted Actions, merge or deployment.

## Deterministic causal counterexample

1. Create an isolated bare Git remote, clone it twice, initialize `LedgerStore` and `WorkQueueGitLedger`, and subscribe `Lead` to `planning`.
2. Construct one valid initially Available `Planning` stage P (no dependencies or source lineage). Choose one fresh UUID `op`.
3. Clone A calls public `WorkQueueGitLedger.execute a "Reviewer 2" op 0 (PublishStage P)`: **Ok, AlreadyApplied=false**.
4. After 1,300 ms, clone B calls **the identical public method**, same actor, UUID, expected version 0, and same byte-for-byte typed `PublishStage P`.
5. Actual result: **`Error OperationIdReused`**, rather than the immutable original operation receipt with `AlreadyApplied=true`. Verified B can still load the committed canonical project: **Version=1, Receipts.Count=1, Works.Count=1**.

Condensed actual console evidence:

```text
FIRST=Ok ... AlreadyApplied = false
SECOND=Error OperationIdReused
VERSION=1 RECEIPTS=1 WORKS=1
```

The independent F# script referenced the exact frozen `Common.dll` / `JarvisClient.dll`, made only disposable `/tmp` bare Git clones, cleaned the scratch repository in `finally`, exited 0, and its external `.fsx` file was subsequently removed. The registered source worktree remained clean and at the exact frozen SHA.

## Exact code path

- `Client/Modules/WorkQueueGitLedger.fs:558-561`: public `execute` generates `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()` **on every invocation**.
- `Client/Modules/WorkQueueGitLedger.fs:440-448`: `change` embeds that fresh `now` as `serverWitnessMillis` in `intent.RequestJson`, alongside unchanged expected version and mutation.
- `Client/Modules/LedgerStore.fs:1946-1955`: `intentHash` hashes the full canonical request, including the varying witness.
- `Client/Modules/LedgerStore.fs:1976-1995`: the already-applied UUID is compared against this newly computed hash; despite unchanged user command, the retry receives `OperationIdReused`.
- The existing `CrossCloneStaleWriterAndOriginalReplayReceipt` and other focused fixtures use internal `executeAt` with **the same explicit test timestamp** on replay; they do not exercise the public call with a newly sampled clock. Newly added malformed-nested-payload tests also use `executeAt`.

This is not simply a stale-version CAS rejection, nor an actually changed semantic payload. The first operation remains canonically committed and readable. The public retry entrypoint cannot recover its immutable result after a plausible transport ambiguity, defeating the original UUID/receipt semantics of this foundation.

## Required narrow correction

Make **same operation UUID + actor + expected project version + semantic WorkMutation** produce the same canonical request identity across actual public retries, independently of wall-clock passage. Preserve the original witnessed publication time in the committed state/receipt or a durable per-operation journal, but do not regenerate and rehash that time as if it were user-supplied semantic input on replay. A true changed actor/expected version/mutation must still return `OperationIdReused`. Add public-`execute` (not merely `executeAt`) two-clone + delayed retry tests including post-commit uncertain delivery and assertion of the *exact* original receipt, `AlreadyApplied=true`, unchanged remote head/version and no second claim/availability witness. Keep the newly corrected pre-CAS roundtrip and all four prior fail-closed guards.

**Conclusion:** ChangesRequested on this frozen SHA. The two new poison corrections pass, but a separate independently executed P1 original-UUID retry failure prevents acceptance of the canonical foundation.
