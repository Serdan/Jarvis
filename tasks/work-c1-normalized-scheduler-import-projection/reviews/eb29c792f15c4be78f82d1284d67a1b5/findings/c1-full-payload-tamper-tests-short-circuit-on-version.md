# Post-decision review note — C1 negative-test isolation (nonblocking)

**Frozen candidate:** `3e795e008295f800a3010ca4bf67cb9a52be2977`. **Recorded review verdict:** Accepted (decision sequence 6252) for the **default-off source only**. This note was added after that decision; it is **not** a new ChangesRequested verdict and does not change the canonical review status.

## Independently observed limitation

The correction's actual source predicate is substantive: `originalRow` in `Server/WorkLegacyImportProjection.fs` (around lines 225–230) checks `row.Version = 1L && row.Value = expected`, and the reconstructed typed `OriginalC1` codec mirrors the original importer's `SnapshotJson` format. The full C1 account, queue, graph, stage (including waiver receipt values), origin, role, review, integration, terminal, validation and operation receipts are compared to the original typed inspection.

However, the new `tamperImportedJson` helper in `Server.Tests/WorkLegacyImportProjectionTests.fs` (around lines 266–285) corrupts test rows through `WorkSqliteAuthority.Commit` with an existing-row `ExpectedVersion`. `WorkSqliteAuthority.applyRecord` increments `version=version+1` on every successful update (around lines 402–433). The new C1 payload-tamper tests, including the same-key changed waiver value, therefore present a **version-2** record, which `originalRow` rejects at `Version <> 1` even if the new whole-value equality predicate were absent. Those cases demonstrate fail-closed behavior for ordinary versioned store mutations, but **do not isolate the new full-body equality check** as a regression test.

The source-level equality check exists and appears correct, and all supported `WorkSqliteAuthority` writes advance version. The record `WorkSqliteRecordGuard` monitors version/generations/source SHA, not `BodyJson`; this relies on the intended versioned-writer trust contract for concurrency. No successful unauthorized promotion through a supported writer was reproduced. This distinction matters: a test-evidence limitation is not equivalent to a source-level bypass.

## Recommended later test hardening

In a *disposable, test-only* SQLite fixture, introduce one **same-version, same-key, body-only** drift after import (e.g. update `body_json` using `Microsoft.Data.Sqlite`, preserving `version=1`, `SourceSha`, graph, generations and all other witness fields) and assert `Incomplete`, no new projection receipt and no `Project/authority`. A changed waiver receipt value with its existing dependency key and a changed account/role payload would directly prove that `row.Value = expected` is necessary. `Server.Tests/WorkSqliteAuthorityTests.fs` already contains raw disposable SQLite connection patterns. Retain the existing version-bumping tamper tests because they cover ordinary store-mediated stale writes.

This is a **nonblocking quality improvement** for the already accepted default-off code, not authority to enable C2/C3 writer cutover, production original-Git authentication, Scheduler dispatch, dual writers or deployment. Such actions still require independently reviewed production proofs and explicit operator authorization.