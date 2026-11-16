# WQ11: accepted-adapter, fake-only two-project cap-one smoke

## Explicit scope

`WorkQueueAcceptedAdaptersCapOneSmokeTests.TwoRealProjectsOneGlobalCapPinnedAdmissionAndOneShotOriginal`
is a **test-only**, default-off, in-process fake-provider integration scenario.
It is not a live Scheduler, browser send, provider, agent session, host transport,
account migration, source job, or production activation. `CanDispatch` remains
false; the WQ08 report is read-only and never treated as an admission capability.
The test uses .NET 11 Release and creates/deletes all backing state itself.

This is separate from the earlier
`WorkQueueReleaseAcceptanceTests.CanonicalDemandLaunchesFakeProviderFirstConnectThenReleasesCapOne`,
which intentionally uses WQ07's inert fixture admission, a Git-only fake
original marker and one-project preview. That earlier result remains a valid
isolation regression but does **not** prove the accepted WQ03/WQ04/WQ08 bridges.

## Actual accepted adapter seams

- **WQ02/WQ04**: two unrelated Git project authorities each have two
  independently fetching clones. Each canonical planning stage comes from an
  original `ReviewWorkflow.createTask` UUID and
  `WorkQueueReceiptProducer.produce`, with WQ04 role catalog and immutable
  source-origin blob. A concrete downstream prerequisite in project B makes
  its candidate blocking. The test derives planner project, role, work,
  source, available-sequence and blocking evidence from verified Git snapshots,
  instead of fabricating a claim that the producer already succeeded.
- **WQ08**: `WorkQueueCrossProjectPlanner.planFakeOnly` receives explicit
  read-only snapshot ports for the complete two-project set and one WQ07
  account. Genuine WQ07 initialization yields `MigrationPhase=LegacyActive`,
  whereas WQ08 requires `Activated`. The test **first asserts that the
  unmodified real account is `NeedsAttention` with
  `AccountNotActivated`**. Only for the isolated fake-only ranking branch,
  an injected in-memory projection copies the actual Git head, capacity and
  original tokens but substitutes the hypothetical `Activated` marker. It
  never writes migration state or supplies an executable authorization.
  On that clearly synthetic planner input, WQ08 chooses the blocking
  candidate across projects and refuses stale role version and duplicate
  browser operation UUID. This is **not** proof of end-to-end production
  account activation; the caller still makes one separate explicit WQ04
  admission request with pinned work/origin against the real WQ07 ledger.
- **WQ07/WQ04**: `WorkQueuePracticalAdmission.admitExplicit true` reserves one
  independent account-global token and writes the exact WQ02 project credit
  plus WQ04 role/source witness at the winning project CAS. Exact replay is
  idempotent; the other project's valid demand cannot obtain a second token.
  Neither this test nor production registers an automatic admission loop.
- **WQ03**: `OriginalBrowserJournal` is explicitly provisioned in a separate
  host continuity directory. A durable pre-send one-shot marker is read after
  reopening the store, before the fake provider callback. Only the callback's
  matching launch/work/session result reaches WQ01 `InitializeAgent` via
  existing WQ02 adapters. Replaying the original ID does not call the provider
  again or register another agent. A conflicting launch UUID is rejected.
- **Terminal/recovery**: WQ02 claim and completion create the genuine
  project-terminal proof. Forged proof cannot release global capacity; the
  original WQ07 release succeeds and replay is idempotent. That permits the
  other project's demand to obtain the one cap token. Its fake provider
  returns no result: WQ03 records `Unknown`, WQ02 holds
  `SubmittedUnregistered(OutcomeUnknown)`, and the global token remains held.
  WQ08 reports the original unknown as an attention boundary; no new send,
  initialization, or release is inferred.

A missing unprovisioned WQ03 journal must fail closed. An admitted launch
without an original journal is not runnable. The fake-only planner reports
`Unknown` when evidence is missing/uncertain, and `NeedsAttention` for
contradictory pinned role or original UUID identity. Those are deliberately
distinct, non-executable decisions.

## Verification

From isolated `test/wq11-accepted-adapters-capone-worker7` worktree:

```sh
dotnet restore Client.Tests/Client.Tests.fsproj
dotnet build Client.Tests/Client.Tests.fsproj -c Release --no-restore
dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore \
  --filter 'FullyQualifiedName~TwoRealProjectsOneGlobalCapPinnedAdmissionAndOneShotOriginal' \
  --logger 'console;verbosity=normal'
```

Also rerun adjacent WQ11, WQ03 original journal, WQ04 practical admission,
WQ08 cross-project planner, WQ07 account adapter, and role/source regression
selections. Record actual pass counts and exact source SHA in the frozen review,
not estimated or earlier unrelated test results.

The test checks the six disposable source worktrees are clean and deletes
its bare remotes, clones, SQLite original journal and continuity anchor.
