# Automation system properties — incremental source-bound harness

## Double-release invariant (2026-11-24)

After exact-operation replay, a distinct release operation must fail and leave
both clients' observed reservation status and capacity unchanged. Focused job
b6214a930d9d4593a67e83abd572ca46 passed 2/2 (51 s, exit 0) on published production da167f19cd100c86e8f14cc163ed2ef2028fd82c with the exact test delta overlaid; this is not final composition acceptance.

## Source and scope

Private registered worktree automation-system-properties-w20, pinned production
base 08994560914ac473c4c9d935bf584c0e3cc7b3cb (origin/main at start).
Only this standalone test directory is changed. Client and Server are actual
ProjectReferences; the existing hosted Health GitFixture is linked unchanged.
No deployment, migration, live account, real provider or unattended activation.

This is **incremental evidence, not final milestone acceptance**. The current
source has signed disposable Scheduler admission and pre-send project fencing,
not the final existing-client-auth dispatch and terminal/reconciliation path.
The test adapter is deliberately isolated in AccountFixture.fs so the changing
authority implementation can be substituted without rewriting the ownership
oracle. Do not preserve obsolete signing in production or claim this synthetic
coverage witness is live session evidence.

## Model and transition matrix

| Transition / invariant | Production seam / observed evidence |
| --- | --- |
| Fresh, missing, stale activity; checkpoint; recovery | NativeHealth.checkOnce/current/recover; exact task/attempt versions; real Git recovery and checkpoint receipts; independent age/phase/generation model |
| Cross-project ownership and stale lineage | Two disposable canonical projects, each two independent clones; read both canonical snapshots after each settled command; preserve task, attempt, owner, spec, source ref and generation |
| Challenge block, resolve, replay | ChallengeWorkflow.create/blockAttempt/resolve; no runnable ownership while blocked; verified fresh wake locator, forged-generation denial; resolution retry cannot commit again |
| Authenticated committed wake and STOP | executeAuthenticatedCommandWithChallengeWake, registerHost, real Host.RunOnce/StopAsync, real signed account/project head reads, inspectAutomaticActivation; test callback returns Disabled because native operational dispatch is not wired |
| Shared account reservation/capacity | NativeSchedulerAccountReservation.reserveVerifiedDisposable, real signed roster approval, two clients race on captured coverage and one slot; exactly one winner, duplicate and stale-generation denial, roster quota changes without dropping occupancy |
| Pre-send claim fence | claimVerifiedDisposable using actual Started attempt/source lease; stale original rejected following Health recovery; one fresh fence on the other project |
| Occupied uncertain outcome and false terminal | Disposable account transition CAS; original generation and session identity; Unknown remains occupied; positive fake terminal verifier cannot invent immutable canonical terminal record |
| Real SQLite activity and restart | OptionalFeedbackStore and registered HubService (injected authenticated principal), fresh/old/reordered/foreign/reset observations, real NativeHealth recovery; wide age margins for real clock; exact boundary covered by deterministic transport and existing coupled suites |
| Conditional liveness | Canonical eligible state and actual fresh committed host wake are asserted under available authority; no unconditional fairness or successful operational dispatch claimed |

MixedHealthChallengeWake seeds 101–103 use guided reachable prefix and
randomized project/client/command suffix; JARVIS_SYSTEM_EXTRA_STEPS (0–256,
default 16) increases the budget. SharedAccountReservationAcrossHealthChallenge
seeds 211–212 exercise a controlled concurrent account race and guided
cross-feature histories with generated post-uncertainty ownership changes.
The account oracle is a separate expected map of original dispatch identities,
reservation states/versions and assignment generations, not a copy of ledger
output. Concurrency accepts either winner but requires exactly one committed
reservation. Delete-chunk shrinking reruns real isolated histories and retains
the same named invariant; JARVIS_SYSTEM_SHRINK_BUDGET (0–32, default 6) bounds
failure-only cost. A separate shrinker unit canary checks a minimal witness.

## Commands and evidence

From root:
```sh
dotnet test tests/AutomationSystemProperties20261124/AutomationSystemProperties20261124.fsproj -c Release
```

On exact already-built Client/Server references, --no-restore
-p:BuildProjectReferences=false is appropriate. Use --filter
FullyQualifiedName~MixedHealthChallengeWake or
FullyQualifiedName~SharedAccountReservationAcrossHealthChallenge for iteration.
Test-only negative controls JARVIS_SYSTEM_STALE_MUTANT=1 (replace stale
transport candidate with current one) and JARVIS_SYSTEM_DROP_WAKE=1 (drop actual
post-commit callback) must RED. Never set these for normal testing.

Historical intermediate passes (not the final source):
- 50e786e323f449c3ac20e9f379f20dd9: 3/3 initial Health/Challenge, 2m22s.
- b8e5170947524335acd3da1aeba7a8ba: 2/2 account composition, 1m53s.
- 1506cd88bd734e4ca6d6dfc0e47bee98: 2/2 actual committed host wake and STOP, 1m51s.
- 6f39df14224046bbb877351cca312aa2: production Server Release build exit 0.
- Current combined Release test job cc3d385894804265add3741671a08ecf;
  negative stale mutant job aa47366f73574bce81e86f5bb118db2e.
  These jobs were started after compiling the current source; collect terminal
  receipts and record actual outcome on resumption. Do not call them green yet.
  TRX files under /tmp/jarvis-w20-system-properties.

## Existing suites and remaining parity gates

Reuse and retain:
- Client.Tests/ChallengeStateMachinePropertyTests.fs: transition/index/mixed
  receipt models; pending Worker 14 partial-receipt correction must carry
  known-versus-unknown model parity.
- scripts/tests/automation-health-coupled-20261122 and
  tests/AutomationHealthHosted20261123: production activity/CAS, JWT/host
  boundary, restart and current-process coverage.
- scripts/tests/scheduler-property-account-cas-20261122: generated actual
  account CAS differential; rebind to pending existing-auth account authority.
- scripts/tests/scheduler-property-planner-20261122: planner/roster model;
  align to actual new native runtime planner input and selection.
- scripts/tests/scheduler-property-project-effect-20261122: abstract and
  partial native B1 projection, including lost-ACK/late-send seal canaries;
  not full B2/native operational parity.
- scripts/tests/scheduler-hostloop-concurrency-20261122: generated STOP/manual
  interleavings; preserve its independent concurrency semantics.

Current outstanding owner dependencies as observed during implementation:
- automation-native-account-authority-binding-20261124 (Worker 12):
  replace disposable signed-admission adapter with accepted existing-auth CAS.
- automation-native-project-no-send-issuer-20261124 (Worker 23):
  original-bound durable no-send exclusion, concurrent send-vs-seal.
- automation-native-provider-session-host-20261123 (Worker 16):
  existing-auth worker/reviewer ingress and provider host.
- automation-native-runtime-authority-dispatch-20261124:
  actual native operational planner/dispatch/terminal path.
- automation-w14-partial-receipt-correction-20261124 (Worker 14):
  mixed-version known/unknown completeness.

Final acceptance still requires actual native planner/provider dispatch through
production host, reviewer claims/CAS, original-bound verified no-send and
terminal release, restart/disconnect, durable commit/ACK fault schedules, exact
fair dispatch opportunity and full composed shrink/negative controls. The fake
provider is acceptable only at the external-effect boundary; never synthesize
canonical authority. Run affected component suites plus this holistic suite on
exact accepted final composition; independent review of oracle, generator,
real wiring and terminal evidence. Do not rerun an unrelated full release suite
merely to inflate test counts. Every semantic change must update its affected
model/generator/oracle in the same or explicitly coordinated dependent change
before release acceptance; review checks actual semantic parity. Nonsemantic
changes may record why no model change is required.

## Verified incremental evidence update (2026-11-24)

- Job cc3d385894804265add3741671a08ecf: 6/6 passed, 5m41s, exit 0, prior committed harness.
- Job 0cadff77d2ce4996bb9db2991d927ecc: 8/8 passed, 7m06s, exit 0, including the cancellation extension and both captured-generation Health/Challenge CAS schedules. TRX system-properties-cancel-race-full.trx.
- Job 385770cb29ce497787106fcc7b207646: both CAS schedules passed, 7s, exit 0 (intermediate before final cancellation prefix).
- Job aa47366f73574bce81e86f5bb118db2e: deliberate stale-candidate mutant RED 1/1, exit 1; seed 101, invariant `stale recovery denied expected 0, actual 1`, bounded shrink trace retained in job output.
- Job 774294a82a0e448fa54477ed07bdbd14: lost committed wake mutant RED 2/2, exit 1; seeds 211/212, invariant `committed wake reaches real host`, expected Reconciled Disabled, actual Dormant.

The cancellation extension exercises terminal cancellation, exact operation replay without another commit, rejection of late resolution, and no wake or Health recovery from the cancelled dependency. The CAS schedules capture the same original Working generation and pause Health at its final activity read; committing the challenge block first excludes stale recovery, and committing recovery first excludes the stale block. Both schedules check the losing operation leaves the canonical head unchanged and then exercise a fresh authorized continuation. These are real isolated Git transitions with controlled interleaving, not evidence of live provider dispatch.

At 12:16 UTC the local origin/main was 5d0656784 (only browser prompt parsing differs from pinned production base); the existing-auth account correction ea25c1584102fc8796ee15f04219836fb6b8d75c remained a frozen independent review, not accepted main. Do not silently compose unreviewed production branches into this test lease or equate the above passing increment with milestone completion.

## Multi-owner and exact identity extension (2026-11-24)

The initial 312eb7810fefe483591e39f8d1d0d9201e56012c increment was independently Accepted (review 4e165a7d066d4c649b60ff1bc67c37e8, sequence 13459; author acknowledgement 13461). That review did not accept the entire milestone or attest an independently rerun test suite.

The next source increment adds real distinct agent/task/source-branch ownership in one canonical project (seeds 301/302), including blocked sibling isolation, a stale sibling recovery, wrong-owner resume rejection without head changes, and exact working/runnable/blocked wait-index observation. Intermediate focused job e2255899fa2d40b58b704f1387eb9755 passed both cases; the full 10-case Release run adf708af6cdd43babe5307181a549b35 targets c59a2c3e81919f192dc648bc399415df2e842665. The a784eb7af275b4b2fa9d7fb013c63ba0a5fe6a9d extension compares the independent expected entire original candidate, original reservation operation, canonical fence, provider-session and terminal identities, exact generation/state, unique keys and account/per-project quota. It explicitly exercises wrong original effect, fence, session and release rejection. Scoped job 2b27b46d97d04a77a967ec53cc24f15f builds actual production references in isolated artifacts. The 71fd74b41effd39e76b5cb476f6469fb6bc340bf extension tests one real shared SQLite store across project-name/owner key pairs, both registered clients, stale-only recovery and restart qualification (job 8867fb59d7004bfe9377127e8526e7e3). The foreign project key in this test is a storage key, not a second canonical project; the other composed tests have two real canonical projects. Terminal results are recorded separately below once verified; a running job is not a pass.

Component parity audit: the historical Scheduler account-CAS source pin 5607baf9913f4976add13c984e0182906d4fe8c7 still exactly matches the pinned base module blob fd9c9591c74ce906e15dfc191c2dbac0995b8dc3 and fixture blob e6988ac42f4191f807b57f04dcb6ddec160b503f. Pending W12 ea25c158 changes both; after accepted publication the source pin must fail until the actual existing-auth fixture/driver is rebound and model semantics updated, not bypassed. Planner Phase 1/2 (320 seeds x14), project-effect JS (256 seeds x48, partially real Git but fake account), generated STOP/manual host interleavings, coupled/hosted Health, and canonical Challenge models remain complementary evidence; none replaces the new native runtime composed test. Retain their independent model oracles and update affected production adapters on exact accepted source before final milestone acceptance. Lead/owners must coordinate any component-file edits outside this isolated test lease.

Verified terminal receipts, 12:29 UTC:
- adf708af6cdd43babe5307181a549b35: exit 0, 10/10 passed, 8m05s, system-properties-owners-full.trx, exact c59a2c3e8 compiled test source.
- 2b27b46d97d04a77a967ec53cc24f15f: exit 0, 2/2 passed, 3m52s, system-properties-original-identity.trx, exact a784eb7af compiled test source, fresh isolated actual Client/Server references.
- 8867fb59d7004bfe9377127e8526e7e3: exit 0, 1/1 passed, 2s test duration, system-properties-shared-activity.trx, exact 71fd74b41 compiled test source, fresh isolated actual Client/Server references.

These staged runs validate the incremental source changes; they are not a single final full-suite run on 71fd74b41 or on a future accepted production composition. The later changes affect only the scoped account and activity tests; all 11 cases have a passing staged result. Do not treat this as evidence of native operational dispatch or actual release. Current production origin/main 5d0656784b3107480318b2fb842cf7b67a2f2c47 still differs from pinned base only in browser prompt parser/test; W12 and W23 remain Started, W16 Blocked, native runtime task unassigned. The next useful system-property increment is existing-auth/operational binding rather than more tests of obsolete signing. Final acceptance requires the remaining actual seams, role quota/reviewer CAS, conditional dispatch opportunity, fault/restart schedules and receipt-completeness parity.

## Checkpoint/recovery CAS extension (2026-11-24)

The second bounded increment 872139fa260e4c1c1f1dfea06514f3212f31ee40 was independently Accepted in review b0a4a3c0842a4cb69dc462b507191320 (decision 13518, acknowledgement 13555). It was not yet integrated at the time of the following run. Commit af7ad34fe1c5385d07cf64a06409189d935b412b adds both deterministic checkpoint/recovery interleavings at the final activity barrier. Checkpoint-first must defeat the stale recovery CAS, but not count as fresh activity: a fresh CAS can recover the same still-stalled owner. Recovery-first must reject the old checkpoint without writing. Exact original checkpoint retry after recovery preserves the sequence, content and successor head. Each case finishes with a real resume, challenge block and committed resolution.

Job 841cb21308494e8cb0e32028776c7623 completed exit 0; 2/2 focused cases passed, 8 s test duration, TRX system-properties-checkpoint-race.trx. Command: `dotnet test tests/AutomationSystemProperties20261124/AutomationSystemProperties20261124.fsproj -c Release --filter FullyQualifiedName~CheckpointAndRecoveryCompeteForCapturedVersion --logger "trx;LogFileName=system-properties-checkpoint-race.trx" --results-directory /tmp/jarvis-w20-system-properties`. Actual Client and Server references rebuilt on pinned base. This is staged evidence, not a fresh full-suite or final composition pass.

Upstream main verified via ls-remote at 45825a2aee0699fc110134bafc153be789eb1c36; initial test increment integrated, producer semantics still unchanged from the original pinned source. Existing-auth account source ea25c1584102fc8796ee15f04219836fb6b8d75c (review bd902f0af91443a7b9b68e1037f49c51 Accepted) and original-bound no-send source 6bb5998af609014fc0c4c69c501f028d827dead2 (review 6c12d587232b4b13a416ab2dac1f70f5 Accepted) are not published on main. W12 account attempt Blocked, W23 no-send issuer Finished, W16 host and W19 runtime Started. Do not merge their independent production source into the test-only lease or claim final composition. Next rebind AccountFixture after the accepted coordinated producer/consumer composition is available: normal `setNativeRosterPolicy/readNativeRosterPolicy`, `readVerified/reserve/transition/readOriginal/releaseNoSend/release` (internal existing-auth seams accessible via the existing Client.Tests friend assembly). Reuse the expected original-candidate and occupancy oracle, replace signing entirely, use real project effect exclusion/terminal issuer and provider boundary, then rebind host wake to actual planner/dispatch. Component account source pin and driver must be coordinated with their owner; preserve exact historical evidence, never bypass the pin. No additional test investment in obsolete signing.

## Frozen Stage C existing-auth incremental adaptation

This test-only increment depends on the unaccepted frozen Stage C composition
19378518c5469b1dda8eeaa9b2cd9e3f7d2f8c82, which contains accepted W12
f94bbceff4203538c7fc70add7c1d50d8bca598d and W23
6bb5998af609014fc0c4c69c501f028d827dead2 as ancestors. It cannot compile
against the older pinned production base until that dependency is composed.
It is not evidence that Stage C has been accepted or published.

AccountFixture now uses existing authenticated native roster, reservation,
project claim and original-bound transition APIs instead of disposable signing.
The preserved independent account oracle compares original candidate/operation,
generation, fence, session, state, per-project and account occupancy across two
real disposable clones and mixed Health/Challenge histories. Wrong original,
effect, fence, session and forged terminal are negative controls. Test-owned
fixture reads the original immutable reserve receipt; no fabricated account
authority or actual provider sends are used.

NoSendReleasePreservesChallengeAndCapacity seeds 401/402 exercise original
reservation, real preclaim exclusion transaction, rejection of forged evidence,
account release and idempotent replay, denial of the old claim after release,
blocked work denial with available capacity, stale Health check while blocked,
challenge resolution and subsequent capacity reuse. Compare actual account
snapshots with a separate expected status map after each settled stage.
NativeProjectEffectTransition.fs is linked into the test assembly as in the
frozen HostTests project: this exercises the real planner code and Git CAS,
not a production-wired issuer. The fixed guided histories are incremental,
not a substitute for generated send-versus-exclusion interleavings, shrinking,
provider/host dispatch, or final exact published composition execution.

Job 4ecd01890fef4589902d76c52736552c: 2/2 no-send cases passed, 41s,
before the additional stale Health check. Job c5118e7706da4213bfad2464001e21a8
failed both existing-account cases at a test fixture null dereference for the
intentional wrong original. This was a fixture bug: preserve the real original
receipt while supplying the incorrect original UUID to the consumer. Corrected
runs and final no-send extension must be recorded from terminal results.

Job d4243864662b4ab893f44f1d0cba4657: 2/2 no-send and blocked-Health cases passed in 46s, exit 0 on the final test source copied over frozen Stage C. The final account-occupancy rerun remains separately tracked.

Job dfe223cfeac54e439f176ffe9408777e: corrected existing-auth occupancy histories 2/2 passed, 3m52s, exit 0. This run used the corrected adapter and pre-Health-check revision of the new separate no-send test; no-send final revision was separately run as above. These staged results are not a full-suite run.

## Project Worker quota increment

ProjectWorkerQuotaIsolatedAcrossProjects seeds 501/502 use two distinct
owners in one real disposable project and a second project. Two client clones
race with the same captured account coverage; the losing fresh retry must be
denied by the existing per-project Worker quota even while account capacity
remains, while the second project can reserve. Original-bound pre-claim
no-send release permits a replacement; replay and a different release operation
cannot release the successor. Both clients' exact original status projections
and quota bounds are checked after each settled step.

Focused job 35a678a2b9664780ba3e5a5da77e9eea passed 2/2 in 29 s
(exit 0), with this test overlaid on integrated source
cb3b2f54a9f586228d5b2b210f1be6600099f932. Command:
`dotnet test tests/AutomationSystemProperties20261124/AutomationSystemProperties20261124.fsproj -c Release --filter FullyQualifiedName~ProjectWorkerQuotaIsolatedAcrossProjects`.
The initial race may reject a contender on stale account CAS; the subsequent
fresh retry establishes the quota check. This is a guided, seed-varied
increment, not a generated comprehensive history or a full-suite pass.
It exercises existing native Scheduler account roster Worker admission only.
It does not establish shared project-total/per-role enforcement across manual
and Scheduler launch paths; that production seam is under Worker 21's
automation-project-role-capacity-admission-20261124 task. After its frozen
interface is available, adapt the independent model to mixed role/manual
admission, unknown occupancy, restart and original-bound release and run
against the accepted composition. No live effects.

## Original claim versus preclaim exclusion (2026-11-24)

OriginalClaimAndExclusionCompeteAcrossChallenge seeds 601/602 race the real
started-project claim against a real preclaim no-send project Git transaction
for the same existing-auth account reservation. The model requires exactly one
successful original fence, preserves the original account identity and
occupancy, and checks the blocked sibling project. When exclusion wins it
checks genuine no-send release, denial of blocked work with free capacity,
receipt replay and capacity reuse after Challenge resolution; when claim wins
it checks duplicate claim and unsupported terminal release remain denied.

The two Task.Run operations permit either winner; this is a bounded scheduling
race, not a controlled proof that both outcomes occurred, a generated long
history, or provider-send execution. The existing project-effect component
suite supplies controlled send/exclusion race coverage; operational host,
trusted project-role admission and final composed acceptance remain outstanding.
The helper refactor exposes the exclusion transaction Result for this race
while retaining the existing successful-call wrapper.

On integrated production source 0cca782ca plus this test-only delta, job
db5359544d62416b89588dc76749b023 passed 2/2 in 30 seconds, exit 0:
`dotnet test tests/AutomationSystemProperties20261124/AutomationSystemProperties20261124.fsproj -c Release --filter FullyQualifiedName~OriginalClaimAndExclusionCompeteAcrossChallenge`.
After the helper refactor, job a2a06d717fa74e3ea36b08551a51c825 passed the four
existing no-send and project Worker quota cases, exit 0, 1m25s; that staged run
predates only the additional assertion in the new race. The complete pre-delta
suite at exact dd211c32afa15bc9319bc959bfb485841d7b21f6 passed 17/17,
10m14s, exit 0, job a3595fa00a77499cb4d683ec6a86d078. These are not a final
milestone acceptance run. The legacy account component prepare script fails
closed at this composition: expected blob fd9c9591c74ce906e15dfc191c2dbac0995b8dc3,
actual 818bb6f8d43ab55177afe17e108e0155e6f43160. Its existing-auth rebind needs
a separately coordinated lease; never bypass the pin.
