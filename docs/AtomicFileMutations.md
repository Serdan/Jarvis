# Atomic workspace text mutations

This implementation addresses finding JRV-2026-11-01 in [the pinned deep review](https://github.com/Serdan/Jarvis/blob/59ad29f5f7432433001838f8c394a5e35d14ed2e/docs/reviews/2026-11-07-deep-review.md) without changing the public MCP tool contracts.

## Transaction lifecycle

**FileIO.EditText<T>** takes a selected checkout root, destination file path, create-parent and dry-run options, and a typed edit callback. The same implementation serves WriteFile and PatchFile.

1. Acquire an exclusive **cross-process lock**, keyed by the canonical absolute destination path.
2. Recheck root containment, existing parent components, destination type and symlink restrictions. Missing parents are allowed only when creation was requested.
3. Read the current text, apply the expected hash or absent-file precondition inside the lock, and compute the intended change and typed command result. Failed preconditions do not create parent directories.
4. For accepted non-dry-run edits, verify that existing targets can still be opened for writing (so directory rename permission does not bypass a read-only file), create requested parents, stage content in a unique sibling file with owner-only temporary permissions, flush the stage, then restore the original file mode or normal new-file creation mode before publication.
5. Recheck destination and parents and compare the original text. Reject external changes detected during staging.
6. Publish atomically: File.Move with overwrite=false for new files; File.Move with overwrite=true for existing files on Unix; File.Replace for existing files on Windows. Clean up abandoned stages in finally and release the lock.

The lock file lives in the current user's application-data Jarvis/file-edit-locks directory, not the worktree. Its filename is a SHA-256 of the path (case-folded on Windows/macOS). The directory is 0700 on Unix, and lock files are 0600. Locks are retained intentionally: unlinking a still-held lock pathname risks two different live lock inodes. Lock acquisition is bounded to 30 seconds; locks for unrelated files are independent.

**ReplaceText** writes BOM-less UTF-8, matching the existing WriteAllText semantics. **AppendUtf8Text** copies the existing raw bytes unchanged before appending UTF-8 bytes, matching AppendAllText even for BOMs and legacy content. Patch diagnostic content and hash fields are computed against the same original snapshot held under lock.

## Guarantees and limitations

- Readers see an intact old file or an intact replacement, never an in-place truncated intermediate created by this API. A pre-publication failure preserves the original file and removes the stage.
- Two *cooperating Jarvis processes* cannot both pass the same stale precondition and publish; the lock covers validation and commit.
- Non-cooperating editors do not honor this advisory lock. A final content/path recheck narrows their race window but cannot guarantee conflict detection against an adversary changing directory entries at the last instant. Strict guarantees against malicious concurrent directory substitution require descriptor-relative native primitives (openat/renameat or Windows equivalents).
- Hard links to the original inode are not overwritten: replacing the destination changes only its directory entry. Existing Unix mode bits, including executable flags, are copied to the stage. Windows uses File.Replace. Staging content is initially owner-only even when the target or default newly created file is broader. Filesystem-specific ownership, ACLs and extended metadata may differ after replacement.
- File data is flushed before replacement. Without a portable directory fsync, this guarantees complete-file visibility to concurrent readers, not irreversible directory metadata after sudden power loss.
- Parent directories created after a successful precondition are not rolled back if a later staging operation fails. Precondition failures create no parent directories.
- No server, MCP protocol, canonical Git ledger, or permission policy changes were required.

## Verification

AtomicTextFilesTests covers independent dotnet-process lock exclusion, simultaneous thread edits, BOM-preserving appends, Unicode, empty contents, parent creation, executable mode preservation, failed stage publication, external edits during staging, and link substitution. Existing HashAndPatchTests and PathSafetyTests retain the prior command behaviors. After merging with the bounded-process remediation, the complete Linux Release suite passed 420/420 Client.Tests and 95/95 Server.Tests on November 8, 2026; no deployment is implied.
