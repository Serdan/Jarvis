# Independent frozen WQ-03 correction review — Accepted (inert, fail-closed foundation only)

**Review** `98eb5bdf48c14bef9eb069e1b5a7570f` • **Reviewer** `Reviewer 1` • **Exact reviewed SHA** `2d3a5b881436826063ece164a4f86c6f654b84fa`.
Base is WQ02-integrated `e0ae297f42217a19cde29fb7729bf509a2448e53`. Prior ChangesRequested source `4f0ac4df3f3f437501c8da054c262706ec3f5e64` is an **unchanged parent**, not a rewritten patch. The isolated independent worktree `reviewer1-wq03-r2-frozen-2d3a` was pinned at the reviewed SHA and clean; `git diff --check` and both ancestor checks passed. Corrective diff modifies only `Client/Modules/WorkQueueGitLedger.fs`, its tests, and `docs/specs/wq03-source-host-safety-dependency.md`; the baseline also includes the prior `Server/WorkQueueContracts.fs` work. No `OrchestrationApi`, Scheduler or live migration activation.

## Independent source findings

1. **Initial source binding/base (previous P2): corrected.** `Client/Modules/WorkQueueGitLedger.fs:269-323` verifies the trusted source checkout and approved fetch/push identities, canonical repository fingerprint, valid target ref, exact approved remote target and actual fetched commit ancestry from the pinned base. `PublishStage` runs this before the source record can be published and `ClaimNext` checks it again (`:890-915`). Invalid repository IDs, nonexistent/unreachable pinned bases and nonexistent target refs leave the two-clone ledger unchanged; see `Client.Tests/WorkQueueGitLedgerTests.fs:1104-1142`.

2. **Pinned successor checkout (previous P1): corrected as a fail-closed claim preflight.** `verifySourceClaimCheckout` (`:326-384`) requires a registered **linked**, detached, generation-specific worktree named for work/launch/claim, with exact HEAD at the approved pinned base or remotely verified WIP continuation and clean tracked/untracked state. The public initial `ClaimNext` rejects an ordinary clone before claiming; the two-clone negative exercises stale successor, valid detached WIP checkout, dirty checkout and externally drifted ref (`Client.Tests/...:1069-1102`). A public successful successor-after-yield end-to-end test remains out of scope because legitimate `YieldSource` is blocked by absent host proof, not simulated as successful.

3. **Old-writer effects / unknown push (previous P1): fail closed, not reconciled.** `YieldSource` first checks exact HEAD, clean source, WIP ancestry and remote tip, then `verifySourceExternalEffectsAndRefProtection` (`:391-393`) **always returns RecoveryBlocked** absent an authoritative host-owned journal. The adapter must not infer process termination or original push resolution from a clean snapshot, human-written `TestEvidence` or `ls-remote`. The public transition retains the claim, generation, token and ledger version; regression `SourceYieldFailsClosedWithoutAuthoritativeJobJournalAndProtectedWipRetention` (`:1030-1068`) verifies no requeue, while `MissingRemoteWipDirtyCheckoutAndUnknownJobRemainClaimedRecoveryBlocked` (`:1143+`) verifies the explicit durable hold path. This is deliberately **not** implementation of a journal reconciler.

4. **WIP immutability / retention (previous P1): no false proof.** The disposable bare remote demonstrably permits force-overwrite and delete of `refs/heads/jarvis-wip/**`, so a matching `ls-remote` does not certify retention or protection. The adapter unconditionally denies normal handoff until effective host policy and bypass/retention evidence are implemented. `docs/specs/wq03-source-host-safety-dependency.md` clearly describes the missing host contract, required operator/Lead actions and no-activation gate. No destructive production ref probe was performed.

5. **CAS invariant:** Existing `Server/WorkQueueContracts.fs:570-596,758-810,1458-1555` continues to reject colliding unfinished source lineages and fence source checkpoint by launch UUID, work/claim/lineage generation and prior receipt. The new client planner rejects an unproven source handoff **before** publishing its project Git CAS. Neither successful recovery nor protected refs are fabricated.

## Independent .NET 11 Release evidence

- SDK `11.0.100` (linux-x64), fresh isolated checkout with local NuGet restore.
- Explicit `dotnet build Client.Tests/Client.Tests.fsproj --configuration Release --no-restore`: **PASS**, 0 errors (2 pre-existing nonblocking warnings).
- Explicit `dotnet build Server.Tests/Server.Tests.fsproj --configuration Release --no-restore`: **PASS**, 0 errors (11 nonblocking warnings).
- `dotnet vstest Client.Tests/bin/Release/net11.0/Client.Tests.dll /TestCaseFilter:FullyQualifiedName~WorkQueueGitLedgerTests`: **25/25 passed**, NUnit actual executed tests.
- `dotnet vstest Server.Tests/bin/Release/net11.0/Server.Tests.dll /TestCaseFilter:FullyQualifiedName~WorkQueueContractTests`: **77/77 passed**, NUnit actual executed tests.
- Earlier `dotnet test --no-restore` returned silent zero without assets; this was discovered, discarded as evidence, restored, then replaced by explicit builds and the above real VSTest executions. Final checkout remains clean.

## Explicit verdict and boundary

**Accepted** for the **corrected, unactivated fail-closed WQ-03 foundation only**. The former unsafe success paths are now closed, and the code does not claim nonexistent host protection or completed push/job reconciliation.

**NOT ACCEPTED as production-capable source handoff or completed WQ-03 acceptance.** `YieldSource` cannot succeed, and there is no successful remote WIP/source successor progression. This review is **not** authorization to integrate as active behavior, enable Scheduler, migrate, release source tokens, deploy or enable hosted Actions. The missing host protection, complete durable original-effect journal, recovery proof and public end-to-end success/negative tests remain explicit **blocking prerequisites for a separately reviewed activation**; see `findings/activation-blockers.md`.

No main push, hosted GitHub Actions, deployment, or Codex.