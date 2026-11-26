# P2 — Exercise negative `.git` identity validation

`Client/Modules/ProjectPaths.fs:186–236` adds the central cached-path identity gate: the worktree `.git` pointer must resolve to an admin directory under the repository's `worktrees` directory, that admin directory's `commondir` must resolve to the expected common Git directory, and its reverse `gitdir` pointer must resolve back to the candidate worktree's `.git` file.

The current negative tests do not exercise those pointer-consistency comparisons:

- `registered_worktree_cache_reuses_paths_without_caching_head_or_status` moves the worktree and recreates the old path as an empty directory, so validation fails at the missing `.git` file before checking pointer identity.
- `registered_worktree_symlink_paths_are_rejected` is rejected by `containsLink` before `isRegisteredWorktreePath` runs.
- `concurrent_worktree_move_during_discovery_never_returns_a_stale_path` primarily exercises watcher/discovery invalidation and a missing old path.

Add a focused regression using real linked-worktree metadata where all paths/files still exist but one identity edge is wrong—for example, rewrite one worktree's `.git` pointer to another registered worktree's admin directory, or alter the reverse `gitdir`/`commondir` pointer. Assert the identity gate rejects it and that a cached public resolution fails closed. Keep a positive case for an intact linked worktree.

This is the main new correctness guard in the hardening commit; the existing 36 passing tests do not establish its negative pointer-mismatch behavior.