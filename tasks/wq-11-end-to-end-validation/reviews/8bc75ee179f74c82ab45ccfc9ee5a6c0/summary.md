# Reviewer 1 — WQ-11 offline Release acceptance: Accept

**Frozen commit:** `173a227ea277aeef87b3391d55f705164d76066d` (base `289e32754b6e2bd59b157d51c3fd79a16e881e98`); owner Worker 7. Diff is three files: six new tests in `Client.Tests/WorkQueueReleaseAcceptanceTests.fs`, unconditional project compile inclusion, and `docs/evidence/WQ11-offline-release-acceptance.md`. No production runtime modification.

## Independent review and execution

- Read all six new tests, their private two-clone Git setup, source-journal setup, cleanup, and the operator readiness matrix.
- Independently executed `dotnet build Client.Tests/Client.Tests.fsproj -c Release --no-restore`: **pass**, 0 errors, 3 existing F# warnings.
- Independently executed `dotnet fsi scripts/test-client-hermetic.fsx -- --no-build --filter FullyQualifiedName~WorkQueueReleaseAcceptanceTests --timeout-seconds 180`: **6/6 pass**, 0 failed, 0 skipped; hermetic scratch environment removed, process exit 0.
- Worker 7's evidence document also records Server Release 275/275 (focused WQ 130/130), related Client 37/37, account/source/role Client 78/78, browser/repair Client 2/2. These additional counts are **owner-reported**, not independently rerun in this review.

## Scope and limitations

The tests adequately cover the current **offline/default-off** acceptance slice: durable Task/Role manual conversation and CAS/replay across clones; stale Git head retry; refusal to trust fabricated canonical producer inputs; fixture-only claim/dependency wake; and WQ03 lost/missing original source-effect journal evidence.

Crucially, the documentation explicitly labels incomplete or unwired positive pathways (live creation-to-queue producer, accepted review/integration on a real canonical route, one-shot real Scheduler launch, two-ledger activation, paired browser session, production migration). It does not represent the inert fixture as deployed scheduler validation. Worker 3's Windows hermetic full Client suite is separate and pending. The optional future 80-scenario catalog is not a condition of this narrow offline smoke acceptance.

**Verdict: Accept** for the frozen **offline-only smoke and honest readiness matrix**, not approval for production, migration, Scheduler activation, main integration, or a global WQ11 end-to-end completion claim. Any later production/release approval needs verified upstream exact frozen integrations and positive live-adapter evidence.
