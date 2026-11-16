# Reviewer 3 — ChangesRequested

**Frozen review:** `4b2fa60d1d134b43b54f9de8f0ea9e05`, source `a268ffe6473dc820d75c732c022da8242ee0e12e`, base `d0065952efc47789ef81a6bef056c515954ee52c`.

## Independent scope and evidence

Read the complete new `Client/Modules/WorkQueueMultiLedgerPreparedRecovery.fs`, all six new `Client.Tests/WorkQueueMultiLedgerPreparedRecoveryTests.fs` fixtures, documentation, two compile includes, and relevant existing WQ07 account and WQ02 project/queue CAS and recovery code. Five changed files (+800 lines); worker's registered source checkout at exact frozen SHA was clean. `git diff --check d0065952..a268ffe6`: exit 0.

Local .NET 11 **Release** on the frozen source (no hosted CI, GitHub Actions, Codex, live provider or migration):
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~WorkQueueMultiLedgerPreparedRecoveryTests`: **6/6 passed** (NUnit, 20.46 seconds).
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~WorkQueueCanonicalMigrationPrepareTests|FullyQualifiedName~WorkQueueProjectMigrationPrepareTests|FullyQualifiedName~AccountCapacityLedgerTests|FullyQualifiedName~WorkQueueGitLedgerTests'`: **67/67 passed** (59 seconds).

Positive source and tests confirm distinct original account/A/B Git CAS transactions, original UUID replay, B stale two-clone writer, account-head divergence, preserved legacy task/review/Agent/Manager/source blobs, blocked conversion stages, private disposable marker, and a default-off public entry point. They do **not** establish production cutover GO.

## Blocking findings

See `findings/recovery-inert-postcondition.md` for precise reproduction reasoning and source references:

1. **P1: no final three-ledger verified-success postcondition.** `attemptOnDisposableFixtureWithInterposition` can return `Failure=None` after idempotent original UUID replay even if the current project Prepared receipt is absent/invalid or the current account head diverged, because `inspect plan` is only attached as metadata, not checked. Require explicit partial/recovery failure when final read-only account + both project statuses are not verified; add changed/missing receipt replay negative.
2. **P1: false `PreparedInert` classification after unrelated queue mutation.** The new coordinator trusts `WorkQueueProjectMigrationPrepare.inspect`, which tests only converted link stages; it never checks that the entire current WQ02 queue is non-runnable and `Launches` is empty. A post-Prepared unrelated available stage can leave all converted links blocked but still make `AllProjectsInertPrepared=true`. Require whole-queue/launch safety checks and negative test.

**Additional coverage improvement (nonblocking separately):** In `seedOriginal` the reviewer registration operation ID is generated and discarded at test line 95; `OriginalRequestHashes` witnesses only four selected operations at lines 114–126. Historical blob assertions also omit original operation-receipt paths. Strengthen the fixture's claimed operation-history/high-water coverage, rather than interpreting an incomplete sampled journal as exhaustive evidence.

**Boundary:** Keep three authorities independent, preserve existing CAS/receipt immutability, default-off public port, no token release or Scheduler/Manager activation. Real host trust, original writer fleet-wide fence/high-water, source-role authority and production migration remain **NO-GO**.