# P1 — Mode 15 obtains owner/version after corrupt reverse-index injection

**Frozen source:** `87bbadfe0bac60ec4d14f51d382841076f427d18` (tests/docs only), `Client.Tests/ChallengeStateMachinePropertyTests.fs:1811–1817`.

The `ChallengeIndexFaultMatrix.probe` case `15` executes:

1. `ChallengeIndexFaults.inject a ownerEdge body` (line 1812), introducing a forged current-generation owner edge into the disposable canonical ledger.
2. **Only afterward**, `ReviewWorkflow.getAgentInbox b "Agent 3"` (line 1813) is used to read `blocked.Blocked.Head.Attempt.Version` and `blocked.Agent.Version` (line 1816).
3. The resulting operation uses those post-corruption versions in `ChallengeWorkflow.requeueAttempt`.

This is precisely the test-oracle ordering problem called out by independent production Reviewer 5, decision sequence **9845**. The test must establish the valid pre-injection direct-requeue request tuple (owner, attempt, task, generation, task/attempt/agent versions) while the canonical ledger is intact, **then** inject the extra edge, invoke the public *zero-dependency* direct requeue without consulting corrupt owner projections, and assert the correct typed integrity error plus identical remote HEAD/sequence/receipts/index/audit. Currently a corrupt projection can influence the request or fail before the intended direct requeue is exercised; a green result would not prove the public fail-open regression is covered.

The case is unchanged in the new tests-only source; earlier failures of this mode against unfixed production remain **expected-red**, not a substitute for this correction. Preserve all original 9000–9003 seeds, other 4–25 matrix modes and the independently reviewed production source boundary. **ChangesRequested** for this correction.