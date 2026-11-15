# Reviewer 2 — independent frozen WQ04 versioned-role authority audit

**Canonical disposition: ChangesRequested** (bounded inert source; not an assessment of any production activation).

- Task: `wq04-versioned-role-admission-proof`
- Request: `ed89f13401cd4c18bce62b189af79f73`
- Frozen exact source: `1cd0b88db631dc4258601e5e531f95c02a34ebd4`
- Parent/main base: `e837d2d67277af8eb4b634ff58d5ba9e1b59d19e`
- Worktree: `worker11-wq04-role-authority`; exact commit directly descends from parent. Only `Client/Modules/WorkQueueRoleAuthority.fs`, `Client.Tests/WorkQueueRoleAuthorityTests.fs`, and their two fsproj compile entries are changed. `git diff --check` passes; no production Scheduler/Manager/source/host route changed.

## Positive evidence

1. The catalog lives at `records/work-queue/role-catalog.json`, with typed current pointers, version history, operation receipts, canonical encoding, and a project-ID check (module lines 16–43, 77–181). It is deliberately separate from WQ02 `Project.Subscriptions`; missing catalog fails closed.
2. `RoleRevisionProof` pins the bound project/ledger/epoch, Git snapshot head, project blob/version and catalog blob/revision. `inspectAt` re-reads both from the caller's current `LedgerStore.transact` snapshot (lines 199–253, 388–397). A role-edit race invalidates a stale preflight rather than accepting a caller-supplied boolean.
3. `configure` uses `LedgerStore.transact` with expected catalog blob OID, a monotonic version/revision check, and semantic same-UUID request identity (lines 418–444 and `Server/WorkQueueProducers.fs:298–322`). The present adapter's normal update path appends, rather than mutating previous role definitions. Historical enabled v1 sessions can remain subscribed after v2 changes, while new demand/admission targets the current enabled revision.
4. All positive results are explicitly `RolePreflight` with `RequiresCanonicalSourceAndAccountProof=true`; they do not themselves execute claims, Scheduler launches, account admission, or host effects. This distinction is essential and preserved.

## Blocking findings

### B1 — Retroactive catalog provision can confer unproven historic role authority (P1)

`initializeCatalog` (lines 403–416) requires only that the project can be read and the catalog path is missing. It does **not** require an empty legacy launch set, a migration fence, or proof that every nonterminal launch was originally admitted against a real versioned catalog. `configure` (418–444) can then create version 1 **after** an existing legacy `Launch { RoleId="Lead"; RoleVersion=1 }` already exists. `loadAt` (199–232) accepts that older launch once a matching enabled history entry exists, because it verifies only current catalog membership by numeric version. `FirstConnect` (373–383) and `ClaimWork` (347–360) can now return successful preflights for a launch whose historical subscription was never proven at original admission.

This is not a demonstrated live execution path: this branch is inert. It is a concrete false-positive **authority proof** at the new adapter's intended boundary. Simply labeling the preflight provisional does not make a retrospectively fabricated historical version trustworthy.

**Required:** refuse provisioning/configuring a catalog against preexisting unproven nonterminal launches, or require separately pinned, verifiable migration/original admission receipts that bind each launch to its role-version content and catalog revision at admission. A negative test should start with a covered WQ02-style launch before any catalog, provision/configure a matching v1 later, and assert that `loadAt`, `FirstConnect`, and `ClaimWork` remain fail-closed.

### B2 — Historical immutability is not independently verified across Git heads (P1)

The catalog is one rewritable JSON blob. `decode`/`validCatalog` (120–172) assert current-map shape, monotonically numbered versions, receipt counts and canonical serialization **within the current blob**. They do not compare the bytes/OID of an earlier role version with an anchored prior version or issuance event. `verifyAt` (238–249) authenticates the *current* Git head and blobs, not historical append-only continuity.

A separate `LedgerStore.transact` writer/repair that replaces an old `Versions[Lead][1]` definition and corresponding `Receipts[oldOp]` entry, while preserving counts, order and canonical JSON, can pass these checks. An old launch pinned only to numeric `RoleVersion=1` would acquire different subscriptions at a fresh head. This need not be possible through the new `configure` function to be an invariant issue in an adapter intended as canonical historical authority.

**Required:** prove cross-revision append-only history against the bound Git lineage (or immutable per-version blob/issuance OIDs with authenticated receipts), and bind each admitted launch to the issued role content, not merely the latest catalog's reconstruction of a numeric version. Add a two-clone test for a *shape-valid* older-definition replacement, not only deleting a history entry.

### B3 — Exact claim/admission races and chosen-work binding remain unproven (P1 test/contract gap)

`inspect (ClaimWork(launchId,workId))` (347–360) checks registration, lease, historical subscription, stage availability and matching of other launches. It does not prove that the supplied work ID is the stage actually selected/credited by `Contracts.claimNext` in the same planner, nor inspect an actual resulting claim. It can preflight any eligible work, whereas the existing claim selector chooses deterministically. The adapter explicitly requires a later joint writer, so do not use this as an executable claim permission yet.

The six tests cover concurrent role-v2 configuration, old-v1/new-v2 preflights, role-message matching, and a sequential stale admission preflight that loses inside an otherwise inert `LedgerStore.transact` marker write. They do not exercise two-clone stale **actual launch admission/claim** versus role update and claimed-work outcome (nor the retroactive-provision and shape-valid history substitution cases above).

**Required:** design the joint-CAS contract to check the **actual selected work/credit** under the same verified role snapshot, reject mismatch/changed catalog, and test two-client simultaneous admission, stale v1 claim vs v2 subscription change, losing CAS, and single claim/receipt. A proof-only API must clearly distinguish `eligible candidate` from `exact claim authorized`.

## Independent verification

- SDK `dotnet --version`: `11.0.100`.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~WorkQueueRoleAuthorityTests --verbosity minimal`: **6 passed / 0 failed**, .NET 11 Release; the suite uses two local Git clones.
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release -v minimal`: restored missing local `project.assets.json`, then **233 passed / 0 failed**, .NET 11 Release. Earlier `--no-restore` returned empty exit-0 output without running tests and is not counted.
- `git show -s --format=%H\ %P`, `git diff --name-only`, `git diff --check`, and worktree status independently verified the exact source, parent, limited file scope and clean checkout.
- Full Client regression command was launched separately; its final result must be recorded only if actually observed. It is not needed to establish the above authority-design blockers.

## Resolution boundary

ChangesRequested applies to the **new inert role-authority proof**. Existing main WQ04 semantic source gates, canonical Worker10 source evidence, WQ05 account/high-water proof, WQ03 journal continuity, and production Scheduler remain separate and must stay default-off. Do not weaken or fake other source/account evidence to make the new catalog pass. Supply a new exact frozen task-bound source and independent review after fixing the historical issuance and claim proof boundaries.
