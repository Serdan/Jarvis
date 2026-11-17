# Linux Client.Release gap: exact-source diagnostic evidence (2026-11-17)

Task: `client-release-acceptance-gap-20261117`. Actor: Worker 10. This is a **diagnosis-only** artifact; there is no source fix, release certification, waiver, deploy, restart, cutover or integration authorization.

## Pinned provenance

| Ref | SHA | Meaning |
|---|---|---|
| local main observed | `ffd2bb3a153cf14e15ed2105e12e8179a540f94a` | Current local checkout and diagnosis branch base |
| `origin/main` observed | `aa2e46ff2927b8e9fd0af25204377c299f2ec576` | Clean accepted comparison baseline |
| AGENTS held isolated merge | `47236e98fba28c5d7d8a3e228eb5cc192fff5875` | `worker10-clientrelease-agents-20261117` |
| global dashboard held merge | `74dbeb87052a5108e6d788a9588e019aabb7d6be` | `integrator-global-dashboard-20261117` |

Exact `git diff --name-status aa2e46f..47236e9 -- Client Client.Tests Server Server.Tests` changes only Server and Server.Tests files: no changes to any Client or Client.Tests path. The dashboard merge does change `Client/Modules/{AgentDashboard,AgentView,ConsoleTui}.fs`, `Client/Program.fs`, and `Client.Tests/{AgentDashboardCache,AgentView,ReviewWorkflow}Tests.fs`, among server files. Exact `aa2e46f..ffd2bb3a` drift comprises only `browser-scripts/chatgpt/launchWorkAgent.js` added and `docs/design/work-original-git-client-publisher-p1c-plan-2026-11-17.md` deleted; there is no Client, Client.Tests, Server or Server.Tests drift.

## Six hermetic .NET 11 Release partitions on held AGENTS merge

These 98 unique fixture-file name filters partition the 98 `Client.Tests/*Tests.fs` source files. Each invocation exited **0**, printed `Test Run Successful`, counted executed tests, and deleted its disposable environment. No single partition was presented as a full unfiltered PASS.

| Partition | Matching fixture names | Executed / passed | Reported test time, min | Jarvis job |
|---|---:|---:|---:|---|
| 0/6 | 17 | 207/207 | 1.4517 | `d6609352f9bb47aa81e9eefaca06a648` |
| 1/6 | 16 | 137/137 | 2.6279 | `6051241189634f228d9d489032adbfb8` |
| 2/6 | 16 | 176/176 | 2.6935 | `6ef0a1c00c7f47fb90e51a894e09d9eb` |
| 3/6 | 17 | 243/243 | 3.1809 | `6f16c2df0fec4436af60c75001f02c1b` |
| 4/6 | 17 | 203/203 | 2.6640 | `33917439e89b4eb7b8ab3dd2263d09de` |
| 5/6 | 15 | 265/265 | 3.4268 | `7085256910f94a568979077f429fbcb1` |

**Total: 1,231 passing partitioned test executions; sum of reported partition test times 16.0448 minutes.** This aggregate is not the walltime of one unfiltered test process. It suggests that a 240-second whole-suite deadline is not a defensible full-run acceptance budget at observed performance. Partitioning can hide cross-fixture order and shared-state interactions.

All six groups used this pattern, with independently disposable runner and Git identities, and a `--filter` of the exact names below:

```text
env HOME=<disposable>/home XDG_CONFIG_HOME=<disposable>/config
    XDG_DATA_HOME=<disposable>/data XDG_CACHE_HOME=<disposable>/cache
    XDG_STATE_HOME=<disposable>/state DOTNET_CLI_HOME=<disposable>/dotnet
    NUGET_PACKAGES=/tmp/jarvis-worker10-clientrelease-baseline-20261117/packages
    DOTNET_CLI_TELEMETRY_OPTOUT=1 GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_NOSYSTEM=1
    dotnet fsi scripts/test-client-hermetic.fsx -- --timeout-seconds 240 --no-build
    --filter 'FullyQualifiedName~FixtureA|FullyQualifiedName~FixtureB|...'
```

`scripts/test-client-hermetic.fsx` further constructs a private temporary HOME/XDG/runtime/temp/Git test identity for the child `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --blame-hang-timeout 75s`, drains output, enforces bounded termination and removes scratch paths. Test commands were run from the exact pinned QA worktree.

### Exact filters

- **Group 0:** `HermeticHarnessTests|FilterSelectorTests|AgentInstructionsTests|ClientShellTests|WorktreeTests|WorkQueueIntegrationSourceAuthorityTests|TaskFirstUiProjectionTests|SourceEffectJournalTests|LegacyWriterProtocolFenceTests|WorkQueueCanonicalSourceEvidenceTests|WorkQueueCrossProjectPlannerTests|WorkQueueWorkReplacementVerticalSliceTests|WorkQueueNativeLifecycleCasTests|WorkStatefulProjectionTests|TerminalLayoutTests|SelfUpdateTests|ActivityPresentationTests` (prefix each term `FullyQualifiedName~`).
- **Group 1:** `WorkspaceDefaultsTests|SignalRDispatchTests|ClipboardTests|BoundedProcessTests|LedgerStoreTests|WorkCompositionLedgerTests|TaskFirstUiSnapshotAdapterTests|SourceEffectRouteBridgeTests|WorkQueueProjectMigrationPrepareTests|WorkQueueCanonicalCasBinderTests|WorkQueueReactiveWakeTests|WorkQueueCrossProjectHostPreviewTests|WorkQueueTwoProjectEphemeralSchedulerVerticalTests|WorkStatefulTransportTests|TerminalFrameTests|ImportNetworkTests` (prefix each term `FullyQualifiedName~`).
- **Group 2:** `ClientConfigTests|ActivityFilterTests|ClientActivityTests|JobViewTests|WorkQueueGitLedgerTests|WorkCompositionQueueBinderTests|WorkQueueOperatorConversationsTests|SourceEffectHostBindingTests|WorkQueueMultiLedgerPreparedRecoveryTests|WorkQueuePracticalAdmissionTests|WorkQueueSchedulerHostLoopTests|WorkQueueDurableLaunchIntentTests|WorkingTurnGuidanceTests|WorkQueueDashboardReadBindingTests|AtomicTextFilesTests|PermissionPolicyTests` (prefix each term `FullyQualifiedName~`).
- **Group 3:** `WorkQueueCanonicalMigrationPrepareTests|ActivityLogTests|ActivityExportTests|WorkQueuePersistentAgentRetirementTests|WorkDashboardViewTests|WorkQueueCredentialTests|AccountAdmissionAdapterTests|WorkCompositionQueueTransactionTests|ProjectBrowserTests|JobManagerTests|BrowserBridgeTests|WorkQueueReactiveWakeGitTests|WorkQueueReleaseAcceptanceTests|WorkQueueClaimBoundDirtySourcePolicyTests|WorkQueueProducerGitTests|PathSafetyTests|WorkQueueReviewIntegrationAdapterTests` (prefix each term `FullyQualifiedName~`).
- **Group 4:** `AgentDashboardCacheTests|AgentCommandInfoTests|WorkQueueSchedulerHostPreviewTests|AccountCapacityLedgerTests|AgentViewTests|WorkQueueSchedulerHeadBindingTests|ReconnectScheduleTests|ActivityNavigationTests|WorkQueueVerifiedDispatchAdapterTests|WorkQueueReservationPermissionsTests|WorkQueueToolFacadeTests|BoundedReadsTests|WorkQueueDashboardHostTests|OriginalBrowserJournalTests|WorkQueueAcceptedAdaptersCapOneSmokeTests|ChallengeWorkflowTests|GitCommandTests` (prefix each term `FullyQualifiedName~`).
- **Group 5:** `ReviewWorkflowTests|ProjectColorAssignmentsTests|RoleInstructionsTests|OrchestrationApiTests|OAuthTests|TerminalMouseTests|WorkQueueNativeCredentialDispatchAdapterTests|WorkQueueConversationTransportTests|WorkQueueRoleAuthorityTests|WorkQueueNativePrivateHostBridgeTests|SkillTests|ResponseDeliveryTests|ProjectCreationTests|HashAndPatchTests|BrowserSchedulesTests` (prefix each term `FullyQualifiedName~`).

## Dashboard exact-head focused regression

Job `8c1b4c0f99464cfd99591e0d590814b9`: at merge `74dbeb87052a5108e6d788a9588e019aabb7d6be`, hermetic Release full build and focused filters `AgentDashboardCacheTests|AgentViewTests|ReviewWorkflowTests|WorkDashboardViewTests|ReconnectScheduleTests|AgentInstructionsTests|ClientConfigTests`: **258/258 passed, exit 0**, 2.4653 minutes. These include source-visible negative-path tests for same-named-but-distinct ledger identities, conflicting alias remotes, offline staleness, remote last-seen provenance, parked agents, and machine filtering. This is scoped coverage, not full dashboard or server release certification.

## Unfiltered comparator and release gate

Historical clean-baseline full unfiltered job `6e52bc6cb77d4c1c91893beb26d27107`: hermetic .NET 11 Release exited **124** after the strict 240-second whole-suite timeout. Captured stdout continued listing passing tests when killed. No full pass count or successful completion exists from that run; activity-persistence warnings are not established as causes.

Two fresh **unfiltered** comparators used already-built Release test assemblies with `--no-build`, separate disposable HOME/XDG/Git profiles and the same 240-second whole-suite cap:

- Clean baseline `aa2e46ff2927b8e9fd0af25204377c299f2ec576`, job `51d2c79448de4d609f45618c9bc15a62`: **exit 124 (timeout, NOT PASS)**. NUnit output was still reporting passing tests at kill, last visible `ImplicitActivationIsReportedEvenWhenSourceReadFails`. No full pass count was reported.
- Held AGENTS merge `47236e98fba28c5d7d8a3e228eb5cc192fff5875`, job `3b104d752f634d0dbb3ca0317491ef09`: **exit 124 (timeout, NOT PASS)**. NUnit output was still reporting passing tests at kill, last visible `EntityReadersRejectUnknownPhaseAndMissingSchema`. No full pass count was reported.

These separately reproduced bounded timeouts on baseline and held merge at slightly different test progress points. Combined with 1,231 green isolated partitioned executions and unchanged Client/Client.Tests source on the AGENTS merge, the evidence **supports an aggregate-duration cap problem, not a demonstrated AGENTS-specific regression or a proven single stuck fixture**. It does not rule out test-order interference or other failures in a full uninterrupted run. The 75-second per-test blame-hang gate remained in effect for each run. No arbitrary timeout extension or code changes were made.

**HOLD** both isolated merges for Client.Release eligibility. The task permits either an exact final-head completed full unfiltered PASS, or a separately reasoned Lead exception meeting all scoped baseline-isolation, negative-path and immutable evidence gates. No timeout, partitioned result, or independent code review by itself authorizes release, default-off integration, restart, production Scheduler or SQLite cutover.

## Follow-up diagnostic route

Both bounded comparators completed with exit 124. Do not invent a stalled fixture. Request a non-overlapping harness/source lease only if an actual test-only correction is required. Otherwise run an independently recorded full unfiltered Release suite with an explicit budget justified by the measured 16.0448-minute partition sum, while retaining bounded per-test blame-hang attribution and hermetic state; capture completed summary and exit. Re-run or reconcile relevant regression coverage on the exact final integrated head, distinct from these older isolated heads. Lead owns any narrowly scoped exception or eligibility decision; Integrator exclusively owns integration.
