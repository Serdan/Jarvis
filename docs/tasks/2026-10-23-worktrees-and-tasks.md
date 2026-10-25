# Git-backed worktrees and tasks — implementation slices

Source specification: [GitWorktreesAndTasks.md](../GitWorktreesAndTasks.md).

Each numbered task is independently testable and should land as a focused commit. Record verification before marking a slice complete. Tests use disposable Git repositories/worktrees; no test may mutate a real user's branches or task files. Each slice includes its protocol/MCP coverage when a new command is exposed.

## 1. Git worktree discovery and identity
- [x] Parse `git worktree list --porcelain` (including NUL-delimited output where supported) into typed entries with canonical paths, branch/HEAD, detached, locked and prunable states.
- [x] Identify the common Git repository from Git metadata rather than directory names.
- [x] Expose local `ListWorktrees` and `GetWorktree` through client command catalog and MCP routing.
- **Tests:** fixtures with main and sibling worktrees, hidden-container worktrees, detached HEAD, locked/prunable entries, and malformed Git output. A non-Git project returns a clear error.
- **Accept:** Given one project, the API reports every Git-registered worktree regardless of its location.

## 2. Project listing and new worktree location
- [x] Retain top-level visible repositories in `ListProjects` while excluding dot-prefixed worktree containers.
- [x] Resolve the default creation directory to `<projectsRoot>/.<Project>-worktrees/<name>` and validate naming/path collisions.
- [x] Handle branch names containing slashes independently of flat worktree names.
- **Tests:** mixed ordinary projects, hidden directories, old sibling worktrees and new containers; reserved names, traversal, symlinks and collisions.
- **Accept:** Project list is uncluttered and valid new worktree destinations are deterministic.

## 3. Create worktree command
- [x] Implement `CreateWorktree` with explicit base ref and new branch.
- [x] Use argument-array Git invocation, normal client permissions and structured result/errors.
- [x] Return branch, path, HEAD and repository identity.
- **Tests:** create from main and non-main refs; branches with slashes; absent refs and duplicate branches/directories; verify project tree remains unchanged.
- **Accept:** A new branch and linked worktree are created in the hidden container and immediately discoverable.

## 4. Worktree selectors across existing operations
- [x] Introduce a verified registered-worktree selector; default to existing main-project behavior when omitted.
- [x] Resolve selected-worktree roots for file, search, patch, Git, command, project-task and job operations; enforce existing permission and path checks.
- [x] Include worktree context in activity records; isolate selections across simultaneous agents and devices.
- **Tests:** execute representative read/write/search/Git/job commands in two worktrees; verify isolation, default behavior, traversal rejection and spoofed selectors.
- **Accept:** Every project-scoped command targets the specified registered worktree without changing another session.

## 5. Inspect safe worktree removal
- [x] Add a reusable inspection that reports cleanliness, locks, running work and integration with a configurable target branch.
- [x] Handle normal merges and uncertain squash/rebase cases without claiming integration without evidence.
- [x] Expose a read-only removal readiness result.
- **Tests:** clean merged, dirty, locked, active job, ahead/unmerged, deleted base and squashed-commit histories.
- **Accept:** The readiness API gives accurate reasons and never mutates the repository.

## 6. Remove worktree safely
- [x] Implement `RemoveWorktree` using readiness checks and normal permissions.
- [x] Remove the Git worktree; remove an empty managed hidden container; retain branch history and unrelated files.
- [x] Return structured refusal details for unsafe removal.
- **Tests:** successful merged cleanup, dirty/locked/unmerged/job refusals, stale directory, nested files and unrelated sibling preservation.
- **Accept:** Safe cleanup works through Jarvis and unsafe cleanup leaves the worktree intact.

## 7. Task folder configuration and HTML discovery
- [x] Resolve the repository-configured canonical task directory, with a documented default.
- [x] Discover existing HTML tasks without relocating or rewriting them; preserve legacy numeric IDs and filename patterns where configured.
- [x] Validate task-root containment, duplicate IDs, unreadable files and symlink escape.
- **Tests:** Wayfold-style HTML-only fixtures, missing directory, alternate configured directory, duplicate and unsafe paths.
- **Accept:** Existing human-readable tasks can be listed in their original locations.

## 8. JSON task schema and read APIs
- [x] Define versioned per-task JSON metadata next to HTML with status, owner, dependencies, checkpoint and extension-field preservation.
- [x] Implement `ListTasks` and `GetTask` including HTML content/path, revision, filters and diagnostics; handle absent JSON companions.
- [x] Use collision-resistant identifiers for new tasks while preserving old IDs.
- **Tests:** valid/malformed/unknown-version JSON, missing companion, filter combinations, unknown fields, duplicate IDs and stable revision detection.
- **Accept:** Agents can inspect mixed legacy and new tasks without changing files.

## 9. Atomic task creation and scoped Git commit
- [x] Implement `CreateTask` producing HTML and JSON in the selected worktree.
- [x] Atomically/recoverably write files, validate identifiers and content, and commit only task-owned paths.
- [x] Report commit hash and paths; obey client write permissions.
- **Tests:** successful creation, ID/path collision, concurrent distinct IDs, crash/failure injection between writes and commit, unrelated dirty/staged files.
- **Accept:** One call leaves a valid, committed HTML+JSON task without including unrelated changes.

## 10. Update task metadata and ownership
- [x] Implement `UpdateTask` with an expected revision check and validated JSON field patches.
- [x] Implement `ClaimTask` with ownership transitions and explicit reassignment.
- [x] Serialize local mutations in a worktree and auto-commit task-only changes.
- **Tests:** status transitions, unknown field preservation, stale revision, conflicting owner, simultaneous updates, write/commit failures and dirty index.
- **Accept:** State updates are committed, independently auditable and protected against stale overwrites.

## 11. Checkpoints and completion
- [x] Implement `CheckpointTask` and `CompleteTask`, including verified evidence, next action, last commit and reopening.
- [x] Preserve the task record after completion; report the resulting Git commit.
- [x] Define deterministic checkpoint commit/coalescing behavior.
- **Tests:** active/blocked/completed transitions, repeated checkpoints, interrupted checkpoint writes, retained history and invalid transitions.
- **Accept:** A fresh agent can recover task progress entirely from repository files.

## 12. Task Git history and multi-branch visibility
- [x] Implement `GetTaskHistory` returning bounded relevant commits and diffs for HTML/JSON paths.
- [x] Offer read-only comparison of task presence/state between registered worktrees where useful.
- [x] Surface merge conflicts between edits to the same task as ordinary Git conflicts.
- **Tests:** create/update/checkpoint history, branch-local task not on main, separate new tasks merged, conflicting updates and bounded history output.
- **Accept:** Task evolution can be reconstructed from Git, including branch-local changes.

## 13. Consolidate existing work-task checkpoint commands
- [x] Review `AttachWorkTask`, `ListWorkTasks`, `GetWorkTask`, `CheckpointWorkTask` and `CompleteWorkTask` against the new model.
- [x] Adapt or replace their persistence so agents encounter one coherent task state model; provide deterministic treatment for existing local checkpoints.
- [x] Preserve distinction from executable `.jarvis.json` project tasks.
- **Tests:** existing checkpoint fixtures, resume-after-restart, completion preservation, command interoperability and regression tests.
- **Accept:** Existing work-task clients have documented, tested behavior and task status has a single repository-backed authority.

## 14. End-to-end verification and instructions
- [x] Update README, command-surface documentation, agent instructions and project-work skill for native worktree/task workflows.
- [x] Exercise two agents operating in independent worktrees, branch merge, completed task history and safe cleanup.
- [x] Run full client/server test suites and solution build; inspect permission regressions and commits.
- **Tests:** end-to-end protocol/MCP flow, worktree isolation, task-specific staging, errors/recovery and Git integration.
- **Accept:** A fresh agent can discover, create, work, checkpoint, complete and clean up using Jarvis, with repository history sufficient for another agent to continue.


## Implementation checkpoint — 2026-10-25

Implementation branch: `feature/git-backed-worktrees-tasks`, checkout `~/Projects/.Jarvis-worktrees/task-management`.

- Worktree discovery, hidden-container creation, registered-worktree selection, inspection and safe cleanup are implemented through the client and MCP.
- Canonical HTML descriptions and Git-backed JSON state support task creation, filters, dependency edits, claiming, checkpointing, completion and bounded Git patch history.
- `CompareWorktreeTasks` shows branch-local task presence and status across all registered Git worktrees.
- Existing HTML work-task checkpoint calls use repository-backed state; ignored local records migrate at their next checkpoint. Markdown-based checkpoints continue using the previous local format until converted.
- Tested on Linux using disposable repositories and linked worktrees. Validation on Windows and macOS native clients, live deployed MCP handshake, and additional forced-crash/failure-injection scenarios remain release acceptance work.
- Feature branch remains isolated from main, ready for integration review and release verification.
