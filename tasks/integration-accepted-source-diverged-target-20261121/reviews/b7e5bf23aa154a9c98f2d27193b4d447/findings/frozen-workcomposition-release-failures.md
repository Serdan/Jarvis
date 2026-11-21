# P0: Frozen integration candidate breaks adjacent WorkComposition Release tests

**Review:** b7e5bf23aa154a9c98f2d27193b4d447  
**Frozen reviewed source:** 1280fb51364afc2bcca06f61ff0e2cd100578a8e  
**Base:** d702352ef705f9991ac1cb14dc15a13cce78aa44  
**Disposition:** ChangesRequested — exact-source test compatibility blocker

## Actual .NET 11 Release evidence

- Managed job `601c3f355f934167b24e4245270bb5ee` was run against the Worker 5 frozen checkout, SDK 11.0.100. **OS exit 1**. Seven adjacent WorkComposition tests failed with `InvalidBinding "Remote target already contains the proposed tip without a verified publisher receipt."`
- `Client.Tests/WorkCompositionLedgerTests.fs`: `composition acceptance requires exact independently reviewed and integrated source proof` failed at line 176 on its composition acceptance path.
- `Client.Tests/WorkCompositionQueueTransactionTests.fs`: `CancelledParentMustNotReleaseHeldOriginalWq04StageWhenPrerequisiteBecomesAccepted`, `DependencyBlockedStageGainsNonWakingGraphHoldInGraphCas`, `DuplicateTaskLinkAfterReleaseIsRejectedOnCreateAndReviseAcrossTwoClones`, `OriginalWq04BlockedThenAcceptedGraphAndWq02ReleaseShareOneCASAndRevokeOnCancel`, and two parameterizations of `RelevantMutationAtomicallyRevokesOldStageAndReplaysExactOriginalUuid` all failed in the shared `approvedChild` fixture at line 181.
- The test fixture in **the frozen Git commit** pre-pushes the candidate SHA to the bare target and then calls `ReviewWorkflow.completeIntegration`. The new managed publisher intentionally rejects such a pre-published commit: `LedgerStore.fs:2372-2374` rejects `previousHead = integratedCommit` without an attributed publication receipt. Thus the failing tests still assume obsolete publication semantics. A fixture that repeats the same previously integrated SHA in another completion also cannot prove a second non-force publication.
- The frozen candidate's complete `ReviewWorkflowTests` job `54d02006665a49c18434cacdcd592bee` **did pass** with OS exit 0, 149/149, duration 2m37s. Passing the affected module does not override the seven concrete adjacent failures.
- Full unfiltered Client.Tests job `fb3c4491239f48a8be1ff75cdbcb7109` for frozen SHA 1280fb513 **was canceled** (started 2026-11-21 06:56:48 UTC, canceled 07:02:32 UTC); it has no full-suite pass/fail result and must not be claimed as green.

## Post-freeze distinction

Read-only Git status after the failures showed HEAD remains `1280fb51364afc2bcca06f61ff0e2cd100578a8e`, but Worker 5 has since modified `Client.Tests/WorkCompositionLedgerTests.fs` and `Client.Tests/WorkCompositionQueueTransactionTests.fs` **without committing them into the frozen reviewed SHA**. `git diff` confirms these changes remove fixture pre-pushes and create distinct reviewed outcomes, which is a plausible correction but **not evidence for this immutable review**. A currently running follow-up job compiling those edits likewise cannot retroactively validate 1280fb513.

## Required action

Preserve the correct security boundary: do not weaken `publishIntegrationTarget` to accept externally pre-published tips. Instead fix the adjacent fixtures to let the managed completion operation perform the initial push and use distinct reviewed commits where independent publications are expected. Commit the test compatibility changes, freeze a **new exact source SHA**, run the relevant .NET 11 Release affected and adjacent suites with actual OS exits, and establish the required full-suite result or explicitly account for any known unrelated failures. Request a new independent review; do not promote or integrate this frozen source.

No Jarvis author source files, production remote refs, schedules, deployments or source leases were changed by Reviewer 3.