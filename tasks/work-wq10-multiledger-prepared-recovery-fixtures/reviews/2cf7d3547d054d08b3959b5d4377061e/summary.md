# Reviewer 2 — ChangesRequested

**Frozen source:** `34b06e396ede89e58fdfa5b641300e262ce5ec44` (review `2cf7d3547d054d08b3959b5d4377061e`), descends from accepted base `d0065952efc47789ef81a6bef056c515954ee52c`. Prior Reviewer 3 ChangesRequested review `4b2fa60d1d134b43b54f9de8f0ea9e05` pinned `a268ffe6473dc820d75c732c022da8242ee0e12e`.

## Independently verified

- Source worktree `worker10-wq10-multiledger-recovery-20261116`: exact frozen SHA, clean; only 5 expected source/test/doc/compile-include files changed from base; base is an ancestor; `git diff --check d0065952..34b06e39` exit 0.
- Local .NET 11 Release `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~WorkQueueMultiLedgerPreparedRecoveryTests`: **8/8 passed**.
- Local .NET 11 Release adjacent `--no-build --no-restore --filter 'FullyQualifiedName~WorkQueueCanonicalMigrationPrepareTests|FullyQualifiedName~WorkQueueProjectMigrationPrepareTests|FullyQualifiedName~AccountCapacityLedgerTests|FullyQualifiedName~WorkQueueGitLedgerTests'`: **67/67 passed**.
- New recovery finalization rejects `Failure=None` when the currently read account or project classification is false; whole-queue inspector checks every Work state, launches, migration epoch and available ordering. Two new fixtures exercise a malformed receipt and a later unrelated Available stage. The fixture now freezes all originally enumerated operation receipt blobs and signed UUID/hash witnesses, including reviewer registration, as well as Agent/Manager/task/review/source history.

## Blocking source-level finding

**P1 — current project Prepared receipt is not bound to its immutable original operation content.** See `findings/original-prepared-receipt-integrity.md`. A second clone may replace the receipt via ordinary Git CAS with a canonical `ProjectPreparedReceipt` changing only `OriginalOperationRequestHashes` or `GraphVersion`; the unchanged original operation UUID then returns `AlreadyApplied`. `WorkQueueProjectMigrationPrepare.inspect` and WQ10 `matchesReceipt` do not verify these original receipt fields or compare the current receipt SHA to `LedgerStore`'s original CAS `OriginalResultJson.changes[]` content SHA. With unchanged queue/graph/account, status becomes `PreparedInert` and final `Failure=None`, falsely treating a changed provenance witness as the original WQ02 receipt. The new `{}` receipt-mutation negative tests malformed JSON only, not a valid but modified receipt.

**Required remediation:** verify current exact receipt content against the original signed CAS operation's recorded receipt content hash at the observed project snapshot; retain identity, actor, request and historical high-water checks; add a valid canonical receipt mutation + original UUID replay two-clone negative. Preserve partial outcome and observed heads without re-running a committed UUID or pretending three repos are atomic.

**Boundary:** No Scheduler, Manager, production migration/cutover, real source-role authorization, fleet-wide old-writer fence/high-water, public route or host bridge is established by this default-off disposable fixture. No hosted GitHub Actions or Codex were run. ChangesRequested is specific to fixture recovery correctness, not an assertion of live activation.