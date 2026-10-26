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

New task IDs are collision-resistant when omitted. Existing numeric task descriptions such as `docs/tasks/305-component-authority.html` resolve as task `305`, with state at `docs/tasks/305.json`; the HTML is preserved in place. Date-prefixed task IDs such as `2026-10-24-runpod-benchmark-lifecycle` keep their complete identifiers and state filenames. A file already named `<id>.html` uses the same basename. Ambiguous numeric descriptions or invalid JSON are reported rather than silently rewritten.

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

Use `ListTasks` and `GetTask` to inspect HTML and JSON, including revision hashes and diagnostics. `ListTasks` filters by status, owner, dependency and next actor. `CompareWorktreeTasks` summarizes the task records in all Git-registered worktrees, optionally filtering by `nextActor` with the same semantics as `ListTasks`. It preserves branch-local statuses, includes worktree/branch identity alongside task ID, revision, owner and handoff fields, and returns per-worktree diagnostics when a checkout cannot be read. `CreateTask` creates and commits the HTML+JSON pair, and can resume an interrupted write when only an identical HTML description was saved. `UpdateTask` (including optional JSON-encoded dependency changes), `ClaimTask`, `CheckpointTask` and `CompleteTask` validate, write and commit JSON state. The status vocabulary is `open`, `active`, `blocked`, `completed`. Existing JSON using `in_progress` is interpreted as `active` for reading and filtering; the original JSON bytes remain unchanged until an explicit task mutation. Completion preserves task state and its Git history; `GetTaskHistory` returns bounded relevant commits and patches for both the original HTML description and JSON state.

### Missing task directories and Git recovery

`ListTasks` returns an empty list when the configured task folder is absent;
`GetTask` returns not found, and `CreateTask` creates the folder on demand.
Task reads do not require Git, but mutations do. Before writing any task
files Jarvis verifies that `git` is executable and that the selected project
is the root of a Git working tree. Missing Git, non-repository projects and
other Git-access failures produce distinct actionable errors. Jarvis never
silently initializes a repository or falls back to unversioned task state.

When a Git commit fails after files have been written (for example, a
rejecting pre-commit hook or missing Git identity), Jarvis checks that Git HEAD
and the task files have not changed unexpectedly, then restores the previous
task bytes and unstages **only the task paths**. Existing unrelated staged
changes remain in the index. The original command can be retried after fixing
Git. New task files are deleted on rollback; a previous HTML-only task
remains intact if its new JSON metadata commit fails.

If the Git index cannot be safely restored, HEAD changed unexpectedly, or
another process/hook edited the task files, Jarvis **preserves the current
files** and returns an explicit reconciliation error rather than overwriting
someone else's work. A newly created empty task directory may remain after a
rolled-back commit; reads continue to report an empty task list.

Mutations reject pre-existing staged or unstaged task JSON edits, preserve unknown JSON fields and use optimistic `expectedRevision` checking where provided. Claims reject another owner until explicitly reassigned. Jarvis serializes task mutations for a selected worktree within one local client, stages only the task's paths, and rejects preexisting staged edits to those paths. Each successful mutation returns the Git commit hash. The commit is local to the selected branch and subject to the client's version-control permissions; task changes become visible on main when integrated through Git.

## Existing checkpoint API

The existing `AttachWorkTask`, `GetWorkTask`, `ListWorkTasks`, `CheckpointWorkTask` and `CompleteWorkTask` commands are supported for current callers.

For canonical HTML tasks, attaching creates Git-backed JSON state, checkpointing updates it, and completion retains it. An older ignored `.jarvis/tasks/<id>.json` checkpoint for an HTML task is carried into the repository JSON at its next checkpoint update; the local record is retired only after the commit succeeds. Until migration, reads can still recover the existing checkpoint.

Older task descriptions outside the configured HTML folder, including Markdown tasks, continue using their previous local checkpoint format. They can be converted deliberately as those projects adopt canonical HTML task files. This preserves existing running sessions and their checkpoint files.

On recovery, inspect task state, current repository status and client activity. Git remains the authoritative history for canonical HTML tasks.

## Verification

Tests create disposable repositories and worktrees and exercise Git-backed task commits, branch isolation, legacy state migration, safe cleanup, rejection of staged task edits, conflicting owner claims and revision checks. Run `dotnet test Jarvis.slnx` for the full regression suite.


## Reviewer/agent handoff (protocol 3.15)

Task **status** (`open`, `active`, `blocked`, `completed`) is independent of
**turn ownership** (`nextActor`: `agent`, `reviewer`, or absent).
The `owner` records the implementing agent; a reviewer updating an assignment
does **not** claim or reassign ownership.

Reviewer instructions are stored in `assignment`, separate from the agent's
execution evidence in `checkpoint`:

```json
{
  "status": "active",
  "owner": { "agent": "agent-6", "worktree": "Wayfold::world-store" },
  "nextActor": "agent",
  "lastUpdatedBy": { "role": "reviewer", "id": "orchestrator" },
  "assignment": {
    "instructions": "Integrate the next real movement writer.",
    "baseCommit": "abc123",
    "revision": "f6c8a1...",
    "updatedAt": "2026-10-26T00:00:00Z"
  },
  "checkpoint": {
    "summary": "Previous position-store implementation verified",
    "verified": ["Rollback tests pass"],
    "nextAction": "Previously proposed next action",
    "lastCommit": "def456"
  }
}
```

An orchestrator calls `UpdateTask` with `assignmentInstructions`,
optional `assignmentBaseCommit`, required `actorId`, and an optimistic
`expectedRevision` and/or `expectedAssignmentRevision`. Jarvis updates
the assignment and generates a **fresh opaque assignment revision** on each
reissue, sets `nextActor=agent` by default, and records
`lastUpdatedBy={role:reviewer,id:<actorId>}`. The current `owner`,
`checkpoint`, and `status` remain unchanged unless explicit metadata
fields request an update. `UpdateTask` can also update `nextActor`
without changing instructions when a reviewer explicitly requests it with an `actorId`.
`ListTasks` accepts a `nextActor` filter, and task summaries expose
`nextActor`, `lastUpdatedBy`, and `assignmentRevision`.

The implementing agent reads the current assignment and calls `CheckpointTask`
with `acknowledgedAssignmentRevision` equal to the assignment revision it
actually executed, plus optional `actorId`. Jarvis rejects missing or
stale acknowledgements once an assignment exists. The checkpoint preserves
the assignment, records acknowledgement, and marks `nextActor=reviewer`,
`lastUpdatedBy={role:agent,id:<actorId or claimed owner>}`.
`CompleteTask` also requires acknowledgement of an existing assignment. It
marks the task `completed` and clears `nextActor`, without changing the owner,
checkpoint or retained evidence. Completion on a feature branch does not
imply the implementation has been merged.

Existing tasks **without** an `assignment` remain readable and may use their
legacy `checkpoint.nextAction` without an acknowledgement. Older checkpoints
are never rewritten just to introduce the new structure. When an assignment
does exist, it supersedes the checkpoint's legacy `nextAction` as the next
set of instructions for the agent. The JSON and Git commits retain the
historical checkpoint.

As of protocol 3.16, `CheckpointTask` and `UpdateTask` also clear `nextActor`
when setting status to `completed`, so completed tasks have no pending turn.
Both `ListTasks(nextActor=...)` and `CompareWorktreeTasks(nextActor=...)`
exclude completed tasks, including older JSON records still storing
`nextActor=reviewer`. Unfiltered reads preserve the historic JSON and full
task list; no migration or rewriting of existing task files occurs. When a
worktree contains no matching tasks it still appears as an empty group,
retaining branch identity and any error diagnostics.

This is a **branch-local handoff protocol**, not a distributed lease or proof
that an agent's ChatGPT session is currently executing. Concurrent edits in
different Git worktrees still require normal Git integration and conflict
resolution. The new 3.15 command fields require a coordinated server/client
rollout and refreshed MCP schema; updating the client can interrupt active
agent sessions, so obtain explicit permission before invoking `UpdateClient`.
