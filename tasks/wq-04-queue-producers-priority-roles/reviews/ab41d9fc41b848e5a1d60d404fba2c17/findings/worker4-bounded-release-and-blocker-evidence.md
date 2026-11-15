# Worker4 frozen source and bounded Release evidence (owner-submitted)

Exact source: `76ca7864002157cde6901a562ea64dba8306e68c`, branch `refs/heads/fix/wq04-canonical-samecas-worker4`, baseline current main `e837d2d67277af8eb4b634ff58d5ba9e1b59d19e`. Task `wq-04-queue-producers-priority-roles`, new frozen task-bound review `ab41d9fc41b848e5a1d60d404fba2c17`. This is implementation-owner evidence, **not** an independent review verdict.

The branch cherry-picks accepted Worker10 canonical source reader `0ba335368d6f621f186fbc0e062833da8e0b8625` (local `b445ffb6`) and adds a typed audit-only binder, adversarial tests and blocker documentation; Worker11 `1cd0b88d` is explicitly **excluded** due independent ChangesRequested seq3808. Main WQ04 ID-independent `PublishStage`, `PostMessage`, `AdmitProject`, `ClaimNext` and `ProduceEvent` holds are **unchanged**. No production route calls the new binder. Its result algebra offers only `RecoveryBlocked`, and its `LedgerStore.transact` planner returns `IncompleteCoverage / RecoveryBlocked` without a write.

## Test evidence (local .NET 11.0.100, Release)

- `dotnet build Client/Client.fsproj -c Release`: **passed**, 1 preexisting IDisposable warning.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~WorkQueueCanonicalCasBinderTests -v quiet`: **4/4 passed**, two-clone forged UUID/hash/event, stale high-water/role and no-write race included.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --filter 'FullyQualifiedName~WorkQueueCanonicalSourceEvidenceTests|FullyQualifiedName~WorkQueueCanonicalCasBinderTests' -v quiet`: **11/11 passed**, 0 failed, 0 skipped.
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release -v quiet`: **233/233 passed**, 0 failed.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore -v quiet`: **INCONCLUSIVE: bounded 240-second timeout** (ExitCode=-1, TimedOut=true, no completed test count or verdict). Never label this a pass. Concurrent other worktrees were running .NET tests; no attribution of timeout to binder or unrelated tests is proven.
- `git diff --check`: passed; isolated worktree clean after exact commit.

## Negative authority boundary

Worker10 `CanonicalEventEvidence.Coverage` is *only* `IncompleteCoverage`: original canonical operation receipt and entity are verified, but not original mutating-host job/push journal, immutable remote source lineage/protection, complete event/index high-water and same-CAS project effects. Worker11's frozen role proof is rejected due retroactive v1 authority, unproven append-only history, and claim-next race binding. A current role value, caller-selected ID or matching counter is not authority. A safe positive source producer requires jointly authenticated canonical-source/host evidence, historical role and account token authority, and one writer CAS (or separately fail-closed handoff). This branch deliberately has no positive route, Scheduler/source activation, migration, deployment, GitHub Actions or main push.

See `docs/work-queue-wq04-cas-binder-blockers.md` for exact missing invariants and source boundary design. Independent reviewer should check no synthetic positive path was introduced and decide Accepted or ChangesRequested on **this exact SHA**.