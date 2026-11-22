# F1 — destructive cleanup accepts arbitrary prefix-named user files (P1, blocking)

**Frozen source:** `9c0427dde4d25f1e2d684f85a85b1fd6fd0228c7` against `dcac884fc061869572b12a03befc12f11c009f2f`.

**Location:** `scripts/publish-client-downloads.cs:115-122`, especially line 120; whole-directory swap and `Directory.Delete(backup, recursive: true)` at lines 57-83.

**Original requirement:** phase-1 publishing must remove stale native-Windows download artifacts without accidental user-data removal, refuse unmanaged files/symlinks, and preserve a complete previous release on failure. The source claims unknown files fail closed and the Node suite tests `personal-notes.txt`, but recognition uses `name.StartsWith("JarvisClient-")` rather than an allowlist of managed filenames.

**Independent reproduction:** In a disposable `/tmp/jarvis-review26-prefix-*` fixture with the frozen publisher, an existing `artifacts/downloads/JarvisClient-personal-ledger` containing unrelated personal content was accepted; fake child dotnet emitted both expected RID artifacts. The real script returned exit 0, replaced downloads, and irreversibly removed the personal file when deleting its backup. Reviewer job `8a806abe89c24f309045c1c6e80b4250`: `{"exitCode":0,"wasPersonalFileDeleted":true}`. No real user directory or live service was used. Existing 10/10 tests pass but do not exercise this prefix collision.

**Impact:** silent data loss on a routine "successful" publish; this breaks the explicit unmanaged-file safety invariant even when all publishes/checksums succeed.

**Bounded correction suggestion (subject to independent adjudication):** identify previously generated artifact names using explicit exact filenames (including historically published Windows filename(s) to be retired) rather than an unrestricted prefix; reject other names without child publish or directory swap. Add a fixture negative for `JarvisClient-personal-ledger` asserting byte-for-byte preservation and no publish calls. Do not alter updater, runtime, bridge or shared paths.
