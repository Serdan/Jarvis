# P1 — Review/integration receipt rows are accepted without terminal stage provenance

**Frozen commit:** `76944ed50389b73b93ee307063049e8e80f6a368`

**Location:** `Server/WorkLegacyImportPreflight.fs:231-267, 308-349`; downstream `Server/WorkLegacyImportSqlite.fs:227-236`.

The preflight validates the contents of a review/integration receipt and validates a terminal stage *if one is found*, but does not require the reverse: every accepted receipt must correspond to the matching stage's finalized domain decision.

Counterexamples, using `twoProjects() |> reviewAndIntegration` in the existing tests:

1. Change `R.Domain` to `DomainState.Undecided` while leaving the accepted `Reviews` entry untouched (and leaving `R.State = Blocked`). The `FrozenReview` domain cases at lines 308-316 do not reject this, and the orphan check only checks kind/subject. The accepted receipt can still authorize `T` and satisfy a graph acceptance.
2. Change `T.Domain` to `DomainState.Undecided` (and `T.State = Blocked`, `IntegrationAuthorized = false`) while leaving `Integrations` untouched. The integration-specific domain checks do not run, and the orphan check only checks kind. Its integration receipt still counts as acceptance evidence and is written as an observed integration record by C1.

This contradicts the requirement that no incomplete or unknown approval/integration be materialized as a verified outcome. The `acceptedEvidence` set is currently formed from receipt lists before those lists have been consistently bound to stage state.

**Required:** Match *every* accepted review and integration receipt to the correct stage, subject, domain receipt, frozen source, review authorization, and observed integration effect as applicable. Reject terminal receipt data with an undecided/stale stage. Build the accepted-evidence set only from proven stage/receipt pairs. Add fail-closed negative tests for both counterexamples and graph acceptances that cite those receipts. The true production writer fence remains a separately outstanding cutover gate; this finding concerns the submitted offline C0/C1 contract itself.
