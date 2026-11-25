# Provisional review checkpoint

Reviewed snapshot: `c877f7bc7076bf9cbca3652174cfcab9b15b7fb8`
Base: `2be5afb056fc31b0f5f5c7410afaed225b0216bf`
Target: `refs/heads/main`

## Assessment

The implementation appears correct by source inspection. It retries only known transient sharing/lock acquisition failures, with a bounded delay schedule. It rechecks the binding directory and lock path for reparse points before each acquisition attempt. The protected operation is not retried, and remote fetch/ancestry verification remains outside the process-shared lock. Verified loads and task/review write preflights use retries; dashboard-local inspection remains single-attempt.

The net diff touches `Client/Modules/OrchestrationApi.fs`, `Client.Tests/OrchestrationApiTests.fs`, and `Client.Tests/ReviewWorkflowTests.fs`. The ReviewWorkflowTests change is comment-only. GitHub reported no workflow runs and no commit status checks for this head.

## Non-blocking test-quality concern

`launchTemplateWriteRetriesBeforeJournaling` releases the lock after a 60 ms sleep and asserts only that the eventual error is invalid `operationId`. It does not assert that the call waited for the lock. The test would also pass if the preflight were bypassed or if operation-ID validation were moved ahead of lock acquisition. Add an elapsed-time assertion or deterministic synchronization that proves the lock was contended and then acquired before validation. A focused regression for replacing the binding directory/lock with a symlink between attempts would also protect the newly added revalidation.

No implementation defect has been confirmed. This is a coverage gap, not evidence that the current code bypasses the lock.

## Resume dependency

The review-claim operation is still nonterminal: JobId `b6cc61dfa742446c9641fb6618f6d0f9`, operationId `75df4d12-50c6-4f4d-8f0c-d6b906eed92b`. Do not poll while Running. Once terminal evidence is available, reconcile the claim once, confirm reviewer ownership/version, then record the final decision. The initial inbox read is JobId `9cb3f54c617840aca94f8fc12ac5f8a5` (`TaskRead`); required evidence is its terminal inbox payload and canonical post-activation state. The worktree-list operation `b98cf860e5484de19ccae14617a2f74c` is not needed for this review because the frozen GitHub snapshot has been inspected.
