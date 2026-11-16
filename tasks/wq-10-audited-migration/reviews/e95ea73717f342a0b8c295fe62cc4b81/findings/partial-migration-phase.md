# P1 — Activated account plus Frozen project incorrectly reports ready

**Frozen source:** `88863eead9c763e984bd34d3111812f67fd7ac2a`  
**Location:** `tools/Wq10MigrationPreview/Program.cs:117-124,175-182,226-232,393` (account/project inspections never reconcile migration phases).

The report reads `account.migrationPhase` and each canonical project's `state.migration` but never checks their relationship. `InspectAccount` only emits a **WARNING** for `Activated`. With an account marked `Activated` and a project still `Frozen`, and all four canonical coverage flags true, the inspector returns `PREVIEW_READY_FOR_REVIEW`. Its inert plan also unconditionally proposes `Account: ... Preparing` even though the account is already `Activated`.

This is a concrete partial A/B migration mismatch. The accepted design (`docs/specs/work-queues-and-ephemeral-executions.md` around lines 444-446, 524-528) requires matching account-Activated/project-Prepared(epoch) authority and treats partial migration as unready. **Do not compare the raw account and project Git-ledger identity epoch numbers:** they are independent binding epochs. Compare migration *phase* and, only once a real Prepared receipt exists, its specified account migration epoch.

**Independent reproduction:** disposable two-clone fixture: set account `state.json.migrationPhase="Activated"`; create canonical `records/work-queue/project.json` with `state.migration={"case":"Frozen","fields":[]}`, empty works/launches/messages and all four coverage flags `true`; supply the three proof booleans `true`. Observed `PREVIEW_READY_FOR_REVIEW`, only `WARNING:ACCOUNT_ALREADY_ACTIVATED` and historical-notification warning.

**Requested correction:** add a blocker for incompatible/partial cross-ledger migration phases, distinguish an existing activated account from a fresh Preparing preview, and exercise this fixture. No real epoch change or mutating command is needed.
