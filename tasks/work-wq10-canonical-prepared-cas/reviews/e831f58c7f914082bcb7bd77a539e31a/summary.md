# Reviewer 4 — WQ10 P1 symlink-escape re-review

**Verdict: ACCEPT** frozen commit `c96fa642553823b9f2fc4b117cd5632485da59ab` against `53105d3eaa1b476fadfe5470e962b4c5796fe8f9`.

## Evidence

- `Client/Modules/WorkQueueCanonicalMigrationPrepare.fs`: supplied clone/bare metadata recursively rejects symlinks and junctions, object alternates and shared-worktree redirection; authority paths and marker are confined to explicit disposable scratch.
- `isolatedClone` creates fresh, unpredictable, private clones with no checkout, no local hardlinks, an empty template, sanitized `GIT_*` environment and disabled system/global config. The existing fetch readers use those bindings, not the caller-supplied clone metadata.
- `attemptCanonicalPrepare` always returns a typed blocked assessment, and Prepared, SchedulerEnabled and ProductionCutoverAuthorized remain false. Missing authenticated Preparing/Prepared CAS, old-writer fence, source/role coverage and partial-receipt recovery are explicit.
- **Independently run:** `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~WorkQueueCanonicalMigrationPrepareTests --no-restore`: **11/11 passed** (.NET 11 Release); frozen-range `git diff --check`: clean.
- Submitter also reported Python WQ10 **49/49** and inspector Release build clean; these were not rerun in this independent check.

The prior P1 nested clone and bare-authority metadata escape is addressed **for this approved-disposable-fixture, assessment-only scope**. No canonical writer, migration authorization, production activation, or scheduler handoff is approved by this verdict.

## Nonblocking follow-up

Negative tests currently compare outside-directory **file-name sets**, not byte hashes of existing files; strengthen negative-space regression coverage by hashing file contents too. This private-clone design is not an OS sandbox for a hostile same-user-controlled bare repository; future production migration requires a separate authenticated and isolated authority/write design.

No blocking source finding remains within the frozen WQ10 security re-review scope.