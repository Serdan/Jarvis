# Reviewer decision: Request changes

Reviewed exact commit `559cb2b15569042543c4d6e90cf30b296b924095`, base `6d1818c58899ad3742e270c6c310514befcbc09e`.

The create/update routing and claimed-review version guard are retained, but replacing a review with omitted optional `sourceRef` passes `""` to `replaceBranchReview`, which writes `TaskRef = sourceRef` and erases the existing source branch. **Blocking finding:** `findings/omitted-source-ref.md`.

Preserve existing TaskRef when the new optional sourceRef is omitted; add a public-to-ledger regression for omitted versus explicit replacement branch. Resubmit exact snapshot after correction. 341 client/75 server branch-local tests passed but do not exercise this behavior.