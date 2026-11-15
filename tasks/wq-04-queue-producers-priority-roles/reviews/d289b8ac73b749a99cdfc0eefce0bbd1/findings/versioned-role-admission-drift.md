# F01 — Blocking: versioned role demand diverges from canonical project admission

**Severity:** Blocking correctness / ChangesRequested. **Frozen source:** `6f0686720e56e198e085111cd8f22f5c6a6d7b83`.

## Evidence
- `Server/WorkQueueProducers.fs:274-320,355-411` models versioned `RoleCatalog` and selects demand using `RoleConfiguration.QueueSubscriptions`; in-flight launches are rematched using their pinned role version.
- `Server/WorkQueueContracts.fs:428-444,457-511,644-654` still derives launch compatibility from `Project.Subscriptions.[launch.RoleId]`, without looking at `launch.RoleVersion` or the new catalog. `reconcileCredits` uses the same legacy compatibility. The WQ-04 Git mutation path never writes configured role versions into a canonical project role catalog; `configureRole` is only a separate pure value.
- `Server/WorkQueueProducers.fs:238-242` also tests role-message subjects against the legacy `Project.Subscriptions` rather than current versioned role configurations.

## Deterministic counterexample from exact source
1. Canonical project has `Lead -> { planning }` in `Project.Subscriptions`, and an Available `FrozenReview` stage.
2. Versioned catalog configures `Lead` version 1 with `QueueSubscriptions={ review }`, Enabled=true and matching coverage.
3. `selectRoleDemand ... "Lead"` chooses the review stage because `eligibleRole` uses the catalog.
4. `Contracts.admitProject` for the same `Lead` v1 launch rejects `NoCompatibleMatching`: `compatible` checks the stale `{ planning }` project map. The opposite drift can allow a launch credit for work removed from its pinned subscription under the existing admission path.

This breaks the requirement that non-agent Scheduler demand, launch reservations and simultaneous credit rematching agree on versioned eligibility, even while all focused happy-path tests pass. The review cannot independently authorize integration based solely on an inert standalone selector with conflicting eligibility invariants.

## Required correction
Make one version-aware, canonical, CAS-visible compatibility predicate authoritative for demand selection, admission, `reconcileCredits`, Role-message projection and claim validation. Pin historical role versions for existing launches; reject/hold missing versions and uncovered role catalogs. Add two-clone negative tests for subscription add/remove, in-flight pinned launch, concurrent eligibility mutation and existing-credit rematch/fencing. Keep runtime activation disabled.