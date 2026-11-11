# Independent review — audited administrative Working-to-Ready correction

**Verdict: RequestChanges — one P1 unaddressed genuine-activity freshness/fencing requirement.**

Frozen source `f68449e6975f06b787d82554ebfd65a4b565bfa9`, branch `refs/heads/feature/admin-working-ready-correction`, base/current main `ecb59e2272c7e6c4e127345f5034695c2f6e5465`, task `task-c4460fbd65a244419650fc5ef5669a6f`.

## Positive review findings

- New `ReviewWorkflow.correctAgentWorkingStateAs` uses canonical `LedgerStore.transact` and verifies exact task/attempt/agent versions, assignment generation, task/attempt identity, expected Started/InProgress/Working state, correct ownership and active assignment.
- It writes **only** the target agent entity (Working→Ready, incremented agent version) and a distinct immutable `TaskLifecycleEvent` with operation/actor/requester/target/reason/before/after states and versions. Task, attempt, generation, prior checkpoint, integration/review history and owner assignment are untouched. `enterState "Ready"` clears active session/turn clock without fabricating a Yield or moving target last-seen.
- `TaskApi.write` routes a distinct `CorrectAgentWorkingState` action through the existing authenticated administrative actor path. Pending-intent field mapping and typed OAuth MCP tool surface are present. Historical schema-v2 lifecycle events remain readable with optional new fields.
- New tests cover wrong IDs/versions/generation, missing task, absent actor, wrong status, own worker Yield racing administrative correction, one-time immutable event/idempotent replay, and no fake worker checkpoint.
- Independent **Client Release 550/550 passed** (job `dfe7bda4f6c642ce9affb8bd87a5aecd`) and **Server Release 128/128 passed** (job `bc5136f7e6c446e1b9f4f914f729107e`). Exact frozen source SHA and worktree clean; `git diff --check ecb59e2..f68449e` exit 0, `git merge-tree --write-tree main f68449e` exit 0 (tree `31b1072baa19f82c706fde869580db525f618f93`). No source edits, production modification, integration or deployment.

## Blocking finding — P1

**A stale administrative correction can override recent genuine ordinary activity by a still-Working agent.** Finding: `findings/p1-recent-working-activity-not-fenced.md`.

- `Client/SignalR/Client.fs:685–691` logs ordinary tool activity via `rt.Tui.RecordAgentSeen`, stored in a **local noncanonical sidecar** by `Client/Modules/ConsoleTui.fs:1568–1597`.
- The automatic activation branch in `Client/SignalR/Client.fs:129–175` changes ledger state only for **Ready→Working**, not normal requests received while already Working; a genuine read or a long-running tool can occur without incrementing task/attempt/agent ledger versions.
- The new correction at `Client/Modules/ReviewWorkflow.fs:2012–2064` compares only those canonical versions/assignment fields and Working status, not last-seen/activity epochs or in-flight task fences. An operator with a previously observed version can therefore transition an actively working agent to Ready **without a stale-version conflict** even after recent ordinary tool activity. This defeats the task's explicit requirement that recent genuine activity invalidate stale corrections.
- Existing tests check that an intervening **Yield** (canonical version bump) wins. They do not test intervening ordinary source work that has no canonical version bump. This is a code-path/invariant proof; no production interruption or compiled failed regression is claimed.

Required: implement a coherent authoritative recent-work fence/inactivity precondition (not naive cross-device trust in a single TUI sidecar), with negative-space tests for a Working agent's ordinary active tool call versus an operator using stale versions. Preserve existing exact CAS, immutable audit and ownership invariants.

**Decision: RequestChanges** until the genuine-activity concurrency requirement is met and independently re-reviewed. Review does not authorize integration/deployment.
