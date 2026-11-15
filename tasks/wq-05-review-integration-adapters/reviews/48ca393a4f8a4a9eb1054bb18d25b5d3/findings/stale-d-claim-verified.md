# WQ05 stale downstream D claim — independent closure

**Reviewer 3, exact reviewed SHA** `8754fa775f865969ce0c832cf6b0ae03c2e6407d`.
Prior review `8e620e4c3de347d79fb8cf7f6f83380e` ChangesRequested seq3655 specifically lacked an actual D claim.

## Findings

1. **PASS — explicit claim, not readiness only.** The disposable model adds D `admitProject`, `dispatchOnce`, `initializeAgent`, and `claimNext`. A `claim-D` event pins and asserts project Version, GraphVersion, NextAvailability, and D StageGeneration, ClaimGeneration, AvailableSequence before invoking `Contracts.claimNext` (tests 232-239, 663-689).
2. **PASS — B hard-edge versus stale D claim.** The first clone locally produces D `Claimed "D-launch"`, ClaimGeneration 1, on its stale ungated branch (728-741). Its attempt to push that local history fails after B's remote hard-edge. Refreshed canonical state has D Blocked, ClaimGeneration 0, no D launch, ProducerCount 0; replaying stale claim against B returns CasConflict (742-754).
3. **PASS — gate-first negative.** With B hard-edge already remote, D admission fails NoCompatibleMatching and earlier-generation D claim fails CasConflict (881-893).
4. **PASS — post-waiver one winner.** Following a *synthetic* verified effect and waiver, two clones locally attempt current-generation D claims. Only one disposable Git push succeeds. Refreshed canonical D is Claimed once, ClaimGeneration 1, matching PrimaryWorkId/EverClaimed and unchanged GraphVersion/NextAvailability; original push UUID preserved (801-831).
5. **PASS — replay/high-water.** Exact original D claim op ID replays without change; a distinct competing stale op ID returns CasConflict (832-836). Project, graph, queue, stage, claim and FIFO high-waters are asserted in the event replay.
6. **PASS — source/test evidence.** `a73d2690` ancestor of frozen `8754fa77`; only one Client.Tests file changed; `git diff --check` clean; `git status --porcelain` empty. Independent .NET 11 Release Client focused 13/13, Server WorkQueueContract 77/77.
7. **PASS — fail-closed adapter.** WQ05 effecting adapter routes still return `AuthoritativeJoinRequired` and `isAuthorizedIntegration` false (adapter 251-278); no Client production call sites for the effecting methods found.

## Evidence boundary

This is real **local disposable two-clone Git CAS** with **synthetic contract event replay**, not a production `WorkQueueGitLedger.execute` downstream claim or WQ04 same-CAS canonical provenance join. Neither WQ03 host original-push/job/WIP journal and recovery safety nor genuine external waiver approval is proved here. Do not enable production source handoff, Scheduler, remote-push dispatch, integration or deployment on this acceptance.

**Result: Accept this bounded regression correction.**
