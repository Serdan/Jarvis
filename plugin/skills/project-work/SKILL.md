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

For canonical HTML tasks, use `ListTasks`, `GetTask`, `CompareWorktreeTasks`, `CreateTask`, `ClaimTask`, `CheckpointTask` and `CompleteTask` instead of manually editing JSON or staging task files. Jarvis commits task state on the selected branch. Include verification and a concrete next action in checkpoints. The older `AttachWorkTask` and `CheckpointWorkTask` commands remain compatible: HTML tasks use repository JSON, while Markdown checkpoints preserve their existing local state until converted.

When recovering from a stalled or fresh session, always inspect both persisted work-task state and recent client activity. Use `ListWorkTasks` and `GetWorkTask` for durable intent, task text, checkpoint, and next action; use `GetClientActivity` for the recent execution trail, including commands, messages, attempts, and interruptions. Neither source replaces the other. Reconcile both before continuing, then verify the current files, git state, and jobs so stale checkpoints or interrupted activity are not mistaken for the repository's actual state. Activity is restored after client restarts; `Interrupted` records do not establish an operation's final outcome. Use `GetConnectionDiagnostics` when investigating a disconnected client.

For targeted edits, prefer patching over replacing an entire file. Use file creation or full replacement when that is the natural operation.

For builds, tests, formatting, and linting, prefer a configured project task when one exists. Otherwise use a bounded command. Use jobs only for work that genuinely needs long-running asynchronous execution. Supply a short human-readable `reason` on `RunCommand` and `StartJob` so the local activity history explains why the operation is needed; reasons do not change authorization.

Use `Message` for brief progress, handoff, or turn-completion notes in client activity, optionally scoped to the project. It creates a persisted informational entry without local approval and is visible through `GetClientActivity`.

Before a commit, inspect git status and the relevant diff. Commit only the intended paths and use a message that describes the coherent change.

Do not ask the user for a Jarvis session key. Authentication and routing come from the user's OAuth-linked account and authenticated local Jarvis client.

Treat local Jarvis permission prompts as authoritative. If a local action is denied or requires confirmation, report that state rather than trying to bypass it.
