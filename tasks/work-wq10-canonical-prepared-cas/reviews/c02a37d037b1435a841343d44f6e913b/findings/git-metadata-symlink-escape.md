# P1 — Nested Git metadata symlink escapes the disposable boundary on a read-only assessment

**Frozen source:** `0b9af0ff62d9f82dd3cb51aa218808f699b433fd`  
**Location:** `Client/Modules/WorkQueueCanonicalMigrationPrepare.fs:112–120`; caller at `:194–226` (account/project fetches).  
**Severity:** P1 / blocking safety boundary.

`safeClone` calls `noSymlinkAncestors` on the clone directory and its top-level `.git` only. It does not inspect `.git/objects`, `.git/refs`, or nested writable metadata. The alternates check only rejects an actual `objects/info/alternates` file. A normal clone with a real `.git` directory but a symlinked `.git/objects` passes `approvedRoot` when the link destination is outside `DisposableRoot` (and no alternates file exists). Then `AccountCapacityLedger.fetch` / `LedgerStore.fetch` perform `git fetch` and update local refs/object storage, so the supposedly read-only assessment writes outside its approved scratch.

**Independent disposable reproduction:** using a new bare remote + clone entirely in a temporary fixture, move `clone/.git/objects` to a sibling directory *outside* the fixture, replace it with a symlink to that sibling, advance the remote by one commit, and execute the same `git fetch --no-tags --no-write-fetch-head --refmap= origin refs/heads/main:refs/jarvis/fetch/one` used by the ledger reader. Git followed the nested symlink and created **three new object files outside the approved fixture**. `clone/.git` remained a real directory; `safeClone` contains no guard against the linked `objects` directory. No production paths were touched.

This violates the task's explicit **no external filesystem writes outside private scratch** guarantee even though `CanonicalPrepared` correctly stays false. The existing tests cover a symlinked *clone root* and an alternates *file* but not this case.

**Required correction:** before any Git invocation, ensure every Git-writable metadata subtree used by fetch/trust-ref updates is confined and non-redirecting, including nested `.git/objects`, `.git/refs` and linked descendants (and equivalent bare-authority structures). Prefer freshly created, fully private Git metadata under an isolated scratch boundary over validating attacker-modifiable repositories by path. Address concurrent symlink replacement/TOCTOU with OS isolation or equivalent fail-closed mechanism. Add regression fixtures where nested metadata links point outside scratch, remote advances, and the assessor rejects *before fetch* with no outside mutations.

The typed fail-closed migration result, source scope, and 9/9 focused .NET 11 Release tests are otherwise positive; this is a concrete missing negative-space safety test.