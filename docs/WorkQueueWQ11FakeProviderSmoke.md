# WQ11: disposable fake-provider first-connect and terminal Git smoke

## Boundary

This is an **explicit test-only, default-off integration smoke**, in
`Client.Tests/WorkQueueReleaseAcceptanceTests.fs` under
`CanonicalDemandLaunchesFakeProviderFirstConnectThenReleasesCapOne`.
It adds no production Scheduler, timer, browser account, host-provider
registration, migration, epoch activation, or live project/account mutation.
The production read-only Scheduler preview continues to report
`CanDispatch=false`.

The test constructs a disposable local bare Git remote and two independently
fetching clones using `withTwoClones`, plus an independent WQ07 account authority
with a global cap of one. The account and project identities, all original
operation UUIDs, and fake session/registration IDs are generated for the test.
The fixture deletes its temporary remote and clones after execution and
checks both source working trees are clean.

## Actual integrated adapter path

1. **Canonical demand**: invoke `ReviewWorkflow.createTask` with a stable
   original UUID; invoke the accepted `WorkQueueReceiptProducer.preview/produce`
   to create actual WQ04 planning work and immutable source-origin evidence,
   not a fabricated successful `PublishStage`. Configure an enabled version-1
   WQ04 `Lead` role whose planning subscription matches WQ02's existing
   role subscription. The WQ08 `WorkQueueSchedulerHostPreview.preview`
   independently reads the second clone and selects that same work ID,
   role version, source operation UUID/hash, project head, origin blob and
   free account capacity.
2. **Global-first, project-second**: explicitly call
   `AccountAdmissionAdapter.admitInertFixture`, the existing WQ07/WQ02
   adapter used for strictly isolated tests. Read the project from the
   opposite clone and confirm its launch credit points at that specific
   WQ04-selected work. Independently fetch the account token; its launch,
   project-ledger authority, role and version, and original admission UUID
   must match. Cap=1 denies a simultaneous second fake admission.
3. **Original send + first connection**: write a stable, replayable
   `LedgerStore.transact` marker *before* invoking the default-off fake-provider
   callback. The callback consumes the selected launch UUID and WQ04 work ID,
   returning those same identities plus a fresh ephemeral session key only
   for the first non-replay invocation. `connectIfReturned` checks both
   returned identities and routes that result into WQ01 `InitializeAgent`;
   without a provider result there is no call to `InitializeAgent`.
   `DispatchOnce` and `InitializeAgent` still use existing WQ02 Git fixture
   adapters and WQ01 transitions. Original marker replay skips the callback;
   replaying the dispatch/first-connect UUIDs remains idempotent.
4. **Claim, terminal, release**: perform `ClaimNext` and `CompleteOrdinary`
   through WQ02's real Git adapters; verify the completed stage and immutable
   WQ02 project-terminal record from an independent clone. Use the accepted
   `AccountAdmissionAdapter.releaseTerminal` with its current-project proof
   verifier to release the original global token, replay its release UUID,
   and observe cap=1 available again from the opposite account clone.
5. **Unknown original**: the later original invokes the fake provider once
   with a missing response; the test explicitly verifies the absent result
   cannot reach `InitializeAgent`. Its durable marker remains readable from
   the other clone with `OutcomeUnknown` WQ02 dispatch state; replay cannot
   invoke the callback or initialize another agent. Its global token stays
   held for manual reconciliation and is **not** silently released.

## Validation

Run from the isolated worktree using .NET SDK 11 and Release configuration:

```sh
dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore \
  --filter 'FullyQualifiedName~CanonicalDemandLaunchesFakeProviderFirstConnectThenReleasesCapOne' \
  --logger 'console;verbosity=normal'
```

For surrounding WQ11 regressions, also run the existing acceptance, canonical
CAS binder, Scheduler preview and WQ07 account adapter tests. Record the
exact commands and observed pass/fail counts in the frozen review.

Predecessor review on 2026-11-16 independently observed **41 passed, 0
failed, 0 skipped** for the adjacent Release selection above, but found its
provider counter disconnected from the first-connect result. That result is
historical and is **not** acceptance evidence for the corrected callback.
Fresh correction verification on 2026-11-16 on .NET 11 Release in the
isolated `fix/wq11-fake-provider-handoff-worker7` worktree:

- Focused test with `-c Release --no-restore --filter
  'FullyQualifiedName~CanonicalDemandLaunchesFakeProviderFirstConnectThenReleasesCapOne'`:
  **1 passed, 0 failed, 0 skipped**, exit code 0.
- Adjacent test selection with `-c Release --no-build --no-restore --filter
  'FullyQualifiedName~WorkQueueReleaseAcceptanceTests|FullyQualifiedName~AccountAdmissionAdapterTests|FullyQualifiedName~WorkQueueSchedulerHostPreviewTests|FullyQualifiedName~WorkQueueCanonicalCasBinderTests'`:
  **43 passed, 0 failed, 0 skipped**, exit code 0.

Both runs exercised the corrected source. The test also checks its two
disposable Git working trees are clean before fixture cleanup.

## What this does **not** prove

- **Not production dispatch.** `AccountAdmissionAdapter.admit` remains
  fail-closed; the test calls `admitInertFixture` explicitly. The required
  production same-CAS WQ04 role/source/credit proof is not implemented here.
  In particular, the legacy WQ02 fixture admission cannot write the
  WQ04 role-admission witness *atomically* with its project CAS. A later WQ08
  preview therefore reports `ProjectUnknown` for the historical launch,
  even after the terminal has freed global capacity and a later canonical
  WQ04 task is Available. The acceptance test checks this **visible
  fail-closed boundary**; it does not misrepresent the second fixture probe
  as WQ04-authorized runnable demand. This is a missing production adapter.
- **Not a real browser send or original WQ03 journal.** The test's deliberately
  small fake original marker is a local Git operation, not the live browser
  extension's send, confirmation, recovery, or WQ03 one-shot host journal.
- **Not a real external first connection.** The callback's in-process fake
  result feeds the WQ01 `InitializeAgent` state transition, not a genuine
  browser, transport, interactive agent, or remote account launch.
- **Not the cross-project Scheduler.** WQ08 is read-only one-project demand
  selection. Complete cross-project priority, admission, timers, and cutover
  remain unavailable; production `CanDispatch` is intentionally false.
- **No speculative fencing.** Unknown originals expose the exact original
  marker and dispatch state for agent reconciliation, rather than hiding
  evidence or assuming a retry is safe.
