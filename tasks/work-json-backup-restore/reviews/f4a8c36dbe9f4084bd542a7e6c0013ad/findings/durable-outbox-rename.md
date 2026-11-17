# P1 — Captured backup is acknowledged before its directory publication is durable

**Frozen source:** `a434f351976af5dd503921c001da56e4a2c36530`  
**Location:** `Server/WorkBackupReplication.fs:55-70` (especially lines 62, 67–68); related restore publication `Server/WorkSqliteBackup.fs:432-437`.

`Capture()` writes each file with `FileOptions.WriteThrough` and `Flush(true)`, then uses `Directory.Move(temporary, destination)` and immediately returns `Ok sequence`. It does not sync the staging directory or the enclosing spool directory after the rename. An atomic rename protects against readers seeing partial files and ordinary process crashes, but **does not establish post-power-loss persistence of the directory entry** on filesystems that require directory fsync. After an abrupt host crash/power loss, a sequence that was reported captured may have no committed `s-...` entry; `PublishPendingAsync` only enumerates entries still present and therefore cannot detect that loss. The restore path likewise acknowledges `File.Move(sealedPath, destinationPath)` without a durable directory-publication boundary.

This conflicts with the source's explicit *crash-durable local staging* guarantee and the reviewed scope's crash-safe backup publication. The existing 11 tests cover process-level retry, outage, corruption, and WAL sealing, but not durability of rename metadata.

**Required:** Either implement and verify a platform-appropriate durable publication boundary (sync artifact contents **and parent directory metadata** before acknowledging `Capture`; apply analogous treatment to acknowledged restore), or explicitly narrow this API to **process-crash-safe only** and add a durable recovery/reconciliation protocol before any stage can assert that `Capture=Ok` means locally durable. Do not treat a successful atomic rename alone as a power-loss durability guarantee. Keep the default-off / no-cutover restriction unchanged.

**Scope:** This finding concerns only the frozen physical substrate; it does **not** demand completion of F0 typed logical projection, production backup scheduling, or Recovery in this review.
