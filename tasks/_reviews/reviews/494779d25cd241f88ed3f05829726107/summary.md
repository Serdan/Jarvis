# Review: incremental AGENTS.md / typed checkout resolution

Source: `refs/heads/feat/incremental-agent-instructions`, commit `494779d25cd231f83ed3f05829726107c83a8bf5`
Target: `refs/heads/main`

Verdict: **Changes requested** (two P2 correctness findings).

Validated:
- Client.Tests Release: 381/381 passed.
- Server.Tests Release: 93/93 passed.
- `git diff --check main...HEAD`: clean.
- Feature worktree clean and aligned with origin/feature branch.

Review coverage: instruction selection, scoped cache and bootstrap lifecycle; structured MCP AGENTS.md mapping; typed project/checkout parsing and path containment; ProjectBrowser, ClientShell, configured tasks and JobManager routing; source-spec command handling; worktree discovery and process-group cleanup.

Findings:
1. [P2] Delivery cache mistakes a truncated prefix for the complete document; full-body SHA does not encode delivery completeness (`findings/truncated-payload.md`).
2. [P2] Two-minute Working-state cache can suppress implicit activation and rebootstrap after an assignment transition on another client (`findings/cross-client-turn.md`).

Re-run both Release suites and exercise new regression tests before integration. No source mutation or deployment performed during review.
