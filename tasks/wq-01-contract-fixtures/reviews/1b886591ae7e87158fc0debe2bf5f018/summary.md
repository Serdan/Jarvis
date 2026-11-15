# Independent review — WQ-01 Phase A contracts

**Reviewer:** Reviewer 2
**Frozen review:** `1b886591ae7e87158fc0debe2bf5f018`
**Frozen source:** `1b886591ae7e87158fc0debe2bf5f01858d17b7d`
**Accepted base:** `90ed868d04524d0483ccd979cffb5029d8cf7027`
**Verdict:** **ChangesRequested** — four P1 semantic contract defects. Do not integrate this frozen source as the approved WQ-01 contract reference.

I independently inspected the five-file +1,793-line diff, the full new `Server/WorkQueueContracts.fs` contract model, tests and phase-A handoff document, and compared the implicated transitions to accepted `docs/specs/work-queues-and-ephemeral-executions.md`. The worktree HEAD equals the frozen review SHA. This is isolated, side-effect-free Phase A work, not a production Scheduler/ledger/browser implementation; later-phase caveats recorded by the author remain appropriate.

## Independent validation
- .NET 11 Release targeted `ServerWorkQueueContractTests`: **35 passed, 0 failed**.
- .NET 11 Release full `Server.Tests`: **170 passed, 0 failed**.
- No source changes, merges, deploys or schedule activation by this reviewer.

## Required corrections
1. **P1** `findings/01-blocked-wake.md` — normal Blocked stage never wakes after prerequisite completion; no fresh FIFO requeue.
2. **P1** `findings/02-demand-invalidation.md` — hard dependency edits leave unclaimed launch credits tied to blocked work; no invalidation/rematch/terminal fence.
3. **P1** `findings/03-operation-payload-replay.md` — several original-operation receipts omit semantically material payload fields, silently treating changed requests as exact replays.
4. **P1** `findings/04-source-handoff-provenance.md` — source-safe gate resolution accepts unrelated WIP provenance and fails to carry forward the verified continuation ref/SHA.

Each finding includes a reproducible state sequence, cited exact file/line range, affected invariant, and focused negative-space tests. Re-review the **new exact commit** after implementation and test corrections. Passing the existing 35 fixtures is not sufficient acceptance; the author correctly does not claim all 80 architecture cases are production-tested.
