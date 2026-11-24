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
