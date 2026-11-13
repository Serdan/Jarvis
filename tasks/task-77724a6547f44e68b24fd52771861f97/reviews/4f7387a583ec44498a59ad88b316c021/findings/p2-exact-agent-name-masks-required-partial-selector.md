# P2 — Exact-name shortcut silently disables required @Worker partial matching

**Frozen:** `b2c2644b4f61cfb8e24ce6625232561fa41a7eaf`, `Client/Modules/AgentView.fs` function `matchesTerms` / `matchesAgent`.

The task specification explicitly states **`#Wayfold@Worker` must match all agents with names containing `Worker` in Wayfold**, while `@Worker 1` should still distinguish Worker 1 from Worker 10. The new implementation tests whether a selector term happens to equal *any current agent name in the snapshot*:

```fsharp
if exactAgentNames.Contains term then String.Equals(row.Agent, term, StringComparison.OrdinalIgnoreCase)
else row.Agent.Contains(term, StringComparison.OrdinalIgnoreCase)
```

This makes the same `@Worker` filter's meaning **depend on unrelated roster membership**. If a legitimate agent named `Worker` exists, it switches to strict exact-name matching and hides `Worker 1`, `Worker 2`, etc. When `Worker` disappears the identical filter suddenly matches them again. The UI gives no indication of this hidden mode switch, and Tab cannot expand `@Worker` beyond the current LCP when a literal Worker candidate exists.

## Independent reproducible test

Added solely to a disposable reviewer detached worktree `/tmp/jarvis-reviewer1-tui-worker-prefix-b2c2` at the **exact frozen SHA**. `AgentViewTests.projectAgentWorkerSelectorIncludesExactWorkerAndNumberedWorkers` constructs a Wayfold roster `Worker`, `Worker 1`, `Worker 2`, plus an unrelated `Jarvis/Worker 3`. It asserts `visibleRows "#Wayfold@Worker"` returns all three Wayfold workers. **Observed false** (NUnit expected True, actual False), exit 1, job `fcdf0a323a9f4d88ac0491ad5911dbec`. This exercises production `AgentView.visibleRows` and the real selector parser; no owner source change was made.

Existing positive tests only include numbered workers without a bare `Worker` name, so the exact-name switch is not exercised. The frozen full .NET 11 Client Release suite still passes 595/595.

**Required correction:** define partial agent matching so `@Worker` preserves the task's all-name-containing-Worker semantics even with an exact bare name present, and ensure `@Worker 1` still disambiguates Worker 1 from Worker 10 through an explicit, predictable name/token boundary or other documented matching rule. Do not make matching silently depend on incidental presence/absence of a same-named agent. Add both negative roster tests plus existing order/project-scoping/Tab regressions.

**Severity: P2**, blocking this UI feature's stated selector semantics.
