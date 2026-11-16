# Worker 7 combined-source evidence

- Frozen review: `df851e83facd437ebeb46ed620dc0616`; exact composite `1d87393fa5e7f99da2605b641844768c78d348e4`, parent `ed358380400efaefcc3b95de9f03f8d1ad2678c5`.
- Exactly four changed paths: `Client/Modules/TaskFirstUiProjection.fs`, `Client.Tests/TaskFirstUiProjectionTests.fs` and one F# compile include each in `Client.fsproj` and `Client.Tests.fsproj`. No WQ04/WQ05/WQ08 or live TUI/Scheduler source changes.
- Accepted WQ09 source/test files are byte-identical to independently Accepted `7ce5c3a085c9ad7a12d4e389686e3e7ded656200`. Blob IDs `5ac42992a725a9e7a5531177b4db7163e5538816` and `6ba91b8f76f46b426c27bb7c4b88d378e0fde36b`. `git diff --exit-code 7ce5c3a -- <two files>` = 0 and `git diff --cached --check` = 0 before commit. Checkout clean after commit.
- .NET 11 Release targeted Client tests: filter `FullyQualifiedName~TaskFirstUiProjection|FullyQualifiedName~WorkQueue|FullyQualifiedName~AgentView`. Initial build/test ExitCode 0; repeat with `--no-build --no-restore` **118 passed, 0 failed, 0 skipped, 47 seconds**. Includes matched-but-unjoined and stale/reordered two-clone account-capacity UNKNOWN negatives, claimed dependency-gate holds and terminal-safe previews.
- Additional full Client Release `--no-build --no-restore` run was attempted with a strict 180-second cap. It **timed out without a test summary**, not a verified pass and not evidence of a test failure. Independent reviewer may rerun under a suitable bound if needed.
- Requested review focus: source byte identity, provenance and exact parent, preservation of all existing WQ04/WQ05/WQ08 code/tests, no false account-global free capacity absent cross-ledger join, strict message key identity, terminal-safe titles/IDs, accepted per-project U/T review counts, and no live UI/Scheduler/claim/launch activation. No hosted GitHub Actions, main push, deployment, migration or Codex.

---

## Reviewer 2 independent verdict — Accepted (inert WQ09 only)

Exact frozen composite `1d87393fa5e7f99da2605b641844768c78d348e4`, exact main parent `ed358380400efaefcc3b95de9f03f8d1ad2678c5`. Independently verified the four-path diff, .fsproj includes, clean worktree and zero whitespace errors; both added source/test Git blobs are **byte-identical** to Reviewer 5's already Accepted WQ09 `7ce5c3a`. Frozen grep finds no production caller; neither ConsoleTui nor Scheduler/host is touched. Account capacity still returns Unknown without verified two-ledger heads/high-water, claimed nonclear gates stay held, invalid message identity is Unknown, terminal preview escapes untrusted fields, and accepted per-project U/T review counts remain unchanged.

**Independent .NET 11.0.100 Release verification:** `dotnet test Client.Tests/Client.Tests.fsproj --configuration Release --filter 'FullyQualifiedName~TaskFirstUiProjection|FullyQualifiedName~WorkQueue|FullyQualifiedName~AgentView' --nologo`: exit 0, **118 passed, 0 failed, 0 skipped** (48 seconds); only pre-existing FS0760/FS3369 warnings. Targeted, not a full Client/Server pass. Full independent findings: `findings/reviewer2-wq09-currentmain-composite.md`.

**Accepted** for canonical integration of *this exact inert read-only composite only*. All live UI, claims, message posting, account reservation, Scheduler, YieldSource, host activation, deployment and migration remain explicitly gated. No reviewer main push or GitHub Actions.
