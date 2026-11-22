# F1 — Existing inbox Git-process budget regresses on the six-path candidate (P1)

## Contract and source location

Frozen source `d948e2cdc8aceba5236f621f62d99f3422dfe4fc` is the sole child of verified main base `dcac884fc061869572b12a03befc12f11c009f2f`. The task requires restoring accepted Challenge integrity invariants while preserving unrelated main behavior. An unchanged existing Release performance test, `Client.Tests/ReviewWorkflowTests.fs:6249-6326`, invokes `PerformanceRegression.boundedInbox`, whose unchanged `Client.Tests/PerformanceRegression.fs` limit is at most 12 Git process starts.

The candidate adds `verifyOwnerWaitIndexesAt` at the start of `ReviewWorkflow.getAgentInbox` (`Client/Modules/ReviewWorkflow.fs`, near 3416) unconditionally. That calls `LedgerStore.listCompleteWaitIndexesAt` for the owner's index prefix, which launches `git log --first-parent --diff-filter=A --format= --name-only <snapshot> -- <prefix>` (`Client/Modules/LedgerStore.fs`, near 1535). This additional Git process occurs even when no Challenge wait exists; it is not background or unrelated fixture noise.

## Source-bound reproduction

- Independent clean, main-rooted base worktree `/home/serdan/Projects/.Jarvis-worktrees/reviewer3-challenges-baseline-20261122` at `dcac884f`: `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~AgentInboxBulkLedgerBenchmarkAndSnapshotCorrectness` completed exit 0, **3/3 PASS**, job `e372e0e1335e4bf29e3e76428e8be27a`, TRX `/tmp/jarvis-reviewer3-challenges-baseline-20261122/reviewer3-baseline-perf.trx`.
- Independently rerun on frozen clean author worktree at `d948e2cd`, built Release net11.0 binaries: same targeted tests with `--no-build` completed exit 1, **0/3 PASS**; cases `(0)`, `(220)`, `(2200)` each report **13 > 12 Git subprocesses**. Job `abe3a8cabc6e4594b296a83a5d683373`; TRX `/tmp/jarvis-reviewer3-challenges-source-20261122/reviewer3-source-perf.trx`.
- Author's full unfiltered candidate TRX `/tmp/jarvis-w14-challenges-20261122/full/challenges-full.trx` independently inspected: 1,385 PASS / 3 FAIL, same three tests (total 1,390, with one skipped reported in terminal). This is **not full-green**.

## Impact and disposition

Regression is causally linked to this six-path candidate, not inherited from main; the zero-record case confirms a mandatory extra Git process even in an empty inbox. A blanket claim that the full-suite RED is unrelated is not supported. It breaks an existing process-count acceptance invariant on every inbox read and adds history traversal work to the hot path.

**ChangesRequested** on the frozen source. Preserve missing-index fail-closed detection and the independently demonstrated safety tests. A bounded repair could batch/reuse the historical proof without a new process per inbox; alternatively any proposal to revise the preexisting budget requires separate evidence and independent scope/adjudication rather than silently editing the non-leased performance test. This finding does not invalidate focused Challenge functional success, authorize wider edits, or imply integration/main publication.
