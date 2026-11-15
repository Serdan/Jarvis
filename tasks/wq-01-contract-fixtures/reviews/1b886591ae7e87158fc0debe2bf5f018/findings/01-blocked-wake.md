# P1 — Blocked work has no dependency-satisfaction wake transition

**Frozen SHA:** `1b886591ae7e87158fc0debe2bf5f01858d17b7d`  
**Source:** `Server/WorkQueueContracts.fs:265-282, 789-815, 660-692, 897-921`; tests `Server.Tests/WorkQueueContractTests.fs:125-138, 381-396`.  
**Architecture:** `docs/specs/work-queues-and-ephemeral-executions.md` §5/§7: `Blocked --dependency satisfied--> Available` with fresh FIFO availability.

**Reproduction:** Publish an Available work item A and an unsatisfied prerequisite B. Add hard edge A→B while A is Available. `setHardPrerequisite` changes A.State to `Blocked`. When B is completed, `completeOrdinary` changes B only. No contract transition recomputes A.State; `orderAvailable` hard-requires State=Available, so even though all A's prerequisites are now satisfied A is omitted forever. The only `Blocked/Available` branch in `resolveDependencyGate` requires that A is *still Claimed*, and therefore does not apply to this ordinary Blocked stage. Publishing a stage initially Blocked has the same missing wake path.

**Impact:** A required review, integration or implementation can remain permanently stranded after its prerequisite succeeds; blocker-first FIFO and downstream progress become incorrect. Existing tests manually replace prerequisite states or exercise the claimed-gate path, not a normal Blocked→Available wake transition.

**Required:** Model an atomic dependency-satisfaction (and cancellation/waiver) projection/wake transition, revalidate graph/index coverage, assign new server-witnessed availability time and monotonically increasing sequence exactly once, and make replay idempotent. Add two-client stale-CAS tests for prerequisite completion vs wake, including already-Blocked work and cycles.
