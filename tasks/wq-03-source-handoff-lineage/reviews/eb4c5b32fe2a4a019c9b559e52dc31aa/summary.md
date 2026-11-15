# Independent WQ-03 post-transport combined-source review — ACCEPTED (inert foundation only)

**Review:** `eb4c5b32fe2a4a019c9b559e52dc31aa`  
**Reviewer:** Reviewer 2  
**Pinned reviewed commit:** `21add89f26fc3297160cf1d54f4b19464c20259d`  
**First parent:** `b94b99f2df6af293b669988e5b999fffbd82f102` (main including accepted P1 transport)  
**Second parent:** `2d3a5b881436826063ece164a4f86c6f654b84fa` (independently Accepted WQ-03 correction)  
**Review scope:** Inert WQ-03 combined source on `integration/wq03-inert-post-transport-review`, **not** permission to activate source-writing.

## Independent provenance and integrity evidence

- `git show --no-patch` verified exact two-parent merge. `git merge-base --is-ancestor` returned exit 0 for both parents and for accepted transport `c7c0ea883be9bb1fd3b2b1e54de991bae122e1c5` beneath the first parent. Accepted WQ-03 correction `2d3a5b88` is the unchanged second parent.
- First-parent `git diff --name-status b94b99f2..21add89f` changes exactly four WQ-03 files: `Client/Modules/WorkQueueGitLedger.fs`, `Client.Tests/WorkQueueGitLedgerTests.fs`, `Server/WorkQueueContracts.fs` and `docs/specs/wq03-source-host-safety-dependency.md`. There are no other first-parent source changes. A comparison of these four files from `2d3a5b88` to `21add89f` is empty, proving the previously accepted WQ-03 bytes are preserved across the combined merge.
- `git diff --check b94b99f2..21add89f` passed. The selected linked worktree was exactly `21add89f` and the final `git status` was clean. No live `WorkQueueGitLedger.execute`/initialization wiring was found outside the foundation/test surface. No main push or hosted action.

## Independent source and state-machine audit

1. **Initial repository and pinned-base provenance (pass in current boundary).** `WorkQueueGitLedger.fs:269-312` verifies approved fetch/push repository identity, canonical repository fingerprint and exact approved target ref, fetches remote target objects, and checks the pinned base is an actual ancestor. `PublishStage` performs this inside the prepublication planner (`:889-894`). Tests `FirstGenerationRejectsWrongRepositoryMissingAndUnreachableBaseBeforePublication` prove forged repo, missing/unreachable base and absent ref cause no ledger publication.

2. **Exclusive source writer and generation-pinned claims (pass in current boundary).** `WorkQueueContracts.fs:570-596` excludes a second unfinished implementation stage on the same repository/task/target/lineage. `claimNext` advances the exact claim generation under project CAS (`:758-805`). `verifySourceClaimCheckout` (`WorkQueueGitLedger.fs:326-385`) requires a clean, linked, detached, registered worktree with deterministic work/launch/generation identity and exact pinned HEAD. Public `ClaimNext` invokes that check before CAS publication (`:901-917`). Stale ordinary clone, dirty successor, drifted WIP remote tip and valid pinned successor cases are exercised by deterministic two-clone tests.

3. **WIP checkpoint and stale-session fencing (pass structurally, no successful live yield).** `WorkQueueContracts.fs:1458-1527` fences `YieldSource` by original launch UUID, stage and claim generation, source identity, lineage generation and one immutable receipt per claim. Success would advance source continuation and terminalize the prior launch exactly once. The canonical projection validates source handoff receipts and forbids invented continuations (`WorkQueueGitLedger.fs:550-617`). The ledger path does not expose a proof-free ordinary completion for Implementation or Integration (`:819-825`).

4. **Unreconciled original effects and WIP host protection (correctly FAIL CLOSED).** `verifySourceHandoff` (`:399-449`) independently checks HEAD/WIP SHA, pristine tracked and untracked state, pinned ancestry and exact approved remote ref tip. Crucially, even if these pass, `verifySourceExternalEffectsAndRefProtection` (`:387-393`) returns an unconditional `RecoveryBlocked` error: the adapter has no authoritative mutating-process/original-push journal and no independent host policy/retention attestation. Consequently an unprotected bare Git ref or caller-supplied `TestEvidence` cannot release the source claim. `MarkSourceRecoveryBlocked` (`WorkQueueContracts.fs:1532-1552`) records a durable hold without releasing the original launch, claim or capacity. Regression tests cover clean WIP with absent authoritative proof, missing pushed WIP, dirty checkout, remote force-overwrite/delete and blocked claim retention.

5. **Activation and semantics boundary (documented).** `docs/specs/wq03-source-host-safety-dependency.md` explicitly requires host-enforced create-only WIP ref immutability and retention, credential/bypass proof, host-owned complete pre-dispatch effect journals and original-operation reconciliation, and clean detached worktree admission before any live activation. It correctly treats `ls-remote`, status cleanliness and human-entered evidence as insufficient. A successful source handoff / successor end-to-end path remains intentionally unavailable.

## Independent real .NET 11 Release evidence

SDK `11.0.100` on the Linux Jarvis client. At the exact frozen combined commit, local (not hosted) commands executed:
- `dotnet test Client.Tests/Client.Tests.fsproj --configuration Release --filter FullyQualifiedName~WorkQueueGitLedgerTests --no-restore --verbosity minimal` — built Common, Client and Client.Tests in Release; **25 passed / 0 failed / 0 skipped**, exit 0, job `dffc047ef92d49458b94caca24709cc5` (35 seconds of executed tests).
- `dotnet test Server.Tests/Server.Tests.fsproj --configuration Release --filter FullyQualifiedName~WorkQueueContractTests --no-restore --verbosity minimal` — built Common, Server and Server.Tests in Release; **77 passed / 0 failed / 0 skipped**, exit 0, job `f0174e5751a943338c6ef0b34585009c`.
- A supplementary unfiltered `Client.Tests` Release run emitted local activity-persistence permission warnings and did not return a completed test verdict; it was explicitly canceled (job `0b45077c83804e96b1e15ed8d4df22d0`). It is **not** claimed as passing evidence and does not invalidate the independently completed focused suites.

## Explicit verdict

**ACCEPTED for integration of the exact, unactivated, fail-closed WQ-03 combined-source foundation only.** No new blocking regression against this restricted review scope was found. The source preserves the Accepted WQ-03 parent without rewrite and retains the accepted P1 transport ancestry.

**NOT ACCEPTED for production-capable source handoff, queue runtime activation, Scheduler activation, migration, deployment, or release of source claims.** Host-side immutable WIP retention, authoritative pre-dispatch and terminal effect-journal coverage, original push settlement, and separately reviewed successful end-to-end source succession remain mandatory blocking dependencies. Do not infer a full WQ-03 feature completion or authorize a cutover from this review. See `findings/activation-gates.md`.

No GitHub Actions, main push, deployment, Scheduler activation or Codex was performed.
