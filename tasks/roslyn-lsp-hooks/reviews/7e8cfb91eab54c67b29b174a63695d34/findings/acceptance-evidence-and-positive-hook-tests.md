# P1 — Mandatory acceptance evidence and positive commit-hook tests are incomplete

**Frozen review commit:** `de596e2d0f0f70fbe35a72a2c8b822d2708941db`.

## Independently verified

In the exact registered clean Worker 6 review worktree, I ran:

`dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter "FullyQualifiedName~CodeQualityHooksTests|FullyQualifiedName~GitCommandTests" --verbosity minimal`

Result: **23 passed, 0 failed, 0 skipped**, .NET 11, exit 0. `git diff --check 944bca7fa38cb5e973b04ce7829966cca351202b de596e2d0f0f70fbe35a72a2c8b822d2708941db` returned exit 0 with no output. These establish focused test success and whitespace integrity, **not** full task acceptance.

## Missing acceptance

1. `docs/CodeQualityRoslynHooks.md` expressly reports the full client-suite run timed out after 180 s, with activity-persistence permission errors. There is no passing full-suite verdict on the reviewed commit.
2. The same evidence report explicitly leaves the required **Wayfold small/large edited-file formatting parity against `dotnet format`, cold/warm latencies and daemon/launcher RSS** unverified. Two disposable roots formatted successfully, but actual project settings/analyzer compilation isolation across distinct worktrees was not proven.
3. `Client.Tests/CodeQualityHooksTests.fs` covers config gates and pure edit parsing but not live session restart, asynchronous prewarm/idempotence, two project/analyzer contexts or missing executable recovery.
4. The new `Client.Tests/GitCommandTests.fs` cases cover **partial-stage refusal** and **unapproved-hook refusal**, but do not execute a *successful* opt-in pre-commit formatting/fix from actual committed files. There is no positive exact-commit-content assertion covering auto-fix, advisory vs blocking, or preservation of unrelated staged/dirty changes with a live enabled hook. Existing non-hook path-limited behavior tests cannot substitute for that coverage.

The canonical task explicitly requires those tests and measurements. Please provide bounded reproducible results and tests using isolated disposable projects/worktrees; never modify unrelated Wayfold files or deploy for the review. If full-suite failures are unrelated environmental failures, document and isolate them rather than implying a pass.

**Verdict:** blocking acceptance-evidence request; no claim that unexecuted scenarios are broken.