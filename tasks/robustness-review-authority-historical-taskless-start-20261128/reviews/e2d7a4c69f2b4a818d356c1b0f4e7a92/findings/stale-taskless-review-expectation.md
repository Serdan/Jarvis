# P1 — An obsolete taskless-review expectation remains in the compiled test suite

Reviewed source: `fc94b8cd31e34fe05305c0e72343653a31ff5692` against base `914907d14803ddf861e438419147903dcbf8f7f7`.

`Client.Tests/ReviewWorkflowTests.fs:3712-3781` still contains `RoleOnlyReviewerTasklessLifecycleYieldsWithoutTaskAndHoldsReviewAuthority`. It starts the registered Reviewer agent in taskless `Working` state, creates an unclaimed review from Owner, then calls `expectStale (ReviewWorkflow.claimBranchReview ... "Reviewer" 1)` and asserts that the review remains unclaimed.

That expectation is now incompatible with the submitted implementation. `Client/Modules/ReviewWorkflow.fs:4588-4603` removes the `guardReviewAuthorityAt` call from `claimBranchReview`; the transaction now checks only the review's expected version and `Unclaimed` status before committing the claim. With the fixture's valid version-1 unclaimed review, the claim succeeds, so the existing `expectStale` assertion fails. I confirmed the entire 3712-3845 test block is byte-identical between the base and submitted commits. The test file remains included by `Client.Tests/Client.Tests.fsproj`.

The author summary says the two superseded authority-hold tests were replaced, but this third, older taskless-review assertion remains. This is a deterministic test-suite regression, not merely missing coverage.

**Required correction:** update this test to assert the intended successful claim and exact ReviewerId/version while preserving its unrelated taskless lifecycle, role-instruction, and STOP assertions. Then run the focused test and the relevant client test qualification.

Reviewer verification was source-based. The submitter's summary reports release qualification job `f14d36559c4948ab8c3d5a4abf5f834a` still running; I did not poll or rerun that engineering job.