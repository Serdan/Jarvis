# Git-backed worktrees and task management

Status: proposed specification
Date: 2026-10-23

## Purpose

Jarvis shall expose Git worktrees and repository-backed tasks as native client operations. Git and the checked-out files are authoritative. The client manages task files and Git operations, while the server authenticates and forwards commands. Agents use native task commands for state changes and routine commits.

The main repository remains a normal top-level project. Existing worktrees are discovered from Git metadata irrespective of their location. New worktrees use hidden project-specific containers; discovery uses Git metadata for all registered worktrees.

## Repository and worktree model

Example:

```text
~/Projects/
  Jarvis/
  .Jarvis-worktrees/
    task-management/
  Wayfold/
  .Wayfold-worktrees/
    input-config/
    pathfinding/
  Bloom/
```

- `ListProjects` continues listing top-level visible projects only; it excludes dot-prefixed directories and does not list nested worktrees as independent projects.
- `ListWorktrees(projectName)` invokes Git's machine-readable worktree listing (`git worktree list --porcelain -z` where supported), resolves absolute canonical paths, and reports every registered worktree even outside `~/Projects` or in old sibling layouts.
- Git common-directory identity, not folder names, establishes the relationship between a main project and its worktrees. Distinguish a main worktree from linked worktrees via Git metadata. Include branch, HEAD, detached state, locked/prunable indicators, path, and local working-tree cleanliness.
- Existing worktree access remains supported. Commands accept a stable *worktree selector* (canonical absolute path or an opaque ID returned by discovery) in addition to the project; the selector is resolved and verified by the client against Git's registered worktree list, not trusted as an arbitrary filesystem path. Omitted selector means the main project as before.
- For new worktrees use `<projectsRoot>/.<mainProjectName>-worktrees/<safeName>`. Keep directory names flat and independent of branch names. Branch names may contain slashes. Fail if the target exists or is registered elsewhere; do not silently reuse.
- Validate project/worktree identity, path containment for newly created directories, path normalization, symlinks/reparse points, and Git arguments. Use argument arrays, never shell interpolation. Existing worktrees outside the project root may be accessed only through registered, explicitly selected worktree identifiers and with the same permission enforcement as normal projects.
- `CreateWorktree`: specify base ref and requested branch; report actual path and branch. Creation explicitly selects a base ref and new branch, with collisions reported as errors. `GetWorktree` / `ListWorktrees`: read-only. `RemoveWorktree`: refuse if dirty, locked, unmerged, or running work is associated; never force or delete a branch by default. Report why cleanup is refused. Remove the empty hidden parent directory after a successful removal, if safe. Determine integration against an explicit target branch (default project's configured main branch, not hard-coded `main`), and distinguish merge from squashed/rebased integration; don't claim integrated on branch-name heuristics.
- Worktree selection is scoped to each command invocation (or explicit session selector, if implemented separately); never silently change another agent's selected worktree. Worktree identity should appear in client activity.
- Local jobs and file operations in a selected worktree must use that worktree's root and established path/permission protections. No path traversal via task or worktree identifiers.

## Canonical task files

The repository defines one canonical task folder, configurable per project (for example `docs/tasks` for existing projects). Existing Wayfold HTML files remain in place. Configuration is version-controlled and belongs to the repository, with a documented default when absent.

- Preserve human-readable HTML for the substantive description, rationale, requirements, and acceptance criteria.
- Store machine-managed task metadata and state as JSON, in the same canonical task directory, one JSON per task. Default convention: `<id>.html` and `<id>.json`; projects may configure naming patterns only where necessary to accommodate existing tasks. Each task has an independent JSON state file.
- HTML and JSON are both versioned and branch-local. Jarvis reads and writes the selected worktree's checkout; each branch retains its own version until Git integration.
- Existing HTML-only tasks remain readable and may receive a JSON companion lazily. Adding JSON state preserves the existing HTML content. Existing task IDs must remain valid. New IDs should be collision-resistant across independent branches (e.g. ULID); do not require centralized numeric allocation. Display legacy numeric IDs unchanged.
- Task JSON is deliberately small and extensible, for example:

```json
{
  "schemaVersion": 1,
  "id": "282",
  "title": "Configurable input bindings",
  "status": "active",
  "dependencies": ["274"],
  "owner": {
    "agent": "optional session identifier",
    "worktree": "optional stable worktree reference"
  },
  "checkpoint": {
    "summary": "Input routing implemented",
    "nextAction": "Finish conflict handling",
    "verified": [],
    "lastCommit": null
  },
  "updatedAt": "2026-10-23T18:00:00Z"
}
```

- Status values: `open`, `active`, `blocked`, `completed`. A task can be reopened; changes are recorded in Git. Do not conflate implementation completion with branch integration. A task may be completed in an unmerged worktree.
- JSON is authoritative for structured status; HTML is authoritative for prose. If both contain conflicting status labels, do not silently infer from HTML or rewrite the description. Report discrepancies and document a one-time import rule.
- Task dependencies reference stable task IDs. Missing dependencies should be reported, not auto-created. Validate schema, identifiers, task root, and duplicate IDs; reject unknown status strings and invalid state updates. Preserve unknown well-formed extension fields when updating known fields.

## Task operations

Native commands, operating in the selected worktree:

- `ListTasks`: read-only summaries and filters by status, ID, dependencies, owner. Invalid individual records are surfaced as diagnostics rather than silently omitted.
- `GetTask`: JSON metadata, human-readable HTML content/path, file revision, and Git revision.
- `CreateTask`: create HTML and JSON together; validate unique ID and absent paths.
- `UpdateTask`: validated partial patch to structured fields; optional expected revision/hash for optimistic concurrency.
- `ClaimTask`: record owner and active state with expected revision; refuse an existing different owner unless explicitly reassigned. This is a branch-local claim, not a distributed lock.
- `CheckpointTask`: update progress summary, verified evidence, next action, and last commit without losing description.
- `CompleteTask`: record completion and verification; preserve JSON and HTML in Git (unlike deleting transient execution files).
- `GetTaskHistory`: return relevant Git commits/diffs affecting the task files, bounded in size.
- Optional repository-wide inspection may compare tasks across discovered worktrees and label branch-local/new/unmerged changes. This is an observational view, never a second authoritative state.

Retain unrelated `RunProjectTask` commands from `.jarvis.json` as build/run automation; do not confuse them with issue/task records. Legacy WorkTask API endpoints are retired. The authoritative state is the committed repository task JSON. Audit and archive older local `.jarvis/tasks/` records without overwriting the active assignment, checkpoint or ownership; do not retain two competing notions of task status.

## Automatic Git commits

- Create, material status/ownership/criteria updates, and completion produce a task-scoped commit on the selected branch. Checkpoints may be coalesced only with explicit, deterministic semantics; a successful persisted checkpoint must not be silently lost on a crash.
- Stage only the exact task file paths changed by the command, never `git add -A` or other dirty working-tree files. Refuse/diagnose preexisting staged changes to those paths instead of committing someone else's edits.
- Mutations are atomic at the file-system level where practicable: write temporary files in the same directory, validate, then rename; keep HTML/JSON pair creation recoverable if interrupted. A successful mutation response identifies the resulting commit hash and changed paths.
- If Git commit fails, return a typed failure with on-disk and index state and recovery guidance. Do not report a successful completed task operation merely because the JSON write succeeded. Don't quietly amend arbitrary existing commits.
- Task-only auto-commits still obey normal Jarvis workspace-write permissions/trust settings. Automatic commits are local to the selected branch; branch integration follows the existing Git workflow.
- A local command-level mutex may serialize operations within one client/worktree to avoid races; use optimistic expected revision checks to prevent stale overwrites. This cannot guarantee cross-device claiming of the same branch; document this limitation.
- Multiple branches editing the same existing task may conflict when merged. Preserve normal Git conflict behavior and surface explicit resolution instructions; no implicit last-writer-wins merge of status or checkpoint fields.

## Worktree and task lifecycle

1. Discover main project and existing worktrees via Git.
2. Agent creates/selects its feature worktree (or uses an existing registered one).
3. Agent creates or updates HTML task description and corresponding JSON state in that worktree through Jarvis.
4. Jarvis validates and commits task-only changes automatically.
5. Agent implements and checkpoints the task within its branch.
6. Agent merges work through the normal development process. Task files and their history merge with the branch.
7. Jarvis cleanup verifies integration and clean state, removes the worktree, and leaves main repository tasks intact.

Tasks created on a feature branch need not be visible in main until merged. Do not pretend worktree-exclusive ownership implies cross-branch exclusivity; collision-resistant IDs address new-task collisions, while edits to shared task IDs still require normal Git conflict resolution.

## API and compatibility

Add worktree operations to client command protocol, capability catalog, client/server forwarding, MCP tools, and metadata. Preserve optional/default main-project behavior of existing commands. Grouping in `ListProjects` may be offered separately but must not break clients that expect existing project names. Define structured errors for unknown project/worktree, ambiguous selector, dirty tree, unmerged branch, invalid task, stale revision, Git failure, and permission rejection.

The current local modules `ProjectBrowser`, `ProjectPaths`, and `ClientShell` provide discovery/path/Git entry points; inspect their semantics before editing. Ensure selected-worktree paths cannot bypass workspace safety merely because their Git common directory belongs to an allowed repository.

## Verification / acceptance

1. Main projects stay discoverable; hidden containers are excluded from `ListProjects`.
2. Git worktree discovery finds both old sibling and new hidden-container layouts, including detached/locked cases.
3. Create/inspect/select/remove worktrees on Linux, Windows, and macOS; reject unsafe paths and removal of dirty, locked, or unmerged worktrees.
4. Existing commands target the selected worktree without altering other sessions.
5. Existing Wayfold-style HTML tasks require no format conversion; metadata companions are optional until first structured mutation.
6. Concurrent creation on separate branches produces distinct IDs; tasks remain branch-local until merge.
7. All task mutations validate schema, enforce revision checks, and commit only intended task files; staged unrelated user changes are never committed.
8. Interrupted writes and failed commits have recoverable, accurately reported outcomes.
9. Existing work-task checkpoint APIs either migrate predictably or are replaced with documented tested behavior.
10. Full client/server protocol and permission tests pass; normal project file paths and job execution remain unaffected.

## Implementation order

1. Worktree discovery/identity, selectors, creation, safe removal and tests.
2. Configurable canonical task directory, HTML+JSON task model, validation and read commands.
3. Transactional task mutation, task-scoped Git auto-commit, state/history commands.
4. Integrate old work-task commands, update agent instructions and documentation, end-to-end multi-worktree tests.

Existing worktrees remain at their current paths and are discovered through Git.
