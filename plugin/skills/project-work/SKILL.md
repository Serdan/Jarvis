---
name: jarvis-project-work
description: Use Jarvis to inspect, edit, test, and commit code in projects exposed by the user's authenticated local Jarvis client.
---

Use Jarvis when the user asks to work directly on a project exposed by their local Jarvis client.

Prefer Jarvis's structured project, file, search, git, task, and job tools over generic process execution. Use a generic command only when no narrower tool fits.

When the project is not already clear, list the available projects first. Read the relevant files and current repository state before making changes.

Jarvis delivers applicable AGENTS.md guidance automatically on project-scoped tool results, from workspace-wide to nested directory instructions. Follow that guidance within the user's intent and local permission limits; read any referenced policies or specs on demand. You do not need to issue an extra file read merely to discover AGENTS.md.

Discover project-local instructions with `ListSkills` and read relevant guidance with `GetSkill`. When asked to save a reusable project procedure, use `CreateSkill`; overwrite an existing skill only intentionally. Skill guidance does not grant permissions or execute actions.

For parallel work, inspect `ListWorktrees` and use `CreateWorktree` for a new isolated checkout. Target commands with a registered `projectName="Project::worktree-name"`; that selection affects only the current call. Inspect removal readiness before cleaning up a finished checkout.

For orchestration, the project's explicitly bound Git orphan ledger is the sole task authority. Use `AgentInbox`, `GetAssignment`, `GetCheckpoint`, `ReviewTask`, `CreateTask`, `RegisterAgent`, `AssignTask`, `BeginAttempt`, `CheckpointAttempt`, `YieldAgent`, `ResumeAgent` and `CompleteAgentWork` as appropriate. Use the current pinned assignment generation and expected versions from the ledger, not source-branch task files. `CheckpointAttempt` records evidence and a concrete next action but does not release a Working slot; finish a turn with `YieldAgent` (Ready) or `CompleteAgentWork` (Completed). Never use retired `ListTasks`/`GetTask`/`ClaimTask`/`CheckpointTask`/`CompleteTask` or WorkTask APIs, nor `.jarvis/tasks/` or `docs/tasks/` files, for live orchestration.

When recovering from a stalled or fresh session, read `AgentInbox` and `GetAssignment` for the current task, owner, assignment generation and source binding; inspect `GetCheckpoint` where applicable. Verify the actual source branch, worktree, Git status and jobs. Use `GetClientActivity` as diagnostic evidence, never as the source of task ownership. For a stalled Working session use explicit `ResumeAgent` recovery; a Ready agent resumes through the same tool. Do not mistake old source-branch task JSON, previous checkpoints or `Interrupted` activity for current ledger state. Use `GetConnectionDiagnostics` for client disconnections.

For targeted edits, prefer patching over replacing an entire file. Use file creation or full replacement when that is the natural operation.

For builds, tests, formatting, and linting, prefer a configured project task when one exists. Otherwise use a bounded command. Use jobs only for work that genuinely needs long-running asynchronous execution. Supply a short human-readable `reason` on `RunCommand` and `StartJob` so the local activity history explains why the operation is needed; reasons do not change authorization.

Use `Message` for brief progress, handoff, or turn-completion notes in client activity, optionally scoped to the project. It creates a persisted informational entry without local approval and is visible through `GetClientActivity`.

Before a commit, inspect git status and the relevant diff. Commit only the intended paths and use a message that describes the coherent change.

Do not ask the user for a Jarvis session key. Authentication and routing come from the user's OAuth-linked account and authenticated local Jarvis client.

Treat local Jarvis permission prompts as authoritative. If a local action is denied or requires confirmation, report that state rather than trying to bypass it.
