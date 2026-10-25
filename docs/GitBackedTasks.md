# Git worktrees and repository task files

Jarvis uses the local Git repository to manage worktrees and task state. These commands are introduced with agent protocol 3.14. All operations run on the authenticated client and use the normal local permission policy. The server routes native MCP commands.

## Worktrees

For a project `Wayfold`, use `ListWorktrees("Wayfold")` to discover every Git-registered worktree, including ones already in sibling directories. The response identifies each checkout's path, branch, HEAD, main checkout, detached/locked/prunable status, and observed working-tree cleanliness.

`CreateWorktree("Wayfold", "input-config", "feature/input-config", "main")` creates the branch and checkout in:

```text
~/Projects/
  Wayfold/
  .Wayfold-worktrees/
    input-config/
```

The directory name is a single safe, flat component. Git branch names may contain slashes. `ListProjects` shows top-level projects and omits registered Git-linked worktrees (as well as dot-prefixed directories). Existing worktrees are discovered by Git regardless of their directory layout.

A project-scoped command can target a registered checkout using `projectName="Wayfold::input-config"`. The suffix is the worktree directory basename and must identify exactly one registered worktree. All normal file, search, Git, process and job commands use the selected root. Without a selector the main project is used. Selectors are per-call and do not change another agent's context.

`InspectWorktreeRemoval` checks whether the checkout is clean, integrated with a target ref, unlocked, and free of running Jarvis jobs. `RemoveWorktree` performs the verified removal and removes an empty managed hidden container. Branches remain in Git.

## Repository tasks

Task descriptions remain readable HTML in a canonical task folder. Jarvis writes one JSON file per task for structured state. The default folder is `docs/tasks`; the repository can select another location with this root-level `.jarvis.json` property:

```json
{
  "taskDirectory": "docs/tasks"
}
```

Executable build/test tasks under the existing `"tasks"` property in `.jarvis.json` are separate from the task-file system.

New task IDs are collision-resistant when omitted. Existing numeric task descriptions such as `docs/tasks/305-component-authority.html` resolve as task `305`, with state at `docs/tasks/305.json`; the HTML is preserved in place. A file already named `<id>.html` uses the same basename. Ambiguous numeric descriptions or invalid JSON are reported rather than silently rewritten.

A typical JSON state:

```json
{
  "schemaVersion": 1,
  "id": "305",
  "title": "Production component authority",
  "status": "active",
  "dependencies": ["274"],
  "owner": {
    "agent": "agent-6",
    "worktree": "Wayfold::world-store"
  },
  "checkpoint": {
    "summary": "COW-backed position state implemented",
    "nextAction": "Verify retained snapshots",
    "verified": ["Focused storage tests pass"],
    "blocker": null,
    "lastCommit": "abc123"
  },
  "updatedAt": "2026-10-25T12:00:00Z"
}
```

Use `ListTasks` and `GetTask` to inspect HTML and JSON, including revision hashes and diagnostics. `ListTasks` filters by status, owner and dependency. `CompareWorktreeTasks` summarizes the task records in all Git-registered worktrees, preserving branch-local statuses and returning per-worktree diagnostics when a checkout cannot be read. `CreateTask` creates and commits the HTML+JSON pair, and can resume an interrupted write when only an identical HTML description was saved. `UpdateTask` (including optional JSON-encoded dependency changes), `ClaimTask`, `CheckpointTask` and `CompleteTask` validate, write and commit JSON state. The status vocabulary is `open`, `active`, `blocked`, `completed`. Completion preserves task state and its Git history; `GetTaskHistory` returns bounded relevant commits and patches for both the original HTML description and JSON state.

Mutations reject pre-existing staged or unstaged task JSON edits, preserve unknown JSON fields and use optimistic `expectedRevision` checking where provided. Claims reject another owner until explicitly reassigned. Jarvis serializes task mutations for a selected worktree within one local client, stages only the task's paths, and rejects preexisting staged edits to those paths. Each successful mutation returns the Git commit hash. The commit is local to the selected branch and subject to the client's version-control permissions; task changes become visible on main when integrated through Git.

## Existing checkpoint API

The existing `AttachWorkTask`, `GetWorkTask`, `ListWorkTasks`, `CheckpointWorkTask` and `CompleteWorkTask` commands are supported for current callers.

For canonical HTML tasks, attaching creates Git-backed JSON state, checkpointing updates it, and completion retains it. An older ignored `.jarvis/tasks/<id>.json` checkpoint for an HTML task is carried into the repository JSON at its next checkpoint update; the local record is retired only after the commit succeeds. Until migration, reads can still recover the existing checkpoint.

Older task descriptions outside the configured HTML folder, including Markdown tasks, continue using their previous local checkpoint format. They can be converted deliberately as those projects adopt canonical HTML task files. This preserves existing running sessions and their checkpoint files.

On recovery, inspect task state, current repository status and client activity. Git remains the authoritative history for canonical HTML tasks.

## Verification

Tests create disposable repositories and worktrees and exercise Git-backed task commits, branch isolation, legacy state migration, safe cleanup, rejection of staged task edits, conflicting owner claims and revision checks. Run `dotnet test Jarvis.slnx` for the full regression suite.
