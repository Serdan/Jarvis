# Independent review — side-effect-free cross-agent observation (P1)

**Frozen source:** `9033bcdc0213b320b1391d96a6cb305d34ffed50` on `refs/heads/feat/observe-agent-readonly`; target `refs/heads/main` (currently `2a52211ba2cc2c3fa3204fe8ea2d1c6f9ca27965`). Review is unclaimed; no author approval or production deployment.

## Contract and authority
`observe_agent(projectName, agentId, targetAgentId)` makes actor and subject distinct. The client's incoming command rejects an observer label differing from the separately delivered tool-call actor *before* source resolution or activity attribution. The observer must be a registered agent in the bound project, and the target must be a registered agent in that same ledger; the authenticated OAuth user's workspace permission is the authorization boundary. Agent labels are not separate authentication principals.

The command is excluded from Ready-to-Working activation, Seen cache writes, working-turn guidance and notifications. The existing self-`agent_inbox` still activates a Ready assignment once; its tool metadata and capability catalog now correctly mark it as state-mutating, unlike the read-only observer.

## Single canonical snapshot
`LedgerBindingConfig.loadWithSnapshot` fetches and verifies the bound Git ledger once, preserving the pinned trust head. `ReviewWorkflow.getObservedAgentInboxAt` validates *both* registered observer and target inbox against that **same immutable commit** via `getAgentWorkingTurnAt` and extracted `getAgentInboxAt`; existing self-inbox APIs retain their behavior. The eight-agent test asserts exactly **one remote fetch** per observation.

Return is a compact projection: state/version/sequence, execution/disposition, Working clock, focus task/attempt, blocked task IDs, counts of pending reviews/notifications/feedback and operator flags. It deliberately omits notification bodies, review evidence, source documents and session IDs. The result reports the canonical Git `snapshotCommit` and freshness plus a separate optional **client-local** lastSeen timestamp, never represented as canonical or a global heartbeat.

## Validation performed
- Focused Release regression: eight Ready agents × two observer sessions, each with exactly one Git fetch, no state/version/sequence/last-seen/ledger-head mutation, forged actor/unknown target/cross-project failure, real self-inbox activation and stable repeated Working clock — **passed**.
- Complete Server Release tests, including MCP metadata and permission tests: **123 passed, zero failed**.
- Full Client Release suite on frozen single-snapshot source: **527 passed, zero failed** (4m 20s). Combined Client + Server: **650 Release tests passed, zero failed**.
- `git diff --check`: clean. `git merge-tree --write-tree` into current main: succeeds without conflicts.

## Independent reviewer focus
Check observer-vs-actor identity and authority semantics; no implicit activation/Seen side effects; exact one-snapshot consistency even under concurrent ledger changes; response data disclosure and explicit lastSeen provenance; MCP metadata and version bump to protocol **3.63**; regression sufficiency and cross-project denial. Follow-up global Agents dashboard task is separate and should consume this non-mutating observation contract.

**Release gate:** protocol 3.63 changes both client and server; deployment requires separate explicit operator authorization.