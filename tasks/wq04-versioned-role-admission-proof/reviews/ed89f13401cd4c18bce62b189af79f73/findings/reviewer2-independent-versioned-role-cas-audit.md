# Reviewer 2 — corrected independent WQ04 historical role authority findings

**Final disposition: ChangesRequested.** This document **supersedes** my earlier provisional "Accepted" assessment of this frozen source. The canonical Reviewer 2 decision was already published as **ChangesRequested at sequence 3808**, and the deeper historical-provenance audit in `findings/reviewer2-role-historical-provenance-audit.md` correctly identifies blockers that the six positive/negative test cases do not cover.

- Review: `ed89f13401cd4c18bce62b189af79f73`; task: `wq04-versioned-role-admission-proof`
- Exact frozen SHA: `1cd0b88db631dc4258601e5e531f95c02a34ebd4`; direct base: `e837d2d67277af8eb4b634ff58d5ba9e1b59d19e`.
- Scope remains isolated/inert: new `Client/Modules/WorkQueueRoleAuthority.fs`, `Client.Tests/WorkQueueRoleAuthorityTests.fs`, two fsproj compile entries. Existing WQ04/WQ05/WQ03/Server source unchanged; `git diff --check` passed.

## Verified strengths

1. The new role catalog in `records/work-queue/role-catalog.json` is separate from unversioned WQ02 `Project.Subscriptions`; absent catalog fails closed. Normal `configure` appends monotonically numbered role versions using `Producer.configureRole` and `LedgerStore.transact`, with expected revision and same-UUID semantic replay checks.
2. `RoleRevisionProof` binds project, ledger, epoch, head, project/blob/version, catalog blob/revision, and `inspectAt` revalidates the pinned proof against a single Git snapshot inside a planned transaction.
3. Normal role preflights distinguish current enabled role for fresh demand/admission from pinned historical role for registered claims/first connection, and implement matching for unclaimed live launches and canonical pending RoleMessage work. All successful preflights expressly require canonical source and account proof and are **not** themselves executable authority.
4. Independent local .NET 11 Release targeted tests ran twice on the frozen worker11 worktree: each **6 passed, 0 failed, 0 skipped**. Those two-clone cases verify concurrent *normal* role updates, idempotent original UUID replay, v1/v2 subscription behavior, stale proof rejection, role-message linkage, and *malformed/deleted* history rejection. This verifies what is tested, not all history-issuance or claim-outcome invariants. `git rev-list --parents`, `git diff --name-status`, and `git diff --check` confirmed direct ancestry and isolation.

## Blocking findings

### B1 — retroactive catalog provisioning creates unproven historical authorization

`initializeCatalog` only requires a readable WQ02 project and an absent catalog record; it does not require zero preexisting launches or an authenticated migration/original role-issuance receipt. A preexisting `Launch(RoleId=Lead, RoleVersion=1)` can therefore be followed by new catalog creation and later `configure(...RoleVersion=1...)`. `loadAt` merely resolves that now-present historic numeric version. `ClaimWork` or `FirstConnect` can return a successful preflight even though the version was not issued at the launch's original admission. **Required:** reject provisioning with unproven nonterminal launches, or perform a separately authenticated migration that binds each launch to its original issuance and content. Test the retroactive-backfill scenario across two clones.

### B2 — latest-head canonical JSON does not establish immutable historical bytes

`decode`/ `validCatalog` check current JSON schema, counts, ordering, dense version numbers and receipts, but there is no cross-head or per-version issuance anchor proving that an earlier `Versions[Lead][1]` body and associated receipt have never changed. Generic Git-ledger CAS can replace them with a different *shape-valid*, canonical serialized definition while preserving counts and pointers. A prior launch pinned only by numeric version would then acquire changed subscriptions at a fresh head. Normal `configure` being append-only is insufficient evidence that the stored history is immutable. **Required:** authenticate append-only transition lineage or separately immutable per-version OIDs / issuance receipts, with launch binding to issued content. Add two-clone shape-valid history-substitution regression.

### B3 — eligible work preflight is not exact claim-result authorization

`inspect(ClaimWork(launchId,workId))` establishes candidate availability, historical subscription and matching, but does not bind the supplied ID to the exact work/stage/credit selected by `Contracts.claimNext` within an actual same-CAS mutation. The admission-race test proves a stale pinned *marker* transaction is rejected; it is not a two-clone race for a real launch/claim outcome. **Required:** in the future combined writer, verify exact chosen work and resulting credit under one authenticated project/catalog/source/account snapshot; reject mismatched claim or stale role, with adversarial two-client launch and claim tests. Until then call this feasibility, never permission.

## Decision and limits

**ChangesRequested for this specific inert role-authority proof.** The missing historical issuance and cross-head continuity invariants are substantive fail-closed gaps despite passing Release tests, not merely a lack of production integration. Keep the current WQ04 semantic ingress denial and all Worker10 source high-water, WQ05 account, WQ03 host, Scheduler/Manager and production gates unchanged. Correct on a new isolated source commit and request a new task-bound frozen review. No hosted Actions, main push, production activation/deployment, migrations, source edits or Codex performed in this review.
