# P1 (blocking): WorkComposition refuses the newly authorized clean-merge receipt

**Frozen reviewed source:** `15eaecd90117529ef0a43c441c161f8e23bdde9e` (parent `51d933c4bd2d2fb5ec2bbe0365f0c4bae7ce360d`; task base `d702352ef705f9991ac1cb14dc15a13cce78aa44`). **Review:** `e7f4290f5d6c4e1c9dc23316af1692d8`. **Disposition:** ChangesRequested; cross-component functional acceptance blocker, not a claim of a runtime failure in existing direct-path tests.

## Source-bound contradiction

1. `Client/Modules/ReviewWorkflow.fs:1362-1377` now explicitly accepts a mechanically verified, non-force-published two-parent integration **I** where `integratedCommit <> currentRecord.SubmittedCommit` **S**. Its canonical `IntegrationSubmission` persists `SubmittedCommit = S`, `IntegratedCommit = I`, `Status = "Integrated"`, and the attributed `VerifiedNonForceGitPush/v1` publication fields.
2. The unchanged `Client/Modules/WorkCompositionAcceptanceAdapter.fs:100-121` still requires `submission.IntegratedCommit = submission.SubmittedCommit` at line 104, among its `checkAt` conditions. If false, it returns `InvalidBinding "Composition acceptance requires an independent review of this exact spec and an integrated canonical source receipt."`
3. Therefore, for an existing WorkComposition unit whose review kind correctly matches `reviewPurpose graph unit`, take Accepted reviewed source **S** from B, advance remote target to unrelated-on-first-parent T, submit exact S, construct conflict-free I=(T,S), and successfully call managed `completeIntegration`. `WorkCompositionAcceptanceAdapter.acceptIntegrated` cannot accept I's otherwise-valid canonical receipt solely because `I != S`. The new integration protocol does not work end-to-end for this existing consumer.

This is demonstrable by the Boolean equality in the frozen source; no end-to-end WorkComposition clean-merge negative test was present, so do **not** describe it as an executed failing test.

## Related regression evidence

The previous frozen review `b7e5bf23aa154a9c98f2d27193b4d447` identified seven obsolete WorkComposition fixture failures from pre-pushing the target. The current frozen source includes corrected direct-publication fixtures in `Client.Tests/WorkCompositionLedgerTests.fs` and `Client.Tests/WorkCompositionQueueTransactionTests.fs`. Reviewer 1's `.NET 11 Release` focused managed job `ceca393755344a8daaf29189fd6d8e77` exited **0** with **20 passed / 0 failed / 0 skipped**. These tests keep direct `IntegratedCommit == SubmittedCommit` acceptance green but do not exercise the distinct clean merge that is the feature of this P0 task.

## Required correction

Adapt `WorkCompositionAcceptanceAdapter.checkAt` to accept **verified managed mechanical merge** integration receipts in addition to direct fast-forward ones; bind it to the canonical exact Accepted review/source and durable managed publication proof rather than merely dropping equality. Preserve the independent reviewer, reviewPurpose/composition-closure, target/ref/version and CAS protections. Add a real two-clone/bare-remote WorkComposition acceptance regression with S reviewed on B, target independently advanced to T, clean I=(T,S) published by CompleteIntegration, then WorkComposition `acceptIntegrated` successfully recording acceptance. Include negative forged/unverified receipt cases and retain direct/historical compatibility. Re-freeze and run the relevant .NET 11 Release suites with actual OS results before another independent review.

**Safety:** No Jarvis author source, branch, integration target, live remote, schedule or deployment was modified by this reviewer.