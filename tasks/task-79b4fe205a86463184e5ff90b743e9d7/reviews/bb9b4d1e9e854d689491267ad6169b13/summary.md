# Independent re-review — ObserveAgent and uniform state-change response visibility

**Verdict: Accept — no blocking findings.**

Frozen source: `8716439cfe4b6187978b3ae259d97a435f26510e` on `refs/heads/feat/observe-agent-readonly`; base `a9adf77b0b1b75420f2d99e901319df713e817b3`, target `main`. This re-review addresses the previous ChangesRequested review `d6f9ffaf3efa42a2a64d8f6319dd2db5`.

## Previously blocking issue: resolved

Prior P2 finding required that the caller of self-`agent_inbox` see the actual *committed* Ready-to-Working transition, not just the resulting state. The revised client captures `AgentInboxActivation` only when `activateReadyAgent` confirms a new ledger write (not `AlreadyApplied`). It includes the correct actor/project, `activatedByThisCall`, `beforeState=Ready`, `afterState=Working`, committed head and operation ID. Repeated reads show `activatedByThisCall=false` without inventing prior state. The receipt remains in the typed transport envelope if the subsequent inbox read fails. The server now attaches the receipt to visible MCP text and `jarvis/inbox_activation` metadata, preserving the primary result/error.

## Independent code review

- Cross-agent `ObserveAgent` remains a separate, non-activating command. Observer/target labels are distinguished and checked against the transport label; authority comes from authenticated workspace permissions, not a named agent principal. Both identities and the target projection are derived from one freshly verified canonical Git-ledger snapshot. Last-seen is client-local, explicitly noncanonical.
- Public state-effect reporting now attaches a consistent `agentStateChanges` JSON block and `jarvis/agent_state_changes` metadata to forwarded project tool responses, including errors. An empty list only asserts there is **no confirmed canonical change**; it is not a claim that an uncertain operation could not commit.
- Cross-project notification dispatch merges confirmed source/destination state effects; failures after a confirmed source write retain evidence. Intermediate invalidation may be duplicated, but not a correctness blocker.
- The additional text/metadata preserves existing primary success/error content, AGENTS.md instruction handling, and image/export result construction. `AgentCommandResponse` still deserializes legacy clients lacking the optional activation field. Protocol 3.63 remains a coordinated client/server release boundary.
- The older `agent_inbox` can still be misused to inspect a different agent by label, contrary to its documented self-inbox intent; this is a pre-existing API limitation and does not undermine the new explicitly observational path.

## Independent verification

- Stable .NET 10 SDK `dotnet build Jarvis.slnx -c Release`: **passed**, zero errors.
- Full `Client.Tests` Release suite: **527 passed, zero failed** (job `d8a521a690524710a404287c83bea658`).
- Full `Server.Tests` Release suite: **126 passed, zero failed** (job `ca9da4e6f9144f569f24a1f88efd6b47`).
- **653 total passing tests**, including new client regression for first/repeated activation and post-activation read failure and new server tests for public MCP evidence, errors, and cross-project aggregation.
- `git diff --check`: clean. Frozen worktree: clean; exact HEAD matches frozen source. `git merge-tree --write-tree main 8716439c...`: success, no integration conflicts.

No deployment, source mutation, or integration performed. Lead should integrate the accepted commit and coordinate the client/server deployment only with operator authorization.
