# WQ11 — Work-to-Scheduler vertical-slice acceptance (default off)

Source branch: `test/wq11-integrated-vertical-slice-worker1`, created from
`origin/main` at `53105d3eaa1b476fadfe5470e962b4c5796fe8f9`.
The only intended new source is
`Client.Tests/WorkQueueWorkReplacementVerticalSliceTests.fs` plus its
`Client.Tests.fsproj` compile include and this runbook. Do not deploy,
turn on the Scheduler, change an epoch, send a real browser/provider request,
or use hosted GitHub Actions.

## What the fixture actually proves

The fixture uses **two disposable, independently identified Git project
authorities**, each with a pair of approved clones, and an independently
identified **account authority with cap 1**. All account and project ledger
operations use pinned remote Git state. No persistent Agent is created as a
Work owner; a fixture registration of role `Lead` only enables the legacy
canonical task-to-Work receipt producer. The source worktrees and temporary
remotes are checked clean and deleted.

The new test intentionally spans authority boundaries rather than adding
another WQ03/WQ04/WQ07 fake-provider test:

- WQ02 producer publishes canonical, source-originated Work in both projects.
  WQ04 pinned role catalog and the WQ09/Work-native read view observe the
  project-B Work at the same verified Git head.
- A durable Project → System → Feature composition is written through
  `WorkCompositionGitLedger` with a cross-system hard prerequisite.
  A second client re-reads the parent graph and proves that the dependent
  unit is **Blocked**, even while the separate WQ02 stage reads
  **Available**. This is an **unbound composition-to-queue gap**, not permission
  to schedule the stage.
- WQ08 global host preview re-reads both project heads and the account cap,
  verifies role authority and explicitly detects the missing immutable
  source ref/commit required for global selection. WQ04 independently ranks
  B as blocking-first, but WQ08 correctly reports no global winner. A
  second host replay returns the same no-go without adding an account token.
- Work-native ClaimNext is rejected with `RejectedMissingBinding` and
  cannot mutate the Git head. A caller-supplied `Accept` evidence string is
  rejected by `WorkCompositionGitLedger` rather than being promoted to
  independently verified review/integration evidence.
- The production reactive wake defaults to disabled. An *injected*,
  opt-in-only fixture wake against the actual `LegacyActive` account remains
  guarded; source/capacity and dependency events alone cannot mint an
  executable Work launch or browser request.

The existing `WorkQueueAcceptedAdaptersCapOneSmokeTests` covers the
complementary WQ02/WQ03/WQ04/WQ07 fake-only path, including one-shot journal,
duplicate-clone CAS, provider-first-connect fake receipt, and terminal-release
successor. It does **not** make the composed Work graph authoritative for
admission, and no tests should suggest it does.

## Missing bindings — explicit go/no-go

| Binding | Verified today | Decision |
| --- | --- | --- |
| Composition Feature acceptance to Work source/queue | Parent graph and WQ02 ledger separately pinned; `Accept` requires an internal same-head canonical verifier not wired to Work stage publication | **NO-GO** |
| Work-native List/Get to pinned WQ09 view | Head/graph/role is read-only, claim disabled | **GO for observation only** |
| Work-native ClaimNext/Checkpoint/Complete | `MissingClaimBinding` / `MissingTerminalBinding`: same-CAS Work, account credit, ephemeral launch, session and terminal evidence absent | **NO-GO** |
| Global WQ08 account/project comparison | Two approved authorities, cap 1, pinned roles; task-origin lacks immutable source ref/commit, so `SourceLineageUnknown` prevents a global winner | **GO for fail-closed read-only inspection only** |
| Cross-project executable Scheduler intent | No durable pre-send intent binding from global candidate to original WQ04/WQ07 admission and WQ03 one-shot journal; migration remains `LegacyActive` | **NO-GO** |
| Reactive event to admitted Work | Re-read/recheck seam is available; no installed live feed/timer or composed-unit acceptance trigger | **NO-GO for live scheduling** |
| WQ03 original browser/provider & WQ07 terminal | Separate approved fake-only original/replay/release tests exist; no live provider-first-connect, external effect reconciliation, or cross-host production adapter proof | **NO-GO for actual launch** |
| Final Work/Agent migration | No verified Windows disposable account/profile, no cutover authorization or epochs, no end-to-end production bindings above | **NO-GO** |

**Required contract before flipping any flag:** A canonical, reviewed adapter
must verify `(projectId, unitId, unitSpecVersion, graphVersion,
review/integrationReceiptOid, sameHeadWorkOriginOid, stageGeneration)` at
one Git CAS snapshot, reject stale/contradictory lineage, and atomically
publish the corresponding eligible Work transition. The later Scheduler
intent must preserve original, durable
`(accountHead, projectHead, roleVersion, workId, workGeneration,
sourceOperationId, sourceRequestHash, launchUuid, originalBrowserUuid)`
through WQ04/WQ07 reservation, WQ03 exactly-once journal, provider
connection, immutable terminal and cap release. An incomplete or uncertain
effect must halt reconciliation, not trigger a fresh UUID or a second send.
Do not mistake `WouldLaunch`, `Available`, or `WorkUnitPhase.Accepted`
in isolation for global launch permission.

## Local verification (Linux, .NET 11, no hosted CI)

Run only in the isolated branch, using cached packages when offline:

```sh
dotnet restore Client.Tests/Client.Tests.fsproj --ignore-failed-sources
dotnet build Client.Tests/Client.Tests.fsproj -c Release --no-restore
dotnet vstest Client.Tests/bin/Release/net11.0/Client.Tests.dll \
  --TestCaseFilter:'FullyQualifiedName~WorkQueueWorkReplacementVerticalSliceTests'
dotnet vstest Client.Tests/bin/Release/net11.0/Client.Tests.dll \
  --TestCaseFilter:'FullyQualifiedName~WorkQueueAcceptedAdaptersCapOneSmokeTests|FullyQualifiedName~WorkCompositionLedgerTests|FullyQualifiedName~WorkQueueToolFacadeTests|FullyQualifiedName~WorkQueueReactiveWakeGitTests'
dotnet vstest Client.Tests/bin/Release/net11.0/Client.Tests.dll \
  --TestCaseFilter:'FullyQualifiedName~WorkQueueCrossProjectHostPreviewTests|FullyQualifiedName~WorkQueueCrossProjectPlannerTests|FullyQualifiedName~WorkQueuePracticalAdmissionTests'
```

**Verification record (2026-11-16, Linux, .NET SDK 11.0.100, Release):**
restore succeeded; build succeeded with 0 errors and 3 existing unrelated
warnings. Executed NUnit/VSTest groups: new vertical slice **1/1**,
composition/Work facade/reactive wake/accepted adapters **12/12**, and
cross-project host/planner/practical admission **38/38**; total **51/51**
passed, 0 skipped. Each fixture deletes its scratch remotes/checkouts; no
provider, browser or migration action was performed. The first bare
`dotnet test --no-restore` emitted no test count and is *not* counted as
evidence. No Kruso acceptance is asserted without a separately approved
disposable Windows OS profile.

## Review/integration boundary

Request a **new task-bound frozen** independent review for this exact source
commit after the build and focused suites pass. A reviewer must inspect the
test boundaries and all negative-space claims, especially false readiness.
Only Lead may integrate an accepted commit. This document is not migration
approval and cannot authorize deployment or cutover.
