# P1 — Prewarming is wired to a retired activation path

**Snapshot:** `de596e2d0f0f70fbe35a72a2c8b822d2708941db` (base `944bca7fa38cb5e973b04ce7829966cca351202b`).

## Source evidence

- `Client/SignalR/Client.fs:766`: `TaskReadCommand` directly invokes `OrchestrationApi.TaskApi.read`. The current Jarvis agent lifecycle transitions a Ready assignment to Working when `agent_inbox` is read; the actual canonical inbox activation was observed in this review turn.
- `Client/SignalR/Client.fs:774-788`: the sole call to `CodeQualityHooks.prewarm` is conditional on `TaskWriteCommand` action `"ResumeAgent"` with a resolved registered checkout. `git grep -n CodeQualityHooks.prewarm` against the frozen commit confirms no other call site.
- The public simplified lifecycle no longer exposes `resume_agent`. Consequently the normal Ready → Working transition cannot trigger this prewarm path. `RoslynLsp.formatDocument` initializes a session synchronously on the first matching file hook instead.

This violates the requested **asynchronous prewarm when source context becomes known**, including the requirement that the first real edit should benefit from a warm session. The code has an async prewarm implementation but does not call it from the active transition.

## Required correction

Connect asynchronous, idempotent prewarming to the supported activation/source-binding flow, after validating an exact registered checkout. If the inbox activation does not yet resolve source context, trigger when that context is established; do not prewarm the main checkout by accident. Add instrumentation/tests proving a Ready agent activation initiates one nonblocking session, repeated inbox reads do not duplicate it, distinct worktrees remain separate, and unknown context defers safely. Keep shutdown/removal cleanup.

**Verdict:** blocking change request. This is a source-path finding, not a failed runtime benchmark.