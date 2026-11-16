# WQ-11: offline/default-off Release acceptance

**Date:** 2026-11-16  
**Task:** `wq-11-end-to-end-validation` (Worker 7)  
**Acceptance source baseline:** `289e32754b6e2bd59b157d51c3fd79a16e881e98` (`main` at isolated worktree creation)  
**Development branch:** `refs/heads/test/wq11-offline-release-acceptance-worker7`  
**Runtime:** .NET SDK `11.0.100` (Release), Linux.

## Acceptance scope and evidence boundary

This is a **practical offline smoke**, not the architecture document's full optional 80-scenario catalog, not a production cutover, and not a representation that WQ04/WQ05/WQ06/WQ08 are independently integrated/activated. The tests use private temporary Git bare repositories, two disposable local clones, explicitly fixture-only WQ-02 mutations committed through disposable Git CAS, and a private host source-effect journal. They never target the operator's live Jarvis project/account ledgers.

Six additional executable scenarios are located in `Client.Tests/WorkQueueReleaseAcceptanceTests.fs`. The `Client.Tests.fsproj` entry is unconditional, unlike the explicitly gated upstream WQ06 transport fixtures. The existing Linux hermetic runner isolates HOME, XDG, temp and Git configuration before starting NUnit; its Windows counterpart belongs to Worker 3.

| Scenario | Underlying boundary | Meaning of passing |
|---|---|---|
| Manual Task/Role, one claim, handled reply and replay | Real two-clone operator conversation Git ledger; cross-clone CAS; separate WQ02 project | Original message and reply survive; no implicit scheduler demand or duplicate handler |
| Conflicting writer and later retry | Real two-clone operator conversation Git ledger | Stale writer receives visible conflict; fresh version can commit; original UUID stays replayable |
| Missing canonical producer evidence | Public WQ02 execution route | Caller-invented Task, review and integration assertions cannot appear as trusted stages |
| Primary Claim, dependency wake and replay | **Explicitly inert** WQ02 two-clone fixture API | Protocol transition works in fixture; does **not** prove a real source-producer/scheduler route |
| Lost original Git-push acknowledgment | Disposable trusted WQ03 source-effect journal, reopened | Uncertain original UUID remains observable and is not redispatched |
| Missing original operation | Disposable trusted WQ03 journal | Missing source evidence is explicitly RecoveryBlocked rather than treated as empty/successful |

## Release command evidence

- **Server focused:** `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-build --filter 'FullyQualifiedName~WorkQueue' --logger 'console;verbosity=minimal'`. Result: **130 passed, 0 failed, 0 skipped**, 130 total; exit 0. Source baseline above, .NET 11.
- **Server full Release:** `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-build --logger 'console;verbosity=minimal'`. Result: **275 passed, 0 failed, 0 skipped**, 275 total; exit 0.
- **Client new suite:** `dotnet fsi scripts/test-client-hermetic.fsx -- --no-build --filter 'FullyQualifiedName~WorkQueueReleaseAcceptanceTests' --timeout-seconds 180`. Result: **6 passed, 0 failed, 0 skipped**, 6 discovered/executed; exit 0; disposable environment removed.
- **Client supporting WQ03/WQ04/WQ05/WQ06:** same hermetic `dotnet fsi` runner and `--no-build --timeout-seconds 240`, filter `FullyQualifiedName~SourceEffectJournalTests|FullyQualifiedName~WorkQueueReviewIntegrationAdapterTests|FullyQualifiedName~WorkQueueOperatorConversationsTests|FullyQualifiedName~WorkQueueProducerGitTests`. Result: **37 passed, 0 failed, 0 skipped**, 37 discovered/executed; exit 0; disposable environment removed.
- **Client browser unknown / direct repair:** hermetic runner and `--no-build --timeout-seconds 180`, filter `FullyQualifiedName~compositeLaunchNeverReplaysUncertainPostInjectionOutcome|FullyQualifiedName~DirectRepairKeepsOriginalGitBackupAndAuditWithoutSchemaMigration`. Result: **2 passed, 0 failed, 0 skipped**, 2 discovered/executed; exit 0; disposable environment removed.
- **Client expanded WQ02/WQ04/WQ07:** hermetic `dotnet fsi` runner, `--no-build --timeout-seconds 300`, filter `FullyQualifiedName~WorkQueueGitLedgerTests|FullyQualifiedName~WorkQueueRoleAuthorityTests|FullyQualifiedName~WorkQueueCanonicalSourceEvidenceTests|FullyQualifiedName~WorkQueueCanonicalCasBinderTests|FullyQualifiedName~AccountCapacityLedgerTests|FullyQualifiedName~AccountAdmissionAdapterTests`. Result: **78 passed, 0 failed, 0 skipped**, 78 discovered/executed; exit 0; disposable environment removed. Includes `PracticalTaskProducerPublishesImmutableOriginAndRunsThroughFixtureClaim` (still explicitly fixture), `PracticalReviewAndIntegrationPreserveImmutableHardDependency`, account cap-one races, roles, source lineage, and original operation replay.
- **Two-clone cleanup:** new tests use a dedicated temporary `wq11-acceptance-*` Git tree and `wq11-journal-*` source journal, deleted under `finally`; failed removal fails the test. The hermetic harness independently removes its test profile root and checks executed counts. No live repo/config deletion is authorized.

## Integration readiness (not authorization to deploy)

| Boundary | State as of baseline | What is demonstrated | What remains necessary |
|---|---|---|---|
| WQ01 contract state machine | **Proven in focused Release pure tests** | Blocking-first FIFO, UUID/session, gate semantics and recovery paths among existing 130 green WQ tests | Not evidence of wired runtime request routing |
| WQ02 Git ledger/optimistic CAS | **Tested locally, default-off** | Local bare Git and two-clone replay/conflict paths in Client tests | Integrated canonical producers/host adapters and account/project real authority before enabling |
| Task/Role durable operator conversation | **Offline Git-backed manual route** | Manual post, claim, reply and recovery across clones; queue creation intentionally absent | Remaining WQ06 transport and role-message queue-producer integration; scheduler wake is a separate contract |
| WQ03 source-effect continuation | **Journal records visible; yield authorization not proven** | Original operation identity survives restart and unknown outcome is not retried | Trusted terminal/remote result provenance and complete host route coverage; do not infer a safe-to-yield verdict |
| WQ04 source-event producer | **Incomplete for positive canonical route** | Forged or missing events are surfaced rather than silently admitted | Integrate exact source event/index/role authority, then add positive creation-to-queue proof |
| WQ05 review/integration | **Unverified end-to-end** | Existing adapter unit/fixture tests exercise immutable review SHA and integration hold rules | Freeze and independently review the source; verify actual accepted review and exact target/source commits through live adapters on disposable ledgers |
| WQ07 account-global cap=1 | **Fixture and foundation, not dispatch authorization** | Account CAS and cap-one semantics exist in tests | Prove canonical account/project snapshots and one launch through runtime adapter |
| WQ08 no-Manager scheduler | **Inert/default-off** | Hypothetical planner and gated runtime checks are covered; the simulator does not call a provider | Connect verified project/producer/role witnesses and one-shot dispatch journal; separate operator-licensed activation |
| Browser original send/launch unknown | **Supporting existing isolated regression tests** | Existing BrowserBridge tests protect uncertain post-injection outcomes from automatic replay | Verify browser send boundary under a real paired-extension session; manually inspect/repair unknown original effects |
| WQ10 migration and epoch | **Not attempted** | No production scheduler, legacy Manager or migration state changed | Separate audited offline preflight, operator-approved canary and explicit activation gates |
| Windows full hermetic Client.Tests | **Separate Worker 3 assignment** | Linux-only test harness currently fails closed on Windows | Worker 3 must provide independent Windows Release verdict; this task must not claim F03 closed |

**Release disposition:** no production activation, migration, service restart, GitHub Actions, or automatic scheduler rollout is authorized by these results. Continue exercising the real positive integration route only once upstream frozen reviews and canonical source/role/head evidence are available. Verify every dependency's exact integrated source SHA and independent verdict before making a release claim.

## Operator engineering policy

Favor visible, recoverable real workflows and concise evidence over speculative interlocks. Agents are trusted to reconcile conflicts and can directly repair malformed Git ledger records with the old blob SHA, explicit diff/receipt and audit note. Do not silently lose state or treat a fail-closed fixture as a working runtime adapter. Future 80-case negative-space work is optional, based on demonstrated defects.
