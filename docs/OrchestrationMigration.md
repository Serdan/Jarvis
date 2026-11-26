# Migrating to Git-backed Jarvis orchestration

> **Document status:** This is a migration guide and historical record, not the current MCP API reference. Current public tool names and lifecycle semantics are documented in [`AgentCommandSurface.md`](AgentCommandSurface.md). Dated protocol/deployment observations are maintained in [`ServerUpdates.md`](ServerUpdates.md).

This guide covers the selective migration of a project's active coordination from source-branch task files to the canonical Git-backed ledger. Updating Jarvis itself does not create a ledger or migrate tasks.

## What changes

| Previous workflow | Git-backed workflow |
| --- | --- |
| Task HTML/JSON files, commonly under `docs/tasks/`, in ordinary source branches/worktrees | Canonical task, agent, attempt and review records on `refs/heads/jarvis-orchestration` |
| Different worktrees may contain different copies of coordination state | One verified remote ledger ref is shared across clients and worktrees |
| Legacy source-branch task handlers | Public ledger tools such as `create_task`, `task_overview`, `assign_task`, `agent_inbox`, `checkpoint_attempt`, `yield_agent` and `complete_agent_work` |
| Source branch/worktree can be confused with task ownership | Agent owner is a project-local label; source branch/worktree is separate context |

The old source-branch task implementations are removed, not forwarded. Reused public names operate on the orphan ledger, never on old `docs/tasks/` files. `list_configured_tasks` and `run_project_task` are local command-runner tools; they are **not** replacements for the canonical `task_overview` and task lifecycle APIs.

The orphan branch is never merged into `main` or a source branch, and no source worktree needs to check it out. The server remains stateless; the verified Git remote is the authority. Old task files may remain in Git as historical reference, but there is no automatic importer, synchronization or compatibility fallback.

## 1. Verify the connected client and server

1. Confirm the client responds to `list_projects`; a green SignalR connection alone is insufficient.
2. Inspect `list_devices` and `get_connection_diagnostics` to identify the selected client, its registered state and reported protocol.
3. Call `list_commands` and inspect `ProtocolVersion`. This is a capability/compatibility summary, **not** the actual MCP tool schema. Inspect the active MCP `tools/list` definitions for public method names and argument schemas; see [`AgentCommandSurface.md`](AgentCommandSurface.md).
4. Confirm the client supports the current task/review capability families and can execute a harmless read such as `ledger_health(projectName, agentId)` or `task_overview(projectName, agentId)` for an already bound project.
5. Exercise the migration on a disposable Git remote before initializing an important project's ledger.

Do not start a migration through an out-of-date or unresponsive client. A requested update/restart is not proof that the new client has connected and executed a command.

## 2. Choose the project and inspect its remote

For each project:

1. Choose the writable canonical Git remote (normally `origin`). Inspect both fetch and push URLs and permissions with `git remote -v`.
2. Determine who can read that remote. If it is public, the orphan branch's task descriptions and metadata will be public too. Do not store secrets there.
3. Check for an existing ledger branch:

   ```sh
   git ls-remote --heads origin refs/heads/jarvis-orchestration
   ```

   If it exists, verify and bind it; do not initialize another one. If Git reports a network or authorization error, resolve that first rather than interpreting the result as an absent branch.
4. Inventory only the work worth carrying forward: stable task IDs, current specifications, owners, source branches/worktrees/commits, pending reviews and next actions. Inspect source-side `docs/tasks/`, Git history and durable checkpoints.

Do not migrate every completed, obsolete or cancelled task merely to preserve history. Do not delete or silently rewrite original task files.

## 3. Initialize a new ledger or bind an existing one

**New ledger — only when the remote ref is absent:** use `initialize_ledger(projectName, projectId, ledgerId, epoch, remoteName, approvedFetchUrl, approvedPushUrl, acknowledgeVisibility; optional operationId, deviceId)` with fresh stable project/ledger UUIDs, a positive epoch, the independently inspected remote identity, explicit visibility acknowledgment and a caller-generated stable operation UUID. This is an approved Git write; it is not a side effect of reading a project.

**Existing ledger — including another machine or checkout:**

1. Inspect the actual remote branch and independently verify its trusted commit.
2. Obtain the same project UUID, ledger UUID and epoch from the initialized ledger; do not generate replacements.
3. Use `bind_existing_ledger(projectName, projectId, ledgerId, epoch, remoteName, approvedFetchUrl, approvedPushUrl, expectedTrustedHead, acknowledgeVisibility; optional operationId, deviceId)` with those IDs, the verified remote identity and expected trusted head. Supply explicit visibility acknowledgment and a new stable operation UUID.
4. Check `ledger_health(projectName, agentId)` on each participating client and verify the same canonical project/ledger/epoch and compatible Git ancestry.

The local trust binding is per checkout/client configuration, keyed by the absolute project-root path. A different checkout or computer may require an explicit bind. A missing binding must cause canonical ledger tools to fail; it must never fall back to V1 task files.

Do not manually create or merge the orphan branch by checking it out in a source worktree.

## 4. Recreate only active tasks

For each task that still needs work:

1. **Create the current specification.** Use `create_task(projectName, agentId, title, html; optional taskId, operationId, deviceId, manualGrantCredential)` with the latest intent, acceptance criteria and a brief reference to the historical task file/branch. Omit `taskId` to derive a stable ID from `operationId`; reuse the same operation ID on retry. Do not create duplicates.
2. **Register agent labels.** Use `register_agent(projectName, agentId; optional operationId, deviceId)` for intended workers/reviewers. An agent ID is a project-local slot label, not an authentication principal.
3. **Assign ownership.** Use `assign_task(projectName, taskId, attemptId, agentId; optional operationId, deviceId)` to assign one registered, unoccupied agent to an Open task. Reviews are independent; an implementation attempt does not contain a reviewer.
4. **Read the canonical assignment.** The worker uses its own `agent_inbox(projectName, agentId)`. A Ready assignment may be activated into Working by this self-inbox call. Use `get_assignment(projectName, agentId, taskId, assignmentGeneration)` to inspect the immutable generation, pinned spec and instructions. `observe_agent(projectName, agentId, targetAgentId)` is the read-only path for inspecting another agent.
5. **Do not call a retired resume tool.** There is no public `resume_agent`. The self-inbox is the ordinary way for an assigned worker to pick up a Ready assignment. A PascalCase `ResumeAgent` may appear in legacy wire records/replay compatibility; it is not a public MCP method.
6. **Persist progress with the correct lifecycle operation.** `checkpoint_attempt` records immutable progress and leaves the worker Working. `yield_agent` records a checkpoint and continuation instruction, then returns the assigned worker to Ready. `complete_agent_work` records a completion checkpoint and releases the assignment; it does **not** close the task.
7. **Recover or defer deliberately.** Use `recover_agent_assignment` only for explicit recovery of an interrupted assignment; it records a checkpoint and creates a new assignment generation. Use `defer_attempt` to release a Queued/Started attempt as Deferred or Blocked, and `resume_attempt` to explicitly requeue a Deferred/Blocked attempt with a new generation. These operations are not interchangeable with ordinary yield or completion.

Read versions and generations from the latest canonical ledger. Never guess. Where a tool accepts optional zero-valued expected versions, zero means resolve current ledger state; a supplied nonzero value is a strict assertion. Some administrative operations require exact positive versions. Follow each live MCP tool schema.

Use a new operation UUID for each new logical mutation. When retrying the same uncertain operation, reuse the original UUID and unchanged parameters. Reconcile its outcome before issuing a different mutation. Provider session IDs are transport-managed; do not invent them.

## 5. Task, review and source lifecycles are independent

Jarvis provides coordination records and operations; it does not impose one universal implementation/review/merge workflow.

- `get_task`, `task_overview`, `agent_inbox` and `task_lifecycle_history` read canonical task/agent state. `assign_task` and `reissue_assignment` record ownership/instructions. `complete_agent_work` releases the worker's assignment; `close_task` separately closes the task. If a task is closed with an outstanding assignment, use the explicit `abandon_attempt` operation to release it without reopening the task.
- `submit_review` publishes an immutable review snapshot. `reviewer_inbox`, `claim_review`, `release_review`, `decide_review`, `withdraw_review`, `review_details`, `list_review_documents`, `read_review_document` and `put_review_document` manage independent review records and findings. Claims and verdicts affect the review, not the task or worker assignment.
- Multiple reviews may coexist for one task. An owner transfer, cancellation or task closure does not automatically withdraw a review. Releasing a claim leaves the same snapshot available to another reviewer.
- Source branches, rebases, commits and worktree changes are ordinary Git operations. `set_attempt_source` records optional attempt source context; it is not a review verdict or an integration gate. Review snapshots retain their own exact commits.
- Integration is separate again: `submit_integration`, `complete_integration` and the related read tools record exact reviewed/integrated commits. A review verdict alone does not merge or integrate source code.

There is no automatic "final review" gate on `close_task`. The review namespace under `tasks/<id>/reviews/` is an indexing convention, not a task ownership or lifecycle dependency.

## 6. Verify the migration

Before considering a project migrated, verify:

- The canonical orphan ledger is bound and `ledger_health` reports the expected project/ledger identity and trusted head.
- The relevant active tasks have been recreated from current specifications, with the intended registered owners.
- An assigned worker can read its inbox, checkpoint, yield, resume from Ready by self-inbox, and complete; state survives a client restart.
- Deferred/Blocked attempts and interrupted Working assignments are handled through their distinct explicit recovery operations.
- Reviews can be submitted, claimed and decided without automatically changing task ownership or merging source code.
- The orphan branch remains separate from source branches.

Optional read-only Git inspection without switching branches:

```sh
git fetch origin refs/heads/jarvis-orchestration
git ls-tree -r --name-only FETCH_HEAD
git show FETCH_HEAD:ledger.json
# For an existing task:
git show FETCH_HEAD:tasks/TASK_ID/task.json
```

Use the public ledger tools for live writes. Do not push hand-edited ledger JSON or force-push the orphan branch.

## 7. Stalls, retries and failed cutovers

- **Stalled Working assignment:** inspect canonical state and the latest checkpoint before any recovery. Ordinary checkpointing does not end the Working turn. An explicit `recover_agent_assignment` changes the assignment generation; do not use it merely to refresh activity.
- **Deferred or Blocked attempt:** use `resume_attempt` with exact task/attempt/agent versions and generation. It is not a Working-session recovery tool.
- **Uncertain write:** inspect `get_operation_status(projectName, agentId, operationId)`, `pending_operations(projectName, agentId)` and `reconcile_pending_operations(projectName, agentId)`. A verified `Applied` receipt confirms success. `NotFoundAtVerifiedHead` does not prove that a timed-out push can never arrive. Reuse the original UUID and unchanged command when retrying. `acknowledge_pending_operation(projectName, agentId, operationId, expectedRequestHash)` requires the exact verified request hash for a matching applied pending record. For an unresolved outcome, do not replay blindly or edit canonical task state just to clear a journal entry.
- **Stale version or unavailable remote:** reread canonical state and stop speculative writes. Never let a timer automatically reassign a task.
- **Migration failure:** stop new ledger writes and preserve Git history and pending-intent records. Historical source-branch task files are not a synchronized fallback.

A project is migrated when its canonical ledger is bound, relevant active tasks are recreated, new coordination writes use the ledger tools, and participating clients agree on canonical state. Updating Jarvis alone does not complete this per-project step.

## Historical protocol notes — not the current public tool list

The following notes preserve migration context. Version labels refer to historical changes; they are **not** instructions to call old tools or proof of the current deployment protocol.

- **Protocols 3.45–3.47:** the old source-branch task handlers were replaced by the orphan-ledger workflow and unprefixed capability families. Version-prefixed internal command categories and the old V2 integration channels are historical.
- **Protocol 3.46:** the ledger moved to orthogonal agent/attempt state. The one-time `MigrateOrthogonalAgentState` tool was removed; current projects must already use the current ledger schema. Do not attempt that historical migration command.
- **Protocols 3.50–3.51:** public `acknowledge_assignment`, `revise_task_spec_pinned`, `cancel_open_task` and `adopt_task_spec` were retired. Their behavior, where still supported, is folded into current task tools such as `revise_task_spec` and `reissue_assignment`; historical internal ledger actions remain in old records.
- **Protocols 3.52–3.54:** public `park_attempt`, `block_attempt`, `bind_attempt_source` and `rebind_attempt_source` were consolidated into `defer_attempt` and `set_attempt_source`. The old PascalCase wire/ledger action names may remain in history.
- **Protocol 3.53:** the separate public `replace_review` tool was folded into `submit_review` for replacing an unclaimed review snapshot. Claimed reviews remain immutable.
- **Protocol 3.56:** `review_task` became `get_task`; `list_configured_tasks` remains distinct from canonical `task_overview`. The one-time orthogonal-state migration tool was removed.
- **Protocol 3.58:** self-inbox activation was introduced and the public `resume_agent` tool was removed. The internal `ResumeAgent` action is historical/replay compatibility, not a current MCP tool.
- **Protocol 3.63:** `observe_agent` added side-effect-free cross-agent observation. It remains distinct from self `agent_inbox`, which may activate a Ready assignment.

Historical PascalCase names such as `ResumeAgent`, `ParkAttempt`, `BlockAttempt`, `BindAttemptSource`, `RebindAttemptSource`, `AdoptTaskSpec`, `ReplaceReview`, `AcknowledgeAssignment` and `ReviewTask` are internal/retired names, not current public MCP method names. Use the lower-snake-case names documented in [`AgentCommandSurface.md`](AgentCommandSurface.md).

See the [orchestration architecture](specs/orchestration-architecture.md) for the normative ledger model.
