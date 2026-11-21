# Independent Reviewer 1 — ChangesRequested

**Task:** `challenge-property-model-review-repair-20261121`. **Request:** `06d4e9e1d2c0415d9d55697d0dff61cb`. **Exact immutable Worker 7 source:** `332020cd50f3a2a3986933763edb8f81251e0b1c` at `refs/heads/test/challenge-property-model-review-repair-worker7-20261121`; parent is original tests-only rejected `cd895d493b419e127ededea15f814ab53e484407`, original review `a17c4d9f4e584c449c0876a205e317bb` (ChangesRequested seq9780). Baseline `d702352ef705f9991ac1cb14dc15a13cce78aa44`; target `refs/heads/main`.

## Provenance and scope

Read Reviewer role v2, root/project AGENTS.md, Reviewer shared assignment, original and repair pinned task specs, all four prior independent findings and exact candidate source/docs. `git show` confirmed candidate parent and diff; relative to parent only `Client.Tests/ChallengeStateMachinePropertyTests.fs` and `docs/testing/challenge-state-machine-property-tests-20261121.md` changed. Relative to original base this adds the test project compile include only. `git diff --unified=0` had **no hunk overlapping original four-mode index fault fixture**, preserving seeds 9000–9003. Exact checkout HEAD after tests still `332020cd...`, Git status clean. No reviewer source edits, merges, pushes, main publication, Scheduler activation or deployment.

The repair **substantively expands** independent generated state (192 × 44 pure mixed Challenge/task/attempt/agent/dependency/generation commands), real two-clone fixture comparisons, typed sequential negative outcomes, HEAD/sequence and receipt replay checks, structured category-conserving shrinker, full Challenge-only event-history checks, and 17 additional reverse-index fault scenarios. This addresses substantial parts of the old review, but leaves the blocking oracle gaps below.

## Independently executed .NET 11 Release evidence

SDK `11.0.100`; tested the **frozen source** at `332020cd...`, project `Client.Tests/Client.Tests.fsproj`.

- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter "FullyQualifiedName~Challenge&FullyQualifiedName!~ChallengeReverseIndexFault" --verbosity minimal`: **exit 0, 47 passed, 0 failed**. Built the Release net11.0 binaries from the frozen tree; 2m25s test duration.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter "FullyQualifiedName~SeededReferenceTracesMaintainConjunctionAndTerminality|FullyQualifiedName~TwoCloneStateMachineTracesMatchCanonicalChallengeAndAudit" --verbosity minimal`: **exit 0, 2/2 passed**.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter "FullyQualifiedName~MixedChallengeAttemptPropertyTests" --verbosity minimal`: **exit 0, 2/2 passed**.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter "FullyQualifiedName~ChallengeReverseIndexFaultTests" --verbosity minimal`: **exit 1, 1 passed / 3 failed**. The unaltered **seeds 9000, 9001, 9002** independently fail with source SHA, expected integrity rejection but actual `Ok` on the old production implementation; seed 9003 succeeds.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter "FullyQualifiedName~ChallengeReverseIndexFaultMatrixTests" --verbosity minimal`: **exit 1, 6 passed / 11 failed**. Deliberately red modes **4, 5, 6, 7, 8, 9, 11, 15, 16, 18, 20** reproduce production fail-open responses in disposable real bare-Git clones; positive historical N1→N2→N3 control mode14 passes. Exit1 is not a green gate or a test-fixture infrastructure failure.

## Blocking review findings

1. **P1** `findings/cas-loser-error-family-unchecked.md`: the competing-writer test at lines 654–685 accepts *any* loser `Error` so long as exactly one operation returns `Ok`; no typed CAS/version-conflict oracle. This leaves the original negative-error concern unresolved for concurrent writers.
2. **P1** `findings/generated-coverage-not-per-class.md`: mixed generator assertions at 1228–1258 enforce only aggregate outcome counts and `seen` action names, not each required operation's positive and negative states. Missing class-specific coverage can pass silently.
3. **P1** `findings/mixed-audit-and-assignment-record-oracle-gap.md`: the mixed real-Git model checks state projections, returned operation receipts, wait/index paths and immutable HEAD, but does not independently verify canonical task lifecycle event actor/action records or issued `assignments/{generation}` records for N→N+1; cross-state audit/issuance corruption may escape the oracle.
4. **P2** `findings/ref-identity-and-partial-cancel-matrix-coverage.md`: add explicit wrong ledger/ref identity and partially canceled multi-dependency wait negatives rather than treating a ProjectId-only mismatch and single-dependency cancellation as complete coverage.

## Verdict and next boundary

**ChangesRequested on the tests-only exact frozen commit.** This is due to test-harness oracle/coverage omissions, **not** because the intentional RED production regressions fail on the original production code. Preserve the new red probes and all original seed assertions. Fix the P1 harness issues on a **new immutable tests-only source commit and independent new review**; address P2 coverage if feasible. Separately owned Worker 4 production correction `48f1a8c6030677960a3d84629c4e29cecf56a132` was **not** overlaid or claimed as fixed here, and must be independently reviewed and validated before any source integration. No full unrelated project suite or two-source overlay was run. Review evidence and production readiness remain separate.