# Explicit Agent Identity and Activity Attribution

Status: **Approved design; not implemented**  
Task: `task-cada37e778e54b558e362158f069db22`  
Scope: Jarvis public MCP tools, server-to-client command transport, TUI activity tracking, documentation and tests.

## Decision

Jarvis agents are **ephemeral named work slots**, not separate security principals. All agents use the same authenticated connection and have the same capabilities. Agent identity is a coordination and display convention, not an authorization or audit boundary.

1. Standardize a single relevant agent identity field as **`agentId`** on the public MCP surface. Replace single-agent public parameters `ownerId`, `actorId`, and `reviewerId` with `agentId`.
2. Require `agentId` on **agent-initiated operations**, including normal development tools (file operations, shell commands, Git, task and review operations). These calls update that agent's TUI `Seen` timestamp.
3. Leave genuinely actor-independent operations usable without an agent, notably initial registration, browsing/discovery, and administrative/status actions not performed on an agent's behalf. Document the exact exceptions in the catalog rather than silently making a mandatory field optional.
4. Where **two distinct agents are inherently referenced**, keep meaningful names, such as `fromAgentId` and `toAgentId` for notifications. Do not add a separate caller/initiator ID for single-agent operations. For `assign_task` and `resume_agent`, `agentId` denotes the assigned/resumed agent, regardless of who submitted the call.
5. Do not infer agent identity from session metadata; do not require provider-specific conversation/session IDs. Do not invent ownership, access controls, or durable auditing from agent labels.
6. Centralize `Seen` updates at the incoming command boundary, not in 100 tool implementations. Record an incoming accepted/recognized agent-attributed invocation, **including one whose execution fails**, because `Seen` means activity, not success. Do not count calls rejected before a recognizable agent identity can be extracted (e.g. malformed JSON or requests rejected before dispatch).
7. Retain ledger versioning, task/attempt validation, device routing, and existing authorization based on the authenticated user and OS permissions. An explicit `agentId` must not grant new privileges.

## Existing implementation to replace

- `Server/McpTools.fs` `RequestDeviceRouting.withMetadata` reads the **OpenAI-only** `request.Meta["openai/session"]`; `ClientService.SendCommandToUser` forwards it as `sessionId`.
- `Client/SignalR/Client.fs` maintains `workingSessions`, identifies some commands through `workingActor`, and resolves sessions against the ledger via `checkWorkingGuidance`. `RecordAgentSeen` is currently called from this heuristic session path.
- `Client/Modules/ConsoleTui.fs` persists `agentSeen` in `agent-seen.json`, keys values by `project + "::" + agentId`, and renders relative `Seen` from `AgentView.lastSeenAt`. Preserve these useful presentation/persistence pieces; remove only session-dependent attribution.
- Ledger records use internal role-specific fields such as `OwnerAgentId`. They need not be renamed: a public MCP argument rename is **not** a request to rewrite the Git ledger schema.

## Public MCP schema migration

Perform a **complete catalog inventory** before implementation and classify each command by whether it is (A) agent-attributed, (B) agent-independent, or (C) inherently multi-agent.

Representative renames confirmed in `Server/McpTools.fs`:

| Current public field | New public field | Affected methods (examples) |
| --- | --- | --- |
| `ownerId` | `agentId` | `AssignTask`, `ReissueAssignment`, `CheckpointAttempt`, `SetAttemptSource`, `AcknowledgeReviewFeedback`, `YieldAgent`, `ResumeAgent`, `CompleteAgentWork`, `ReissueQueuedAttempt`, `RecoverAgentAssignment` |
| `newOwnerId` | `agentId` | `TransferOwner` (target of ownership transfer) |
| `actorId` | `agentId` | `SubmitIntegration`, `CompleteIntegration`, `PutReviewDocument`, `ReleaseReview`, `WithdrawReview` |
| `reviewerId` | `agentId` | `ReviewerInbox`, `ClaimReview`, `DecideReview` |
| `agentId` | unchanged | `AgentInbox`, `RegisterAgent`, `AcknowledgeNotification`, `ParkAgent` |
| `fromAgentId`, `toAgentId` | unchanged | `NotifyAgent` (genuine sender/recipient distinction) |

Apply `agentId` consistently to agent-initiated project/file/search/read/write/command/Git/job/skill/review utilities as well as state transitions, including read-only and failed calls. Where an operation's existing parameter identifies the *subject* rather than the caller (e.g. fetching another agent's inbox), retain or explicitly define a different subject parameter **only when the distinction is essential**. Avoid introducing a redundant `actorId` just to record initiators.

Keep `projectName`, `taskId`, `attemptId`, `deviceId`, `operationId`, `requestId`, `reviewId`, etc. unchanged. `deviceId` is routing, not identity. Do not confuse `agentId` with OAuth user ID.

### Boundary-level flow

1. Validate/deserialize the MCP tool call and extract its declared `agentId` centrally according to the catalog classification.
2. Forward the identity as explicit transport data (one field, not a provider session). Avoid hardcoding field-name interpretation separately in the SignalR handler and TUI.
3. On reception, record `RecordAgentSeen(projectName, agentId)` once per recognized agent-attributed call **before operation outcome is known**, after normal routing/validation and without bypassing permissions.
4. Process the command normally; a failed command still counts as seen. Status queries with no agent identity do not modify `Seen`.
5. Reuse the existing TUI state and persistence format unless an actual deficiency is demonstrated.

`Seen` is a local timestamp reflecting **last observed Jarvis activity**. It is not the agent's `Working` state age, a heartbeat, an authentication claim, or a guarantee of successful work. Timeouts, lost requests before reaching the receiving client, and external activity outside Jarvis do not update it.

### Separating unrelated session functionality

The OpenAI session value currently also supports **conversation-specific device selection** and working-turn guidance. Removing it for **agent identity** must not accidentally break device selection. Treat these as separate concerns:

- Stop requiring or consulting `openai/session` for agent attribution/`Seen`; remove associated `workingSessions` and ledger session lookup *only where no other feature needs them*.
- Preserve explicit `deviceId` routing and user-wide fallback. Independently decide whether ChatGPT-specific conversation-device selection remains as an optional feature; it must not gate agent activity tracking.
- If working-turn guidance relies on a session to detect long turns, explicitly redesign that feature around the ledger state / configured timing or state a separate retention rationale. Do not silently remove time-limit behavior.
- Do not silently rewrite historical ledger `AssociatedSessionIds` fields. Evaluate removal/migration separately after checking retained compatibility requirements.

## Rollout

This changes public MCP argument names and increases required parameters for development operations. It therefore **breaks existing agent calls and cached tool schemas**.

Implement coordinated client/server changes, update command catalog/schema metadata, tests, agent instructions, examples, and any relevant client integrations. Deploy **only after a new explicit operator approval**. Refresh/reconnect the MCP plugin so agents receive the new schemas. No indefinite `ownerId`/`actorId`/`reviewerId` aliases or dual-schema compatibility layer is required; use a documented, deliberate cutover.

Agent-independent bootstrap and agent registration remain possible without a pre-existing agent ID. Preserve stable ledger task/attempt identifiers across the migration.

## Acceptance criteria

- The documented single-agent public MCP tools expose `agentId` rather than `ownerId`, `actorId`, or `reviewerId`.
- Agent-associated file, Git, command, review, and task calls all update `Seen` without MCP session metadata.
- A failed, recognized agent-associated call updates `Seen`; an unrelated request with no agent does not.
- `resume_agent` updates the target agent's `Seen` using its explicit `agentId`, regardless of the initiating provider; `assign_task` works analogously.
- ChatGPT, Gemini, and other MCP clients behave identically when supplying the same explicit identity; no provider-specific key names or session-ID parsing.
- Two-agent notifications keep unambiguous sender/recipient semantics.
- Cross-project agent keys, persistence after restart, and rapid-call persistence throttling continue to behave correctly.
- Permission failures, missing required identity, malformed inputs, and stale ledger versions are tested.
- Device routing, assignment state transitions, notifications, reviews, working-turn guidance and the task ledger remain functional; relevant test suites pass in Release.
- Documentation explicitly states that labels do not constitute authentication/authorization and are not a high-assurance audit trail.

## Implementation guidance

Favor one shared agent-identity extraction/propagation design with well-defined exceptions over hand-maintained duplication. Avoid adding a second identity system. Complete and review the API inventory before modifying tool signatures. Prioritize optimal maintainability over quick backwards-compatible scaffolding.
