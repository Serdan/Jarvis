# Independent Reviewer 2 verdict — ChangesRequested

- **Task:** `work-c1-normalized-scheduler-import-projection` (C1/C2 original importer → E0/E1a SQLite normalized Scheduler; default-off only).
- **Frozen head:** `d6f49a6c8b29d645e4641a0595a704190e30e3ed`; exact baseline `2a51920bcedf3fed185a2956b7dc812474107881`.
- **Diff:** exactly four files, 954 additive lines: `Server/WorkLegacyImportProjection.fs`, `Server.Tests/WorkLegacyImportProjectionTests.fs`, and one compile include in each project. No existing Scheduler, importer, writer, API, recovery, backup, or live route was modified. Isolated source worktree clean.
- **Independent verification:** local `.NET 11` `Release` `dotnet test Server.Tests/Server.Tests.fsproj --filter FullyQualifiedName~WorkLegacyImportProjection --no-restore`: **9 passed / 0 failed / 0 skipped**. Full `Server.Tests` Release `--no-build --no-restore`: **448 passed / 0 failed / 0 skipped**. These successful runs do **not** close the source proof gaps below.

## Blocking findings

1. **P1:** `findings/c2-fence-evidence-partial-binding.md` — the C2 original-writer receipt-to-SQLite-audit comparison omits source/fence/high-water/quiescence fields that the retained C2 audit actually contains. A changed receipt can satisfy the current partial predicates before `coverage=Verified` is written.
2. **P1:** `findings/c1-imported-source-not-exactly-sealed.md` — the imported C1 queue/account/graph/stage values are only partially compared to the inspection/projection. Key sets and union cases do not prove source payload identity, especially waiver receipts and state/gate payloads. SQLite guards pin whatever raw row is observed, even if its original imported body already drifted.

## Disposition and boundaries

**ChangesRequested** at the exact frozen commit. Close both source-evidence predicates and add fail-closed negative tests, then submit a **new frozen review**. Retain the positive disposable C1→E0→E1a test, UUID/idempotency and two-clone checks. The tests' `CaptureVerifier(Some proof)` is a constructed fixture, **not** a separately reviewed production Git/role/approval verifier. There is no evidence of live migration authorization, writer-fence cutover, Scheduler activation, deployment, or production execution. This review does not authorize any of those actions.