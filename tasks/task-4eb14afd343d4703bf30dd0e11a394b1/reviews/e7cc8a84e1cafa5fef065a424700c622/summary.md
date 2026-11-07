# Review of bounded-process lifecycle — JRV-2026-11-02

**Final reviewed head:** `1dddf2f04917c937570c5980419b069d213f3326` on `fix/bounded-process-execution`  
**Base:** `e50c24afceb03d7caf180dee2099144f27ebeb89`  
**Task:** `task-4eb14afd343d4703bf30dd0e11a394b1`  
**Reviewer:** Jarvis Lead (self-review; no independent second agent)

## Scope and findings

Reviewed `Client/Modules/BoundedProcess.fs`, migrated callers in `ClientShell.fs`, `ProjectPaths.fs`, `LedgerStore.fs` and `ProjectCreation.fs`, the command-level tests, and `docs/BoundedProcessExecution.md`.

**No remaining source-level blocking findings** in the Linux process lifecycle or typed command compatibility paths after corrections. The review caught and corrected platform issues before the final snapshot:

- **Windows stdin corruption:** An earlier startup barrier consumed one byte from the executable's standard input. Replaced it with a per-command named `EventWaitHandle` so startup authorization is independent of inherited, closed, or supplied stdin. The helper is assigned to a kill-on-close Windows Job Object before signaling launch.
- **Apphost invocation semantics:** `dotnet JarvisClient.dll` requires a DLL argument, while a direct framework-dependent apphost and a published single-file app must not receive it. Corrected selection and added a deterministic test.
- **Git standard-input semantics:** Canonical Git commands intentionally close unused stdin; the shared runner now models `InheritInput`, `ClosedInput` and `TextInput` explicitly to preserve EOF and avoid hanging commands.
- **macOS native loader:** Darwin `kill` and `setsid` now bind explicitly to `libSystem.B.dylib`, while Linux uses `libc`. Mac helper remains subject to native smoke testing.
- **Output encoding:** Restored configured/default process output decoding rather than assuming every Windows program emits UTF-8. Explicitly sends supplied input as BOM-less UTF-8.

## Behavioral guarantees reviewed

- A single monotonic deadline covers process launch (after synchronous OS start returns), concurrent stdout/stderr drains, optional stdin write/flush and immediate process exit. A parent exit alone does not bypass the timeout while a descendant holds inherited pipes.
- Linux uses an isolated `setsid` process group and negative-PID kill; Windows uses an independently signaled Job Object group with kill-on-close; macOS uses a managed session group host.
- Both normal completion and timeout terminate owned descendants. Streams are drained concurrently and output prefixes are bounded per stream. Cancellation returns a typed error rather than hanging.
- The previous tool response shape (`ExitCode`, `TimedOut`, `StdOut`, `StdErr`, `Truncated`) and `ClientShell` executable checks remain unchanged. Git read paths preserve their former unbounded blob output; project-init Git and worktree discovery now have bounded diagnostics.
- `StartJob`, clipboard selection-owner helpers, self-updating, and OAuth/browser launch remain intentionally outside the bounded executor because their lifetimes differ.

## Release evidence

- **Final reviewed head:** 43/43 focused Client Release tests passed, including the subprocess regressions and LedgerStore, ProjectCreation, ClientShell and Worktree flows. `git diff --check` clean.
- **Previous source head `e7cc8a8`:** full Client Release suite completed **401 passed / 1 failed** (402 total). The sole failure was the independently tracked, previously reproducible `JobManagerTests.startJob strips sensitive inherited environment` five-second completion timeout. No subprocess-executor failure appeared. The final commit changes only the selection of Darwin native library symbols and documentation; the current head's focused Release tests passed.
- **Server Release:** 95/95 tests passed; no server/protocol files were changed.
- **Platform evidence limits:** Linux behavior is runtime-tested, including deliberate pipe-holding descendants and normal-exit descendant cleanup. Windows Job Object and macOS group-host runtime behavior have **not** been smoke-tested on those operating systems. The project currently has no tracked cross-platform CI runner; do not claim cross-platform runtime validation based on a Linux compile.

## Disposition

**Accepted for source correctness** based on the reviewed implementation and focused regression coverage, with two explicit release-readiness constraints: (1) the existing unrelated JobManager test must be stabilized or assessed before claiming a green full Client Release gate; (2) Windows/macOS native process-group smoke tests are required before publishing an updated cross-platform client distribution. **No merge, production deployment or other-agent notifications authorized by this review.**

This is a self-review, not an independent reviewer verdict. The implementation is preserved on its isolated remote branch.
