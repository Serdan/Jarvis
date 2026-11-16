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
3. **Original send + first connection**: create a stable, replayable original
   fake-provider marker as a real `LedgerStore.transact` operation *before*
   invoking the in-process fake provider. Save the original launch/work
   identity, WQ04 source UUID/hash and marker UUID. The provider has no
   external effects and yields only an ephemeral test session key. Invoke
   `WorkQueueGitLedger.executeInertFixture` for `DispatchOnce` and
   `InitializeAgent`; these execute the existing WQ01 transition contracts.
   Replaying the exact original marker, dispatch and first-connect UUIDs from
   the other clone must not cause a second fake send or registration.
4. **Claim, terminal, release**: perform `ClaimNext` and `CompleteOrdinary`
   through WQ02's real Git adapters; verify the completed stage and immutable
   WQ02 project-terminal record from an independent clone. Use the accepted
   `AccountAdmissionAdapter.releaseTerminal` with its current-project proof
   verifier to release the original global token, replay its release UUID,
   and observe cap=1 available again from the opposite account clone.
5. **Unknown original**: test a distinct later original fake send whose
   response is lost. Its durable marker remains readable from the other clone
   with `OutcomeUnknown` WQ02 dispatch state; replay must not invoke the fake
   provider again. Its global token stays held for manual reconciliation and
   is **not** silently released.

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

Observed on 2026-11-16 using .NET 11 and Release configuration: **41 passed,
0 failed, 0 skipped** for
`FullyQualifiedName~WorkQueueReleaseAcceptanceTests|FullyQualifiedName~AccountAdmissionAdapterTests|FullyQualifiedName~WorkQueueSchedulerHostPreviewTests|FullyQualifiedName~WorkQueueCanonicalCasBinderTests`.
The focused `CanonicalDemandLaunchesFakeProviderFirstConnectThenReleasesCapOne`
also passed before the final helper was tightened; the final 41-test pass
includes the tightened durable-marker-based fake-send-once behavior.

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
- **Not a real first connection.** Only a fake in-process provider supplies
  a disposable session key to WQ01 `InitializeAgent`; no genuine browser,
  transport, interactive agent, or remote account was launched.
- **Not the cross-project Scheduler.** WQ08 is read-only one-project demand
  selection. Complete cross-project priority, admission, timers, and cutover
  remain unavailable; production `CanDispatch` is intentionally false.
- **No speculative fencing.** Unknown originals expose the exact original
  marker and dispatch state for agent reconciliation, rather than hiding
  evidence or assuming a retry is safe.
