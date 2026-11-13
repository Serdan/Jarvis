# P2 — Absent numeric agent selector becomes task-text search and returns the wrong agent

**Frozen source:** `4083d165714a05409b8943937f78df566122ac4c`, `feature/tui-project-agent-filter-tab`, base `39168b69295a565528e7fc83f4278da81569afa5`. Independent re-review `2f52cab7f4dd4be0baa377ecece723cc` (following previously requested changes for bare `Worker` masking partial `@Worker`).

## Confirmed source path

- `Client/Modules/AgentView.fs:303-310` calls `ActivityFilter.parseTermsWithCandidates` with names from **the current roster**, and later searches each row with `FilterSelectors.matchesAgentTerm` plus ordinary searchable text (including task ID/title/source).
- `Client/Modules/FilterSelectors.fs:70-90` `selectorAndText` consumes the **longest current-name-compatible sequence** of whitespace-separated words from an explicit `@agent` selector. When that exact sequence is not present as a substring in *any current agent name*, it falls back to the **first word only** and converts remaining words into **unscoped free text**.
- Thus `#Wayfold@Worker 1` with only a Wayfold `Worker 2` in the roster is parsed as `Projects=["Wayfold"], Agents=["Worker"], Text=["1"]`, rather than `Agents=["Worker 1"]`. A task whose ID/title contains `1` makes the wrong `Worker 2` appear in the results. When an unrelated `Jarvis/Worker 1` is added, the same literal query parses `@Worker 1` as a complete agent selector, and the Wayfold Worker 2 disappears. The meaning of an identity filter therefore changes with **unrelated roster presence**.
- The new `matchesAgentTerm` numeric end-boundary helper is correct in isolation but is never given `Worker 1` for this absent-name case. This is distinct from and not repaired by removal of the old roster-dependent exact-name set.

## Independent negative reproduction

Created only in reviewer-owned clean-from-frozen worktree `reviewer1-agent-selector-roster`, HEAD `4083d165...`; no Worker 2 branch changes. Appended test `AgentViewTests.missingNumericAgentSelectorMustNotFallBackToTaskText`, which uses `Worker 2` in Wayfold with an unrelated focused task titled `Sprint 1 follow-up`, and applies `#Wayfold@Worker 1`. Expected **no agent** because Worker 1 is absent. **Observed: `Worker 2`**, NUnit `Expected: <empty>; But was: <"Worker 2">`, exit **1**, job `50c9a1bfc72a4b8fa12926a0e93ae14e` (`Client.Tests/AgentViewTests.fs:420`). Test also contains a second roster variation with Jarvis Worker 1; the first failing assertion stops execution, but its correct behavior follows from the inspected parser and matching rules.

The **44/44 existing AgentView and FilterSelector Release tests passed independently** at the unmodified frozen source (job `920e0cfff9fe485cba7e724dbf61810e`), showing why they missed this absent-name negative case. Frozen worktree `tui-project-agent-filters` at exact HEAD was clean, and `git diff --check 39168b69..4083d165` exited 0.

## Required correction

Explicit agent selector tokens, especially a numeric or multiword identity like `@Worker 1`, must not silently migrate into unrestricted descriptive text solely because that name is absent from the current roster. Preserve predictable selector parsing independent of unrelated agents; a selector for an absent agent should return no agent even if task metadata contains the same digit/word. Keep ordinary free text after completed selectors, partial prefix matching, case-insensitive matching, Tab longest-common-prefix behavior and `@Worker` broad selection. Define a deterministic way to distinguish selector tails from free text without relying on incidental roster presence (e.g. grammar/token boundaries or explicit disambiguation if necessary), and test both absent/present candidate rosters, unrelated project addition, numeric task metadata, and Enter/Tab live updates.

**Severity P2 — blocks this re-review.** Wrong agent selection is a correctness failure of the specified `@agent` filter, not a display-only stylistic issue. No source edits to frozen owner branch, merges, deployment, or production changes by reviewer.
