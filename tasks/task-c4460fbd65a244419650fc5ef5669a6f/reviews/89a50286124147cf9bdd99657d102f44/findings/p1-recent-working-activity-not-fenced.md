# P1 — Working→Ready correction can override recent genuine agent activity without a CAS conflict

Frozen commit: `f68449e6975f06b787d82554ebfd65a4b565bfa9`; task `task-c4460fbd65a244419650fc5ef5669a6f`.

## Exact code-path defect

The task expressly requires that a stale operator correction **cannot override recent genuine activity** of the target agent; an agent's own Yield must also win.

- `Client/SignalR/Client.fs:685–691` records each normal incoming agent tool call with `rt.Tui.RecordAgentSeen(project, agentId)`. The implementation in `Client/Modules/ConsoleTui.fs:1568–1597` updates only a local, persisted activity-sidecar dictionary and TUI timestamp. This is **not** a canonical ledger entity change.
- `Client/SignalR/Client.fs:129–175` activates an agent only if its cached canonical state is **Ready**; if already **Working**, the ordinary source read or other tool request does **not** necessarily change the task, attempt or agent ledger versions. Thus a genuine tool request, including a long-running read, can occur while all three CAS versions remain unchanged.
- New `ReviewWorkflow.correctAgentWorkingStateAs` at `Client/Modules/ReviewWorkflow.fs:2012–2064` compares task/attempt/agent versions, generation, assignment identity, Started/InProgress and `agent.State = "Working"`. It neither checks a recent canonical activity receipt nor fences in-flight operations. If an operator gathered those versions before the agent's normal tool call, the correction will **still succeed during or immediately after that call**, changing a genuinely active Working agent to Ready and clearing its Working-turn marker/session.
- The new acceptance tests cover an intervening **Yield** (which writes the agent ledger version), not intervening ordinary activity or an in-flight read (which generally does not). Indeed the positive correction fixture accepts correction immediately after beginAttempt without an idle/grace window.

This is a deterministic lack of a freshness guard, not a claim that a production correction has already interrupted an agent. It also does **not** mean local last-seen sidecar timestamps are suitable as a cross-device source of truth: they are not.

## Required correction

Define and enforce a versioned/canonical freshness or activity-fence contract adequate for an administrative takeover: an exact expected last-active epoch/receipt, bounded inactivity/lease guard, or another linearizable mechanism that cannot silently overwrite an already-Working agent's recent ongoing requests. Keep ownership, task/attempt/generation, checkpoint, and target last-seen unchanged when a correction actually succeeds. Fail closed when source-of-truth activity cannot be determined; do not merely compare a stale local TUI timestamp to a stale ledger snapshot.

Add a negative-space concurrent test: begin Working, capture the operator's expected versions, perform an ordinary tool call by the worker (including a request blocked in execution) **without a worker Yield**, then issue correction using the old expected versions; it must fail without changing target state or creating a lifecycle audit event. Test same-device and distinguish cross-device guarantees explicitly. Current tests only establish that genuine ledger writes already cause CAS failures.

**Severity P1:** the feature's principal safety requirement (do not administratively stop an agent that has genuinely resumed working) is not guaranteed by the new version checks. No merge/deploy before correction and independent re-review.
