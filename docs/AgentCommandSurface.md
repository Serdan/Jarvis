# Jarvis MCP command surface

This is the concise reference for the **current public MCP surface**. Public tool names are lower snake case; JSON arguments use the names and types exposed by the active MCP server.

The repository currently declares client command protocol **3.68** in [`Common/Messages/AgentCommand.fs`](../Common/Messages/AgentCommand.fs). The dated live observation for the connected server and selected device is recorded in [`ServerUpdates.md`](ServerUpdates.md). That observation is not a guarantee about every registered device or deployment.

## Which catalog is authoritative?

Do not conflate these three interfaces:

1. **MCP tool definitions** are the actual public API: tool name, input schema, required and optional parameters, defaults, and description. Inspect the active server's MCP `tools/list` result. In this repository, the corresponding `[<McpServerTool>]` method signatures and attributes in [`Server/McpTools.fs`](../Server/McpTools.fs) define those tools. If a connected tool definition and source disagree, record the discrepancy rather than guessing.
2. **The public MCP tool `list_commands`** takes no user arguments and returns a server-generated `ListCommandsResult` containing `ProtocolVersion` and `Commands`. Each capability descriptor has `Name`, `Description`, `Permissions`, `MutatesState`, `RequiresConfirmation`, `SupportsDryRun`, `MaxInputBytes`, `MaxOutputBytes`, `InputSchemaJson` and `OutputSchemaJson`. The list is assembled from the shared `AgentProtocol.capabilities` catalog (with internal capability families filtered), ledger alias descriptors and server-local descriptors; it is useful for compatibility and permission diagnostics, **not** a substitute for MCP `tools/list`. In the observed protocol-3.68 response, the schema fields were null. A capability name or description therefore does not establish a public MCP method name or argument schema.
3. **The client wire command `ListCommandsCommand`** returns `AgentProtocol.listCommandsResult` from the client. It exposes the client's internal command/capability names; these PascalCase wire names and command records are not public MCP tool names. This is distinct from the server-side MCP tool `list_commands`.

The implementation of the server-side capability result is in `Server/McpTools.fs` (the `mcpListCommandsResult` and `listMcpCommands` definitions); the client protocol version and capabilities are in `Common/Messages/AgentCommand.fs`. The response/evidence contract is described by [`AgentCommandResponse.schema.json`](AgentCommandResponse.schema.json).

## Identity, project and checkout selection

- `projectName` identifies the logical Jarvis project.
- On agent-attributed tools, `agentId` is a project-local actor label, **not** an authentication principal. OAuth identity and the user's permissions provide authority. Do not use an agent label to infer identity or permission.
- `deviceId` is optional on many tools. When omitted, the server uses the conversation's selected device/default routing.
- Checkout-oriented file/search/Git-status/diff/commit and execution tools expose optional `worktreeName` to select a registered Git worktree. Omit it for the main checkout. Worktree-management tools have their own explicit name/path arguments. Keep `projectName` as the logical project name; do not encode a worktree in `projectName`. Ledger coordination tools are project-scoped and do not take `worktreeName`.
- The internal `Project::Worktree` selector is an implementation compatibility detail, not the public parameter to use when the MCP schema exposes `worktreeName`.

## Workspace and development tools

The current surface is broader than the original local-file bridge. Common public tool families include:

| Purpose | Public MCP tools |
| --- | --- |
| Project discovery | `list_projects`, `get_project_details`, `create_project` |
| Files and repository guidance | `list_directory`, `search_files`, `search_text`, `read_file`, `read_file_page`, `read_files`, `get_agent_instructions`, `read_image`, `export_file` |
| File changes and local execution | `write_file`, `patch_file`, `run_command`, `run_project_task`, `import_file` |
| Git and worktrees | `get_git_status`, `get_git_diff`, `git_commit`, `list_worktrees`, `get_worktree`, `create_worktree`, `inspect_worktree_removal`, `remove_worktree` |
| Asynchronous local jobs | `start_job`, `list_jobs`, `get_job_result`, `cancel_job` |
| Project skills | `list_skills`, `get_skill`, `create_skill` |
| Client and server maintenance | `update_client`, `update_server`, `get_server_update_status` |

This is a navigation aid, not a generated exhaustive tool list. Use the active MCP `tools/list` schema for exact arguments and defaults. In particular, `list_configured_tasks` and `run_project_task` concern locally configured executable tasks; they are not the canonical Git-ledger task APIs.

## Canonical task, agent and review tools

Canonical coordination state is stored in the project's bound remote Git ledger. The main public task tools include `create_task`, `revise_task_spec`, `get_task`, `task_overview`, `get_task_spec_page`, `diff_task_spec`, `assign_task`, `reissue_assignment`, `close_task`, `cancel_task`, `reopen_task` and `task_lifecycle_history`.

### Agent observation and assignment

- `agent_inbox(projectName, agentId, deviceId?)` is **self-inbox only**. Reading your own inbox may activate your Ready assignment into Working and update last-seen. The returned state is the canonical state after any activation.
- `observe_agent(projectName, agentId, targetAgentId, deviceId?)` is the read-only way to inspect another registered agent in the same project. It does not activate Ready assignments, update last-seen or expose private notification bodies.
- `get_assignment(projectName, agentId, taskId, assignmentGeneration, deviceId?)` reads an immutable assignment generation, including its pinned specification and instructions. Older generations remain readable after reissue or recovery.

### Lifecycle mutations

| Public tool | Required arguments | Semantics |
| --- | --- | --- |
| `checkpoint_attempt` | `projectName`, `taskId`, `agentId`, `summary`, `nextAction` | Writes an immutable checkpoint for the current Working assignment. **Does not end the Working session or release the assignment.** |
| `yield_agent` | `projectName`, `taskId`, `agentId`, `summary`, `nextAction` | Ends the current Working assignment segment, records a checkpoint and continuation instruction, and transitions the assigned agent to Ready. |
| `complete_agent_work` | `projectName`, `taskId`, `agentId`, `summary` | Records a completion checkpoint and releases the current assignment. It does not itself close the task. |
| `recover_agent_assignment` | `projectName`, `taskId`, `agentId`, `expectedAttemptVersion`, `expectedAgentVersion`, `assignmentGeneration`, `reason` | Explicit administrative recovery of an interrupted assignment. Records a checkpoint and issues a new assignment generation. |
| `defer_attempt` | `projectName`, `agentId`, `taskId`, `expectedTaskVersion`, `expectedAttemptVersion`, `expectedAgentVersion`, `assignmentGeneration`, `reason` | Defers a Queued/Started attempt and releases its matching agent assignment. Optional `disposition` is `Deferred` (default) or `Blocked`. |
| `resume_attempt` | `projectName`, `agentId`, `taskId`, `expectedTaskVersion`, `expectedAttemptVersion`, `expectedAgentVersion`, `assignmentGeneration`, `reason` | Explicitly requeues a Deferred/Blocked attempt with a new generation and makes its unoccupied owner Ready. It is not a general-purpose resume for a Working or Ready agent. |

Additional lifecycle context is available through `get_checkpoint`, `task_lifecycle_history`, `inspect_attempt_source` and `get_task`.

### Administrative lifecycle controls

- `reissue_assignment(projectName, taskId, agentId, expectedAttemptVersion, expectedAgentVersion, assignmentGeneration, instructions, ...)` updates an unstarted Ready assignment; it cannot rewrite a Working assignment. Optional `newSpecHtml` and the pair `expectedCurrentSpecBlobOid` + `expectedTaskVersion` are mutually exclusive.
- `reissue_queued_attempt(projectName, taskId, agentId, expectedAttemptVersion, expectedAgentVersion, assignmentGeneration, reason)` explicitly assigns a queued attempt to an unoccupied agent. It does not preempt a current Ready or Working assignment.
- `abandon_attempt(projectName, agentId, taskId, expectedTaskVersion, expectedAttemptVersion, expectedAgentVersion, assignmentGeneration, reason)` ends an implementation attempt and releases its assignment if occupied, retaining historical reviews and leaving other agents' work unchanged. An active task returns to Open.
- `set_agent_state(projectName, agentId, targetAgentId, state, expectedAgentVersion, reason)` administratively parks/unparks only an idle, unassigned agent. `state` is exactly `NeedsWork` or `Parked`.
- `correct_agent_working_state(projectName, agentId, targetAgentId, taskId, attemptId, expectedTaskVersion, expectedAttemptVersion, expectedAgentVersion, assignmentGeneration, reason)` is an operator correction of one exact assigned Working agent to Ready. It does not create a worker checkpoint or reissue the assignment, and is not a substitute for ordinary `yield_agent`.

### Source context and version preconditions

- `set_attempt_source(projectName, taskId, agentId, expectedAttemptVersion, assignmentGeneration, sourceRef, ...)` records the attempt's source branch. Initial binding requires **both** `baseOid` and `targetRef` and verifies Git ancestry (detached/uncommitted sources are disallowed unless the corresponding opt-in is supplied); a later branch change omits both and preserves the pinned baseline. It does not replace an independent review snapshot.
- `checkpoint_attempt` accepts optional `sourceCommit` and owner-reported `evidence`; evidence is not independently verified. If supplied, `sourceCommit` must be an available lowercase 40- or 64-character Git commit SHA. The current schema bounds `summary`, `nextAction` and `evidence` to 8192 UTF-8 bytes each.
- On `checkpoint_attempt`, `yield_agent` and `complete_agent_work`, omitted/zero `expectedAttemptVersion`, `expectedAgentVersion` and `assignmentGeneration` resolve the current ledger snapshot. A supplied nonzero value is a strict assertion. Other tools may require exact positive versions; follow the actual tool schema and never guess.
- Use a new caller-stable `operationId` for each new logical mutation. When retrying an uncertain operation, reuse its original ID and unchanged parameters; reconcile before issuing a different mutation.

### Lifecycle evidence and errors

A requested action is not proof that a transition happened. A mutation rejected before commit does not change lifecycle state. Establish success from the tool result and canonical ledger state.

The command response distinguishes `AgentStateChangesStatus` as `confirmed`, `unknown` or `pending`. `AgentStateChanges` is complete only when confirmed; `KnownAgentStateChanges` is a known subset under an unknown outcome, not a complete list. `PendingJob` is an in-flight operation, not a terminal receipt.

There is one important self-inbox nuance: `agent_inbox` can commit Ready-to-Working activation before its subsequent inbox read fails. An error on the overall inbox call therefore does not, by itself, prove that no activation occurred. Inspect `InboxActivation`/the state-change status and reconcile with canonical state. For uncertain or pending writes, use `get_operation_status`, `get_job_result` and the applicable ledger reads rather than guessing.

### Other coordination families

- Independent reviews use `reviewer_inbox`, `review_details`, `submit_review`, `claim_review`, `release_review`, `decide_review`, `withdraw_review`, `list_review_documents`, `read_review_document` and `put_review_document`. Review claims and verdicts do not implicitly change task ownership, agent execution or source integration.
- Taskless activity has separate `start_taskless_work`, `yield_taskless_work` and `recover_taskless_work` operations; it does not claim an implementation task.
- The separate Work queue exposes `list_work`, `get_work`, `connect_work`, `claim_next_work`, `checkpoint_work`, `complete_work` and `set_work_priority`. Live Work mutations remain default-off in this build; tool presence does not imply that a write/launch path is enabled. Do not conflate Work records with agent assignments.

## Historical names

The following PascalCase names are historical/internal wire or ledger action names, **not current public MCP tools**: `ResumeAgent`, `BindAttemptSource`, `RebindAttemptSource`, `ParkAttempt`, `BlockAttempt`, `AdoptTaskSpec`, `ReplaceReview`, `AcknowledgeAssignment`, `ReviewTask` and `MigrateOrthogonalAgentState`. Current public equivalents, where one exists, are documented above in lower snake case. In particular, there is no public `resume_agent`; do not substitute `resume_attempt` for it.

See [`OrchestrationMigration.md`](OrchestrationMigration.md) for the historical ledger cutover and [`ServerUpdates.md`](ServerUpdates.md) for dated client/server protocol observations.
