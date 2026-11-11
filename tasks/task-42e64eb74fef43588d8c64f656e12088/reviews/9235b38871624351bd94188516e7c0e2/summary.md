# Independent review — shared roles and persistent agent instructions

**Verdict: Request changes (one P2 correctness regression).**

- **Frozen commit:** `569f20d32c979e10969cbb0cd9a4f7b47e4ca0e4` (`feat/shared-agent-roles`), pinned base `ecb59e2272c7e6c4e127345f5034695c2f6e5465`.
- **Source inspection:** reviewed `RoleInstructions.fs` (versioned RoleDefinition and per-agent config, strict Git ledger paths, CAS mutation, idempotent operation receipts, bounded bodies, role/agent resolution, current-hash provenance); `AgentInstructions.fs`, `ReviewWorkflow.fs`, `OrchestrationApi.fs`, `Server/McpTools.fs`, associated regression tests and user documentation.
- **Independent Release tests on frozen worktree:** Client.Tests **554/554 pass**, Server.Tests **127/127 pass**, focused RoleInstructionsTests **7/7 pass**. The latter are included in full Client test count. Tests actually executed against frozen branch; no source edits.
- **Merge compatibility:** `git merge-tree --write-tree ecb59e2 569f20d3` returned clean tree `7e97e807ca21584e893d9d6981ce2f2e39e446a6`; this is not approval to integrate.
- **Issue requiring correction:** `findings/remote-ledger-blocks-local-agents-md.md`. Bound-ledger `get_agent_instructions` now waits for a verified remote ledger fetch before reading even local `AGENTS.md` for an **unregistered** caller. A temporary ledger outage blocks otherwise independent local repository guidance. The healthy-remote unregistered compatibility test does not cover this regression. Keep source guidance independently available with explicit disclosure that role layers could not be resolved; do not silently claim completeness or bypass malformed known-role verification.
- **Evidence limit:** The offline regression was proven by exact synchronous `Result.bind` code path, not by a live fault-injection run. A regression test is required with the fix. No other blocking source defect was identified in the inspected code.

**Next:** Worker 2 should correct this behavior and add negative tests, then publish a new frozen commit and request fresh independent review. Hold merge/deployment until accepted and separately authorized.