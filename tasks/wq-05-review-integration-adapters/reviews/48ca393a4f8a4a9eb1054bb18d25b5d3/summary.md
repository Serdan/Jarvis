# Reviewer 3 — independent WQ05 task-bound frozen review

**Verdict: Accepted for the scoped WQ05 stale/fresh downstream-D claim-generation regression.** This is **not** authorization to integrate or activate WQ04 producers, WQ03 host effects, Scheduler, or production remote pushes.

- Task: `wq-05-review-integration-adapters`
- Review: `48ca393a4f8a4a9eb1054bb18d25b5d3`
- Frozen exact SHA: `8754fa775f865969ce0c832cf6b0ae03c2e6407d`
- Prior ChangesRequested: `8e620e4c3de347d79fb8cf7f6f83380e`, exact `a73d2690857fdf57475198dc16cde496a28130ef`, decision seq3655
- Target: `refs/heads/main`; review inspected in isolated clean `wq05-worker9-r3`.

## Independent source/patch audit

`git rev-parse HEAD` matched the immutable reviewed SHA. `git merge-base --is-ancestor a73d2690 8754fa77` returned 0. `git diff --stat a73d2690 8754fa77` changes only `Client.Tests/WorkQueueReviewIntegrationAdapterTests.fs` (113 additions, 4 deletions). `git diff --check` returned 0; `git status --porcelain` was empty after tests. The production adapter itself was not modified.

The prior finding, `findings/stale-downstream-claim.md`, correctly distinguished a stale local `Available` projection from an actual exact-versioned D claim. The correction now explicitly records a `prepare-D` admission/dispatch/initialize sequence and a `claim-D` event that invokes `Contracts.claimNext`, rather than only asserting D readiness. The event captures and checks project Version, GraphVersion, NextAvailability, D StageGeneration, ClaimGeneration, and AvailableSequence before claim (test lines 232-239 and 663-689).

## B hard-edge defeats stale D claim

The first disposable clone records an original integration push intent; B's hard edge is committed by the refreshed competing clone. The stale clone locally finalizes/marks integrated, creates D launch admission, and performs `Contracts.claimNext` to reach `Claimed "D-launch"`, generation 1 (lines 721-741). Pushing that stale chain to the disposable bare Git remote fails non-fast-forward; after refresh, canonical model D is Blocked with ClaimGeneration 0, no D launch and ProducerCount 0. Reapplying the original stale D claim against the gated project returns CasConflict (lines 742-754). A gate-first scenario also refuses fresh D admission (NoCompatibleMatching) and rejects an earlier-generation claim (CasConflict, lines 881-893).

## Fresh D claim and exactly-once outcome

After a **synthetic** verified original-effect/waiver path, D becomes Available. Both refreshed clones admit the same D launch and independently construct exact-generation `claim-D` events. Both locally claim once, but only the first clone's Git push succeeds; the second non-fast-forward push fails. Refresh converges to exactly one canonical Claimed D, ClaimGeneration 1, the expected launch PrimaryWorkId/EverClaimed, unchanged graph/FIFO high-water and the same original integration push UUID; ProducerCount remains 1 (lines 801-831). Same-operation replay returns the unchanged canonical model; a distinct stale claim UUID returns CasConflict (lines 832-836).

These are real **local disposable Git two-clone** CAS conflicts around a **synthetic ordered event replay model**. They are not a production `WorkQueueGitLedger.execute` D claim, WQ04 same-CAS authoritative source join, or WQ03 immutable host journal, remote-push, or job proof.

## Independent execution

- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~WorkQueueReviewIntegrationAdapterTests --nologo -v minimal`: **13 passed / 0 failed / 0 skipped**; executable target net11.0.
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release --filter FullyQualifiedName~WorkQueueContract --nologo -v minimal`: **77 passed / 0 failed / 0 skipped**; executable target net11.0.
- Only routine compiler warnings observed (FS0760, FS3369); no errors.

Read-only `git grep` found the WQ05 effecting adapter method symbols only at their definitions in `Client/Modules/WorkQueueReviewIntegrationAdapter.fs`. The adapter's `startPinnedOriginalPush`, `addHardEdge`, `observeOriginalPushUnderGate`, and `resolveObservedGate` continue returning `AuthoritativeJoinRequired`; `isAuthorizedIntegration` remains false (lines 251-278). The earlier P1 accepted-binding and correction-lineage safety therefore remains fail-closed.

## Scope and residual production blockers

**Accept this bounded correction**: it explicitly exercises the missing D claim transition, stale hard-edge/CAS loss, fresh one-winner claim, high-water/generation checks, and duplicate original UUID replay. **Do not equate it with activation approval.** WQ04 must independently prove canonical provenance and same-CAS source/generation/high-water join; WQ03 must independently prove host original-job/remote-push fences, immutable WIP, journal continuity and recovery. Production Scheduler/YieldSource, integration push or deployment remain disabled.

No source edits, hosted GitHub Actions, production main push, Scheduler activation, deployment or Codex were performed by this reviewer.
