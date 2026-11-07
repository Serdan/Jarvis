# [P2] Broadcast misses implicit Ready->Working transitions on source calls

**Locations:** `Client/SignalR/Client.fs:103-157,583-621`; `Server/Services/ClientService.fs:81-85`; `Server/McpTools.fs:659-668`.

`activateReadyOnActivity` runs **before execution** for nearly every attributed project operation (`activatesAgent` defaults to `true` for source tools such as `ReadFileCommand`, `GetGitStatusCommand`, `GetAgentInstructionsCommand`, etc.). If an agent is Ready, that routine commits a Ready->Working transition.

The server broadcasts invalidation only for successful `TaskWriteCommand` and `TaskReadCommand` whose Action is `AgentInbox`. Ordinary `read_file` is not eligible, even when it **actually started a new Working turn**. Thus the operation updates the canonical ledger, but other clients with a warm Ready-state snapshot receive no hint and can keep using the stale state for up to two minutes. This can delay new-turn guidance and instruction delivery on non-activating reads (ReviewRead or LedgerRead commands). A second source call may force a redundant activation attempt before correcting the state. The defect also applies when a source operation activates the agent and then fails path validation; server filtering on `response.Error.IsNone` misses the committed state change.

The cross-client regression in `Client.Tests/ReviewWorkflowTests.fs:2150-2153` **manually invokes** `invalidateAgentStates` after yielding. It doesn't exercise implicit activation broadcast from the server. `Server.Tests/ServiceTests.fs` only tests the command classifier in isolation.

**Request:** Have the actual transition site convey structured `agent-state-changed` information to the server for broadcasting, including when the ordinary operation subsequently fails, and add a regression: B caches Ready; A's first `read_file` starts Working; B receives invalidation before its next tool call. Cover cross-project sender-inbox activation similarly.
