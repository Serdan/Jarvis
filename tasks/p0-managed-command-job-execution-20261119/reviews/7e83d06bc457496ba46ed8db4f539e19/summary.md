# Independent review — ChangesRequested

**Review** `7e83d06bc457496ba46ed8db4f539e19` (Reviewer 3). **Exact frozen source** `c344d04604a584ac4cac0f4c2d5788054fa7bf33`; target base `a83549a6897a4e26aa4834ff81dc38b6e16ee85e`; review branch `refs/heads/jarvis-review/p0-managed-command-job-execution-20261119/7e83d06bc457496ba46ed8db4f539e19/r1`. Checked matching clean worktree and base ancestry; no source edits or merge.

## What improved

The source fix in `Client/Program.fs:211-253` now awaits the exact managed process JobId rather than treating initial `Running`/exit=-1 as `CommandFailed`; `JobManager.fs:794-829` follows terminal `Completed`, `TimedOut`, `Canceled`, and failures, and does not launch a duplicate command. The >1.2-second marker test and negative helper tests are valuable.

## Integration-blocking findings

1. **`findings/scheduled-consumer-not-regression-tested.md`** — Tests `Client.Tests/JobManagerTests.fs:1234-1314` exercise only `awaitScheduledCommandTerminal`, not `Program.executeScheduledAction` or its scheduler result mapping. The exact previous consumer regression could return while every new test passes. The canonical acceptance requires a deterministic scheduled-action regression proving terminal success, no premature `CommandFailed`, and no duplicate execution, including nonzero/UNKNOWN negatives.
2. **`findings/post-start-unknown-mislabeled-failed.md`** — After a process is started, `JobManager.fs:287-289` can explicitly return a lost-job `ContextError` whose text indicates unknown outcome. `Program.fs:213` passes the text unchanged, but `BrowserSchedules.fs:712` recognizes only the literal `OutcomeUnknown` token, so this uncertainty is persisted as definite `failed`. Recurrence on uncertain outcomes also needs a fencing check. Source-traced conditional scenario, not an observed live eviction.

## Independent verification and limits

- Read frozen patch `47376b7e4..c344d0460` and original task contract; checked SHA, clean checkout and base ancestry.
- Linux .NET 11 **Release** via actual `start_job`/`get_job_result`: focused managed/scheduled-command filter **10 passed, 0 failed**, true process exit code **0**; BrowserSchedules filter **23 passed, 0 failed, 1 skipped**, true exit code **0**.
- Did **not** run the full Client Release suite, long-running >60-second test, combined Worker 11 + Worker 18 integration, actual live scheduler, remote client recovery, deployment or migration. These remain separate integration/operator gates.

**Disposition:** ChangesRequested for the two P1 source/coverage issues. Keep exact frozen source review intact. No integration into current main, Worker 18 source overlap, original-Git release, Work Scheduler/SQLite cutover, restart or deployment is authorized by this verdict. Author should correct on a new immutable commit and submit a fresh independent shared-queue review.
