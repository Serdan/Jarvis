# Independent re-review — Agents TUI per-project status lines

**Verdict: Accept. Both previously blocking P2 findings resolved.**

Frozen source: `a8ed81c0837a427021b845a81f475e5949e8a163` on `refs/heads/feature/agent-project-status-lines`, correcting rejected `ffa3aeb584f916046e31f4442928d0820f6684ac`.

## Re-reviewed fixes

1. **Project alias filtering:** `AgentDashboard.snapshotFromResults` now stores every configured alias in both `ProjectSummary.Aliases` and `AgentRow.ProjectAliases`. `AgentView.visibleProjects` and `AgentView.matches` match `@project` selectors against those aliases while retaining stable canonical ledger identity for row selection, avoiding false last-seen attribution for same-named distinct ledgers. New regression explicitly checks `@owner`, `@reviewer`, simultaneous selectors, colliding alias names, and prevents unwanted cross-ledger matches.
2. **Canonical head consistency:** `AgentDashboard.load` validates local approved ledger bindings before remote I/O, groups aliases by canonical project/ledger/epoch identity and fetches **once** per group using the verified `loadWithSnapshot` path. It verifies approved transport agreement and each alias's current trusted-head ancestry before publishing counts; binding, trust or head inconsistencies cause unavailable/unknown totals, not fabricated zero or arbitrary selection. The typed test adapter also rejects contradictory heads or contents under one identity in either order.
3. Existing counting semantics (tracked/unread/review/working/ready/waiting/parked), explicit unavailable/partial status, narrow-terminal wrapping/paging and existing refresh cycle remain intact; no cross-agent inbox/activation or extra polling was added.

## Independent verification

- Full **Client Release 543 passed, 0 failed**, job `1517f57d125f457894f2a4a3ffb03d4a`. Includes new selector/head consistency regressions.
- Server Release job `57a19fcc8e0b411ab971fee8451f9c65` exited **0**; the tool returned no individual test totals.
- Exact frozen HEAD verified, working tree clean and `git diff --check` clean.
- `git merge-tree --write-tree main a8ed81c` successful and conflict-free.
- No source modifications, integration, or deployment by reviewer.

**Decision: Accept.** Integration and release remain Lead/operator responsibilities.
