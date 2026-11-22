# Scheduler property / planner-capacity independent model — Worker 14

Task: `scheduler-property-planner-capacity-model-20261122`.
Implementation is confined to `scripts/tests/scheduler-property-planner-20261122/**` and this documentation. No production, shared project file, scheduler, browser, account ledger or main changes.

## Authority, exact source, and scope

Pinned source base: `dcac884fc061869572b12a03befc12f11c009f2f` (current observed main when the isolated branch was created). The following accepted source commits were verified to be actual ancestors of that base, not merely name-matched text: Phase 1 `34facab5db97b2f238d2415c8f7ae6b59866b754`, Phase 2 `3ea482c5fedd95b3a88817f0b8a432e93955fd34`, native roster `bfa9cbe94a1e4a10aa18c3f78add6d3fec54a859`. The first test checks that production files for Phase 1 and Phase 2 and `Client/Modules/NativeSchedulerRosterPolicy.fs` are byte-for-byte identical to their Git blobs at the base, and that the base is an ancestor of HEAD. GA SDK version observed: `11.0.100`.

Native roster policy proves signed configuration and approved membership when its separately gated disposable signer fixtures are valid. It **does not prove occupancy**, free Worker/Reviewer sessions, an atomic reservation, or launch authority. Phase 2 deliberately exposes `accountCapacity.complete:false`, `sessions:null`, `reservations:null`, `completeness.projects:false`, and the default-off diagnostic explicitly reports `sideEffects:0` with no candidates. The tests exercise those actual production APIs without falsely treating the WQ07 token quota as native Scheduler capacity.

## Independent reference model and generation

`planner-stateful.test.mjs` builds two distinct projects, three Ready Worker/attempt/source-lease identities and three frozen review identities per project, verified account/project capacity views, and an independent selection model. The oracle derives its own SHA-256 candidate identity and applies separate admissibility, existing occupancy, review eligibility, and provisional account/project quota rules. It never calls production helpers to compute the expected decisions.

The seed generator uses a deterministic xorshift state and **320 seeds × 14 transitions = 4,480 stateful transition checks**, with each step comparing the entire ordered Worker and Reviewer candidate-key sequences against the independent oracle. Commands permute Worker started/ready/parked, revisions, priority, source-lease trust, review claim/requeue, per-project and account quota changes, Unknown sessions, terminal/uncertain reservations, and capacity evidence changes. Candidate keys remain stable on replay, and the planner input must remain byte-identical. The shrinking harness repeatedly removes command segments and retains only traces reproducing the original mismatch category, with source SHA, seed, category and minimal trace printed on failure. A deliberate forged candidate is rejected and shrinks to its single relevant Unknown-session transition. The `maxAgeMs:0` option is a separate typed `TypeError` negative control.

The property asserts **no new oversubscription**. If previously observed occupancy already exceeds a newly lowered quota, the valid invariant is *zero additional candidates*, not `existing occupancy <= new quota`. An initial red test incorrectly required the latter; it was corrected in the test oracle, with no change to production.

### Additional adversarial tests

`adapter-git-boundary.test.mjs` independently creates a **new disposable bare account Git remote and two bare project Git remotes** for each test, each with real commits and approved canonical refs. Tests compare actual `captureCanonicalSnapshot` output to exact fetched remote heads, verify read-only HEAD stability, check genuine Ready assignment projection, missing canonical assignment issue, corrupt/truncated JSON producing `CanonicalSnapshotError`, and project identity mismatch. No fake complete capacity is introduced, even when account WQ07 configured work-token capacity is positive.

Other negative cases include zero/negative quotas, missing account/project capacity, contradictory local/account capacity records, incomplete or stale snapshots, unverified source leases, stale attempt generations and assignments, Unknown sessions, an Uncertain reservation, duplicate reservation identity, unverified agent, absence of trusted project membership, and a 20-client simultaneous read-only planning fan-out followed by **simulated serialized reservation**. The fan-out demonstrates deterministic identical proposals, not an atomic concurrent reservation. A later reservation/CAS effect boundary requires independent production evidence and is not inferred here.

## Reproduction and known limitations

From the isolated worktree, with the source base available in Git:

```sh
node --test scripts/tests/scheduler-property-planner-20261122/*.test.mjs
SCHEDULER_PROPERTY_SEED=177 node --test scripts/tests/scheduler-property-planner-20261122/planner-stateful.test.mjs
node --test scripts/tests/jarvis-ledger-planner.test.mjs scripts/tests/jarvis-phase2-snapshot-adapter.test.mjs
dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --filter 'FullyQualifiedName~NativeSchedulerRosterPolicyTests|FullyQualifiedName~WorkQueueSchedulerHeadBindingTests' --logger 'trx;LogFileName=scheduler-worker14-narrow-release.trx'
```

The `SCHEDULER_PROPERTY_SEED` selector accepts 1 through 320. Each seed reproduces the same ordered 14-step trace; on failure, the recorded minimal JSON command sequence uses `op`, `project`, `index`, `value`, and `step` and can be supplied to `runTrace` in the test file. All production modules are imported unchanged from the pinned checkout. The real Git fixtures are created under temporary private directories and removed by test teardown.

Safety assertions are stronger than simple liveness tests: no dispatch is permissible on absent proof; unknown capacity consumes slots if *verified* or remains incomplete if not; repeated unknown browser submissions are not retried by the planner; cross-project occupancy must consume shared account quota; proposal-only fan-out is not an atomic CAS proof. The independent oracle intentionally models the supported core Worker/Reviewer eligibility and quota policy, **not** the complete Challenge requeue state machine, every malformed input reason, or fairness guarantees absent from the production contract. Separate existing production regressions check Challenge hold categories and Phase 2 historical index chronology. No live schedules, browser launches, production account writes or scheduler activation were attempted.

## Terminal evidence (November 22, 2026; source base above)

- Own Node suite: Jarvis Job `f7f981fb54ad4d448f3d59dae2eecd1e`, exit 0, **11/11 tests passed, zero skipped**; underlying deterministic trace comparisons **4,480** plus explicit negatives.
- Existing Phase 1 + Phase 2 Node regressions: Jarvis Job `7a439fbd2a0e4c3f932beb698b2d17af`, exit 0, **76/76 tests passed, zero skipped**.
- Initial broad .NET 11 Release filtered run: Jarvis Job `c151806e04be4662b8de830dab1412d9`, exit 1, **28 passed, 1 failed, zero skipped of 29**. The one existing unrelated failure was `Client.Tests.WorkQueueSchedulerHostLoopTests.StopJoinsConcurrentManualStepWithoutBackgroundLoops`, at `Client.Tests/WorkQueueSchedulerHostLoopTests.fs:318`: expected `manuallyRunning.IsCompletedSuccessfully` True, observed False. Evidence: `Client.Tests/TestResults/scheduler-worker14-release.trx`. **Not adjudicated as flaky or fixed.** No out-of-lease correction attempted.
- Narrow native roster + head-binding .NET11 Release run: Jarvis Job `3377b112b7dd4f6486d5e225e73f3657`, exit 0, **17/17 tests passed, zero skipped**, TRX `Client.Tests/TestResults/scheduler-worker14-narrow-release.trx`. This proves only the scoped native roster/head-binding gate, **not** the failed broader host-loop gate.

The source-pinned tests have not been reviewed or integrated. Independent frozen review and an authoritative verdict are required. A production correction for the host-loop failure, if independently reproduced and adjudicated, belongs to a separate bounded task and source lease.
