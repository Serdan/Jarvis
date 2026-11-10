# Independent review — per-project agent status lines in Agents TUI

**Verdict: RequestChanges — two P2 correctness findings.**
Frozen source: `ffa3aeb584f916046e31f4442928d0820f6684ac` on `refs/heads/feature/agent-project-status-lines`; base `2a52211ba2cc2c3fa3204fe8ea2d1c6f9ca27965`; target `main`.

## Reviewed behavior

The implementation derives per-project Tracked/Review/Unread/Working/Ready/Waiting/Parked counts from the existing verified Git-ledger dashboard, distinguishes unavailable from zero, preserves global totals, and adds compact per-project status lines, paging and narrow-terminal wrapping. Stable ledger/project/epoch identity deduplicates bound aliases and distinguishes same-named ledgers. A canonical identity-based row key avoids agent-selection collisions. Local last-seen attribution is intentionally suppressed when a display-name alias could refer to multiple ledgers. The new path uses the existing dashboard refresh cycle rather than cross-agent inbox calls or a new timer.

## Findings

**P2: Deduplication breaks `@project` filters for non-primary aliases** — `findings/project-alias-filter.md`.

`AgentDashboard.snapshotFromResults` retains the **first** name of a deduplicated ledger and keeps other aliases only in `ActivityKeys` (last-seen). Both `AgentView.visibleProjects` and `AgentView.matches` filter against that retained `Project` name alone. Example: when `owner` and `reviewer` are bound aliases to the same ledger, `@reviewer` now filters out the entire status line and agent roster. This is an explicit acceptance regression in project/filter selection. Preserve aliases as first-class selection/filter metadata for both summaries and rows; add `@owner`/`@reviewer` tests, including collisions among distinct same-named ledgers.

**P2: Parallel duplicate-ledger snapshots are arbitrarily discarded without head consistency** — `findings/duplicate-ledger-heads.md`.

`AgentDashboard.load` fetches every alias concurrently. Each `projectSnapshot` has a verified immutable `snapshot.Commit`, but its return tuple drops the commit before `snapshotFromResults` deduplicates by `HashSet.Add(identity)` and silently keeps the first alias. If a write lands between the alias fetches, a prior Ready/0-unread result may overwrite the later fetched Working/1-unread result in the final refreshed dashboard. Deduplicate alias identities *before* fetching to obtain one canonical view per ledger, or carry exact commits and select the newest verified descendant / flag a conflict. Add a regression for different valid heads under a common identity. This is a genuine freshness correctness issue, not a claim that different projects must share one Git commit.

## Independent verification

- Client Release suite: **540 passed, zero failed**, job `6ae01671da1240c9be5c32c8fa93cf37` (exit 0).
- Server Release command: **completed with exit 0**, job `1df0bc816d3a46c0b13ab285ac3c56dd`; provider returned no per-test output, so no individual Server count asserted.
- Exact worktree HEAD equals frozen `ffa3aeb...`, worktree clean.
- `git diff --check`: clean.
- `git merge-tree --write-tree main ffa3aeb...`: succeeds without conflict.
- No source edits, integration or deployment performed by reviewer.

The principal feature and tests are substantially correct, but alias filtering and concurrent duplicate snapshot selection must be fixed before acceptance.
