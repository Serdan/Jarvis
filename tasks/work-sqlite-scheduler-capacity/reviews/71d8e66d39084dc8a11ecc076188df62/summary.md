# Reviewer 2 — E1a independent frozen review

**Verdict: Accepted, strictly within the default-off E1a journaling component.** Source `ee175b410958c4f0fc682791c2baf5e849adfaad`, base `e86e0133886f4f51b703a76167384071245e103c`. Examined all four changed files and the existing E0 reservation/claim implementation, actual C1 offline importer, and task acceptance boundary.

## Verified
- Durable `prepare` atomically writes immutable account/project/work/role/source/ref/generation evidence and a `Prepared` external-effect row; guards include current project, import marker, legacy head, launch, stage, role, origin, and account capacity version.
- The original account-bound reservation UUID and its receipt are required, and replay requires the same launch/account binding. No second capacity credit is taken or released.
- `markUnknown` transitions both launch and effect rows to `Unknown` with a separate durable operation receipt before any future transport attempt; same-UUID replay is idempotent, a new operation cannot resubmit an unknown launch, and unrelated launch/account receipt laundering is refused.
- `refuseExternalDispatch` always rejects. No production provider/browser/process transport, scheduler host registration, migration, deployment, or cutover is introduced in this diff.
- Independent test execution on clean exact-head worktree `sqlite-scheduler-e1a-worker1-20261117`: .NET 11 Release targeted `FullyQualifiedName~e1a_` **4/4 passed**; complete `Server.Tests/Server.Tests.fsproj` Release `--no-restore` **406/406 passed**, 0 failed/skipped. `git rev-parse HEAD` matched the frozen SHA and `git status --porcelain` was empty.

## Mandatory boundary / not accepted by this verdict
Actual C1 `WorkLegacyImportSqlite.commitOfflineFixture` writes raw legacy queue/stage/role records (including `legacy-queue` and `origin/*`), not the normalized `Project/authority`, stage, role, and post-E0 reservation rows consumed by E1a. The E1a tests seed that normalized state synthetically. Therefore these tests **do not prove an end-to-end raw-C1-import -> normalized-scheduler -> dispatch contract**, nor verified old-writer quiescence or source-authority takeover. The missing typed conversion/projection and exact C2 independently accepted/integrated original-Git writer fence must be demonstrated before E1b dispatch/cutover, with real-import two-project negative tests, fail-closed unknown recovery, and an explicit operator GO. Do not promote E1a acceptance to G2/G3/G6, production scheduling, or a C2-writer witness.

No blocking source defects found in the deliberately default-off E1a component at this frozen commit.