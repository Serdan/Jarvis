# P2 — Distinct UUID spellings bypass launch and browser-intent reuse checks

**Frozen source:** `0e369b3e2f582513d5478816ffbb59970b3e6d07`  
**Location:** `Client/Modules/WorkQueueCrossProjectPlanner.fs:120-123, 230-239, 292-293, 354-365`.

`validUuid` accepts any nonempty GUID via `Guid.TryParse`, including uppercase, braces, and compact forms. All subsequent uniqueness and original-identity checks use unnormalized string keys (`Set.ofList`, `Map.ofList`, `ContainsKey`), which distinguish different spellings of the *same* UUID.

**Concrete counterexample (source-level):** Use the otherwise valid one-project fake fixture with a **Released** account token and `TerminalPendingRelease` existing launch whose `LaunchId` is `aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa`. Supply a new eligible candidate with the *same GUID* rendered `AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA` and a distinct browser UUID. Because the token is released the occupied count is zero; the raw-string `ContainsKey` checks miss both original records. With matching project, role, catalog and source proof, the planner returns `WouldLaunch` rather than `NeedsAttention` for a reused immutable launch identity. The same aliasing applies to original browser operation UUID reuse, as well as candidate-vs-candidate duplicate checks.

This breaks WQ08's explicit original-ID preservation and no-reuse safety criterion. The module remains fake-only and `CanDispatch=false`, so no live duplicate send occurs in this submission, but `WouldLaunch` is an incorrect eligibility preview for a future adapter.

**Fix:** Treat UUID identity semantically at every duplicate/reuse comparison (parse once to `Guid`/canonical D-form, or require and validate a single canonical textual format at each port boundary). Ensure account tokens, existing launches, candidate IDs and original browser IDs all use the same identity normalization before matching; do not silently mint or substitute replacement IDs.

**Regression tests:** Released-token + terminal-launch case with uppercase candidate alias must not return `WouldLaunch`; a reused browser UUID with case/format variant must be rejected; duplicate candidate UUID aliases must report conflict. Preserve the exact caller-supplied original IDs in diagnostic output.

**Validation:** The existing 17 focused .NET 11 Release tests passed on the frozen worker worktree, but do not cover mixed UUID spellings. The counterexample above follows directly from the frozen implementation; no additional executable repro was run (the local shell runner denied `bash`).