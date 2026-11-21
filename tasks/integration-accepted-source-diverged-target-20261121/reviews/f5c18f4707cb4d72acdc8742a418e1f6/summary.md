# Reviewer 3 — independent P0 integration-protocol review

**Verdict: ChangesRequested.** The frozen candidate has positive ordinary-concurrency tests and passes the independent .NET 11 Release gates, but two safety/provenance gaps remain. Do not promote or integrate based on this review.

- Task: `integration-accepted-source-diverged-target-20261121`
- Review: `f5c18f4707cb4d72acdc8742a418e1f6`
- Exact frozen source/review head: `1317089967fc2907527d8899f6e8a513d3ffd751`
- Base: `d702352ef705f9991ac1cb14dc15a13cce78aa44`
- Source branch: `refs/heads/fix/integration-accepted-diverged-worker5-20261121`
- Reviewed scope: `Client/Modules/ReviewWorkflow.fs`, `Client/Modules/LedgerStore.fs`, `Client.Tests/ReviewWorkflowTests.fs`, `docs/ManagedReviewIntegration.md` (four tracked files; 289 insertions, 38 deletions).
- Verified candidate checkout HEAD exactly equals frozen SHA, tracked/untracked status clean before and after tests; `git diff --check <base> <source>` OS exit 0. No changes to any author's source.

## Independently verified behavior

1. `submitIntegration` checks canonical independent Accepted status, pinned review ref/version/SHA, source and target binding, re-fetches the immutable review ref, captures the advertised target SHA and publishes a create-only managed integration ref with intent/replay fencing. It no longer requires a stale reviewed source to descend from the advanced target.
2. `completeIntegration` checks remote source/integrated ancestry, and for managed entries either permits the exact reviewed source as a direct compatible fast-forward or requires a distinct commit with exactly two parents (target first descending from submission target, immutable reviewed SHA second). It compares the integration tree with Git's conflict-free `merge-tree` result; it rejects explicit conflicting resolutions and arbitrary edited trees under ordinary Git object-resolution conditions.
3. The added two-clone/bare-remote regression exercises an Accepted S branching from B, independent target T1 then racing T2, stale non-force push rejection, clean recomposition with the second target and successful receipt. Negative tests cover wrong/missing/extra parents, extra tree edits, conflicting merge, forced remote rewrites to a head missing the integrated commit. Existing tests cover unsupported statuses, immutable source/ref binding, managed ref create-only retry and legacy receipts.
4. Real lease delta verification uses `git diff <base>...<workerSHA>` from each branch's merge base: Worker 8's activity module, Worker 11's managed-command work and Worker 18's BrowserSchedules work have no file intersection with the four candidate files. A direct two-tip diff would misleadingly include historical baseline differences.

## Independent .NET 11 Release evidence

SDK `11.0.100`, actual Jarvis candidate Git `2.56.0`, exact source as above.

- Managed OS job `5e0178be21454e9db1501f17668a1203`: `dotnet test Client.Tests/Client.Tests.fsproj -c Release --framework net11.0 --no-restore --filter FullyQualifiedName~ReviewWorkflowTests --verbosity minimal` — **OS exit 0, 148 passed, 0 failed, 0 skipped, runner duration 2m34s**.
- Managed OS job `ac42bd86656d4d89a23fc33821736d21`: `dotnet test Client.Tests/Client.Tests.fsproj -c Release --framework net11.0 --no-build --no-restore --filter 'FullyQualifiedName~WorkQueueIntegrationSourceAuthorityTests|FullyQualifiedName~WorkQueueCanonicalSourceEvidenceTests|FullyQualifiedName~WorkQueueCanonicalCasBinderTests|FullyQualifiedName~LegacyWriterProtocolFenceTests|FullyQualifiedName~WorkCompositionQueueTransactionTests|FullyQualifiedName~WorkCompositionLedgerTests' --verbosity minimal` — **OS exit 0, 86 passed, 0 failed, 0 skipped, runner duration 2m41s**.
- No independent *unfiltered* Client.Tests or Server.Tests Release run is asserted. Test green does not establish absent adversarial protections.

## Blocking findings

**P0-1 — Local Git replace-object spoof of mechanical merge verification.** See `findings/local-replace-ref-mechanical-proof.md`. `LedgerStore.fs:2259-2302` runs parent/tree comparisons through Git without `GIT_NO_REPLACE_OBJECTS=1`. In a completely disposable bare-remote Git 2.47.3 fixture, a remote commit J with an unreviewed `extra.txt` had a distinct real tree but, after a local `git replace J I`, the default parent/tree inspection reported the clean exact merge I's tree and required parents. The actual remote commit was not clean; replacement-disabled inspection exposed the difference. This is a source-level trust defect plus independent Git reproduction, not a claimed full Jarvis exploit. Current candidate worktree had no replacement refs at inspection, which does not make its future verification safe. Harden all trust-bearing object/history reads and add a negative end-to-end fixture.

**P0-2 — Missing final non-force publisher proof allows loss of racing target history.** See `findings/nonforce-publication-receipt.md`. The ledger completion API receives only the integrated SHA and checks present remote ancestry; it has no preceding target-head/push receipt or verified target-branch no-force policy. In a disposable bare-remote fixture T2 advanced after T1; normal push of clean I=(T1,S) failed, but forcing I over T2 discarded T2 while **all current completion predicates still passed**. The runbook acknowledges push flags cannot be inferred from ancestry and mandates external protections, but positive verified no-force enforcement for the target branch is not established by this candidate. Require fail-closed publisher/host proof or durable CAS publication receipt, plus a negative target-race/force-rewrite regression.

## Scope and next action

Request changes on the immutable candidate; do not re-review the unchanged Accepted source simply because the target advanced. Correct the two proof boundaries and add focused genuine bare-remote regressions, freeze a **new** source SHA, run exact .NET 11 Release gates, then request another independent review. Completion does **not** authorize integration, publication, main mutation, deployment, schedules, or retired Manager/Health restart. No code, source branches, live remotes or schedules were modified by this reviewer; only canonical review documents were written.