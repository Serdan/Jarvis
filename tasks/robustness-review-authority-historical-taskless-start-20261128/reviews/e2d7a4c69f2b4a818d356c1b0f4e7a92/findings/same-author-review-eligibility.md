# P1 — Same-author review is still allowed; the required independence invariant is not covered

Reviewed source: `fc94b8cd31e34fe05305c0e72343653a31ff5692`.

The task specification explicitly requires independence from the reviewed author and a regression for same-author rejection. The submitted changes do not preserve or prove that invariant.

In `Client/Modules/ReviewWorkflow.fs:4485-4524`, `submitBranchReview` records `OwnerAgentId = requesterId`. In `Client/Modules/ReviewWorkflow.fs:4588-4603`, `claimBranchReview` validates the request ID, expected version and `Unclaimed` status, but never rejects `review.OwnerAgentId = reviewerId`. `decideBranchReview` then permits that claimant to decide the request because it checks only that the review is Claimed at the expected version by that same reviewer.

The existing test `Client.Tests/ReviewWorkflowTests.fs:982-1006` explicitly says “the submitter may also claim” and exercises self-claim for both Owner and Reviewer. The new history tests use distinct requesters and R1/R2/R3 claimants, so they do not exercise the required same-author rejection. The only persisted author identity in this review record is the requester stored as `OwnerAgentId`; if the intended source-author identity differs, the record/API needs to carry that identity explicitly rather than silently treating request submission as independent.

**Required correction:** enforce the intended reviewed-author/claimant separation inside the canonical claim transaction (using the authoritative author identity), update the existing self-claim test, and add a production-route regression proving same-author claim/decision cannot mutate the review while a distinct eligible reviewer can still claim it. Preserve the existing version/CAS and winning-claimant checks.

This finding is independent of the historical-start guard removal; it is a separate acceptance requirement stated in the task specification.