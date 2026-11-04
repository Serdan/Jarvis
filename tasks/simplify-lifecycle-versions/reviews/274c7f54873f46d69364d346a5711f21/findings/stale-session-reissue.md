# P1 — Zero-version lifecycle calls can act on a different assignment generation

**Snapshot:** `23a6a5582f238d40b1a9a5fa446513c72682f6bc`

**Location:** `Client/Modules/ReviewWorkflow.fs`, `yieldAgent`, `resumeAgentWithSession`, `completeAgentWork`, and `checkpointAttempt` (around lines 1334–1595).

Each operation now accepts `assignmentGeneration=0`, resolves it to `attempt.AssignmentGeneration` under the transaction, and skips attempt/agent version checks for zero. The remaining task/owner/focus checks only establish that the *current* assignment belongs to the same agent and task, not that the *calling session* owns that generation. Example: the agent's old session was working on task T generation 1; Lead administratively recovers/reissues T into generation 2 for the same owner; a new session starts T; the stale old session invokes `complete_agent_work(T, owner, summary)` or checkpoint/yield with zeros. The current generation and version resolve successfully and the old session mutates/completes generation 2. The previous required exact generation/version prevented this stale operation.

**Correction:** Preserve a stable execution-context fence for versionless operations (for example, verify current active session association/attempt-generation context, or require a stable attempt/generation handle when the server cannot establish it). Do not simply require the three bookkeeping versions again. Maintain explicit optional preconditions. Add a regression with stale-session call after same-task administrative recovery and new-session resume, alongside a valid current-session call. Ensure retries remain idempotent and historical work is not silently assigned newer authority.
