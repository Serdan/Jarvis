# P1 — Contracted hot-path golden coverage is incomplete

**Frozen source:** `fcadb38ed2312e961e1329f54ca1bbf6e56df7cb` (base `a598c6b1460908d3005e769c0ed53096b9ccfa35`).

**Requirement:** The task spec explicitly requests .NET 11 Release synthetic 0/220/2200 cold/warm/advance goldens for GetTask, ReviewerInbox, ReviewDetails, ListWorktrees, ChallengeList, JobResult polling, notification reads and preflight; fixed subprocess/fetch/batch/bytes budgets; and two-device contention.

**Observed:** `Client.Tests/TaskOverviewBatchPerformanceTests.fs:273-353` adds the 0/220/2200 `branchReviewerQueue` performance metric, a one-time GetTaskWithSpec fixed process/fetch assertion, and a ReviewDetails entity identity check **outside** the measured metric. `Client.Tests/WorktreeTests.fs:54-84` checks two-worktree functional unknown/selected cleanliness but has no independent Git process/fetch/output bound. No new source-pinned cross-size goldens for JobResult polling, notification reads, preflight, or two-device contention are introduced by this commit. Existing `ChallengeWorkflowTests.fs:461-495,1109-1162` exercise some bounded challenge reads but do not supply the specified new comparable 0/220/2200 hot-path baseline.

**Impact:** The deterministic regression contract cannot detect a return of per-entity Git subprocesses/remote fetches on the remaining frequently used MCP paths. A passing reviewer-queue test cannot establish the required other read budgets.

**Requested disposition:** ChangesRequested against this original task's acceptance scope. Add focused source-pinned/bounded controls for the remaining operations or obtain explicit independent narrowing of the task contract; no need to impose a particular test-framework implementation. Keep wall-clock percentile numbers informational.