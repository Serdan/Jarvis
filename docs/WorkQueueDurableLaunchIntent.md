# WQ12: durable source-origin planning launch intent (default-off)

This bridge persists **one planned original identity** for a WQ04 `CreateTask` planning Work generation in the original project Git ledger. It does not enable a production Scheduler, browser provider, or model call. It is intentionally separate from the read-only WQ08 selection projection, WQ07 account reservation, WQ04 project role/source admission, and WQ03 browser journal.

## Authority and sequence

1. The operator supplies the **complete approved account/project set**, bound to independent Git remotes, and explicitly invokes `WorkQueueDurableLaunchIntent.prepareExplicit true` with an audit actor. Disabled is the default. No injected test-only source strings confer authority.
2. WQ08 reads all project heads, roles, WQ04 canonical origins, blocking-first FIFO demand, existing launches and the account cap from independently fetched Git snapshots. The projected winner is **not** a launch candidate.
3. For **planning** Work only, the binder verifies exact Work ID/generation, current enabled role/version, role catalog OID, project head/version, original `CreateTask` operation UUID/request hash/sequence, original Git commit, immutable work-origin blob OID and historical source entity SHA. A planning CreateTask may have no source branch/commit. For implementation/source-writing Work the exception is never available: approved immutable ref/commit and isolated generation-scoped worktree remain mandatory.
4. A create-only **project Git-CAS** record at `records/work-queue/launch-intents/<project-work-generation-hash>.json` holds the immutable creating-operation UUID, launch, WQ03 original, admission, abort, release, dispatch, and first-connect UUIDs, along with pinned account/project/source/role evidence. The planner rechecks the project and account heads inside the winning CAS. Neither local process state nor server storage owns the IDs.
5. A second client must recover the existing canonical record using `readPrepared`; the reader verifies its original `WQ12.PreparePlanningLaunch` operation and historical Git blob, rejecting any subsequently replaced record. A losing client adopts only the winning identical original or fails closed. An absent ACK is not evidence of failure. Never mint a replacement original while the earlier one may exist.
6. Explicit `admitPreparedExplicit true` first requires the complete approved project set. For a **first** admission, it re-evaluates blocking-first cross-project FIFO, the unchanged account head, account capacity, current role and exact WQ04 source; for an existing launch it permits only exact original-UUID replay. It then invokes the existing default-off WQ07/WQ04 global-token/project-credit admission. The prepared record alone holds **zero** account credits and grants **zero** sends. Real browser/model dispatch is not connected; fake-only acceptance must continue through the already-fenced WQ11 adapter and WQ03 original journal.

## Recovery / cannot proceed

Treat missing or contradictory project membership, account or project head conflict, absent original source operation/entity, changed Work generation, disabled/mismatched role, missing work-origin blob, unresolved composition acceptance, source-writing without approved ref+commit, held or uncertain cap, foreign canonical intent, or an uncertain original browser outcome as **NeedsAttention / Unknown**. Preserve the original operation and both Git heads; inspect verified original receipts. Do not automatically release any held account token after an uncertain Git write, journal receipt, or missing ACK.

**Unproven two-ledger transaction:** A project Git-CAS prepare and account Git-CAS reserve are separate authorities. There is no atomic cross-remote transaction binding both updates at once. This module deliberately does **not** declare a prepared intent to be an admitted launch, and does not invoke WQ03 sends. A later safe scheduler handoff must revalidate the original role/source and winning cross-project selection at its admission boundary, preserve the original UUIDs, and reconcile any unknown WQ07/project outcome. Preparation is intentionally still default-off.

## Disposable regression evidence

`Client.Tests/WorkQueueDurableLaunchIntentTests.fs` exercises two independent project Git ledgers with two clients per project and a separate two-client account ledger (cap 1). It tests original task lineage with absent source branch, stable replay, concurrent project CAS, immutability after tampering, one canonical admission token, held-cap denial, composition-linked and incomplete-set rejection. All Git remotes and checkouts are disposable local temporary directories; no live browser, provider, migration or deployment is invoked.

Run with .NET 11 Release:

```sh
dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter "FullyQualifiedName~WorkQueueDurableLaunchIntentTests|FullyQualifiedName~WorkQueueCrossProjectHostPreviewTests|FullyQualifiedName~WorkQueueWorkReplacementVerticalSliceTests|FullyQualifiedName~WorkQueueVerifiedDispatchAdapterTests"
```

A passing local fixture is not a production cutover approval. Source commit, review verdict and Lead integration must be recorded independently.
