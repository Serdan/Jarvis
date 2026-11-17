# P2 — Approval fast-negative gate is account-global, not selected-source scoped

**Frozen source:** `768f23da13519beb0b54fa3c1f517bfd8dc23700`

In `Client/Modules/CodeQualityHooks.fs`, `hasApprovedPrewarmCandidate ()` iterates all paths from `JARVIS_APPROVED_CODE_QUALITY_ROOTS` and returns true if **any** approved root has an enabled Roslyn hook. In `Client/SignalR/Client.fs`, every sourced Working `AgentInbox` calls `InboxPrewarmDispatcher.TrySchedule`; its background delegate consults this account-global predicate before calling `prewarmFromInbox` for the **current project**. Therefore:

- With project A approved and configured, and project B unconfigured (or not approved), project B's sourced inbox still triggers the background `git worktree list` and registered-selector resolution because project A satisfied the gate.
- Subsequent `CodeQualityHooks.prewarm validated` still protects Roslyn initialization by its checkout-specific config/approval checks. **This is not an unapproved Roslyn launch**, and discovery now happens off the response path, so the earlier synchronous-blocking P1 is fixed.
- The existing new `inbox prewarm avoids Git discovery for unapproved and no-hook roots` test varies only a single root and cannot catch this mixed-project counterexample.

To avoid unnecessary process churn, restrict the cheap candidate check to the canonical project/source identity or an explicitly approved project-scoped index before enumerating worktrees. Preserve exact source-ref-to-registered-checkout verification, no-main fallback and cancellation/failure isolation. Add a two-project negative regression: A approved with enabled hooks, B no hooks, B sourced inbox returns and does not enumerate Git or prewarm Roslyn.

**Severity:** P2 performance/negative-path coherence, not evidence of a synchronous inbox stall. No live mixed-project performance measurement was made.