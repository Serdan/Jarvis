# P2 — Self-inbox Ready-to-Working transition is not visible in the MCP result

**Frozen commit:** `9033bcdc0213b320b1391d96a6cb305d34ffed50`

## Requirement

The task explicitly requires `agent_inbox` to **show any activation before/after state clearly**, in addition to prominent documentation that reading it can start a Ready assignment.

## Evidence

- `Client/SignalR/Client.fs` `activateReadyOnActivity` recognizes Ready and writes its transition before dispatching the inbox read. `processIncomingCommand` collects only changed **agent IDs** in `implicitChanges`; it does not carry a before/after transition into the inbox payload.
- `Client/Modules/OrchestrationApi.fs` `TaskApi.read` returns `getBranchAgentInbox`, whose payload is the single post-operation canonical state. `AgentInbox` has no `activatedByThisCall`, `previousState`, or equivalent transition field.
- `Server/McpTools.fs` `toCallToolResult` sends only `AgentCommandResponse.Result` as MCP result content. The separate `AgentStateChanges` list is used for SignalR cache invalidation and not surfaced to the caller.
- The new regression proves the internal transition occurred, but it checks `AgentStateChanges` via the in-process transport envelope, not a user-visible MCP `agent_inbox` result.

Consequently, an MCP caller receiving `state: Working` cannot tell whether its current inbox call **just activated** that agent or the agent was already Working. A caller whose read fails after a successful activation receives even less indication of the committed change. The old cross-agent `agent_inbox` footgun remains possible if used contrary to documentation.

## Required change

Expose the **actual committed** Ready -> Working transition in the caller-visible inbox/tool response (e.g. `activation: { activatedByThisCall: true, beforeState: "Ready", afterState: "Working" }`, or an equally clear contract), without inferring a transition from the final Working state alone. A repeated inbox read must explicitly report no new activation. If activation commits but the subsequent inbox read fails, preserve visible mutation evidence instead of silently making the call look wholly unsuccessful.

Add a test at the public MCP response boundary for first activation, repeated read, and committed-activation/read-error behavior. Keep the side-effect-free `observe_agent` response unchanged.

**Severity:** P2 acceptance gap. The new observation operation itself behaves correctly, but a specified lifecycle-visibility guarantee is missing.
