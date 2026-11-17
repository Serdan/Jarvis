# G1b red-first stale-restore boot-fence contract — test-only evidence

**Date:** 2026-11-17  
**Status:** RED-FIRST TEST CONTRACT ONLY. No G1b implementation, production restore, host/Scheduler enablement, or acceptance.  
**Base:** registered isolated `test/work-recovery-g1b-bootfence-red-worker12-20261117` at clean `51b67bf1d5ba7e16512b915f8004456440b3489c`.  
**Scope:** new `Server.Tests/WorkRecoveryRestoreBootFenceRedFirstTests.fs` and one Server.Tests compile include.  
**Design:** independently Accepted design-only `60cacc98152da3f66cc13f63224d32854d400c53`, review `9f386cc72d004deba5b4ad79bf7be3e2`, especially sections 3–5 of `docs/specs/work-sqlite-domain-backup-contract-2026-11-17.md`.

## Reason for this separate RED lane

Current F1 `WorkSqliteBackup.restoreEmpty` verifies and seals disposable authority SQLite, then returns a process-local `WorkSqliteRestorationReceipt { RequiresRecovery=true }`. It does not persist a pre-serve admission lock. Current G1a `WorkRecoverySqlite.seed` can atomically downgrade project coverage, stage gates and account capacity *after* a caller supplies a complete snapshot, but does not authenticate a trusted complete F1 restore witness or execute before every possible host admission. A stale backup N cannot prove absence of an N+1 launch, claim, review, authorization or external side effect.

For now the fixture uses **the narrow F1-v2-admissible flat bodies**, not a fabricated full authenticated B/E/G v3 export. It stores two project IDs on one account, a Prepared stage and launch, an Unknown effect and receipt/audit at N. The original source then writes an additional Unknown effect at N+1; the backup is restored into an empty quarantined DB and opened as a *new* authority instance, without calling G1a seed. This deliberately tests the prohibited *pre-seeding window*.

## Executable assertions

| Test | Required | Expected against current G1a + narrow F1 |
| --- | --- | --- |
| `sealed_stale_restore_must_lock_fresh_admission_before_recovery_seeding` | New Alpha launch/stage/account admission denied before any G1a seed, despite absent N+1 effect in restored N | Intentional RED: missing durable boot fence |
| `stale_restore_must_fence_other_project_on_same_account_capacity` | Beta in shared account cannot admit while Alpha has unresolved stale external effect | Intentional RED: shared account not fenced pre-serve |
| `historical_receipt_and_same_uuid_replay_do_not_authorize_new_dispatch` | Exact old receipt/replay remains historical evidence; no later new-UUID admission is authorized | Intentional RED: retained receipt, but fresh admission still allowed |
| `even_empty_backup_requires_a_durable_pre_serve_recovery_gate` | Even zero-row restored backup must block new writes until complete current high-water/auth proof | Intentional RED: no persisted lock |
| `ordinary_intact_sqlite_restart_is_not_a_stale_restore` | Ordinary new authority process against the original durable SQLite may continue normal guarded operation | Expected PASS control |

Fresh ordinary transactions include version-guarded stage writes and a shared-account capacity reservation; they are an observable B-level proxy for missing early admission lock, not a claim that a real Scheduler/client/credential route is exercised. Tests require no production dispatch, network, cloud, original source mutation, manual grant, or host cutover. An accepted G1b design may place an equivalent *authenticated* pre-serve guard above B, but must then replace this proxy with an executable real Scheduler/tool/claim gate before claiming GREEN acceptance. A low-level B transaction failure caused by unrelated corruption does not demonstrate a valid Recovery protocol.

## Mandatory later G1b implementation gates — not claimed by this fixture

1. **B / Worker20:** trusted closed typed writer and full original repository/ref/commit, role/verifier and graph provenance, distinct same-name projects across devices, relational validation, no leaked bearer/manual grant in any backup/evidence path.
2. **F1 / Worker13:** complete, credential-safe, lossless deterministic authority snapshot, original receipt/audit/account history, trusted Git backup root and verified full-account restore witness. The current F1 `RequiresRecovery` boolean is *not* a database lock.
3. **G1b:** persistent `RecoveryRequired` in quarantined restore *before serving*, denial of every Scheduler reserve/claim, tool permission, scheduled verifier, effect dispatch and other mutator, across restarts and independent client instances. No immediate replay of an old UUID as renewed permission.
4. **Reconciliation:** N versus N+1 source/review/integration/captured-launch/unknown-effect evidence; two same-named cross-device identities; STOP, stage/claim/role and account-wide capacity; progressive scoped releases with exact signed/versioned proofs while unverified Work stays blocked.
5. **Controls:** ordinary intact-database restart does not spuriously enter Recovery; unsuccessful fencing never partially serves. Backed-up receipts and source SHA alone do not prove an original review, integration, source/ref identity or negative remote effect.

These are deliberately **unimplemented contractual obligations**, not tests claimed to run or pass. The RED fixture must stay on its isolated branch and **must not be merged intentionally failing into main**. G1b production files (`WorkSqliteAuthority`, `WorkSqliteBackup`, `WorkSnapshot*`, importer, Scheduler and host) are untouched. Later B and F1 code must be independently reviewed/integrated before G1b implementation is GO.

## Evidence log

- Test-only source baseline: `51b67bf1d5ba7e16512b915f8004456440b3489c`.
- Verified SDK: `dotnet --version` = **11.0.100**. Target: `net11.0`, configuration `Release`.
- First red-first run: 5 discovered, 3 intended admission-fence RED, 1 intact restart PASS, and 1 *bad assertion* on `ReadReceipt.Replayed` (corrected before freezing). This preliminary run is not the acceptance evidence.
- **Corrected proof run:** `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~WorkRecoveryRestoreBootFenceRedFirstTests --logger 'trx;LogFileName=work-recovery-g1b-red-20261117.trx' --results-directory Server.Tests/TestResults`.
- **Result: 5 discovered, 4 intentionally RED, 1 positive PASS, 0 skipped; exit 1 (expected).** All four failures report `RED G1b: pre-serve Recovery lock absent; ordinary admission was accepted` (at original sequence 1 or 2), not compilation, fixture or assertion errors. The intact-database restart test passes. No G1b production implementation is claimed.
- TRX local evidence: `Server.Tests/TestResults/work-recovery-g1b-red-20261117.trx`; SHA-256 `522d77d52d28f692ef26e554a20e5202e3b1f68b1c88af71fe8cbd1ad696732f`. TRX is a generated local artifact, not merged into source.
- Only this namespace's five tests were run in the corrected proof; no claim of full Server.Tests suite, current B/F1 integration, live host, or full-domain acceptance.
- Exact test-only branch commit and independent review decision remain to be recorded; neither an authored RED contract nor a submitted review authorizes merging intentionally failing tests.
