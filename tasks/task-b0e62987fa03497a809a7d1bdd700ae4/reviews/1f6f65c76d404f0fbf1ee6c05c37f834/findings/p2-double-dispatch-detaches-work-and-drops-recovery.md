# P2 — Integration layers detach real command execution and lose correlated recovery

**Frozen integration source:** `93b2a2520caf3512441b5d3eed6e443606a679b5` (merge parents `39168b69295a565528e7fc83f4278da81569afa5` and previously accepted `0f97f595e10338014ac9f7a9b9e0df7487c583a0`).

This defect was created by composing two individually valid dispatch implementations during the **manual conflict resolution in `Client/SignalR/Client.fs`**, not by the already accepted Worker 5 feature alone.

## Exact combined runtime path

1. `Client/Program.fs:201–257` wires the actual SignalR callback to **`CommandDispatchQueue(8,128)`**. Each worker's `Execute` delegate is `Client.receiveCommandAndReplyWithTiming`; only an exception from this delegate triggers `replyUnhandled` with the original correlation ID and typed `UncertainOutcome`.
2. `Client/SignalR/Client.fs:934–945`: `receiveCommandAndReplyWithTiming` itself calls **`BoundedResponseIngress(96)`** `TryDispatch` to start `receiveCommandAndReplyCore` in a separate Task.Run. On success, **it discards the returned completion Task and returns `Task.CompletedTask` immediately**. `CommandDispatchQueue` therefore thinks the operation and reply are finished, frees its worker slot, and drains its pending queue. The intended 8-worker execution limit is bypassed; up to **96** real command/reply tasks can remain in flight.
3. `Client/SignalR/HubConnection.fs:23–50`: once that inner registry reaches 96, `TryDispatch` rejects with `Error "Client command-ingress capacity reached ..."`, **before executing the command**. The merged `receiveCommandAndReplyWithTiming` turns this into a faulted Task, which the outer `CommandDispatchQueue` treats as an unexpected post-execution exception. `Program.fs:212–227` calls `CommandDispatchRecovery.unexpectedResponse`, returning **`UncertainOutcome`** despite a provably *not executed* command, instead of the same confirmed-zero overload evidence used by the outer queue.
4. Conversely, if `receiveCommandAndReplyCore` **actually throws after it was admitted** by the inner registry, `BoundedResponseIngress` catches/logs via its `onError` callback and discards that failure. Since the outer delegate had already returned success, **`Program.replyUnhandled` never runs**, no prompt correlated uncertain response is sent, and the server must rely on the longer tracker timeout/disconnect path. This defeats Worker 5's explicit failure/recovery guarantee.

## Independent reproducible negative-space tests

Added **two focused NUnit tests only to disposable reviewer-owned detached worktree** `/tmp/jarvis-reviewer1-dispatch-negative-93b2`, containing the exact frozen integration source. No owner code or frozen commit was modified.

- `combined dispatcher preserves worker limit under stalled operations`: composes the **actual production types** `CommandDispatchQueue(2,8)` + `BoundedResponseIngress(4)` using the exact merged wrapper behavior: inner `TryDispatch` result's completion Task is discarded and outer delegate returns `Task.CompletedTask`. Six calls are successfully admitted to the outer queue while all inner work is stalled. Expected **0** inner rejections and at most two actual executions; observed **2** premature inner rejections (NUnit `Expected: 0; But was: 2`). `dotnet test ... --no-build --filter Name~combined` exited 1; two other matching tests passed.
- `composed dispatcher propagates inner fault to correlated recovery`: accepted operation throws inside inner `BoundedResponseIngress`, whose `onError` runs. The outer `CommandDispatchQueue`'s correlated `OnUnhandled` **never fires**; NUnit `Expected: True; But was: False`, test exited 1. Original `Client/Program.fs` wires only the **outer** `OnUnhandled` to `replyUnhandled`; the **inner** `onError` merely logs. This isolates lost prompt recovery independently of any Git remote or HTTP flakiness.

These are independent test probes of the **real production types and the exact composition** visible in `Program.fs` and `Client.fs`; they do not use a live external SignalR server to generate the 96-call overload, nor claim an observed production incident.

## Required correction

Use **one authoritative bounded dispatch/admission path** for actual command execution and correlated response delivery. For instance, under the already detached `CommandDispatchQueue` worker, run/await the real `receiveCommandAndReplyCore` to completion instead of calling another detached `BoundedResponseIngress`; alternatively deliberately retain a single ingress registry with explicit bounded admission and proper correlated recovery. Either way:

- While the configured worker slots are busy, admitted work must remain queued **without** an extra inner premature rejection or unbounded real execution beyond the chosen concurrency cap.
- Any guaranteed **pre-execution rejection** must emit a prompt normal `ExecutionFailed` / confirmed **`AgentStateChanges=[]`**, never `UncertainOutcome`.
- Any **unexpected execution exception** after admission must deliver the correlated typed `UncertainOutcome` with original operation ID (when known), unknown/known partial mutation-state evidence and no blind retry. Do not silently consume the failure in a logging-only callback or wait for a server tracker timeout.
- Preserve current .NET 11 client-owned tool-job auto-promotion, bounded pending `PendingJob` semantics, existing OAuth scopes, cross-project source/destination state evidence and explicit cancellation contracts. Add regression exercising the **production combined wiring**, not isolated queue/registry unit tests only.

**Severity: P2 (blocking integration acceptance).** Under concurrent slow calls this reintroduces avoidable capacity failures and an incorrect outcome classification, and suppresses prompt correlated recovery. Full existing suites pass because they test the two implementations separately, not their merged live composition.
