# [P1] Native cgroup kill failure disappears on launcher exit

**Frozen head:** `44c854f45cf1e43d2baeac401b97835a08c49bda`

**Location:** `Client/Modules/JobManager.fs:183-203, 288-307, 449-487`.

When the Linux opt-in cgroup scope is present, `terminateGroup` correctly throws if `LinuxJobScope.tryKill` cannot write `cgroup.kill`. But `completeObservedExit` unconditionally catches and discards that exception (`try terminateGroup() with _ -> ()`) and then calls unbounded `proc.WaitForExit()` under `job.LaunchGate`. If the launched command leaves a descendant inheriting stdout/stderr, failed cgroup cleanup leaves the pipes open and `WaitForExit()` may never finish, holding the launch gate and blocking `CancelJob` from retrying. If the output pipes are closed instead, the callback marks `Completed` (or `Canceled` whenever a cancellation was merely *requested*) despite the failed kernel cleanup. After the helper exited, `cancelJobForProject` skips `TerminateGroup` based on `alreadyExited` and can return `Ok` even though descendants were not killed. No observable error is retained in the job result, contrary to the explicit fail-closed cleanup contract.

**Impact:** A transient permission, scope, boot, or `cgroup.kill` I/O error can leave escaped/unreaped scoped descendants without a usable retry or trustworthy completion/cancel acknowledgment. The server's separate correction witness remains fail-closed, but the newly added native StartJob cleanup itself does not.

**Required:** Record/report kernel cleanup failure on the job, avoid an unbounded wait while the scope kill is unverified, and permit retry of exact-kernel-scope termination even after the launcher process has exited. Do not fall back to killpg/PID-only signals. Cover injected `cgroup.kill` failures with a live descendant retaining redirected pipes, a descendant without pipes, and cancellation retries.

**Reviewer evidence:** Read-only independent source path analysis; focused Client.Tests and Server.Tests normal-path suites pass but do not exercise forced native kill failure. No branch edits.
