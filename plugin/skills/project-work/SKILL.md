---
name: jarvis-project-work
description: Use Jarvis to inspect, edit, test, and commit code in projects exposed by the user's authenticated local Jarvis client.
---

Use Jarvis when the user asks to work directly on a project exposed by their local Jarvis client.

Prefer Jarvis's structured project, file, search, git, task, and job tools over generic process execution. Use a generic command only when no narrower tool fits.

When the project is not already clear, list the available projects first. Read the relevant files and current repository state before making changes.

For targeted edits, prefer patching over replacing an entire file. Use file creation or full replacement when that is the natural operation.

For builds, tests, formatting, and linting, prefer a configured project task when one exists. Otherwise use a bounded command. Use jobs only for work that genuinely needs long-running asynchronous execution.

Before a commit, inspect git status and the relevant diff. Commit only the intended paths and use a message that describes the coherent change.

Do not ask the user for a Jarvis session key. Authentication and routing come from the user's OAuth-linked account and authenticated local Jarvis client.

Treat local Jarvis permission prompts as authoritative. If a local action is denied or requires confirmation, report that state rather than trying to bypass it.
