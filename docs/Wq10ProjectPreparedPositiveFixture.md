# WQ10 project Prepared — positive disposable-only Git CAS

This slice adds a positive, assembly-internal, private test writer in Client/Modules/WorkQueueProjectMigrationPrepare.fs. It does not replace existing WQ10 default-off adapters. No production Prepared, Scheduler, Manager, or Agent retirement is enabled.

## Same-Git-CAS project conversion

The original project LedgerStore.transact publishes in one remote Git CAS:
- records/work-queue/project-prepared.json: immutable original-operation UUID, exact Frozen project Git head, account/project ledger identities and epochs, account head/version, original journal and request-hash/high-water, historical path/blob-OID coverage, task-to-composition/Work mapping, and RuntimeAuthorized=false.
- records/work-composition/graph.json: existing graph preserved with task-linked feature units, collision checked.
- records/work-queue/project.json: original queue state preserved with new non-waking Work stages Blocked under IntegrationGateHold and migration Preparing (not dispatchable). An immutable original operation receipt is published by LedgerStore in the same tree.

Existing Agent/task/attempt/review/integration/source/WIP history is not modified or deleted. Historical Git blobs remain readable; their paths and object IDs are included in bounded receipts. Exact original UUID replay is immutable and idempotent; mismatched UUID requests are rejected; stale project head is rejected rather than overwritten or force-pushed. Source refs are not changed.

## Separate account recovery and real authorization

The account and project authorities are different Git histories and cannot commit atomically. Read-only inspect distinguishes NotPrepared, ProjectOnly, AccountHeadDiverged, and RecoveryBlocked. An account advance after project-only preparation is not rolled back or represented as operational GO.

The private disposable fixture proves Git transaction mechanics only. Its locally generated original-journal hash/high-water record and simulated Frozen account are NOT independently verified production history, old-writer fencing, trust anchors or operator approval. The exact marker under a private OS temp directory and recursive Git metadata checks restrict the fixture's local sinks; they are not a production sandbox against malicious same-user code.

Public attempt unconditionally returns a typed no-write InvalidBinding. A real operational writer still requires host-pinned trust registry, original source/browser/Agent/Manager journal and source-effect high-water verification, a continuous old-writer fence, full source/review/role lineage coverage, externally authorized project/account repair, compatible clients, separate activation approval, and deployment review. No production migration or Scheduler cutover is authorized.

Worker12's WQ04 composition writer, Worker8's permissions modules and Worker1's credential launch binding are untouched. No hosted CI, Codex or live migration.

## Local verification

    dotnet build Client/Client.fsproj -c Release
    dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~WorkQueueProjectMigrationPrepareTests

The tests use private two-clone Git authorities and prove one project CAS, inert conversion and preserved history, UUID replay and conflicting request rejection, stale-head races, unverified fixture journal failure, public default-off and account/project partial recovery.
