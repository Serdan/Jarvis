# Reviewer 2 - WQ-10 frozen re-review: ChangesRequested

**Frozen source:** `199baf8b99b2fe033d3c654926e0645b12ea028a`; base `289e32754b6e2bd59b157d51c3fd79a16e881e98`. Scope: offline-only `tools/Wq10MigrationPreview/`. No live migration, epoch flip, Manager retirement, Scheduler activation, deployment or integration is authorized.

## Previous findings: verified corrected

- Omitted/null `pendingOperations`, `inFlightSourceJobs`, `uncertainBrowserSubmissions` now give `EVIDENCE_INVENTORY_MISSING` blockers; explicit `[]` is distinct and accepted as an operator assertion.
- Incompatible account/project migration phases, including `Activated` account + `Frozen` project, give `MIGRATION_PHASE_MISMATCH` / `NO_GO`. An already activated account is audit-only, not proposed as fresh Preparing. The tool does not compare unrelated Git-binding identity epochs.

## Independent evidence

- Frozen `dotnet build tools/Wq10MigrationPreview/Wq10MigrationPreview.csproj -c Release --no-restore`: **pass**, 0 warnings/errors.
- `python3 -m unittest discover -s tools/Wq10MigrationPreview/tests -v`: **13/13 pass** on disposable two-clone ledgers.
- `dotnet build Jarvis.slnx -c Release --no-restore --nologo`: **pass**, 13 pre-existing F# warnings, 0 errors.
- Inspected five-file diff (605-line CLI; 343-line Python suite), docs and accepted stage contract. Independently reproduced one false `NO_GO` on disposable fixture inputs. No real ledger or source mutation.

## One new targeted finding

**P1 - Valid successive work stages are classified as duplicate migration.** See `findings/sequential-stages-false-duplicate.md`.

`Program.cs:304-309` declares `MULTIPLE_CANONICAL_STAGES` when two `Works` entries have the same task `subjectId`. However `Server/WorkQueueContracts.fs:575-601` permits distinct stage IDs and generations for a single task, including completed implementation followed by a successor implementation/review. In a two-clone fixture with generation 1 `Completed` and generation 2 `Available`, canonical coverage and origin evidence supplied, and no unresolved work, the preview produces `NO_GO` with the sole blocker `MULTIPLE_CANONICAL_STAGES`. This makes normal migrated task history appear inconsistent.

**Minimal fix:** compare actual logical stage identity (kind, stage generation and relevant lineage), not raw task subject count. Preserve and display expected multiple stages. Add a positive fixture for predecessor + successor and retain a true-duplicate negative fixture. No new proof machinery or live cutover features requested.

**Verdict: ChangesRequested** for this focused correction; both previous P1 findings are resolved. Re-submit a new immutable frozen snapshot for independent re-review.
