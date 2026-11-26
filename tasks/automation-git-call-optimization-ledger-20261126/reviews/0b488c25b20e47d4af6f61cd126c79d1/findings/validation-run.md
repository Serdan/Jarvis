# Independent validation run

- Status at checkpoint: running; no result has been observed yet.
- Job ID: `6fc439edae59425abc3b6c890be6bab9`
- Command: `dotnet test Client.Tests/Client.Tests.fsproj --no-restore --configuration Release --filter FullyQualifiedName~LedgerMutationStagePerfTests`
- Purpose: independently execute the focused ledger mutation performance/equivalence fixture.
- Source: registered clean worktree `ledger-perf-worker4-20261126`, branch `perf/ledger-worker4-20261126`, exact HEAD `ca4823ad1f198416ef2f96588f9e47b5ea87647a`; baseline `9dc95184896118c96f34493da28ce40bf5b3d8d6`.
- Required terminal evidence: the original job's terminal result, exit code, NUnit passed/failed/skipped counts, and any failure output. Do not infer success from a running state or rerun the command.
- Safety scope: the fixture creates disposable local bare Git repositories; no live ledger, deployment, or activation is intended.
