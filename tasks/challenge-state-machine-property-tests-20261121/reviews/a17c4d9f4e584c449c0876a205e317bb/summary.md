# Independent Review — ChangesRequested

**Reviewer:** Reviewer 1 (independent of Worker 7). **Task:** `challenge-state-machine-property-tests-20261121`. **Request:** `a17c4d9f4e584c449c0876a205e317bb`. **Exact frozen source:** `cd895d493b419e127ededea15f814ab53e484407`, from `refs/heads/test/challenge-state-model-worker7-20261121`; base `d702352ef705f9991ac1cb14dc15a13cce78aa44`, target `refs/heads/main`.

## Scope and source provenance

Independently checked frozen source, task HTML contract, active Reviewer role, workspace/project AGENTS.md, producer documentation, source-vs-base Git diff, two-clone fixture/model/receipts/corruption implementations, and production `wakeDependents` behavior. Changed paths are only `Client.Tests/ChallengeStateMachinePropertyTests.fs` (new, 736 lines), `Client.Tests/Client.Tests.fsproj` (one compile include), and `docs/testing/challenge-state-machine-property-tests-20261121.md` (new). **No production source changes.** Exact source and main worktrees were clean before/after; reviewed test worktree HEAD equals frozen SHA. No source edits, publication, Scheduler activation, or deployment.

## Independent executed evidence — .NET SDK 11.0.100, Release

1. `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~ChallengeStateMachinePropertyTests --verbosity minimal`: **exit 1**, 6 passed / 3 failed / 9 total, ~30 s test time. Importantly, this substring filter matches *all* nested fixtures under the same F# module, not just two methods. The committed document incorrectly calls this command 2/2 green.
2. `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter "FullyQualifiedName~Challenge&FullyQualifiedName!~ChallengeReverseIndexFaultTests" --verbosity minimal`: **exit 0**, **45 passed / 0 failed / 0 skipped**, ~1m20s. Includes generated and existing non-red Challenge regressions.
3. `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter FullyQualifiedName~ChallengeReverseIndexFaultTests --verbosity minimal`: **exit 1**, 1 passed / 3 failed / 4 total, ~5 s. Seeds **9000** (orphan next-generation by-challenge accepted), **9001** (wrong-owner index silently accepted), **9002** (orphan next-generation by-owner accepted by inbox) reliably reproduced against real disposable bare-Git fixtures. Mode 3 correctly fails closed. Same three failures occurred in command (1), not transient fixture/setup errors.

The intentionally red production regressions are useful evidence and should be fixed in a separately scoped *production* correction; their existence alone is **not** why this tests-only review requests changes. Do not weaken them or silently edit production source.

## Review findings requiring test-author correction

- **P1** `findings/reference-misses-task-dependency-state.md`: The generated independent `Reference` has only Challenge/receipt state. Block-attempt/dependencies/attempt-agent state/assignment generations are four fixed examples, not generated model-vs-real transitions; required mixed command classes and interleavings are absent.
- **P1** `findings/weak-negative-oracle-and-shrinker.md`: Generic `Error _ -> Rejected` masks wrong error categories and possible unrelated ledger effects; the post-step oracle does not verify remote HEAD and audit actor/action concordance for every rejected/replayed operation. Shrinker treats *any* exception as preserving the original counterexample, including unrelated fixture/Git failures.
- **P1** `findings/reverse-index-fault-matrix-gaps.md`: Only four one-mutation index probes, omitting required missing/extra edges, stale challenge and task/attempt generation, corrupted canonical duplicates, cancellation and identity/ref cases. Input duplicate rejection is not canonical index corruption.
- **P2** `findings/documented-target-filter-selects-red-tests.md`: Correct reproducibility documentation: the model-prefix filter actually selects 9 tests and exits 1; document an accurate two-method filter and separate expected-red command.

## Verdict and boundaries

**ChangesRequested** on frozen source `cd895d...`, for these test completeness and oracle/evidence defects. Preserve reproducible red production cases; expand the simple reference model with dependency/assignment actions, harden typed error/receipt and shrinker checks, complete the explicit corruption matrix, and correct filter documentation. Submit a **new immutable source SHA and independent new review request**; do not mutate this decided review after verdict.

No full unrelated project-wide suite was run; the executed broader gate was the 45-test Challenge filter. No source/remote publication claimed.