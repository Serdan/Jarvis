# Independent frozen E0 SQLite Scheduler correction re-review — Accepted

- **Review:** `6f7c5a88e2744f7db431a083d954270b`
- **Frozen source:** `571217c8202d47c6371cff826447ec7b994fa66d` on `feat/work-sqlite-scheduler-capacity-worker1-20261117`
- **Compared with prior ChangesRequested:** `d5b6c74798bc4d308fb4b4867c2b8620`, frozen `a8e8639b16596c2cbcbe93ee21e94f88496ff8af`
- **Reviewed base:** `0e9a8d4ea958090c94af11952329565358fdd79b`

## P1 correction verification

1. **Claim source/generation provenance:** `withCredit` persists stage-generation and source SHA when specialist credits are reallocated; claim atomically binds `StageGeneration`, `SourceSha`, and `ClaimGeneration` to the actual chosen primary stage. `launchValid` checks the persisted binding against the projection and stage row. The new two-item/different-SHA/different-generation reranking regression exercises the formerly failing transition.
2. **Unsettled specialist credit:** `matchExisting` now requires each candidate stage record to be settled, matching reservation and claim eligibility. New regressions check both `Unknown` and `Prepared`, preserving capacity and refusing an incompatible new reservation.
3. **Fresh persisted replay:** `ReadReceipt` exposes an immutable applied operation receipt; launch records bind the original reservation or claim operation ID and work/generation. `replayOutcome` runs before current capacity/eligibility/EverClaimed checks, returns the bound original outcome after SQLite reopen, and fails closed on reused operation IDs with changed launch identity. New cap-one reservation and already-claimed launch regressions cover fresh SQLite snapshots, replay sequence, and negative cases.

## Independent validation

- `git diff a8e8639..571217c`: exactly `Server/WorkSchedulerSqlite.fs`, `Server/WorkSqliteAuthority.fs`, and `Server.Tests/WorkSchedulerSqliteTests.fs` changed.
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore` on clean worktree at exact frozen `571217c8202d47c6371cff826447ec7b994fa66d`: **359 passed, 0 failed, 0 skipped** (.NET 11).
- No outstanding blocker found in the three remediation paths.

**Boundary:** Accept **E0 default-off scheduler adapter corrections only**. No E1 importer contract, live scheduler/host registration, source authorization cutover, Actions, Codex, deploy/restart or production activation is reviewed or approved here. Later lifecycle deletion of launch result rows will require its own replay/reconciliation contract in E1; no such deletion is part of this E0 scope.
