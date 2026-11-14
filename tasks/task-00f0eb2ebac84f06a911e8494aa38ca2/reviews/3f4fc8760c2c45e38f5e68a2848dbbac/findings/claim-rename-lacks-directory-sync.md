# P2 — Persisted occurrence claim is not guaranteed durable across power loss

**Frozen head:** `2ad69b6cca7966ba79cdaa9c786a40300bec6865`

**Location:** `Client/Modules/BrowserSchedules.fs:94-119, 486-501`.

`persist` writes a newly created temporary schedule file with `FileOptions` defaults, calls `file.Flush(true)`, closes it, then calls `File.Move(temp, path, true)`. The parent directory is **not fsynced after the rename** on Linux/macOS (nor is an equivalent crash-durable journal/claim mechanism used). Syncing temporary file contents alone does not guarantee persistence of the directory entry update on sudden power loss.

The scheduler launches the external CLI/browser side effect immediately after `setActions` returns. On a power failure the renamed `running` claim may be lost while the prior `pending` directory entry remains; restart can dispatch the same one-off occurrence again. This contradicts the explicit "durable claim before side effects, no replay after uncertain outcomes" requirement, although an ordinary process crash after a completed rename need not reproduce it.

**Required:** Make the rename/claim crash-durable (synchronize the containing directory after atomic replace, or use an explicit transactional journal with verifiable commit) before dispatch. Test recovery from interrupted/pending claims and document the filesystem durability assumption. This is distinct from the cross-process atomicity failure.

**Evidence:** Source-level durability analysis; no power-failure experiment claimed, no source edits.
