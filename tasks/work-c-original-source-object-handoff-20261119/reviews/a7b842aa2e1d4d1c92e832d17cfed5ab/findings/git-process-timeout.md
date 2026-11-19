# Finding 2 — The subprocess timeout is not enforced during blocking pipe reads

**Priority:** bounded-test reliability. **Frozen source:** `5e1798003064df3ff23017f42aebb74d89817739`.

In `Server.Tests/OriginalGitSourceObjectHandoffTests.fs:17-22`, the helper calls `child.StandardOutput.ReadToEnd()` and then `child.StandardError.ReadToEnd()` **before** `child.WaitForExit(15000)`. `ReadToEnd` waits for EOF; a Git subprocess that hangs or keeps either pipe open prevents control from reaching `WaitForExit`, so the advertised 15-second limit cannot fire. Sequential reading of two redirected pipes can also deadlock if the child fills one buffer while the caller waits on the other.

The current local-repository fixture succeeds (independent Release run 3/3), but that does not establish bounded failure behavior. Fix with concurrent asynchronous drains plus a deadline that applies to the entire process, kill the process tree on expiry, await/dispose streams safely, and surface a useful diagnostic without indefinite waiting. Use environment/config isolation where appropriate so global Git settings cannot turn a disposable test into credential or hook interaction. Rerun the scoped Release tests and `git diff --check`.
