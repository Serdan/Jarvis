# Git-backed worktrees and tasks — implementation slices

Source specification: [GitWorktreesAndTasks.md](../GitWorktreesAndTasks.md).

Each numbered task is independently testable and should land as a focused commit. Record verification before marking a slice complete. Tests use disposable Git repositories/worktrees; no test may mutate a real user's branches or task files. Each slice includes its protocol/MCP coverage when a new command is exposed.

## 1. Git worktree discovery and identity
- [ ] Parse `git worktree list --porcelain` (including NUL-delimited output where supported) into typed entries with canonical paths, branch/HEAD, detached, locked and prunable states.
- [ ] Identify the common Git repository from Git metadata rather than directory names.
- [ ] Expose local `ListWorktrees` and `GetWorktree` through client command catalog and MCP routing.
- **Tests:** fixtures with main and sibling worktrees, hidden-container worktrees, detached HEAD, locked/prunable entries, and malformed Git output. A non-Git project returns a clear error.
- **Accept:** Given one project, the API reports every Git-registered worktree regardless of its location.

## 2. Project listing and new worktree location
- [ ] Retain top-level visible repositories in `ListProjects` while excluding dot-prefixed worktree containers.
- [ ] Resolve the default creation directory to `<projectsRoot>/.<Project>-worktrees/<name>` and validate naming/path collisions.
- [ ] Handle branch names containing slashes independently of flat worktree names.
- **Tests:** mixed ordinary projects, hidden directories, old sibling worktrees and new containers; reserved names, traversal, symlinks and collisions.
- **Accept:** Project list is uncluttered and valid new worktree destinations are deterministic.

## 3. Create worktree command
- [ ] Implement `CreateWorktree` with explicit base ref and new branch.
- [ ] Use argument-array Git invocation, normal client permissions and structured result/errors.
- [ ] Return branch, path, HEAD and repository identity.
- **Tests:** create from main and non-main refs; branches with slashes; absent refs and duplicate branches/directories; verify project tree remains unchanged.
- **Accept:** A new branch and linked worktree are created in the hidden container and immediately discoverable.

## 4. Worktree selectors across existing operations
- [ ] Introduce a verified registered-worktree selector; default to existing main-project behavior when omitted.
- [ ] Resolve selected-worktree roots for file, search, patch, Git, command, project-task and job operations; enforce existing permission and path checks.
- [ ] Include worktree context in activity records; isolate selections across simultaneous agents and devices.
- **Tests:** execute representative read/write/search/Git/job commands in two worktrees; verify isolation, default behavior, traversal rejection and spoofed selectors.
- **Accept:** Every project-scoped command targets the specified registered worktree without changing another session.

## 5. Inspect safe worktree removal
- [ ] Add a reusable inspection that reports cleanliness, locks, running work and integration with a configurable target branch.
- [ ] Handle normal merges and uncertain squash/rebase cases without claiming integration without evidence.
- [ ] Expose a read-only removal readiness result.
- **Tests:** clean merged, dirty, locked, active job, ahead/unmerged, deleted base and squashed-commit histories.
- **Accept:** The readiness API gives accurate reasons and never mutates the repository.

## 6. Remove worktree safely
- [ ] Implement `RemoveWorktree` using readiness checks and normal permissions.
- [ ] Remove the Git worktree; remove an empty managed hidden container; retain branch history and unrelated files.
- [ ] Return structured refusal details for unsafe removal.
- **Tests:** successful merged cleanup, dirty/locked/unmerged/job refusals, stale directory, nested files and unrelated sibling preservation.
- **Accept:** Safe cleanup works through Jarvis and unsafe cleanup leaves the worktree intact.

## 7. Task folder configuration and HTML discovery
- [ ] Resolve the repository-configured canonical task directory, with a documented default.
- [ ] Discover existing HTML tasks without relocating or rewriting them; preserve legacy numeric IDs and filename patterns where configured.
- [ ] Validate task-root containment, duplicate IDs, unreadable files and symlink escape.
- **Tests:** Wayfold-style HTML-only fixtures, missing directory, alternate configured directory, duplicate and unsafe paths.
- **Accept:** Existing human-readable tasks can be listed in their original locations.

## 8. JSON task schema and read APIs
- [ ] Define versioned per-task JSON metadata next to HTML with status, owner, dependencies, checkpoint and extension-field preservation.
- [ ] Implement `ListTasks` and `GetTask` including HTML content/path, revision, filters and diagnostics; handle absent JSON companions.
- [ ] Use collision-resistant identifiers for new tasks while preserving old IDs.
- **Tests:** valid/malformed/unknown-version JSON, missing companion, filter combinations, unknown fields, duplicate IDs and stable revision detection.
- **Accept:** Agents can inspect mixed legacy and new tasks without changing files.

## 9. Atomic task creation and scoped Git commit
- [ ] Implement `CreateTask` producing HTML and JSON in the selected worktree.
- [ ] Atomically/recoverably write files, validate identifiers and content, and commit only task-owned paths.
- [ ] Report commit hash and paths; obey client write permissions.
- **Tests:** successful creation, ID/path collision, concurrent distinct IDs, crash/failure injection between writes and commit, unrelated dirty/staged files.
- **Accept:** One call leaves a valid, committed HTML+JSON task without including unrelated changes.

## 10. Update task metadata and ownership
- [ ] Implement `UpdateTask` with an expected revision check and validated JSON field patches.
- [ ] Implement `ClaimTask` with ownership transitions and explicit reassignment.
- [ ] Serialize local mutations in a worktree and auto-commit task-only changes.
- **Tests:** status transitions, unknown field preservation, stale revision, conflicting owner, simultaneous updates, write/commit failures and dirty index.
- **Accept:** State updates are committed, independently auditable and protected against stale overwrites.

## 11. Checkpoints and completion
- [ ] Implement `CheckpointTask` and `CompleteTask`, including verified evidence, next action, last commit and reopening.
- [ ] Preserve the task record after completion; report the resulting Git commit.
- [ ] Define deterministic checkpoint commit/coalescing behavior.
- **Tests:** active/blocked/completed transitions, repeated checkpoints, interrupted checkpoint writes, retained history and invalid transitions.
- **Accept:** A fresh agent can recover task progress entirely from repository files.

## 12. Task Git history and multi-branch visibility
- [ ] Implement `GetTaskHistory` returning bounded relevant commits and diffs for HTML/JSON paths.
- [ ] Offer read-only comparison of task presence/state between registered worktrees where useful.
- [ ] Surface merge conflicts between edits to the same task as ordinary Git conflicts.
- **Tests:** create/update/checkpoint history, branch-local task not on main, separate new tasks merged, conflicting updates and bounded history output.
- **Accept:** Task evolution can be reconstructed from Git, including branch-local changes.

## 13. Consolidate existing work-task checkpoint commands
- [ ] Review `AttachWorkTask`, `ListWorkTasks`, `GetWorkTask`, `CheckpointWorkTask` and `CompleteWorkTask` against the new model.
- [ ] Adapt or replace their persistence so agents encounter one coherent task state model; provide deterministic treatment for existing local checkpoints.
- [ ] Preserve distinction from executable `.jarvis.json` project tasks.
- **Tests:** existing checkpoint fixtures, resume-after-restart, completion preservation, command interoperability and regression tests.
- **Accept:** Existing work-task clients have documented, tested behavior and task status has a single repository-backed authority.

## 14. End-to-end verification and instructions
- [ ] Update README, command-surface documentation, agent instructions and project-work skill for native worktree/task workflows.
- [ ] Exercise two agents operating in independent worktrees, branch merge, completed task history and safe cleanup.
- [ ] Run full client/server test suites and solution build; inspect permission regressions and commits.
- **Tests:** end-to-end protocol/MCP flow, worktree isolation, task-specific staging, errors/recovery and Git integration.
- **Accept:** A fresh agent can discover, create, work, checkpoint, complete and clean up using Jarvis, with repository history sufficient for another agent to continue.
