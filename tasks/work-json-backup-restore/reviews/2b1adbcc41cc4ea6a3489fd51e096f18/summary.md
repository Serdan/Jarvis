# Independent review — F1 physical backup durability correction

**Request:** `2b1adbcc41cc4ea6a3489fd51e096f18`  
**Frozen head:** `2b1adbcc41cc7ea6c3489fd51e096f184d9c1113`  
**Frozen base:** `f96aaa1236567e8fa0b8e08d8462117649f061a3`  
**Prior ChangesRequested:** `f4a8c36dbe9f4084bd542a7e6c0013ad`, sequence 6086, frozen `a434f351976af5dd503921c001da56e4a2c36530`  
**Verdict:** **Accepted for this narrowly scoped physical durability correction only.** Not a full F1/G4 acceptance.

## Independent inspection and verification

Reviewed prior Reviewer 3 blocking finding, exact new commit diff and current source at `Server/WorkSqliteBackup.fs`, `Server/WorkBackupReplication.fs`, `Server.Tests/WorkSqliteBackupTests.fs` and the evidence note. The prior P1 was valid: `Directory.Move`/ `File.Move` without parent-directory fsync cannot justify a durable post-power-loss acknowledgement.

The correction now:
- Writes and `Flush(true)`-syncs all staged backup files, then syncs the **staging directory** (persistent file-name entries).
- Ensures the containing spool directory exists and syncs its **parent**, atomically renames staging to the sequence destination, then syncs the **spool directory** before `Capture() = Ok`. A same-sequence retry also checks exact bytes, syncs the existing destination directory and its parent; a failed barrier is `Error`, never an acknowledged capture.
- Only removes an outbox entry following an idempotent successful Git publish; then syncs the spool directory. Failed local directory metadata sync does not claim a successful publication receipt.
- Verifies the sealed SQLite restore, explicitly syncs its closed file, moves it to a previously absent destination in the same directory and syncs the **destination parent** before returning `RequiresRecovery=true`. If a sync fails after the move, the existing destination is unacknowledged and subsequent restore cannot overwrite it.
- Uses Linux `open(O_DIRECTORY|O_CLOEXEC)` plus `RandomAccess.FlushToDisk` (directory fsync); unsupported OS/FS behaviors throw instead of silently weakening the durability receipt. Windows attempts `CreateFileW(FILE_FLAG_BACKUP_SEMANTICS)` plus `FlushFileBuffers`.

**Independently executed** on exact registered worktree `Jarvis::work-json-f1-worker13-20261117`: `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore --nologo --verbosity minimal` — **400 passed, 0 failed, 0 skipped**, net11.0. This includes all 13 focused F1 tests, Linux positive and negative directory-sync paths and missing spool-parent refusal. No source edits were made.

## Residual limits and follow-up gates

The reviewed change addresses the original missing durable rename boundary on the tested Linux filesystem. No real power-cut/hardware test or Windows runtime verification was performed; Windows must be independently exercised and any unsupported directory-flush behavior handled explicitly before claiming Windows durability. Unit tests are not a physical crash guarantee.

This accepted review **does not** accept complete F1/G4, typed F0/B projection, source RepositoryId/Ref/Commit provenance, logical sequence and STOP mapping, arbitrary body-field credential safety, production capture scheduling or Git deployment, active restore, Recovery/Scheduler authorization, or cutover. The source itself correctly reports those incomplete architectural gates and remains default-off. Do not conflate this verdict with permission to integrate or enable a new Work authority.
