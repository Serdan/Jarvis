# Reviewer 3 independent F1a RED-first review — Accepted for test contract only

**Frozen source:** `f4c103940a420d5845ef95b0c9657b4f3d9f05d9`, published branch `feature/work-json-backup-f1a-red-w13-20261117`. Reviewed as an intentionally RED, test-only F1a contract; **not** a green F1 implementation or migration approval.

## Independent source verification

- Retrieved the frozen registered worktree and reviewed `Server.Tests/WorkSqliteBackupTests.fs:450-662`, `Server/WorkSqliteAuthority.fs:157-189`, `Server/WorkSqliteBackup.fs:103-151,350-470`, and the accepted design contract `docs/specs/work-sqlite-domain-backup-contract-2026-11-17.md`. The worktree was clean, on the matching published feature branch.
- Read-only `git diff --stat f4c10394^ f4c10394` completed exit 0 (Jarvis job `6866f65e2b9944da9af294278a1643c8`): exactly **one file** changed, `Server.Tests/WorkSqliteBackupTests.fs`, **215 insertions, no deletions**. No production authority, Scheduler, Recovery, serializer, host or publisher source changed.
- The code demonstrably admits up to 256-character B identifiers, but F1 refuses IDs >128; `safeBody` admits only flat state/status/verifier strings, not nested B/E/G bodies. Restore verifies self-consistent checksum/canonical bytes but does not authenticate a trusted external Git backup root. `restoreEmpty` returns a recovery receipt but its output DB is directly writable without a persistent boot fence. These are real, source-grounded gaps targeted by the tests.

## Independent native execution

- `dotnet build Server.Tests/Server.Tests.fsproj -c Release --no-restore`: **exit 0, 0 errors, 10 warnings** on the frozen source (.NET 11 Release; Jarvis job `86ccee8f04b742c3abec1e9293a48286`).
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-build --no-restore --filter TestCategory!=F1aRed`: **489 passed, 0 failed, 0 skipped** (exit 0; Jarvis job `44817f055a78403bb572a5a18f703d36`). This run preceded the successful independent rebuild and used the same frozen worktree; no production source edits occurred.
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-build --no-restore --filter TestCategory=F1aRed`: freshly rebuilt output **7 executed, 7 failed, 0 passed, 0 skipped**, expected nonzero exit 1 (Jarvis job `1dd88b2338e343d49b0447aa0426be7b`; earlier same-result job `5b8425874bd74fd1ac471e7fa32df127`). Failure reasons match the frozen assertions, not discovery or compilation failure:
  1. Two-project B/E/G full-domain export — `No trusted, credential-free projection for SQLite body`.
  2. Repository/ref/commit preserved through inverse — same missing trusted body projection.
  3. Existing B-admissible 200-character ID — `Unsafe or unsupported record identity`.
  4. Prepared external effect's original operation identity — same missing trusted body projection.
  5. Scheduled verifier/reservation/role/permission/source linkage — same missing trusted body projection.
  6. Rehashed, valid, tampered bundle with no authenticated backup root — `Unsafe backup/restore unexpectedly succeeded`.
  7. Stale restore persisted Recovery/boot fence — `Stale restored SQLite remains writable before persisted Recovery fence`.
- The additional future-format rejection test is outside `F1aRed` and therefore included in the passing baseline.

## Decision boundary and mandatory next gates

**Accept the authored/executed RED-FIRST test contract alone.** These tests make missing semantics executable and their failure is expected; they are not a claim that F1 has implemented them. Raw `BodyJson` staging through permissive existing B does **not** establish authenticated typed ingress, original Git provenance, secrecy against bearer smuggling, or positive end-to-end restore fidelity. The test bodies and assertions must be updated to use Worker 20's separately reviewed and integrated typed B domain constructors before any GREEN/production review; G1b must separately prove persisted pre-admission Recovery fencing and role/account constraints. A hash-matched JSON bundle still requires a trusted repository/ref/commit restore root; source/ref/commit must not be guessed from its SHA or backup path. Future GREEN tests must exercise validated original source, complete account/project membership and held secrets through real authority rather than merely caller-supplied JSON. Existing accepted file/directory fsync, outbox and replication were not modified or re-reviewed.

**Hard NO-GO:** do not merge the deliberately failing F1a branch into main; do not interpret `7 failed` as a regression in accepted production functionality or `489 passed` as authenticated B/E/G coverage. No approval is given for F1b serializer/restore, live backup, Scheduler, new SQLite writer, Recovery release, host restart, migration or Work cutover. A separate exact frozen implementation/review and explicit operator authorization remain required.