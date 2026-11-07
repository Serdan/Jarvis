# Re-review: atomic AGENTS.md delivery and cross-client Working-state refresh

**Source:** `feat/incremental-agent-instructions` @ `3d332539cf979855f52ccd9878f5733a227b03f7`
**Target:** `main`
**Verdict:** **Changes requested**.

The two previously identified functional defects are now corrected: oversized instruction files are not partially injected, and ordinary agent activity explicitly checks for a new Ready/Working generation before delivering instructions. Focused tests model both scenarios and pass.

**Verification:** The prior full Release runs for this exact HEAD reported Client.Tests 382/382 and Server.Tests 93/93. Independently reran focused Client Release 28/28 and Server Release 93/93, plus `git diff --check`; all pass. Worktree clean, same revision as origin.

**New findings:**
1. **P1**: Each ordinary agent operation now incurs at least two serial GitHub ledger fetches, one inside a cross-process trust-binding lock. Severe tool latency/throughput regression; see `findings/remote-fetch-per-tool.md`.
2. **P2**: File omitted -> delivered -> omitted with the original bytes can silently suppress the renewed omission warning, because the cache keys warnings and content separately; see `findings/stale-omission-notice.md`.
3. **P2**: Workspace-root AGENTS.md omission incorrectly instructs users to invoke a project-scoped read_file, which cannot access workspace parent; see `findings/workspace-retrieval.md`.

Correct and re-review before integration. No source changes, merge, or deployment performed during this review.
