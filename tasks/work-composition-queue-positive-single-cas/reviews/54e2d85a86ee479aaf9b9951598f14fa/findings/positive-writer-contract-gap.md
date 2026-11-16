# P1 positive composition/WQ02 CAS remains NO-GO (documented contract gap)

**Scope:** frozen commit `3458224ce3bff9141b05a7891148098db4900053`, reviewed independently by Reviewer 2. **Classification:** release-blocking gap in the wider Agent-to-Work migration; not an undisclosed regression in this diagnostic-only patch.

## Verified evidence

1. Original WQ04 task-stage origin lacks approved role/source lineage: `Client/Modules/WorkQueueCanonicalCasBinder.fs:214-300` uses `NoRoleTransition`, records blank `SourceRef` and `SourceCommit` for `CreatedTask`, and emits empty `RoleId`/zero `RoleVersion`. `Client/Modules/WorkQueueGitLedger.fs:965-988` explicitly requires those role fields to remain empty in the original producer CAS. The binder's `originGaps` requires a positive role version and source. Filling them after publication or fabricating identity is not proof.
2. `Client/Modules/WorkCompositionGitLedger.fs:114-149` accepts reviewed graph transitions through its own original `LedgerStore.transact` and changes only `records/work-composition/graph.json`. `Client/Modules/WorkQueueGitLedger.fs:965-1046` separately publishes `records/work-queue/project.json` and origin. There is no shared validated graph+queue mutation with one original receipt and one Git CAS.
3. `Server/WorkQueueContracts.fs:363-377,538-554` considers WQ02 stage status/gate and WQ02 hard-stage dependencies, but not the composition graph's cross-System/Feature hard prerequisite closure. Thus a verified same-head graph-Blocked / WQ02-Available contradiction is possible. A one-off atomic acceptance is insufficient without atomic revocation on later Reopen/Cancel/Revise/edge changes.
4. `Client/Modules/WorkCompositionQueueBinder.fs` deliberately emits `SameHeadAtomicWriterUnavailable`; `attemptUnderCas` always refuses inside the original transaction and produces no new receipt. The new `OriginalWq04ProducerCannotSatisfySameHeadRoleAndSourceLineageContract` test uses an actual original `CreateTask` receipt and `WorkQueueReceiptProducer.produce`, independent clones and genuine graph prerequisite; it witnesses role/source gaps, contradictory readiness and no-effect refusal.

## Independent execution

- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~WorkCompositionQueueBinderTests`: **5/5 passed**.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~WorkComposition|FullyQualifiedName~WorkQueueProducerGitTests'`: **16/16 passed**.
- **Positive same-transaction accepted WQ02 eligibility tests: 0**. Neither run shows a positive writer exists.

## Disposition

**Accept the accuracy and fail-closed safety of this NO-GO diagnostic only**, because the task explicitly allows an honest missing-contract report if the shared atomic writer is not implementable under present interfaces. **Do not interpret this verdict as positive WQ02/composition acceptance, end-to-end Work migration GO, Scheduler approval, or task completion of the requested writer.**

Follow-on work must bind original WQ04 Work ID/generation and approved source/role and current graph/spec/dependency closure in one trusted original project snapshot, expose internal typed graph+WQ02 planners under **one** project Git CAS and original UUID receipt, apply matching invalidation/hold logic to all graph and queue mutators/readers, then independently prove two-clone positive/negative scenarios with WQ07/WQ03/WQ11 safeguards. Scheduler remains disabled.