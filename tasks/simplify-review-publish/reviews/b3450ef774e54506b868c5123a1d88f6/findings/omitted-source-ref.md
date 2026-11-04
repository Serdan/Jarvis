# P1 — Omitted `sourceRef` erases a review's branch context on replacement

**Snapshot:** `559cb2b15569042543c4d6e90cf30b296b924095`

**Location:** `Server/McpTools.fs`, `SubmitReview` (the new `expectedRequestVersion > 0` routing); `Client/Modules/ReviewWorkflow.fs`, `replaceBranchReview` (around line 2582).

`SubmitReview` makes `sourceRef` optional (default `""`) even when updating a review. A caller can therefore pass only `requestId`, `headOid`, `expectedRequestVersion`, `requesterId`, and task identity. The Update handler forwards that empty `SourceRef` to `replaceBranchReview`, which always writes `TaskRef = sourceRef`. This silently clears the stored source branch reference. The API explicitly preserves `purpose` and `targetRef` on replacement, so losing `TaskRef` contradicts the same principle and damages discoverability/traceability.

**Correction:** When `sourceRef` is omitted on replacement, preserve the original `review.TaskRef`; explicit new branch values should still update it. Distinguish omitted from explicitly cleared if clearing is intended. Add a public-API-to-ledger regression verifying both paths and claimed-review rejection. Keep the immutable head/base and review version checks.
