# P1 — AgentInbox prewarm now synchronously blocks the normal inbox path

**Frozen source:** `e00abdd6432768065af9716758f6115cab4a0f1f` (review `f135fae7b35043a3ac75c49a5f1313cc`).

## Reproducible source evidence

`Client/SignalR/Client.fs:~813-820` handles `TaskReadCommand` by reading the inbox and then calling `prewarmFromInbox` **before** serializing/returning the response. `Client/SignalR/Client.fs:71-101` calls `ProjectPaths.WorktreeGit.list "git" mainRoot (TimeSpan.FromSeconds 10.)` whenever the focused attempt is Working and has a source ref; there is no check for enabled hooks before that call. The branch match then invokes `ProjectPaths.resolveProjectContext(project + "::" + name)`, whose registered-checkout resolver also calls `WorktreeGit.list` (`Client/Modules/ProjectPaths.fs:132-140`). `WorktreeGit.list` invokes the synchronous `BoundedProcess.run` subprocess at `Client/Modules/ProjectPaths.fs:114-130`.

Thus a routine `agent_inbox` read, *even when no Roslyn/code-quality hooks are configured*, performs synchronous external Git worktree enumeration and can be blocked by up to the 10-second timeout at each lookup; repeated inbox reads repeat the work. The Roslyn session itself may initialize asynchronously, but the **inbox path is not asynchronous**. This contradicts the task's nonblocking activation/resume and unchanged-no-hooks behavior, especially on a busy multi-worktree installation.

## Required correction

Do not synchronously enumerate Git worktrees or validate an extra registered selector on the normal `agent_inbox` critical path. Dispatch appropriately bounded prewarm work asynchronously after a successful inbox response, or use a safely invalidated registered checkout binding/cached lookup, and short-circuit immediately when hooks are not enabled. Preserve explicit source-ref-to-registered-checkout identity and fail-closed ambiguity/no-main-fallback checks. Add a deterministic test for a no-hook sourced inbox, slow Git lookup and repeated Working reads, plus real activation-to-prewarm evidence.

**Evidence limit:** This is a source-path and timeout-bound finding; no 10-second live stall was measured.
