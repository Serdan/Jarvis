# P2 — Uncertain client exception recovery emits authoritative empty agent-mutation evidence

**Frozen:** `f78c3bfd17fabd37790c50190043cd513a5e6cc1`, task `task-b0e62987fa03497a809a7d1bdd700ae4`.

## Exact source path

1. The new bounded dispatcher in `Client/SignalR/CommandDispatch.fs` catches an exception thrown by an admitted `QueuedCommand.Execute` and calls `OnUnhandled error` with the original correlation. This can happen after the underlying command has already committed a ledger write or Ready→Working transition but before its ordinary response was returned/sent.
2. `Client/Program.fs` `replyUnhandled` (new code near lines 209–235) explicitly warns: **“the outcome may be committed. Verify the original operation ID before retrying a mutation.”** However the very same fallback constructs `AgentCommandResponse` with `AgentStateChanges = []` and `InboxActivation = None`, irrespective of what the crashed command may have done.
3. `Server/McpTools.fs:368–387` documents an empty array as **explicit canonical mutation evidence** and `attachStateEffects` adds `jarvis/agent_state_changes: []` to public tool metadata and the response body, including error replies. Thus the new recovery path simultaneously states “write may have committed” but gives a definitive zero-change list. A genuine agent activation or state change can have happened without being surfaced, and `ClientService`/MCP invalidation will not know which project/agent changed. The original operation ID is useful for reconciliation, but is **not itself proof of no state change**.
4. `Client.Tests/SignalRDispatchTests.fs` checks that the queue's `onUnhandled` callback ran and that the worker survived, not the actual `replyUnhandled` response shape or the public MCP metadata after a partial commit. Existing `Server.Tests` changes only test correlation timing/abort.

## Required change and regression

Treat unknown canonical state effects distinctly from a **confirmed empty list**. Do not fabricate an authoritative empty `AgentStateChanges` in the unexpected-exception path. Either preserve known committed evidence or carry an explicit typed `unknown/uncertain` status through transport and `McpToolHelpers.attachStateEffects` (including versions/recovery or original operation ID as applicable), without misleading the caller or suppressing required state invalidation. Where mutation has definitely not executed (queue overflow), the empty list is correct and must remain different.

Add an end-to-end failure-injection regression: a command commits a Ready→Working transition or other agent-state mutation, then throws before reply serialization; recovery must retain the original correlation and communicate *unknown or confirmed* mutation state truthfully, not `[]` as authoritative zero. Also verify retry/status guidance uses the exact original operation ID when available. The test must verify public MCP reply metadata and no duplicate execution. Keep unrelated healthy-command behavior and shutdown handling.

**Severity P2 (blocking correctness for this P2 task):** uncertain-outcome semantics and state-change provenance are explicit acceptance requirements. This is a source-path proof, not a claimed observed production mutation loss.
