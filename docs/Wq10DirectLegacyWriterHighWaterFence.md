# WQ10 direct legacy writer/high-water fence (default OFF; cutover NO-GO)

Task: `work-wq10-direct-writer-highwater-fence`. Source: `Client/Modules/LegacyDirectWriterFence.fs`, complementing the independently integrated SignalR boundary in `LegacyWriterProtocolFence.fs`. This is **not** a freeze signer, production activation, retirement, Scheduler or migration implementation.

## Concrete original writer inventory

- **Legacy review/Agent/task/integration state**: 46 `ReviewWorkflow` direct `LedgerStore.transact` call sites, including Agent registration and lifecycle/assignment, task/review claim/decision, older integration submit/complete, state and notification/implicit activity. They now invoke `LegacyDirectWriterFence.transact`. Three earlier immutable source-Git-ref publications (one integration and two branch-review create/replace paths) additionally call `preflightSourcePublication` *before* publishing the ref; otherwise a frozen old writer could orphan source refs despite denial of the later ledger CAS.
- **Role and individual agent instructions**: four `RoleInstructions` mutations (create/update role, attach/detach role, agent instruction update via shared editRole); guarded at actual transaction publication.
- **Challenges / side-effectful reads**: `ChallengeWorkflow.changed` funnels all challenge transition writes plus the implicit operator-index bootstrap through the same guard.
- **Canonical Work-native writes are NOT legacy writes**: WQ02 `WorkQueueGitLedger`, WQ04 composition/binders, WQ05 adapters, source/credential/wake state, Work-native operator conversations, reservation permissions, role-authority, and WQ10 project migration prepare continue using `LedgerStore.transact` without any new veto. This avoids accepting an actor/role/kind string as permission or blocking necessary WQ02 and audited repair writes.
- **Not yet directly protected**: a library caller that intentionally invokes raw `LedgerStore.transact` against a legacy path, older binaries, independently scripted Git pushes, pre-existing browser/Manager schedules and any unseen direct host entrypoints. `AfterAgentIdle` remains separately inert without trusted durable activity proof, not authenticated retirement. The existing default-off SignalR command fence is not a host-wide fleet fence.

## Exact guard / original high-water observation

The existing `JARVIS_WQ10_LEGACY_WRITER_FENCE_BINDING` host-provisioned, permissions-checked file selects the approved original account and project Git authorities and expected frozen heads. The default when unset is the exact prior behavior. An enabled malformed binding fails closed. A direct caller cannot substitute a project ID, actor/Manager label, client-protocol claim, operation type or recovery flag to waive the guard.

For each guarded direct write: fresh fetch the original project Git snapshot, require matching project authority and fresh original account Git identity; allow original account `LegacyActive` for backward compatibility even if disposable WQ02 Work state says Frozen; deny Frozen/Preparing/Validated/Activated with matching pinned original account/project heads and a verified project migration epoch; treat Aborted, stale head, unverified project state and wrong identity as unknown/deny. The outer check also rejects a same-UUID replay that `LedgerStore.transact` would otherwise return without calling the planner. **Inside** `LedgerStore.transact` the check runs on the actual winning original project Git head on every optimistic retry, including after a concurrent project freeze/CAS advance.

`LegacyDirectWriterFence.inspectHighWater` is **read-only evidence**: it observes verified account/project Git heads and the original project Git operation sequence. It explicitly returns **Unknown** for the all-client original pending-intent journal, original source-effect journal and minimum connected/disconnected client protocol. That sequence is **not** an attested original client-journal high-water. A local `PendingIntentJournal` path is not a global, fleet-inclusive authority; an operator-provided report hash or simulated empty journal cannot replace the missing witness.

There are two independent Git authorities. Checking the account epoch and then publishing a project CAS is *not* an atomic account-plus-project transaction. An account-only freeze could win after the final account read and before an already-admitted project push. Reliable production exclusion additionally requires trusted old-client fleet fencing, reconciled original journal/source high-water and a verified freeze/Preparing protocol with operator authorization. None are claimed by this change. A missing/disconnected older client can still bypass an opt-in gate. **Production cutover remains NO-GO.**

## Local .NET 11 Release verification

The extended `LegacyWriterProtocolFenceTests` disposable tests use two real original account clones and two real original project clones. They cover disabled/LegacyActive compatibility, Frozen direct Agent/role/challenge denial with no original Git ref movement, preserved historical reads, same-UUID replay, stale account/project heads, wrong project authority, explicit unknown journal evidence, and a losing two-clone project CAS whose stale old-writer planner must not execute after a synthetic account freeze; also no orphan source review Git ref publication while Frozen. All account migration markers in these fixtures are test-only synthetic edits, not production signer evidence.

```sh
dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~LegacyWriterProtocolFenceTests
dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~RoleInstructionsTests
dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~ChallengeWorkflowTests
dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~ReviewWorkflowTests
dotnet test Server.Tests/Server.Tests.fsproj -c Release
```

No hosted GitHub Actions, Codex, production activation, deployment, scheduler start or cutover.
