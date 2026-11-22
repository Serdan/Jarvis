# Automation production-port callability probe — Worker 23 — 2026-11-22

## Disposition

**HOLD the composed Health → Challenges → scheduler → provider/terminal B2 production gate. GO only for a later, explicitly isolated, default-off test-only composition once independently approved membership, registered activity, scheduler reservation, and host/terminal bindings exist.** This is not authorization to activate Health, subscribe a scheduler, send browser/provider operations, deploy, cut over, or mutate `main`.

The isolated branch `test/automation-real-port-probe-worker23-20261122` was created clean **directly from accepted-only Integrator private r2 `d312f1cc83685e781ce69e57ad0d4bf613bfb843`**, and has not merged anything or changed its production files. It retains the parent source graph; this probe has **independently rechecked all eight accepted Client-path source blob pins** against the exact accepted-only base, but has not substituted source verification for independent acceptance or converted any SOURCE-ONLY review to full Release acceptance. Source-only acceptance is not a composed release pass.

## Tested real-port vs production-evidence matrix

| Boundary | Concrete source / API, exact input-output type | Probe | Classification and missing evidence |
| --- | --- | --- | --- |
| Health decision | `Client/Modules/NativeHealth.fs:49-53,93`: `NativeHealth.checkOnce : Settings -> Dependencies -> Task<CheckSummary>`; `Dependencies = { Enumerate: unit -> Result<Candidate list,string>; Current: Candidate -> Result<Candidate option,string>; Activity: string -> string -> Task<Result<AgentActivitySnapshot,string>>; Recover: Candidate -> Guid -> Result<unit,string> }` | Invoked actual Release assembly with disabled settings and denied canonical enumeration. Disabled path makes **zero** callbacks; no approved roster yields HOLD, no recovery. | **Callable decision / test-injected evidence only.** `NativeHealth.enumerate/current/recover` exist on real `Runtime` at lines 229,282,333 but were not given a live runtime nor a claim of genuine activity. |
| Registered server activity | `Server/Services/HubService.fs:11,124-139`: `GetAgentActivity(projectName:string,agentId:string):Task<AgentActivitySnapshot>`; `Client/Program.fs:475-499` creates `Dependencies.Activity` through `HubConnection.InvokeCoreAsync<AgentActivitySnapshot>` after `registered && Connected`. | Both assemblies compiled and method signature reflected. Additionally, called the actual `HubService.GetAgentActivity` with a no-network `HubCallerContext` and real empty `UserService`: it rejected the unregistered connection with `UnauthorizedAccessException`. No live SignalR, authenticated session, SQLite activity, or activity timestamp was manufactured. | **Real unregistered server negative verified; NOT authenticated E2E.** Hub requires `GetSessionByConnection` Registered, same connection and `UserId`; an unregistered/fake DTO is not accepted as proof. No portable `IRegisteredAgentActivityReader` adapter is exposed for a standalone test. |
| Challenge resolution and canonical Git CAS | `Client/Modules/ChallengeWorkflow.fs:993-995` `ChallengeWorkflow.resolve binding operationId actor challengeId conditionId version evidence blockedReason`; `create/get`, `LedgerStore.fetch` and immutable event/history. | **Actual canonical APIs executed** against two fresh, independent clones of a fresh local bare Git remote, with real initialized ledger and registered agents. Checked create/get → intruder ownership rejection → exact original operation resolve → cross-clone idempotent replay → stale generation/version rejection → Satisfied event/history/evidence. | **Canonical Challenge wake/requeue callable and verified in disposable project Git scope.** New canonical task/attempt/owner fixture calls `blockAttempt`, establishes the issued reverse wait-index and `Waiting` `AttemptChallengeWait`, then cross-clone `resolve` traverses the actual `wakeDependents` path to `Queued` generation 2, owner `Ready` and wait `Requeued`; duplicate and stale generation/requeue rejected. Separate manual zero-dependency `requeueAttempt` covers exact CAS and late replay. No scheduler host subscription or B2 provider/terminal delivery is claimed. |
| Verified scheduler account/project heads | `Client/Modules/WorkQueueSchedulerHeadBinding.fs:33-40,88-97,322-355`: `SchedulerHeadScope`, `realReadPorts` calling `AccountCapacityLedger.fetch`, `LedgerStore.fetch`, `WorkQueueRoleAuthority.loadAt`; `Observer.ReadVerifiedHeads: Guid -> Result<VerifiedWorkHeads,string>`. | Invoked actual `Observer` without approved membership and `ReadVerifiedHeads`: returns `HeadObserverDisabled` and host reports `CanonicalHeadsUnavailable`; no dispatch. | **Real read adapters compile; positive verified-head/membership input absent.** No user-approved complete account/project/role manifest, no activation, no fake account snapshot promoted to authority. |
| Scheduler coordinator/host | `Client/Modules/WorkQueueReactiveWake.fs:49-55,108,136` `Coordinator(WorkWakePorts)`, `Wake : WorkWakeTrigger list -> WorkWakeResult`; `Client/Modules/WorkQueueSchedulerHostLoop.fs:27-47,110` `productionPorts`, `Host.RunOnce`. | Called real `productionPorts` and `Host.RunOnce` with actual disabled `Observer` read. Default off yields `Disabled`; locally opted-in manual step yields `NeedsAttention` without any `Execute`. `Start()` remains false; no scheduler service starts. | **Callable fail-closed control plane, not a composed real planner/executor.** The `WorkWakePorts.Plan/Execute` supplied in the probe are deliberately throwing/inert, not positive producer credentials or provider evidence. |
| Pre-send account reservation / terminal | `Client/Modules/NativeSchedulerAccountReservation.fs:11-83`: `NativeDispatchCandidate`, `NativeReservationRecord`, `INativeDisposableTerminalVerifier.VerifyCurrentTerminal: NativeTerminalLocator -> Result<NativeTerminalProof,string>`. | Compiled production Client assembly and referenced the genuine `NativeDispatchCandidate` type. No reservation or terminal callback invoked because no independently approved account identity/provider/terminal trust root is present. | **Source available, B1 disposable policy only**; source explicitly disclaims production trust root and authenticated session ingress. Synthetic `NativeDisposableCoverage.Complete` cannot qualify as B2. |
| Fake-vs-private host B2 | `Client/Modules/WorkQueueNativePrivateHostBridge.fs:27-55`: `inspectProduction: bool -> PrivateHostGate`; `Client/Modules/NativeProjectEffectFence.fs:1` conditional `#if STAGE_B_DISPOSABLE_TEST_ONLY` and missing from `Client.fsproj`. | Invoked `inspectProduction false/true`; true returns `HostBridgeUnavailable` with 3 missing interfaces. Separate expected RED FSI compile of `open Client.NativeProjectEffectFence` against *production* Client DLL exits 1 FS0039. | **B1 fake-only; B2 not implemented/available.** Missing `ProviderPrivateExecutionBootstrap`, `AuthenticatedProviderExecutionBinding`, and `CredentialAuthenticatedWorkFirstConnect`. `Server/WorkQueueSchedulerInert.fs` hypothetical state is not a production adapter. |

### Minimum missing seams / ownership

1. **Registered Health query port:** encapsulate the existing `Client/Program.fs:475-491` registered `HubConnection` closure in a test-injectable but *registration-attested* client adapter whose server side remains `HubService.GetAgentActivity` and `OptionalFeedbackStore.GetVerifiedAgentActivity`. Owner: Client runtime/SignalR integration with Server authenticated session support. Do not expose fake timestamps as authenticated input.
2. **Smallest composed trigger adapter proposed, not implemented:** a `Client.AutomationChallengeWakePort` receiving a verified canonical `ResolveChallengeCondition` receipt (operation ID + project ledger identity + current Git head) and producing `WorkWakeTrigger.DependencyCommitted` *only after rereading that verified head* for a specifically bound `WorkQueueSchedulerHeadBinding.Observer`. Owner: Client Challenges/WorkQueue orchestration integration (assign through Lead). The current probe finds no registered wiring from `ChallengeWorkflow.resolve/wakeDependents` to a scheduler host. This adapter alone does **not** authorize scheduler startup or B2.
3. **B2 reservation / terminal private host:** independently authorized account/role membership, provider-private bootstrap and credential-authenticated first-connect/terminal witness are externally missing from the checked-in production path. Owner: provider/native host protocol and Jarvis authenticated Work credential integration, not this tests/docs-only lease. B1 `STAGE_B_DISPOSABLE_TEST_ONLY` must not be promoted.

An additional accepted-only default-off **B1** integration test can compose canonical Challenges with verified-disposable Git account/project heads using an independently approved complete fixture. It may exercise a real HEAD reader and CAS, but must retain labels `B1 TEST-ONLY` and `B2 HOLD`. A source-exact unfiltered full Client Release is a *separate exclusive slot*; this probe did not start or replay one.

## Source exactness

Base commit: `d312f1cc83685e781ce69e57ad0d4bf613bfb843`.

```text
Client/Client.fsproj                            5ba8b9c5c9ff2fc25c1464f101b52afa819333bd
Client/Modules/NativeHealth.fs                 4753293d7da5379db18ade6f03e40ac34fb3d58b
Client/Modules/ChallengeWorkflow.fs            981011513c2e7636d9318e907987d4d2232e3fda
Client/Modules/WorkQueueReactiveWake.fs        87f81d557b2f7ed00efccb693b9c08a78e3cb0e1
Client/Modules/WorkQueueSchedulerHostLoop.fs   800a7388dc301a1560de0aa22f07f5a1d9fbad59
Client/Modules/WorkQueueSchedulerHeadBinding.fs 6218666e1f5089773afbe567781b71b2d6c60124
Client/Modules/NativeSchedulerAccountReservation.fs fd9c9591c74ce906e15dfc191c2dbac0995b8dc3
Client/Modules/WorkQueueNativePrivateHostBridge.fs 9a2b51f261da7c1a42e455fcbe78bfb0642f61c8
Server/Server.fsproj                            bb058148a86cf2345d982f3cfe9bc4acc0cbae02
Server/Services/HubService.fs                  eef5972c9901c68de1cb68112b363e3d6c6c5273
```

These are the actual base `git ls-tree HEAD` blob OIDs, not inferred from another branch.

## Managed .NET 11 Release terminal and reproducibility

Passing focused job: `5b669ac8df78446fbcf062dff223d5c8`, **Completed, managed process exit 0**, .NET SDK 11.0.100; run from the isolated registered worktree:

```sh
dotnet test tests/AutomationRealPortProbe20261122/AutomationRealPortProbe20261122.fsproj -c Release --logger 'trx;LogFileName=w23-real-port.trx' --results-directory tests/AutomationRealPortProbe20261122/TestResults
```

Terminal: **6 passed, 0 failed, 0 skipped, total 6**. TRX: `tests/AutomationRealPortProbe20261122/TestResults/w23-real-port.trx` (local generated, not part of committed source). Warnings: FS0066 Server unnecessary upcast; FS3511 Server dynamic state machine; FS0760 Client dispose syntax; FS0988 standalone probe main-program warning; no compile errors.

Expected-RED B2 job: `be6d8708917c4a779785de8032d6d7d3`, **Completed, managed exit 1**, `dotnet fsi --exec tests/AutomationRealPortProbe20261122/ExpectedRedB2.fsx`. Actual diagnostic: `ExpectedRedB2.fsx(6,13): error FS0039: The namespace 'NativeProjectEffectFence' is not defined.` Deliberate negative compiler proof, **not** a failed test in the passing suite and not an invitation to link the fake module into production.

Independent SHA-256 output artifacts:

```text
Client/bin/Release/net11.0/JarvisClient.dll f997b8b73ac8c1299e90ccd1beecbdc3cb535c00a106b693ebf3755d8a1f4cc4
Server/bin/Release/net11.0/JarvisServer.dll 007fb728b9205f686c98728befb1dbe4dfc2efe4ed94ca1c527a0c2d61a1a7e8
tests/AutomationRealPortProbe20261122/bin/Release/net11.0/AutomationRealPortProbe20261122.dll 6d3620e807617b074c859c3f9f4918fb9e1657244fac75063b484bd614d15a06
tests/AutomationRealPortProbe20261122/AutomationRealPortProbe20261122.fsproj 6312427a94d4b77663ea35603d7255f2ba5f4415c4bf5365aa2d99af53453c3f
```

No `Client.Tests.fsproj`, `Client.fsproj`, `Server.fsproj`, production source, linked scheduler, original browser send, live server, `main`, deployment or cutover edited/invoked by the probe. No full unfiltered Client Release run was attempted. The independent review must preserve these scope limits.


## Adjudicated review correction — frozen focused evidence

Original immutable Reviewer 4 `ChangesRequested` review `5aef273b5d404d7aa5ce581fe83ac1cc` at sequence **11660** on `b9a3c223de9164fa827a4bac6b4a3e09e2d722fd` remains unchanged. Independent Adjudicator 1 terminal checkpoint **11681**: **P1 ACCEPT**, requiring canonical block/wait reverse-index/wake and generation-negative coverage; **P2 NARROW**, requiring actual registered Health ingress denial or source-bound no-live RED rather than fabricated activity data. Both repairs are limited to this existing test file and this document.

- **Challenge real path:** `ChallengeBlockedAttemptWakesViaCanonicalReverseIndex` creates a canonical task and assigned attempt on two fresh disposable Git clones sharing a bare local remote, records the authentic `WaitOnChallenges` CAS and reverse-index path, confirms the materialized `Waiting` wait and `Blocked` attempt, then invokes real `ChallengeWorkflow.resolve` from the opposite clone. Verified `Queued` attempt at generation **2**, matching owner `Ready`, persisted `Requeued` wait, operation-ID replay without extra generation and stale generation/requeue refusal. `ChallengeManualBlockHasExactCASRequeueAndLateNegative` calls actual `blockAttempt` with an explicit manual zero-dependency wait then actual `requeueAttempt`, checking exact CAS, cross-clone visibility, idempotent replay and late stale denial. The checked reverse-index is **issued canonical ledger state**, not synthetic DTO input. This does not prove host notification/subscription or B2.
- **Registered Health negative:** `ActualHubServiceRejectsUnregisteredAgentActivityWithoutDbOrNetwork` constructs a real `Server.Services.HubService` with actual empty `UserService`, `ClientResponseTracker`, and an in-process `HubCallerContext` whose connection is **not registered**. Calls the actual `GetAgentActivity("Jarvis","Owner")` and verifies its `UnauthorizedAccessException` with the registered-client guard message from `Server/Services/HubService.fs:127-139`. The test neither constructs a fake activity response nor opens SignalR, queries SQLite or registers a server session. The positive authenticated `Client/Program.fs:477-491` closure is still unavailable as a standalone injectable production port and is not claimed E2E.
- **RED-first provenance:** managed job `14ad6e6e1e4c4fc3903c13a652b31348` exit **1** at NUnit overload ambiguity FS0041 in the newly added index assertion; corrected to scalar count/path assertions. Managed job `7dd7925e31f84b0886c70b4f29620996` exit **1** at actual HubCallerContext `Items` get-only override FS0859; corrected to a get-only property. These are fixture compile REDs, not evidence of a production defect or passed runtime tests.
- **Final focused .NET 11 Release:** managed job `65a081b93d2147039dafb4ddd282b35c`, **Completed, exit 0**, command from the exact registered worker worktree:

  ```sh
  dotnet test tests/AutomationRealPortProbe20261122/AutomationRealPortProbe20261122.fsproj -c Release --logger 'trx;LogFileName=w23-adjudicated-focus.trx' --results-directory tests/AutomationRealPortProbe20261122/TestResults --no-restore --nologo
  ```

  TRX `tests/AutomationRealPortProbe20261122/TestResults/w23-adjudicated-focus.trx`: **total 9, executed 9, passed 9, failed 0, skipped 0**; managed output reported 9 passed and no failed/skipped. This is one focused test project, **not** a full Client Release run. Previously recorded 6/6 and expected B2 FS0039 evidence above remain historical; the new source is not implicitly independently accepted.
- **Current focused build/source hashes**, from independent SHA-256 process `d9387d6e518348cfbc4d16ddcd6a30e2`: Client DLL `c79c7c8cddd85738536448eac02554f3e65e29fb5fba6e62356ede24a790ffe4`; Server DLL `91a48c26c5f4e99ba07564f392fd018862a0cdc550aca490f82c30623a93abc9`; probe test DLL `00e1c1b741221be120c75d40dd470d67a91b673ac4d8f7d43917d16ed97a8dd4`; isolated test fsproj `6312427a94d4b77663ea35603d7255f2ba5f4415c4bf5365aa2d99af53453c3f`; `ProbeTests.fs` `c714a8ff8db672a81952f98fcb53a9e9108e44481c58898040dd6613dfcbe68a`; new TRX `cfe59ea9af3992cf7efb9d7e06c8d87a767ed88d9b1b5a48bd9680f7e0b70e58`. These record local tested bytes, not a B2 source trust token. A fresh frozen independent review is required before claiming source acceptance.

**Source boundary:** `git ls-tree d312f1cc83685e781ce69e57ad0d4bf613bfb843` separately rechecked the eight Client path blob OIDs listed above plus both Server paths with exact matches (job `35debdeb8f6d44948d3cd2e4b7d1c0e3`, exit 0). No production module or project file modifications, full Client suite, live send, scheduler activation, deployment, main mutation or cutover.
