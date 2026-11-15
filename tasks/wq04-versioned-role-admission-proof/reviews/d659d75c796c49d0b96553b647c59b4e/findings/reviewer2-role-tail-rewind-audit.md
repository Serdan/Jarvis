# Reviewer 2 — independent frozen WQ04 role-catalog correction audit

**Canonical verdict: ChangesRequested (P1, historical high-water).**

- Task: `wq04-versioned-role-admission-proof`
- Review: `d659d75c796c49d0b96553b647c59b4e`
- Frozen exact source: `d2dac5bef128c0f8f07662b7e2d6e819be0a4dad`, direct parent `1cd0b88db631dc4258601e5e531f95c02a34ebd4`; base for prior task `e837d2d67277af8eb4b634ff58d5ba9e1b59d19e`.
- Scope: only `Client/Modules/WorkQueueRoleAuthority.fs` and `Client.Tests/WorkQueueRoleAuthorityTests.fs` changed in the correction; no active Scheduler, Manager, source handoff, admission or claim route changed.

## Verified corrections

1. **B1 — retroactive provisioning:** `initializeCatalog` now rejects a catalog created after any project launch (`WorkQueueRoleAuthority.fs:632-649`). `loadAt` insists every launch has an original same-CAS role-admission witness and original WQ02 admission receipt, matching unchanged original decision/witness blobs, role content OID, original catalog OID and admitted fields (`334-432`). There is no permissive legacy backfill. Two-clone legacy-before-catalog and post-catalog legacy admission negative tests are present.
2. **B2 — substitution of *retained* history:** `verifyRoleHistory` verifies each retained role version/receipt against its original Git operation commit and matching original request (`237-303`). `LedgerStore.getOperationOriginAt` confirms one first-parent addition and unchanged operation blob/trailers (`LedgerStore.fs:847-884`). Two-clone tests now substitute a shape-valid earlier definition or receipt; current load rejects both.
3. **B3 — operational claim binding:** `ClaimWork` remains a candidate feasibility preflight, distinguishes the deterministic first candidate, and returns both missing-authority flags. The separately named `authorizeClaimAt` always returns an integrity error until one future combined CAS writer can prove *actual* `Contracts.claimNext` selection/credit, role revision, source, account and generation (`546-574, 598-626`). The two-clone fixture races a real *legacy* admission and claim against a role revision, but correctly treats these as **not** WQ04 authorization. This is an explicit fail-closed integration boundary, not a positive joint-writer proof.

## Remaining blocking finding — C1 / B2 incomplete: historical tail-rewind still passes (P1)

The module authenticates only the role versions and operation receipts **still listed by the latest role-catalog JSON**, not the complete set of historically issued revisions. `validCatalog` checks that the current blob is internally dense with `Revision = Versions.Count = Receipts.Count` (`118-154`); `verifyRoleHistory` folds over `catalog.Receipts` from this blob (`286-303`). It does **not** pin a monotonic, externally authenticated issuance high-water, compare against every prior catalog transition, or enumerate older issued role operations that have disappeared from the current catalog.

Concrete cross-head scenario, using the same generic `LedgerStore.transact` fixture writer that the new tests already exercise:

1. From an empty project, provision the catalog and configure enabled `Lead v1` subscribed to `planning`. Save the canonical v1 catalog blob.
2. Configure `Lead v2` with `Enabled=false` (or narrower `review` subscription). The original v2 operation receipt remains permanently in the Git ledger.
3. With a second clone, apply a Git-CAS file change replacing **only** `records/work-queue/role-catalog.json` by the previously authentic v1 blob, expected current OID = v2. Keep the immutable operation records untouched. This is a canonical, shape-valid blob rewrite; it is **not** a malformed-history attack.
4. At the resulting fresh head, `readCatalogAt` accepts v1 (dense revision 1, one receipt), and `verifyRoleHistory` validates the original provisioning and v1 configure operation. It never looks for the omitted original v2 issuance. With no launches to audit, `loadAt` can return `Current[Lead]=v1` and a valid fresh `RoleRevisionProof`. `PlanNewDemand Lead` can then yield a successful preflight for available planning work, though the most recently issued role revision is disabled or no longer subscribed to planning.

This defeats the task's *historical immutable role versions / revision high-water / new sessions select current issued version* invariant at the proof interface. It is a stronger historical deletion/revision-rewind attack than changing the contents of a retained version. Existing `ShapeValidHistoricalVersionRewriteAndReceiptSubstitutionFailAcrossClones` tests (`WorkQueueRoleAuthorityTests.fs:226-260`) cover substitution, **not** removal of a complete previously issued tail and rollback to an authentic older catalog.

**Required:** bind catalog revision/high-water and complete issuance set to independently authenticated monotonic Git history (e.g. append-only per-version objects or a verifiable full transition chain), and reject a fresh head whose catalog omits any previously issued role/receipt, even when all remaining bytes and receipts are authentic. Add a two-clone regression that restores the old authentic v1 blob after issuing v2, asserts `load`/`inspectFresh` fail closed, and covers the v2-disabled/v1-enabled demand case. A new unproven provision epoch must not reset this history. Keep operational claims denied until the separate combined CAS writer has its own source/account/credit evidence.

## Independent checks and boundaries

- `git show` verified exact `d2dac5b...` parent `1cd0b88d...`, 2-file correction, and `git diff --check` exit 0.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~WorkQueueRoleAuthorityTests --verbosity minimal`: **9 passed / 0 failed / 0 skipped**, .NET 11 Release.
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-build --no-restore --verbosity quiet`: **233 passed / 0 failed / 0 skipped**. Previous fresh build also exited 0, but warnings truncated its test footer; the no-build run gave the explicit count.
- The tail-rewind is a **source-proven adversarial case**, not a regression test executed or a live Scheduler exploit. The branch remains inert. Existing WQ04/WQ05/WQ03 positive execution gates, account permissions, source high-water, and host safety remain separate, untouched and default-deny.

**Disposition:** ChangesRequested for this frozen source; submit a new exact task-bound review once the historical revision completeness invariant and two-clone tail-rewind regression are addressed. No main push, hosted CI, Scheduler/YieldSource activation, deployment, migration or Codex.