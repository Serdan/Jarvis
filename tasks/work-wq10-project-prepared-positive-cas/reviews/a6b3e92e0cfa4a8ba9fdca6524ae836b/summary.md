# Independent review — Accepted (private disposable WQ10 project Prepared CAS only)

**Review:** `a6b3e92e0cfa4a8ba9fdca6524ae836b`
**Task:** `work-wq10-project-prepared-positive-cas`
**Frozen source:** `65fc67ae20c412432dfb1beb8bb86c026448344b`
**Base/parent:** `04b90e0cc43f8939f2c0722ea03d085f3244925b`
**Reviewer:** Reviewer 2
**Verdict:** **Accepted** for the explicitly non-operational, assembly-internal, private disposable fixture boundary. **No production Prepared/cutover GO.**

## Exact frozen scope
`git diff --name-only 04b90e0..65fc67a` gives exactly:
- `Client/Modules/WorkQueueProjectMigrationPrepare.fs` (new)
- `Client.Tests/WorkQueueProjectMigrationPrepareTests.fs` (new)
- `docs/Wq10ProjectPreparedPositiveFixture.md` (new)
- `Client/Client.fsproj` and `Client.Tests/Client.Tests.fsproj` (compile includes only)

No Worker12 WQ04, Worker8 permissions or Worker1 launch implementation source was changed. Independent review worktree remained clean and `git rev-parse HEAD` matched the exact frozen SHA.

## Source evidence
1. **Original WQ02 project Git CAS:** `WorkQueueProjectMigrationPrepare.fs:324-402,409-441` pins the exact Frozen project head, account head/state version/ledger identities, queue and graph blob preimages, and absent Prepared receipt, then gives `LedgerStore.transact` three changes: `records/work-composition/graph.json`, `records/work-queue/project.json`, `records/work-queue/project-prepared.json`. `LedgerStore.fs:2142-2251` verifies the request hash/operation replay before a fresh plan, validates all expected blob OIDs, and publishes all changed files, `ledger.json`, and the original immutable operation receipt together in one commit/push CAS. There is no second independent staging authority.
2. **Old-to-Work identity/coverage:** `WorkQueueProjectMigrationPrepare.fs:171-260,342-382` reads original `tasks/*`, `agents/*`, and `records/roles/*` history at the pinned head, preserves their Git blob OIDs in `LegacyPaths`, checks fixture original UUID/request hashes against `LedgerStore.getOperationAt`, and binds every discovered canonical task to an existing or new composition unit and queue stage in `Conversion`, with task blob OID and task-subtree digest. Duplicate/cancelled task links and ambiguous queue stage matches fail closed.
3. **Non-waking Prepared:** `WorkQueueProjectMigrationPrepare.fs:263-321` requires Frozen and all four WQ02 coverage flags, no launches, no Available/Claimed or Clear-gated blocked work, and creates only `Blocked` stages with non-Clear `IntegrationGateHold`, `IntegrationAuthorized=false` and `Migration=Preparing`. The receipt records `RuntimeAuthorized=false`. `Server/WorkQueueContracts.fs:363-380` excludes those stages from `orderAvailable`.
4. **Replay/races/history:** `WorkQueueProjectMigrationPrepareTests.fs:142-283` checks the exact three changed domain paths, historical Agent blob equality, preserved source branch SHA, inert graph/queue, exact same-original-UUID replay across a second clone, changed-intent UUID rejection, distinct stale competing UUID rejection, independent clone head advancement and simultaneous two-clone one-winner CAS. The original `ops/*` records and review/task/source paths are not targeted for deletion or rewriting.
5. **Partial account recovery:** `WorkQueueProjectMigrationPrepare.fs:443-486` exposes diagnostic-only `NotPrepared`, `ProjectOnly`, `AccountHeadDiverged` and `RecoveryBlocked`. The test advances the separate account Git authority after project Prepared and requires `AccountHeadDiverged`, never a rollback or activation.
6. **Public no-write boundary:** `WorkQueueProjectMigrationPrepare.fs:404-419` makes the public `attempt` unconditionally return a typed blocked `InvalidBinding`. Only `internal attemptOnDisposableFixture` can invoke the writer, behind temporary-root/fixture marker and Git metadata containment. No caller-supplied report SHA, actor, role, journal high-water or claimed fence can make the public path write.

## Independently executed verification
- `dotnet --version`: **11.0.100**.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~WorkQueueProjectMigrationPrepareTests --logger "console;verbosity=normal"`: **5/5 PASS**, zero failures. Explicit NUnit output identifies all five positive/negative cases.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --filter FullyQualifiedName~WorkQueue --logger "console;verbosity=minimal"`: **234/234 PASS**, zero failures or skips, 4m36s. Original WQ02 and adjacent WQ10 client regressions included by filter.
- `git diff --check 04b90e0..65fc67a`: exit 0; `git status --porcelain`: empty.

## Hard NO-GO boundary / future requirements
This review **does not** certify production migration. The witness in `records/wq10-fixture/original-journal.json` is deliberately synthetic and checks only fixture-selected original operation hashes, not independently reconciled full original browser/source/Agent/Manager journal and source-effect high-water. The Frozen account phase is fixture-generated, not operational old-writer fence proof. This is **not** a production trust registry, source/ref/role admission or operator approval. The diagnostic `inspect` does not independently reverify every historical blob and operation origin on future reads; its `ProjectOnly` is not an activation authority. Account and project authorities cannot commit atomically; cross-ledger repair remains operator-controlled and separate.

Before any operational conversion: independently trusted host/operator signer provisioning; verified full historical lineage, source and review/WIP refs and original journal high-water; continuous authenticated old-writer/Manager/connected-client fence; real account/project partial-recovery protocol; compatible clients; and separate Scheduler/cutover approval are still required. No Scheduler/Manager activation, public project/account Prepared, hosted GitHub Actions, Codex, or live cutover was performed.

**Decision: ACCEPTED only within the explicitly bounded private disposable fixture implementation and its fail-closed public path.**