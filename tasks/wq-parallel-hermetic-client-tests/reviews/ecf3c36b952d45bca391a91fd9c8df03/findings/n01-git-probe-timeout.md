# N01 — non-blocking: Git probe's local timeout can be bypassed by blocked pipe reads

**Severity:** P3 hardening; **Disposition:** non-blocking for Linux-only WQ-11 harness acceptance.

**Source:** `Client.Tests/HermeticHarnessTests.fs`, helper `gitCommand` at frozen `1cf0bf7ce6057430f616655f03f33227015a8e4e`.

The helper calls `gitProcess.StandardOutput.ReadToEnd()` and then `StandardError.ReadToEnd()` **before** `WaitForExit(15000)`. If Git stalls while keeping a pipe open or fills stderr while stdout is being read, `ReadToEnd` can block and the nominal 15s deadline is never reached. The paths are new disposable repositories; independent `git init` / commit / log regression completed, and the supported `scripts/test-client-hermetic.fsx` wrapper independently bounds the whole NUnit child process with a process-tree kill, so this does not invalidate this review's required containment/identity/portability corrections.

**Recommended improvement:** concurrently drain stdout and stderr with bounded buffers and enforce the deadline around the entire child operation, killing the process tree on deadline; add a deterministic stalled-child regression if extending this helper. Do not modify the reviewed frozen SHA or delay this review's decision solely for this hardening note.
