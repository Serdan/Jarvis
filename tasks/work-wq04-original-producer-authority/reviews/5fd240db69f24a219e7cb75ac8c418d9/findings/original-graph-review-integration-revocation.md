# P1 — Original WQ04 review/integration graph authority is not revoked on graph mutation

Frozen: `27ccbfed6252948aed645ebaa109a601af7a2b26` (request `5fd240db69f24a219e7cb75ac8c418d9`).

## Reproducible source path

1. `Client/Modules/WorkCompositionGitLedger.fs:222-262` (`planOriginalStage`) now creates an immutable stage/spec/context link for **Planning, FrozenReview and Integration** at the ordinary original WQ04 CAS.
2. An ordinary subsequent `WorkCompositionGitLedger.execute` graph mutation goes through `transactWithVerifier -> queueRevocations` (`:379-505`). But `revocation` unconditionally returns `false, None` when `stage.Kind <> WorkKind.Planning || stage.QueueId <> "planning"` (around `:391-396`). Therefore neither original FrozenReview nor Integration stages are blocked, nor is a claimed/live stage rejected, when changing the linked Feature spec, parent, prerequisites, cancellation or other original `ContextDigest` inputs.
3. Such a mutation makes `stageLinkStillCurrent` false (`:194-207`), yet WQ02 still retains an unchanged Available/Claimed review stage (or integration stage that can later become Available). `WorkQueueRoleAuthority.eligible` (`:626-638`) admits Available stages by queue subscription without verifying this original composition link; the WQ05 accepted-review/integration source checks in `WorkQueueIntegrationSourceAuthority.fs` do not recheck it. `WorkQueueDurableLaunchIntent.verifiedPlanning` checks the original link but only covers Planning and is not a review/integration fence.

## Required correction

Apply the original composition revocation/claimed-stage mutation denial to **all three WQ04-bound stage kinds**, with stage-kind/queue-specific, same-CAS tests: original review and integration Available->Blocked on relevant graph changes; claimed review/integration change fails closed; unrelated graph changes retain eligibility; cloned/replayed CAS mutations do not duplicate or silently restore an old stage. Keep WQ07 account/global capacity, STOP, default-off and no live Scheduler unchanged.

Static source proof; independent .NET11 Release run started but its Jarvis job has not produced a terminal test result yet. A green Planning-only graph revocation test does not cover this path.