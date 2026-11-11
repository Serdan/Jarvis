# P2 — Local AGENTS.md retrieval becomes dependent on remote ledger availability

**Frozen source:** `569f20d32c979e10969cbb0cd9a4f7b47e4ca0e4`

In `Client/Modules/AgentInstructions.fs:500-533`, any nonblank `GetAgentInstructionsCommand.AgentId` and existing trust-binding file take the `LedgerBindingConfig.loadWithSnapshot` path (lines 507-515). That performs a remote Git ledger fetch and trust verification before `getExplicitRepository` is even invoked (lines 516-528). Consequently, a transient remote outage or ledger contention makes the entire explicit instruction request fail, including for a **not-yet-registered** agent that has no role record. This is a regression from the previously local-only repository `AGENTS.md` retrieval. The r2 compatibility fix/tests cover an unregistered agent **with a healthy remote**, not one whose ledger is unavailable.

This matters because repository guidance is intentionally independent of orchestration setup, and the source docs/tests explicitly promise that unregistered callers can read it. A remote coordination outage should not suppress otherwise readable local workspace/project/nested `AGENTS.md`.

**Expected correction:** Keep the local repository hierarchy independently retrievable and accurately labeled when only role resolution is transiently unavailable. Report an explicit, typed **incomplete instruction-layers** diagnostic; never silently claim that required role or per-agent layers were loaded. Preserve fail-closed handling for a malformed/identity-mismatched *known* role record. Avoid unbounded or repeated network fetches on every local hierarchy continuation page when no new role resolution is required.

**Regression tests:** Bound ledger with the remote made unavailable + local `AGENTS.md` + valid unregistered actor must still return local hierarchy with explicit role-layer unavailability; formerly healthy registered role resolution must not be silently treated as empty; malformed known role/configuration must still fail explicitly. Include a paginated hierarchy case.

**Evidence:** Code-path inspection of the frozen branch; no dynamic offline test was executed during this review.