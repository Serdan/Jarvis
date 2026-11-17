# F1 SQLite Work backup / restore checkpoint (2026-11-17)

Status: partial F1 substrate, not accepted F1/G4, migration, Recovery, live backup, or Scheduler cutover.

## Isolated Worker 13 branch

- Server/WorkSqliteBackup.fs creates deterministic format-2 physical JSON snapshots from one consistent SQLite B read transaction, preserving project records, account-global capacity, idempotency receipts, audit sequence, version/fence metadata, schema and manifest hashes. A nonexisting disposable target is checked and restored. Sealing with SQLite BackupDatabase retains WAL frames, and restore returns RequiresRecovery=true. No production host registration.
- Server/WorkBackupReplication.fs provides explicitly invoked durable local staging, isolated asynchronous Git publication, outage retry, idempotent pushes, strict paths and refs, and no Git transport on normal WorkSqliteAuthority.Commit.
- Nine new focused Server.Tests cover concurrent two-project/account snapshots, WAL-safe restore, deletion with durable sequence high-water, corruption and rehashed tampering, invalid secret-bearing fields, Git outage and retained pending bundles, corrupted spool refusal, and local bare-repository Git publish/retry.

## Essential remaining F1 work

The independently accepted F0 WorkSnapshotCodec (format 1) is a typed logical Work format. It requires WorkUnit, WorkStage, Reservation, ScheduledVerifier, Receipt, AuditEvent records, along with full source RepositoryId, Ref and Commit. The accepted B SQLite authority exposes generic body_json plus record kind, version, generations and SHA fields but not source RepositoryId/Ref. B accepts arbitrary JSON-valued string fields. Therefore the F0 logical snapshot cannot be faithfully and safely projected from B columns alone, without a trusted typed domain mapper or an explicit schema extension. Copying arbitrary JSON might export scheduled bearer secrets; guessing provenance would fabricate authority.

The new format-2 physical snapshot is deliberately a strict fail-closed proof-of-concept and does NOT replace or embed F0. It only permits a very narrow whitelist of state/status fields and hashed verifiers. Real domain records with other valid fields will cause export failure. This is safer than silently stripping fields or leaking plaintext, but is not complete F1 backup compatibility and must not be reviewed as final F1.

Next: coordinate canonical B/domain projection contract and full RepositoryId/Ref/Commit provenance, integrate with existing F0 codec, address completeness/versioning and failure-monitoring/backlog policy, run realistic schema and recovery fixtures, then request exact-frozen independent F1 review. Do not change Worker12 SQLite or Worker1 Scheduler; no production backup/deploy/cutover without operator authorization.
