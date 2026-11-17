# Independent review — scoped F1 SQLite physical backup/restore substrate

**Request:** `f4a8c36dbe9f4084bd542a7e6c0013ad`  
**Frozen head:** `a434f351976af5dd503921c001da56e4a2c36530`  
**Frozen base:** `f96aaa1236567e8fa0b8e08d8462117649f061a3`  
**Verdict:** **Changes requested** for this limited substrate only.

## Original review scope

Exact branch tip a434f351976af5dd503921c001da56e4a2c36530 (base f96aaa123): strict format-2 SQLite snapshot/restore, outbox and Git sink. This follow-up permits byte-identical same-sequence recovery after interrupted materialization/staging without committing unrelated checkout changes, and idempotent push retry. The author's `docs/evidence/work-json-f1-backup-restore-2026-11-17.md` documents blocked F0/B schema and provenance equivalence; there is no request to accept whole F1 or integrate production authority.

## Independent verification

Inspected `Server/WorkSqliteBackup.fs`, `Server/WorkBackupReplication.fs`, `Server.Tests/WorkSqliteBackupTests.fs`, the author's evidence, and underlying `Server/WorkSqliteAuthority.fs` schema/identifier validation. On the **exact frozen commit** in `Jarvis::work-json-f1-worker13-20261117`, .NET11 Release tests passed independently: **11/11 focused F1 tests**, **398/398 full Server.Tests**, zero failures. The module fails closed on unsupported `body_json`; WAL-safe sealed restore, deterministic bundle checks, retained outbox after outage, and Git interrupted-precommit / failed-push retry are covered by tests.

## Blocking finding

`findings/durable-outbox-rename.md`: `Capture()` fsyncs file contents then atomically renames its staging directory, but does **not fsync the enclosing directory metadata** before returning `Ok`; a host power loss can lose the acknowledged committed outbox entry. An acknowledged restored target has the same directory-publication durability gap. Fix durable publication or explicitly narrow the guarantee to process-crash-only and add reliable reconciliation before relying on acknowledged local durability. This finding applies within the requested crash-safe substrate scope.

## Explicit out-of-scope gates

This narrow snapshot is **not full F1/G4**: typed F0 logical projection, per-record sequences, full source RepositoryId/Ref, account STOP/status, domain capacity/receipt/effect mapping, credential safety for arbitrary identifier-shaped strings, production Git backups, Scheduler, Recovery, deployment, and cutover remain unapproved. A passing suite and resolution of this review do not relax any of those requirements. No source files were edited by Reviewer 3.
