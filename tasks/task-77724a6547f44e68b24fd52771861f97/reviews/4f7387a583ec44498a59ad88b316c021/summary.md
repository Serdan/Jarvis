# Reviewer 1 — independent frozen TUI project/agent selector and Tab completion review

**Verdict: RequestChanges** — one independently reproduced P2 partial-selector mismatch. Frozen `b2c2644b4f61cfb8e24ce6625232561fa41a7eaf` on `feature/tui-project-agent-filter-tab`, task `task-77724a6547f44e68b24fd52771861f97`, review `4f7387a583ec44498a59ad88b316c021`, base `39168b69295a565528e7fc83f4278da81569afa5`.

## Positive source evidence

- New typed `Client/Modules/FilterSelectors.fs` parses display-only `#project` and `@agent` terms separately, supports contiguous/spaced mixed order, ignores markers embedded in ordinary prose, handles local-snapshot multiword names and free-text search terms with documented AND semantics. It does not touch actual project/agent routing, canonical ledger identity or `@global` protocol sentinel.
- `completeTrailing` computes deterministic case-insensitive **longest common prefix over all matching names**, never cycles, does not guess trailing suffix/separators, and preserves prior text and selectors. `AgentView.completeFilter` scopes agent candidates by preceding project aliases/canonical snapshot, and Activity completion is intentionally project-only because activity logs lack a verified agent ID.
- `ConsoleTui` preserves Enter/Esc/Backspace editing state; adds Tab only in AgentFilterMode and Activity FilterMode and updates visible results after each change. AgentMode `#` and `@` append without clearing earlier terms; project tokens/help/export changed to `#`. `AgentView.projectDisplayName` and aliases preserve duplicate project disambiguation; existing Parked-agent display suppression unaffected.
- The frozen diff touches only Client, Client.Tests and display-filter docs, not Common/Server/MCP/ledger. Exact frozen HEAD and owner worktree clean, `git diff --check 39168b69..b2c2644b` exit 0, `git merge-tree --write-tree main b2c2644b` exit 0, tree `5c34bc171676cc856046af33c788d17aaea07757`.
- Independently full **Client .NET 11 Release 595/595 passed**, exit 0, job `7c8789a355694f68ab7fd654002a6e91`. No Server test required for this client-only source diff.

## Blocking P2 issue: exact name unexpectedly overrides partial `@Worker`

See `findings/p2-exact-agent-name-masks-required-partial-selector.md`.

The task explicitly promises `#Wayfold@Worker` matches **all agent names containing Worker**. However `AgentView.matchesAgent` checks a global `HashSet` of exact agent names and switches from substring to exact equality when any agent's name equals the selector term. With a Wayfold roster `Worker`, `Worker 1`, `Worker 2`, filter `#Wayfold@Worker` shows **only the bare Worker**, hiding numbered workers. The same query starts matching those numbered workers again if bare Worker disappears. This is an undocumented mode switch based on roster membership and violates the stated broad-selector contract.

Independently injected a focused NUnit negative into a **reviewer-owned disposable detached worktree** `/tmp/jarvis-reviewer1-tui-worker-prefix-b2c2` at the exact frozen commit. `AgentViewTests.projectAgentWorkerSelectorIncludesExactWorkerAndNumberedWorkers` expects all three Wayfold Worker rows and excludes unrelated Jarvis Worker 3. It **failed** expected True / actual False, exit 1, job `fcdf0a323a9f4d88ac0491ad5911dbec`. Two initial test-file compiler diagnostics were corrected solely in the disposable reviewer test syntax before the final observed behavior failure; frozen Worker 2 branch was never altered.

**Required:** maintain `@Worker` substring/prefix semantics regardless of an exact bare Worker while still distinguishing `@Worker 1` from `@Worker 10` via an explicit predictable token/exact boundary. Cover both negative roster conditions, project-scoped selection and Tab continuation in regression tests. Do not change other UI modes or task identity.

**Conclusion: RequestChanges (P2).** Otherwise the scoped TUI code and full original suite pass. Submit corrected exact frozen commit for independent re-review; no merge, deployment or source edits by reviewer. Review outcomes already flow through canonical review feedback, so no discretionary named-reviewer notification is required.
