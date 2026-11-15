# Reviewer 2 independent audit — WQ04 semantic legacy-ID correction

**Canonical task-bound review:** 62a2380dfe8a40b2a06fc15fb8fea552; task wq-04-queue-producers-priority-roles. **Claim:** seq3707, Reviewer 2, Unclaimed v1 -> Claimed v2. **Frozen exact SHA:** dc71085090e4c914a3a336baba248907a3fb3030. **Recommended verdict:** Accepted **for the bounded inert/fail-closed correction only**, not for canonical WQ04 producer activation.

## 1. Immutable ancestry and scope

Independent git show establishes dc710850 has the single immediate parent 5e2c1b4164204145653d957bd4adfcf24f597c0a (prior Reviewer 2 combined-source ChangesRequested, review fbf33be4bb0c4dff92a7415f4827db0a, seq3660). That parent's first parent is exactly integrated baseline e14173e116dc03b7dbdf261895fde3c893a643ee and second parent is Worker4's corrected 29b5ae1ee3ea579824ea0f19cab9d902d7713802. New tree is fde8013dfb7366e5fd9b652d752d927a0afec4bf. Frozen worktree worker4-wq04-semantic-fence has exact head dc710850 and is clean. Independent git diff --check 5e2c1b41 dc710850 exits 0.

## 2. Original blocker: public ingress now requires semantic authority independent of stage IDs

Client/Modules/WorkQueueGitLedger.fs:903-946 introduces two disjoint execution paths. The public execute/executeAt path passes inertFixture=false (1117-1130); every public PublishStage, regardless of ID or WorkKind, is rejected before Contracts.publishStage (938-940). Every public PostMessage whose subject is Role is rejected regardless of role-stage ID (941-943). Other source/admission/claim transitions require unavailable canonical proof and reject unconditionally (944-946). Public ProduceEvent is separately rejected for missing canonical source/index at 955-961 and 1078-1082. The only ordinary public PostMessage exception is Subject.Task with no work stage, which cannot publish RoleMessage work. C01 abort-only, stalled-stop/cancel and source-recovery marking are deliberately non-activating exceptions, not producer/admit/claim authorizations.

The old ID-prefix checks at 948-950 and 963-965 remain only behind the internal inertFixture=true branch; they do not defend the public route. Neither malicious IDs like legacy-review-1, old-integration-y, anything-else, nor a prefixed ID can cause unverified semantically derived work to enter the public project CAS. This addresses the exact previous namespace-only review blocker rather than just renaming a test.

## 3. Admission, claim, account and Scheduler cannot promote historical WQ02 records

WorkQueueGitLedger.fs:944-946 rejects public AdmitProject, DispatchOnce, InitializeAgent, Heartbeat, ClaimNext, ClaimAncillary, Work completion and graph/role transitions for lack of canonical origin/index and pinned role proof. This is a gate on mutation semantics, not on the stage ID; even an already-persisted nonprefixed WQ02 stage cannot be claimed through the public API. AccountAdmissionAdapter.fs:422-427 public admit returns TokenRetained before AccountCapacityLedger.reserve, so unverified demand does not consume a global slot. AccountAdmissionAdapter.fs:434-440 keeps only internal fixture admit/abort adapters. Server/WorkQueueContracts.fs:1628-1640 distinguishes the historical pure canDispatchPrototype from the production-facing canDispatch, which always returns false even with fabricated Migration.Activated and all coverage booleans.

## 4. Two-clone adversarial coverage and preserved inert compatibility

Client.Tests/WorkQueueProducerGitTests.fs:253-279 attempts public publication of renamed FrozenReview, Integration, Planning, RoleMessage and PostMessage(Role, roleWork) from two disposable cloned remotes with different versions/IDs, asserting rejection and no work, messages, or version drift. Lines 282-335 first seed an historical Frozen WQ02 project through the internal fixture API with renamed review/integration/planning/role-message records, then show stale clone publication loses CAS; both clones' public AdmitProject, DispatchOnce, InitializeAgent, ClaimNext and ClaimAncillary calls reject and leave state unchanged. Even fabricated activated migration and coverage cannot make canDispatch true. Lines 130-230 retain separate fabricated review/accepted integration/challenge/event/watermark and racing-producer rejection tests. Client.Tests/AccountAdmissionAdapterTests.fs:595-607 independently establishes public admit preserves zero account tokens and project state.

Client/Modules/WorkQueueGitLedger.fs:1132-1144 intentionally makes the accepted WQ02 fixture API internal and requires durable MigrationEpoch.Frozen on fixture transitions (934-937). Repo-wide Client/Server call-site grep finds internal fixture execution only in its module and AccountAdmissionAdapter's internal fixture adapters; production dispatch/adapter entry points do not use it. Existing WQ02/WQ07 multi-clone tests continue to run through these internal fixture methods and retain their original CAS semantics.

## 5. Independent .NET 11 Release execution

Exact clean worktree: worker4-wq04-semantic-fence at dc710850.
- dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~WorkQueue --verbosity minimal: **98 passed, 0 failed, 0 skipped**, net11.0, exit 0.
- dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter 'FullyQualifiedName~WorkQueue|FullyQualifiedName~AccountAdmission|FullyQualifiedName~SourceEffect|FullyQualifiedName~AgentDashboardCache|FullyQualifiedName~AgentView' --verbosity minimal: **118 passed, 0 failed, 0 skipped**, net11.0, exit 0, 58s test execution.
- Observed compiler warnings are outside changed WQ04 code (e.g., Server/Program.fs, Server.Tests/ExportStoreTests.fs, Client/Program.fs and Client.Tests/BrowserSchedulesTests.fs). No production branch, Scheduler or source handoff was invoked. The tests use disposable local Git fixtures.

## 6. Important bounded non-activation findings

The success of these negative tests is **not** proof of an authoritative task/review/challenge/message event resolver, same-snapshot high-water completeness, immutable historical role versions, WQ03 protected WIP/source effects, WQ05 accepted integration adapters, production Scheduler, source handoff, or live account token reservation. WorkQueueProducers.selectRoleDemand still refuses to mint launch authority absent the canonical role/source binding. The pure integration-policy successor tests do not constitute a verified production producer.

LedgerStore.transact at 2155-2161 can return AlreadyApplied for a byte-identical historical successful WQ02 fixture operation before rerunning the new mutation planner. This is immutable idempotent replay of an **already published** historical operation, not a new public producer transition or a fresh claim. A future activation adapter must distinguish historical fixture receipts from canonical authenticated producer authority and must not interpret such replays as fresh approval. It is not a blocker for the present fail-closed, non-activatable scope (public account admission and Scheduler remain disabled).

## Conclusion

**Accepted, bounded:** the exact prior semantic legacy-ID bypass is closed at public ingress, admission and claim regardless of renamed IDs, with two-clone negative evidence and WQ02 fixture compatibility preserved. This decision must not be cited as authorization to integrate an executable WQ04 Scheduler/producer, turn on live WQ03 source succession, reserve production tokens, deploy, migrate, or push main. Prior orphan operation da5088... is separate and has no Accepted disposition from this review.