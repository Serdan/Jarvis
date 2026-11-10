# P2 — Deduplicating project aliases breaks existing @project filtering

**Frozen source:** `ffa3aeb584f916046e31f4442928d0820f6684ac`

**Requirement:** The task explicitly requires preserving project/filter selection and ordering while deduplicating multiple views of one ledger.

**Evidence:** In `Client/Modules/AgentDashboard.fs:217–250`, `snapshotFromResults` stores the first project name for each ledger identity, drops later names from `unique`, and retains aliases **only for presence** in `ActivityKeys`. Neither `AgentRow` nor `ProjectSummary` retains those aliases for filter matching. `AgentView.visibleProjects` (lines 94–101) filters only `project.Project`; `AgentView.matches` similarly passes only `row.Project` to `ActivityFilter.matchesTerms`. Text search using `ProjectLabel` is not equivalent to an `@project` selector.

**Concrete case:** The existing workflow fixture binds project names `owner` and `reviewer` to one ledger. Under the new deduplication the result retains one `owner` summary/roster. The existing selector `@reviewer` now returns no project status line and no agent rows, whereas before this change it showed the `reviewer` view. The new alias regression `aliasesOfOneLedgerPreserveUnambiguousLocalActivityAttribution` only tests last-seen attribution; it does not test `visibleProjects` or `visibleRows` under the second alias.

**Fix:** Preserve the set of all valid project names/aliases for each deduplicated ledger and match `@project` selectors against that set in both the project summaries and agent roster. Keep the canonical identity as the stable selection key. Add tests for `@owner`, `@reviewer`, and an alias paired with another distinct ledger of the same display name, ensuring no unintended cross-ledger match.

**Severity:** P2 functional regression in the promised filtering behavior.
