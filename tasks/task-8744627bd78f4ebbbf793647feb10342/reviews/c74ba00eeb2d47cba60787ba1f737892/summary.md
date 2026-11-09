# Windows bounded-process output loss — independent review

## Reproduction and cause
On KRUSO-PF539JMB (Windows, deployed protocol 3.62), FinanceFlow reports `InvalidBinding: Remote fetch or push endpoint differs from approved binding`. Its persisted trusted binding and Git's `origin` **both** specify `https://github.com/Kruso-AI-Sandbox/FinanceFlow.git` for fetch and push. Direct `StartJob git remote -v` and Node child-process `git remote get-url` return correct output; bounded `run_command git remote -v` and `get_git_status` return exit 0 with **empty stdout**. The broken trust check is a downstream symptom.

The Windows `BoundedProcess.runInternalGroupHost` launched a child without explicit redirection of the helper's already redirected stdio. On Windows this loses output. The fix redirects all child stdio and copies raw stdout/stderr bytes to the helper's inherited output handles, concurrently pumps stdin, drains both output streams before returning an exit code, and preserves existing JobObject kill/timeout behavior. The macOS implementation remains unchanged. No FinanceFlow config or approved ledger binding was modified.

## Frozen source
- Commit: `a11c76f80301abf488e3db5e083885e802d2086f`
- Branch: `refs/heads/fix/windows-bounded-output`
- Target: `refs/heads/main`
- Task: `task-8744627bd78f4ebbbf793647feb10342`

## Validation
- Targeted Release regressions: **2/2 pass** (both output channels, exact Git approved fetch/push URL).
- Full Client Release suite: **528 passed, 0 failed**.
- Server Release suite: **122 passed, 0 failed**.
- `git diff --check`: clean.
- **Remaining release gate:** patched self-contained Windows executable has not yet been exercised on KRUSO; test end-to-end `get_git_status` and `ledger_health` after an explicitly authorized client release. Do not mask the bug by editing its trusted binding.

Review focus: Windows pipe forwarding semantics (including stdin EOF, descendants holding inherited streams, backpressure), correctness of JobObject timeout teardown, and cross-platform isolation.