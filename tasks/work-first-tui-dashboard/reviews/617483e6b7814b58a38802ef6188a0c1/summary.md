# Review summary — Work-first terminal dashboard

**Verdict:** Changes requested  
**Frozen commit:** `681958aa9233d33ebec13d3b8ef8e15bbb1b3093`  
**Base:** `944bca7fa38cb5e973b04ce7829966cca351202b`

The branch keeps the new view read-only, explicitly unbound until its WorkToolSnapshot adapter exists, and leaves legacy AgentsMode available. The changed source is limited to WorkDashboardView, ConsoleTui wiring, and corresponding projects/tests; no scheduler or production cutover was introduced.

**Verification performed (Reviewer 1):** `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~WorkDashboardViewTests`: 7/7 passed. Adjacent Release tests (TerminalMouseTests, TerminalLayoutTests, TerminalFrameTests, AgentViewTests): 54/54 passed. `git diff --check <base> <head>`: clean. Worktree was clean and matched the frozen review SHA. No GitHub Actions used.

**Blocking findings:**
1. `findings/fail-open-active-liveness.md` — an Active category with absent/stopped/unrecognized liveness is displayed as `Active (observed)` instead of an uncertain claim.
2. `findings/status-hidden-by-truncation.md` — readiness and NotReady/UNKNOWN status is last in the row string and disappears under normal narrow-terminal truncation.
3. `findings/missing-terminal-integration-tests.md` — new tests exercise only the pure projection, not required terminal mode/refresh/concurrency/legacy isolation behavior.

Please correct these on a new frozen commit, add negative-path/terminal tests, and resubmit to the shared queue. Lead-only integration after independent acceptance.