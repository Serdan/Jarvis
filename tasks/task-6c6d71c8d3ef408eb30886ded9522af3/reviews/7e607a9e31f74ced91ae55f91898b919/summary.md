# Reviewer 3 — global Agents dashboard independent frozen review

**Verdict: ChangesRequested.** Review `7e607a9e31f74ced91ae55f91898b919`, frozen source `87cccce757c57037cd91ec657eb64aa3ffe487e1`, base `b277cd0d1b850b97ad81d889e2278aaef74e74ae`.

## Findings

### F01 — P1: global path bypasses same-ledger alias remote-consistency gate

`Client/Modules/AgentDashboard.fs` keeps an important existing fail-closed invariant in `refreshGroup` (around lines 370–379): every local alias grouped under one canonical ledger identity must have the **same approved fetch/push remotes**. Otherwise it rejects the dashboard view as an inconsistent binding. The pre-existing `load` path passes the **whole group** to `refreshGroup` (around 410–427).

The new global path instead:
- `discoverClaims` groups project configurations solely by `bindingIdentity`, collects aliases, and discards their underlying bindings (around 438–460).
- `readForRoster` chooses only `claim.ProjectName`, re-inspects that single alias, and calls `refreshGroup request.Identity [| claim.ProjectName, binding, head |]` (around 466–493).

A client with two configured local aliases whose projectId/ledgerId/epoch match but whose approved remotes disagree will therefore **publish one arbitrary alias as a Fresh canonical roster**, when the existing local view rejects the same configuration. This weakens an explicit source-trust invariant in the newly shared cross-device projection. Do not silently merge a conflicting binding merely because identity fields match.

**Required correction:** preserve all configured aliases and verify compatible approved endpoints and descendant heads against the same snapshot before broadcasting; fail closed and expose the inconsistency instead of selecting the first alias. Add a negative regression where two same-ID aliases disagree on approved Git remotes, verifying no global Fresh row is published. Keep valid identical-remote alias deduplication and distinct same-named ledger handling.

### F02 — P2: default global Agents screen omits Parked agents

The task explicitly requires displaying **every known agent** from all clients and **all projects' agents by default** with an optional machine filter. `AgentDashboard.fromGlobalRoster` correctly rehydrates Parked rows and includes them in counts, but `ConsoleTui.fs` uses `AgentView.visibleRows` (around line 914), whose unconditional `row.State <> "Parked"` filter (AgentView.fs around line 336) hides every Parked agent, **even when searching for Parked or its exact name**. The existing test `canonicalAgentSearchUsesProjectStatusTaskAndSourceRef` expects `lookup "Parked" = []`; the broader test `parkedAgentsAreHiddenFromTheTuiButRemainInTheLedgerSnapshot` also encodes the old behavior.

This means a user cannot inspect a known registered Parked agent from another device in the new global screen, despite it remaining counted and possibly holding roles or unread notifications. That is not the promised complete roster.

**Required correction:** expose Parked agents in the global view by default; if the historical hide-Parked preference is needed, make it an explicit opt-in filter/toggle rather than an irrevocable exclusion. Test a remote Parked agent with roles/notifications and machine-filter/search behavior.

## Positive evidence / boundaries

- Reviewed complete 16-file frozen delta, client discovery/hydration and UI, SignalR bridge, server cache, backpressure/fallback, and nine server cache regression tests. The server cache is disposable, user-scoped and keyed by canonical identity; provider refreshes use request IDs and client Git ancestry checks, with stale/offline and unknown-last-seen presentation. Those choices are sound for the scoped goal outside the findings above.
- Independently executed Linux .NET 11 Release client hermetic tests on the exact frozen clean Worker 7 checkout: `AgentDashboardCacheTests|AgentViewTests` **57/57 passed**, exit 0, private scratch removed.
- Independently executed .NET 11 Release `Server.Tests` `AgentRosterCacheTests`: **9/9 passed**, exit 0.
- These successes do not exercise the conflicting-remotes global-discovery case or demonstrate that hidden Parked rows meet the new task semantics. Worker reports solution Release build passed, but the complete unfiltered suite exceeded its 120s command cap; no full-suite acceptance is claimed.
- No source edits, merge, deployment, or new server persistence. Correct F01 and F02 on a new frozen SHA and resubmit for independent review.
