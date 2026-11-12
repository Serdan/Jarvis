# Reviewer 1 — independent frozen SignalR dispatch-stall review

**Verdict: RequestChanges** on one P2 error/evidence-consistency defect. Frozen commit `f78c3bfd17fabd37790c50190043cd513a5e6cc1` at `refs/heads/fix/mcp-dispatch-stalls`, task `task-b0e62987fa03497a809a7d1bdd700ae4`, review `5c2372fe00504f04a3813c0e5e454385`, pinned base `ecb59e2272c7e6c4e127345f5034695c2f6e5465`.

## Independently verified improvements

- Confirmed the underlying SignalR callback serialization: a task-returning `ReceiveCommand` handler blocks subsequent notifications. New `Client/SignalR/CommandDispatch.fs` uses a bounded 8-worker/128-pending channel started off the receive loop, `TryWrite` nonblocking admission and explicit immediate overload rejection; no silent `DropOldest` or unbounded fire-and-forget. Every admitted command's queue/execution timing is observed, and unhandled task exceptions cannot kill a worker. `Client/Program.fs` drains admitted commands on shutdown, with an explicit two-minute timeout and uncertainty warning; this is best-effort, not durable delivery.
- Client replier `receiveCommandAndReplyWithTiming` continues to correlate command/reply using original correlation ID. `ResponseDelivery.sendWithReconnectRetry` now bounds a hung `InvokeAsync` itself and does not retry a send after a timeout that could have delivered. New real local SignalR tests exercise one blocked invocation with unrelated fast ones, prompt saturation rejection over `SendCoreAsync`, reconnect after failing blocked reply, worker recovery after thrown exceptions, and synthetic 64-command latency comparison. Client queue callbacks are async and returned immediately on admission.
- `Server/Services/ClientService.fs` now times routing, client SignalR send, reply wait and total elapsed, distinguishes HTTP upstream abort, tracker timeout/disconnect and signalr-send-failure for logs. Metrics use finite `commandName` and `outcome` tags only; correlation ID appears only in structured slow/error logs, never as metric dimension. No user tokens, command arguments or bodies logged by the introduced code. Existing per-device routing, response correlation and ledger CAS source semantics remain otherwise unchanged.
- Synthetic benchmark with one blocked ledger action and 64 cheap read/job-status commands reported pre-fix p50/p95/p99 **352.2/352.9/353.1 ms** and after **0.3/0.4/0.4 ms**. These are *controlled loopback results*, not measured production latencies.

## Independent tests and branch integrity

- Full frozen **Client Release net10: 558/558 passed**, exit 0, job `1106241ad25c4eb298bb051de12261ee`.
- Full frozen **Server Release net10: 127/127 passed**, exit 0, job `10e02a16d91a43ad89f272adc3c4cde9`.
- `git rev-parse HEAD` exact frozen commit, worktree clean, `git diff --check ecb59e2..f78c3bfd` exit 0.
- **Current target main is net11 at `c12d6cbc1481f4cda6121b9a4892266ef82d52b0`.** `git merge-tree --write-tree main f78c3bfd` exited **1** with content conflict in `Client.Tests/Client.Tests.fsproj` (framework Upgrade net11 versus new SignalR test compile entry and framework refs). Other files auto-merge; never overwrite main's target framework or Microsoft.AspNetCore.SignalR 11 dependency with branch net10 settings. This merge conflict is a separate **integration gate**: resolve in an approved integration worktree and run full Client/Server net11 Release before merge or deployment. It is not itself claimed as an independently reproduced runtime defect in the net10 branch.

## Blocking P2 correctness finding

See `findings/p2-uncertain-recovery-fabricates-empty-state-evidence.md`.

When a queued `ReceiveCommand` unexpectedly throws after a canonical state mutation, the new `replyUnhandled` correctly says the operation **may have committed**, but creates `AgentCommandResponse.AgentStateChanges=[]` and no activation receipt. `Server/McpTools.fs:368–387` explicitly treats an empty list as definitive and emits it in `jarvis/agent_state_changes` metadata and the public response, so the caller simultaneously sees an uncertain committed outcome and a false confirmed-zero state-change record. Tests exercise worker survival and recovery callbacks, but never a post-commit exception and its actual public MCP metadata. Preserve known evidence or carry an explicit typed uncertain/unknown status, with a mutation-before-throw end-to-end negative test, without conflating legitimate pre-execution overload rejection (confirmed zero changes) with a post-execution unknown state.

This failure is not claimed to have occurred in production; it is a concrete source-path inconsistency in the newly added error recovery and undermines the task's correctness/evidence requirements. Do not approve the frozen source until corrected.

## Conclusion

**RequestChanges**, one P2 blocking finding. Frozen branch shows substantial measurable improvement with all local net10 Release suites passing, but needs truthful post-commit error evidence and a fresh frozen independent review. Independently, the branch also needs a coordinated net11 merge resolution/integration regression suite before shipping. No source edits, merges or deployments performed by Reviewer 1.
