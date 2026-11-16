# Independent review — WQ08 unresolved-first bounded scheduler preview

**Verdict: Accepted.** No blocking findings in the frozen change.

- **Frozen source:** `833a72f2d85d08bba3cda92a0dad558b5fb33658`
- **Base:** `4babc474ce4ba23f1e85e0e4f73d35f2791449fc`
- **Scope:** `Client/Modules/WorkQueueSchedulerHostPreview.fs` and `Client.Tests/WorkQueueSchedulerHostPreviewTests.fs` (95 insertions, 6 deletions).

## Source inspection

The prior lexical first-64 selection is replaced with deterministic priority for unresolved/active launches. A historical terminal launch is demoted only when there is exactly one account token with `Released` status and matching project ID, project ledger ID/epoch, role/version, and original admission operation identity. Unknown browser submissions rank first; demand-invalidated and terminal-unreleased launches also precede settled history. Token grouping is computed once, and only the selected 64 launches incur admission/browser journal reads. If launches are omitted, `LaunchListTruncated` reports the total and directs inspection of the full Git ledger.

Existing identity-conflict, missing-token, and released-while-active diagnostics remain in the selected-launch path. The preview remains read-only: `CanDispatch=false`; no token reservation, browser submission, live scheduler activation, or migration was introduced.

## Independent validation

- `dotnet test Client.Tests/Client.Tests.fsproj --configuration Release --no-restore --filter FullyQualifiedName~WorkQueueSchedulerHostPreviewTests --verbosity minimal` on the exact frozen worktree: **12 passed, 0 failed, 0 skipped** (.NET 11, Release).
- Full `Client.Tests` Release run with `--no-build --no-restore` did **not** complete within 180 seconds; the process timed out without a test verdict. Its output included activity-persistence `InvalidDataException`/`UnauthorizedAccessException` diagnostics. This run is **inconclusive**, not a passing suite.
- Frozen worktree Git status clean.
- `git diff --check` reports one new blank line at EOF in the test file (line 416); cosmetic, nonblocking.

## Scope limitation

A bounded 64-entry report cannot enumerate every unresolved launch when more than 64 are unresolved. The explicit truncation issue makes that limitation visible, and ledger inspection remains the required recovery path. This is not a grant to dispatch.
