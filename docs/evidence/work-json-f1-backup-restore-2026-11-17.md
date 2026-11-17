# F1 SQLite Work backup / restore checkpoint (2026-11-17)

Status: partial F1 substrate, not accepted F1/G4, migration, Recovery, live backup, or Scheduler cutover.

## Isolated Worker 13 branch

- Server/WorkSqliteBackup.fs creates deterministic format-2 physical JSON snapshots from one consistent SQLite B read transaction, preserving project records, account-global capacity, idempotency receipts, audit sequence, version/fence metadata, schema and manifest hashes. A nonexisting disposable target is checked and restored. Sealing with SQLite BackupDatabase retains WAL frames, and restore returns RequiresRecovery=true. No production host registration.
- Server/WorkBackupReplication.fs provides explicitly invoked durable local staging, isolated asynchronous Git publication, outage retry, idempotent pushes, strict paths and refs, and no Git transport on normal WorkSqliteAuthority.Commit.
- Thirteen focused Server.Tests cover concurrent two-project/account snapshots, WAL-safe restore, deletion with durable sequence high-water, corruption and rehashed tampering, invalid secret-bearing fields, Git outage and retained pending bundles, corrupted spool refusal, directory durability errors, and local bare-repository Git publish/retry.
- Follow-up fix: a crash after snapshot materialization or partial `git add` used to leave the dedicated Git checkout dirty and make every retry fail before reaching its identical-byte comparison. The sink now accepts only exactly matching untracked/staged additions under the current sequence, refuses unrelated files/edits, and commits only that snapshot path. A local commit followed by a failed push is retried without a duplicate commit. Both cases have regression tests.

## Review 6086 (f4a8c36d) — durable publication correction

- Independent Reviewer 3 requested changes for an acknowledged outbox rename and restored target without a durable parent-directory metadata barrier. Corrected the physical F1 substrate only, without claiming full F1/G4 acceptance.
- Capture now synchronizes the spool directory's parent, file contents, the staging directory's file-name entries, and the spool directory after atomic rename. An exact same-sequence retry repeats the relevant directory barriers before acknowledging success. Failed durability barriers return Error; orphan staging directories remain uncommitted. Git publication remains outside Work mutation paths, and successful outbox deletion now synchronizes the spool directory.
- Restore verifies the sealed SQLite file, explicitly flushes that file to stable storage, atomically moves it to a previously nonexistent destination, then synchronizes the destination parent directory before returning an acknowledged RequiresRecovery receipt. A failure after Move may leave an existing target but does not return Ok or authorize replacing it; operator Recovery must inspect it.
- Linux implementation opens directories with O_DIRECTORY | O_CLOEXEC and calls .NET 11 RandomAccess.FlushToDisk on the native handle (fsync); Windows attempts a directory CreateFileW handle with FILE_FLAG_BACKUP_SEMANTICS + GENERIC_WRITE and FlushFileBuffers. Invalid/unsupported operations fail closed. Power-loss guarantees still depend on filesystem/hardware honoring flush; Windows-specific and real power-cut verification have **not** been run.
- Validated on the exact isolated source with .NET 11 Release: **13/13 focused F1 tests** and **400/400 full Server.Tests** passed. Added negative tests for fsync on non-directory/missing targets and missing spool parent. These do not simulate physical power loss; independent review of the syscall/acknowledgement boundary remains mandatory.

No production scheduler/restore/backup, dual writer, remote deployment, or cutover is authorized.

## Essential remaining F1 work

The independently accepted F0 WorkSnapshotCodec (format 1) is a typed logical Work format. It requires WorkUnit, WorkStage, Reservation, ScheduledVerifier, Receipt, AuditEvent records, along with full source RepositoryId, Ref and Commit. The accepted B SQLite authority exposes generic body_json plus record kind, version, generations and SHA fields but not source RepositoryId/Ref. B accepts arbitrary JSON-valued string fields. Therefore the F0 logical snapshot cannot be faithfully and safely projected from B columns alone, without a trusted typed domain mapper or an explicit schema extension. Copying arbitrary JSON might export scheduled bearer secrets; guessing provenance would fabricate authority.

Verified mapping gaps against the accepted source (not hypothetical):

- F0 requires per-logical-record `Sequence`, but `work_records` has only per-row `version`; `work_meta.last_sequence` and `work_receipts.sequence` are global. A record's last mutation sequence cannot be reconstructed from its current row.
- F0 source provenance is `RepositoryId`, full `Ref`, and `Commit`. B retains only optional `source_sha`; inferring the missing fields would invent authority.
- F0 `AccountCapacity` requires `maxConcurrent`, `stopRequested` and `status`, but `work_capacity` retains `maximum`, `reserved`, and `version`. STOP state and account status are not represented there.
- F0 includes distinct `Reservation`, `ScheduledVerifier`, `Receipt`, `AuditEvent`, and `EffectJournal` logical types; B uses generic `work_records` kinds and separate receipts/audit with different fields. No authenticated, validated domain-to-B projection currently establishes equivalence.
- B's `body_json` accepts arbitrary JSON fields, including credential-like strings. F0's allowlist is not a security boundary for arbitrary strings. The physical backup deliberately rejects untrusted fields rather than leaking them or silently discarding durable data.

The new format-2 physical snapshot is deliberately a strict fail-closed proof-of-concept and does NOT replace or embed F0. It only permits a very narrow whitelist of state/status fields and hashed verifiers. Real domain records with other valid fields will cause export failure. This is safer than silently stripping fields or leaking plaintext, but is not complete F1 backup compatibility and must not be reviewed as final F1.

Next: coordinate canonical B/domain projection contract and full RepositoryId/Ref/Commit provenance, integrate with existing F0 codec, address completeness/versioning and failure-monitoring/backlog policy, run realistic schema and recovery fixtures, then request exact-frozen independent F1 review. Do not change Worker12 SQLite or Worker1 Scheduler; no production backup/deploy/cutover without operator authorization.
