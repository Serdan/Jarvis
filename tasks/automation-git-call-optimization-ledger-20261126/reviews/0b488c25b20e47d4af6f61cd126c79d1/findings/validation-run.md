# Independent validation runs

## Focused mutation fixture

- Status: terminal success.
- Job ID: `6fc439edae59425abc3b6c890be6bab9`
- Command: `dotnet test Client.Tests/Client.Tests.fsproj --no-restore --configuration Release --filter FullyQualifiedName~LedgerMutationStagePerfTests`
- Exit code: 0; NUnit: 5 passed, 0 failed, 0 skipped.
- Purpose: independently execute the focused ledger mutation performance/equivalence fixture.
- Source: registered clean worktree `ledger-perf-worker4-20261126`, branch `perf/ledger-worker4-20261126`, exact HEAD `ca4823ad1f198416ef2f96588f9e47b5ea87647a`; baseline `9dc95184896118c96f34493da28ce40bf5b3d8d6`.
- Safety scope: tests use disposable local bare Git repositories; no live ledger, deployment, or activation.

## Cross-caller equivalence fixture

- Status at checkpoint: running; no terminal result observed yet.
- Job ID: `4aa326190c5d48e58fee42dcc81afb42`
- Command: `dotnet test Client.Tests/Client.Tests.fsproj --no-restore --configuration Release --filter "FullyQualifiedName~WorkQueueCredentialTests|FullyQualifiedName~CrossCloneStaleWriterAndOriginalReplayReceipt|FullyQualifiedName~LostPushAcknowledgementIsReconciledWithoutInventingOriginalSuccess|FullyQualifiedName~NonFastForwardConflictShowsRemoteShaAndPreservesBothBranchesForRecovery|FullyQualifiedName~ReviewsAreIndependentOfTaskPhaseAndAllowMultipleClaims|FullyQualifiedName~ConcurrentIndependentConditionWritersAreCASProtected|FullyQualifiedName~TwoCloneStateMachineTracesMatchCanonicalChallengeAndAudit"`
- Purpose: extend validation across authenticated credential bindings, cross-clone remote advancement, lost-push uncertainty/CAS, and canonical review/challenge writes.
- Source: same exact clean candidate worktree and commit `ca4823ad1f198416ef2f96588f9e47b5ea87647a`.
- Required terminal evidence: original job's terminal result, exit code, NUnit counts, and failure output. Do not infer success from a running state or rerun the command.
- Safety scope: all selected fixtures use disposable local repositories; no live ledger, deployment, or activation.
