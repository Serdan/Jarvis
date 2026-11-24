# Automation system properties — incremental source-bound harness

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
