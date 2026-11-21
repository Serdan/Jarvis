# Challenge reverse-index second-review repair — 2026-11-21

## Source and scope

- Canonical task: `challenge-reverse-index-second-review-repair-20261121`, Worker 4 attempt `651c1e74-2d43-477f-8740-d2cb129938f6`, generation 1.
- Based directly on frozen previous production correction `48f1a8c6030677960a3d84629c4e29cecf56a132`. Reviewer 5 review `7e2d42b10a5c4f0e920f60cd878fb301` returned ChangesRequested at ledger sequence 9845.
- Source branch: `refs/heads/fix/challenge-reverse-index-second-review-worker4-20261121`. No production refs, main, Scheduler, Health or runtime changed. Original source, previous review and Worker 7 property-test-only source remain untouched.
- Non-goal: discover reverse edges under arbitrary unrelated/unknown Challenge IDs (Reviewer 5 mode 8). This change verifies only an owner's existing issued edges and canonical historically linked Challenge prefixes.

## Correctness change

1. A public direct `requeueAttempt` now verifies the full owner reverse-index projection at the pinned canonical snapshot **even when its canonical wait has no dependencies**; a forged current-generation owner edge cannot be skipped. Nonempty waits retain their explicit, exact current owner-index proof, and empty manual waits validly have no issued edge.
2. Owner inbox verification walks only the Challenge IDs independently corroborated by the owner's issued immutable WaitOnChallenges operations. For each known-linked Challenge prefix, all existing edges must have valid immutable issuance, lifecycle, canonical assignment generation, owner and sibling-index provenance. An unissued extra N3 by-Challenge edge is rejected with `LedgerIntegrityError` even after legitimate N1-to-N2 automatic requeue. Issued historical N1/N2 remain valid.
3. No corrupt record is healed, deleted or implicitly authorized. Failed reads/commands never publish a replacement ledger HEAD; notification paths are untouched.

## Real Git red-before / green-after

The tests use a freshly initialized **bare Git remote** and two real application clones; a third detached worktree injects the malformed index via an ordinary Git commit and push. The application clients retain genuine owner/version/CAS requirements. The relevant command is:

```sh
dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore \
  --filter 'FullyQualifiedName~ManualZeroDependencyDirectRequeueRejectsForgedCurrentOwnerIndexWithoutPublishing|FullyQualifiedName~KnownHistoricalChallengeRejectsUnissuedFutureByChallengeEdgeAfterAutoRequeue' \
  --verbosity minimal
```

- Old frozen production `48f1a8...` + only the new test fixture: job `64c375c92ffb41129d322bd9487b8cf4`: **exit 1, 2 failed / 0 passed / 0 skipped**, not timed out. The manual-zero direct requeue advanced canonical Git HEAD; the N3 inbox projection returned `Ok`.
- Corrected production (same two tests unchanged): job `92c47e320d964f64be65c12f7dfbf7d6`: **exit 0, 2 passed / 0 failed / 0 skipped**, not timed out. The negative manual test asserts canonical HEAD and next sequence unchanged and the command's receipt absent; the negative inbox test asserts pinned canonical HEAD and sequence unchanged. Both assert typed `LedgerIntegrityError` and no notifications.
- The original prior Review 5 independently verified frozen Worker 7 property seeds 9000, 9001, 9002 red on true pre-fix `d702352ef705f9991ac1cb14dc15a13cce78aa44` and green on `48f1a8...`; the source preserves these checks and the previously issued legitimate seed 9003. Do not claim new candidate has been independently reviewed.
- **New production/test composition:** unchanged exact Worker 7 test blob from `cd895d493b419e127ededea15f814ab53e484407:Client.Tests/ChallengeStateMachinePropertyTests.fs`, with the repaired source and new ChallengeWorkflowTests.fs in a wholly disposable checkout at base `48f1a8...`; only disposable `Client.Tests.fsproj` included the additional compiled file. Executed `dotnet restore` (exit 0) then `dotnet test ... -c Release --no-restore --filter FullyQualifiedName~ChallengeReverseIndexFaultTests --verbosity minimal` on SDK 11.0.100: Jarvis job `3b56796da0414e1f9b02a1368fae58ec`, **exit 0, 4 passed / 0 failed / 0 skipped**, not timed out, 5 seconds test execution. The disposable Git worktree and file artifacts were removed. This is source-owner testing, not independent acceptance.

## Acceptance tests

- `.NET SDK 11.0.100`, Release, 47 Challenge-filtered tests **passed 47, failed 0, skipped 0, exit 0**, job `35abc78d65054841b74cb3f59eacde5c`. This included both new negatives and existing manual zero-dependency, cancel/partial, reblock, wrong-owner, historical, CAS/receipt and no-notification controls.
- Newly added positive regression `HistoricalChallengeEdgeSurvivesValidSecondGenerationManualZeroDependencyRequeue` exercises N1 Challenge blocking, automatic N2, valid manual zero-dependency N2 block/requeue to N3, preservation of both N1 historical edges, and no notifications. Independent rebuilt Release invocation of this exact positive filter: job `41c145bfe2514c52a300b1059b0996da`, **exit 0, 1 passed / 0 failed / 0 skipped**, not timed out.
- Final rebuilt complete Challenge-filtered Release suite: job `c90e789968ac4cc5a7d51c898ce00022`, **exit 0, 48 passed / 0 failed / 0 skipped**, not timed out; 1m 33s test duration. Includes restored explicit current owner-index proof for nonempty waits, both new negatives and positive mixed-generation history control.
- The broad unfiltered `Client.Tests` suite has not been established green; prior independent attempt on previous source timed out after 540 seconds (Reviewer 5), so only completed scoped gates should be reported.

## Integrity guardrails

Tests operate only on disposable bare Git repositories and do not replay uncertain production effects. An independently reviewed SHA and a later integration/source-compatibility gate are still required before any main publication or live activation.
