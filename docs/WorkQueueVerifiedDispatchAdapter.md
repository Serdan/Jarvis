# WQ11 Work Scheduler: default-off verified-admission and fake original-dispatch adapter

**Status:** explicitly opt-in test-only client module `Client.WorkQueueVerifiedDispatchAdapter`. No production Scheduler, timer, browser transport/provider, account activation/migration epoch change, server database, manager session, or persistent Agent-ownership is installed. The client does not call this module on startup or through normal task transitions. `dispatchFakeOnly false` and `completeFakeOnly false` have no external effect.

## Concrete authority and execution boundary

`WorkQueueVerifiedDispatchAdapter.dispatchFakeOnly` accepts WQ08's **read-only** candidate and report, approved WQ07 account and WQ02 project Git bindings, a previously provisioned WQ03 trusted original journal, distinct stable operation UUIDs, and one injected in-process fake-provider callback. WQ08 `WouldLaunch` is advisory only. The adapter independently verifies WQ07 head and capacity; WQ02 project head/version, available Work generation, dependency-blocking evidence and coverage; current enabled WQ04 role/version/catalog/subscriptions; and WQ04 original source head, operation identity, request hash and immutable origin blob. If missing, changed, wrong-source, or unverifiable, it refuses **before** reserving a token.

This adapter remains **test-only** because WQ08 requires an Activated migration marker while the genuine disposable WQ07 authority initializes as LegacyActive. Tests inject a clearly synthetic in-memory Activated **ranking projection**, but `WorkQueuePracticalAdmission.admitExplicit true` reserves against the unchanged real WQ07 Git ledger and revalidates the WQ04 source/role inside the winning WQ02 Git CAS. This fake-only projection is not proof of production admission, activation or a live cross-project project-set reader.

The only runnable stage kind here is `WorkKind.Planning` with ordinary completion. Implementation, review, and integration require separate source/job/domain receipts; they are intentionally not dispatched or terminalized by this adapter.

The accepted sequence is:

1. Re-read the selected role/source/work and WQ07 snapshot from approved Git heads.
2. WQ07 account-global Git CAS reserves the one slot. WQ04 `admitExplicit true` binds the exact WQ02 work/role/source with immutable decision and role witness in the project CAS. A concurrent project is denied by cap=1.
3. Confirm the held WQ07 token and WQ02 reserved work credit at fresh independent heads. WQ03 must have an explicitly provisioned, continuity-checked host journal. **First win a create-only WQ02 Git pre-send claim** for this exact launch/original UUID, binding the independently verified WQ03 host-journal UUID, admission, role, Work and source hash. Only the newly confirmed CAS winner may commit the local original UUID/intent (including both Git heads and source reference/commit) and invoke the fake callback. A foreign host, prior claim, ambiguous CAS, or missing/mismatched local marker cannot send.
4. Validate an accepted fake reply's work/launch/session identity. Persist its immutable WQ03 receipt, then WQ02 `DispatchOnce` and `InitializeAgent` receipts. A restart after acceptance without the original in-memory session returns `AwaitFirstConnect` with a repair instruction rather than fabricating a session or resending. Unknown callbacks record WQ03 Unknown and WQ02 `SubmittedUnregistered(OutcomeUnknown)`, with WQ07 capacity held.
5. `completeFakeOnly true` is a separate explicit fixture operation: it requires the accepted WQ03 original, commits a WQ02 work claim and ordinary completion, verifies the immutable terminal witness and only then calls WQ07 `releaseTerminal` with project-proof verifier. Repeated terminal/release IDs are reconciled from the Git receipts without double-consuming capacity.

Every report carries `VerifiedSelection`, the exact source/admission/dispatch/first-connect/original IDs, observed account/project heads, role/version, Work, optional origin blob, and a repair action where needed. This is diagnostic evidence, not an executable bearer permission. All WQ02 post-admission execution uses `executeInertFixture` only, which is restricted to disposable Frozen envelopes and does not offer a production execution path.

### Cross-client original-send authority

The create-only Git record is `records/wq11-original-hosts/<launch-n>.json` on the already approved WQ02 project orphan ledger, written by `LedgerStore.putRecord` under the stable original operation ID. Its identity is checked against the immutable creating operation at the latest fetched head. The journal host UUID is enrolled and cross-checked against both WQ03 local anchor and SQLite identity; filesystem paths, actor labels and untrusted client assertions are **not** canonical host identities. No new server-owned authority or production Scheduler is introduced. Independently trusted hosts may both replay WQ07/WQ02 admission, but only one can win the original-send Git CAS; the loser fails closed before its WQ03 marker or provider callback.

The ordinary WQ03 journal still fences same-host sends and preserves original receipts/unknown outcomes. After a crash between winning the Git claim and writing the WQ03 original marker, an idempotent retry deliberately **does not** regain send permission: the claim is already applied but the local original is unproven, so retain capacity and request explicit repair. A new production host migration or cross-host takeover requires separately designed approval and crash continuity; copying a journal directory or trusting a new root is not an authorized send.

## Explicit outstanding authority gaps

- A WQ03 `DefinitiveNoEffect` result is durable proof of provider refusal, **not** a WQ02 pre-registration terminal witness. WQ02 currently exposes `TerminalizeNoClaim` only for a registered launch. The adapter therefore returns `RepairNeeded`, keeps the WQ07 token held, and does not forge a terminal, claim, release, or substitute original. Before production a separately reviewed pre-registration no-effect terminal transition with a verifiable immutable project witness is needed.
- WQ08 preview/planner reports `CanDispatch=false`. A complete authenticated real project-set reader, actual account activation approval, production Scheduler decision lease, live browser handoff/first-connect confirmation, and safe resumption of lost accepted sessions are **not connected**. No browser account is touched.
- No timer or recurring scheduler runs; only the test's caller decides whether to invoke `dispatchFakeOnly true` and `completeFakeOnly true`. No live service deployment, migration flip, source job, real browser send, or automation is implied.

## Reproducible Release verification

Use .NET 11 in the isolated `feat/wq-defaultoff-verified-dispatch-worker10-20261116` worktree:

```sh
dotnet build Client.Tests/Client.Tests.fsproj -c Release
dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore \
  --filter 'FullyQualifiedName~WorkQueueVerifiedDispatchAdapterTests' \
  --logger 'console;verbosity=normal'
dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore \
  --filter 'FullyQualifiedName~WorkQueueVerifiedDispatchAdapterTests|FullyQualifiedName~WorkQueueAcceptedAdaptersCapOneSmokeTests|FullyQualifiedName~OriginalBrowserJournalTests|FullyQualifiedName~WorkQueuePracticalAdmissionTests|FullyQualifiedName~AccountAdmissionAdapterTests|FullyQualifiedName~WorkQueueCrossProjectPlannerTests|FullyQualifiedName~WorkQueueSchedulerHostPreviewTests' \
  --logger 'console;verbosity=normal'
```

The new fixture creates separate temporary Git bare authorities for two unrelated projects and one global account cap=1, independent readers, WQ04 task/producer history, and a WQ03 continuity journal; all are removed afterward. It checks blocker-before-FIFO ranking, real WQ04/WQ07 CAS admission, callback one-shot and restarted re-read, exact first-connect/terminal/release, competing project exclusion, unknown no-resend, definitive refusal retention, and checkout cleanliness. No claim about a passing count should be made without running these exact compiled tests.
