# Independent frozen WQ-02 r3 review — ChangesRequested

**Reviewer:** Reviewer 2  
**Review ID:** `5f8a9c41276e4d42a4f43c568f03e2cd`  
**Exact frozen source:** `683357d7c1432541abcfa59fbbf483feab49a371`  
**Accepted WQ-01 baseline:** `7b589173135178a634495146675dad5db647e5f8`  
**Previous frozen review:** `8f0b3189123049c59146a9b35a5d6409`, ChangesRequested seq3270 on `98cdd713d270a81690773c5c513d9180a65431ab`.  
**Verdict:** **ChangesRequested**, blocking source acceptance of the isolated WQ-02 foundation. Detailed new finding: `findings/p1-public-retry-witness-hash.md`.

## Independently verified at the frozen commit

Read the canonical WQ-02 task acceptance spec, exact frozen branch/lineage/diff and clean worktree, entire 566-line `Client/Modules/WorkQueueGitLedger.fs` including codec and Git planner, the new correction diff in `Client.Tests/WorkQueueGitLedgerTests.fs`, all named focused test cases, applicable inherited `Server/WorkQueueContracts.fs` types/transitions and `Client/Modules/LedgerStore.fs` receipt/transaction implementation.

Independent **local .NET 11.0.100 Release** evidence on frozen SHA:
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~WorkQueueGitLedgerTests --verbosity minimal`: **20 passed, 0 failed, 0 skipped** (Release Client/Client.Tests built).
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore --verbosity minimal`: **212 passed, 0 failed, 0 skipped** (Release Server/Server.Tests built).
- `git diff --check 7b589173... 683357d7...`: **exit 0, no whitespace errors**.
- Frozen Git worktree HEAD equals `683357d7c1432541abcfa59fbbf483feab49a371` and is clean. **No full Client.Tests pass claimed** (separate WQ-11 hermetic-profile work).

## Previous findings corrected in the bounded paths

1. **Earlier P1 null/invalid nested publication poison:** corrected. New preflight `validateMutationPayload` verifies a typed mutation roundtrip, required nested stage/message strings are checked, and `validatedContent` validates the exact decoded canonical project/hash/structural projection before a Git commit. The two new cross-clone malformed-stage and Task/Role-message tests reject publication without remote head/version/receipt mutation and then allow legal publication/claim. The earlier null QueueId path is closed.
2. **P1 forged prerequisite waivers:** `validInitialStage` rejects nonempty published waiver maps and `structurallyCoherent` rejects unproven persisted waivers; regressions remain.
3. **P1 forged already-Registered admission:** `validInitialLaunch` enforces ReservedUnregistered, no lease/claim/credit and initial generation/dispatch state; forged Registered/Submitted/terminal regressions remain.
4. **P1 source-writing completion without proof:** `CompleteOrdinary` rejects Implementation/Integration until remote source/job proof is implemented; dependent wake remains fenced.
5. **P2 unproven Registered no-claim terminal:** `TerminalizeNoClaim` rejects pending authenticated session/journal proof; no false positive release claimed.

## New P1 — public replay is not idempotent

The exact public `WorkQueueGitLedger.execute` resamples `UtcNow` on each call (`WorkQueueGitLedger.fs:558-561`), then includes it as `serverWitnessMillis` in canonical immutable request identity (`:440-448`). `LedgerStore.transact` hashes and compares that full request (`LedgerStore.fs:1946-1955,1976-1995`). Consequently, a delayed retry with **the same actor, UUID, expected project version and exact mutation** is falsely diagnosed `OperationIdReused`.

This was **independently reproduced**, not just inferred: two disposable bare-Git clones, valid Planning-stage publication, first public execute `Ok(AlreadyApplied=false)`, 1,300 ms delay, second public execute with the same original UUID/mutation `Error OperationIdReused`; canonical project remained readable at Version=1, Receipts=1, Works=1. The external F# test script and disposable repository were deleted, and frozen source was unchanged. Existing exact replay tests pass because they use `executeAt` with a fixed test timestamp, rather than repeating the real public API.

The current behavior breaks immutable original-result recovery after an ambiguous network/transport outcome even though no duplicate publication occurs. Full evidence, impact and bounded correction are recorded in `findings/p1-public-retry-witness-hash.md`.

## Required correction and limits

Make actual public retry identity stable under clock passage, preserving original witnessed time in durable committed state/receipt or a per-operation journal without rehashing a new wall-clock value as changed semantic input. Add a delayed two-clone **public `execute`** replay test asserting exact original result and `AlreadyApplied=true`, no new remote record/claim/FIFO position; keep rejection of genuinely changed semantic payloads and preserve all poison/fail-closed regressions. Re-run .NET 11 Release validation and submit a new exact frozen independent review.

This is still an isolated `MigrationEpoch.Frozen` foundation. Real account-global authority/release, first-connect attestation, source-ref/SHA and job reconciliation, producer/queue/graph high-water witnesses, server clock authority, and F06 waiver/integration proofs remain **separate preactivation gates**, not falsely marked complete by these fixture tests. No production activation, migration, deployment, source integration, hosted GitHub Actions or Codex was performed.

**Explicit verdict: ChangesRequested on exact SHA `683357d7c1432541abcfa59fbbf483feab49a371`.**
