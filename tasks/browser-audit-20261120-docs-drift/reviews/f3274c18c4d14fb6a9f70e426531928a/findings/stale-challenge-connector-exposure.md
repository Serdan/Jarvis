# Finding 1 — Current Challenge connector availability is documented incorrectly
**Severity:** Moderate (operational accuracy / task acceptance)
**Frozen source:** `609157037f016f1ee4c09fd6b2b0b4991224fab3` based on `d702352ef705f9991ac1cb14dc15a13cce78aa44`.
**Affected source:** `docs/DurableAgentChallenges.md:3`, `docs/DurableAgentChallengesIntegration.md:5`, and `docs/ManagerHeartbeat.md:39-40`.

These new statements say the current ChatGPT connector does not expose direct Challenge list/get/subscription operations. This is now false in the live connector reviewed on 2026-11-21: exact callable operations `mcp__Jarvis__list_challenges`, `mcp__Jarvis__get_challenge`, and `mcp__Jarvis__set_challenge_subscription` are present, each with documented argument schemas. The independent public `list_commands` check reports protocol `3.68` and the underlying Challenge catalog, consistent with source. The missing-wrapper claim became stale after connector refresh. This is a live connector-exposure statement, not a harmless description of historical source.

**Impact:** Operators and Lead are told to treat a functioning Challenge triage control surface as blocked, contrary to the task's requirement to reconcile observed connected capabilities. This may prevent routine use of the now-exposed structured controls.

**Bounded correction:** Update the three present-tense wrapper-exposure claims to distinguish historical lack of exposure from currently callable tools. Preserve the independent deployment/cutover caveat, optional subscription semantics, empty-triage limitations, and no-unattended-scheduling restrictions. No code or schedule changes are needed. Recheck the live catalog upon publication because plugin refresh can alter wrapper visibility.

**Independent evidence:** Connected tool definitions exposed all three direct methods, with read-only `list_challenges` / `get_challenge` and explicit write `set_challenge_subscription`; `list_commands` reported protocol 3.68. Exact-frozen `git grep -n` confirmed three statements in the named files.