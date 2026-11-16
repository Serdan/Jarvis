# Reviewer 3 independent review — Work-first TUI corrections

**Frozen commit:** `62d30dfe5d7462d18830a14cddd61e92f45e50ac`  
**Pinned base:** `944bca7fa38cb5e973b04ce7829966cca351202b`  
**Verdict:** Accept; no new blocking findings in the scoped correction.

## Prior findings resolved

1. **Fail-open Active liveness:** `WorkDashboardView.workStatus` now requires both non-empty bound claim-launch/session identities and explicit recognized positive liveness (`Running` or `Live (verified)`). Absent, stopped, timed-out, stale and unknown observations remain `Claimed; session UNKNOWN`. Table-driven regression cases cover these.
2. **Status hidden by truncation:** `displayLine` puts readiness before project, Work ID, subject and queue, retaining `UNKNOWN`/NotReady at 40- and 80-column widths. Tests cover long IDs/subjects and unbound/partial coverage.
3. **Missing terminal tests:** A hermetic `ConsoleTui` key/render fixture exercises actual Work/Agents mode switch, filter Esc rollback, selection, concurrent provider refresh, replacement of a slow old provider, and failure clearing old ready rows. Legacy Agents remains separately accessible.

## Independent verification

- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~WorkDashboardViewTests`: **12/12 passed**.
- Adjacent `TerminalMouseTests|TerminalLayoutTests|TerminalFrameTests|AgentViewTests` Release filter: **54/54 passed**.
- `git diff --check 944bca7fa3 HEAD`: clean; checked-out worktree is clean and HEAD matches frozen review source.
- Patch touches only `WorkDashboardView.fs`, `ConsoleTui.fs`, associated F# project files, and `WorkDashboardViewTests.fs`; no scheduler or Git lifecycle mutation, hosted CI, or production cutover.

**Scope limitation:** No live WorkToolSnapshot source adapter is bound yet. This is explicitly documented via the typed read-only `WorkUiAccess.Load` seam and rendered as NotReady; acceptance is for the staged dashboard, not production readiness or cutover.