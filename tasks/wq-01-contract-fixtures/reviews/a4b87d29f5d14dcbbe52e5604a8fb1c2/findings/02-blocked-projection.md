# P2 — unrelated dependency projection silently wakes explicitly Blocked work

**Frozen source:** `143a73a35336305afaab7d796f16f70782982709`  
**Files:** `Server/WorkQueueContracts.fs:495-516, 522-527, 529-571, 926-953`; `Server.Tests/WorkQueueContractTests.fs:415-436`.  
**Severity:** P2 — Phase A state model admits an explicit non-ready `Blocked` stage and later silently makes it runnable on an unrelated mutation. Depending on later-phase producer semantics, this can dispatch work before its actual block has been cleared.

## Concrete reachable sequence

1. `publishStage` (or `postMessage`) accepts a stage with `State=Blocked`, `Gate=Clear`, and `Dependencies=Set.empty`. `normalizePublishedStage` only converts **Available→Blocked** for unsatisfied edges; it preserves this input.
2. Perform `setHardPrerequisite(A, B)` on a different, claimed work stage A. That operation invokes `projectDependenciesChanged`.
3. `wakeBlockedStages` scans **all** Blocked work, not just stages whose dependency satisfaction changed. B has no edges, so `Set.forall` returns true, and B becomes Available with a fresh sequence even though no event resolved B's explicit Blocked reason.

The existing `case_62_63_safe_dependency_gate_resolution_blocks_or_requeues_once` creates exactly such a B (`{ implementation "B" 1L [] with State=WorkState.Blocked }`) and immediately calls `setHardPrerequisite` on A, but never asserts B's status after the transition; hence it does not detect this behavior. A generic completion, cancellation or waiver of unrelated stages can produce the same promotion.

This is an internal model ambiguity even if Phase A intends `Blocked` to mean **only** unmet hard dependencies: accepting a Blocked stage without unmet edges but letting it transition solely because an unrelated event occurs is not a coherent invariant. The accepted design also allows blocking on operator/recovery dependencies that later phases must represent explicitly rather than infer from an empty hard-edge set.

## Required correction

Define and enforce an invariant at publication and projection: either (a) `Blocked` is **exclusively** hard-dependency blocked, so reject/normalize inconsistent published states in the same canonical CAS, or (b) retain an explicit typed block reason/authority and wake only the stages whose blocking dependency disposition has actually become satisfied. Do not allow a projection triggered by an unrelated event to clear an independent block. Ensure legitimate **initially Blocked, then prerequisite-completed** work still wakes exactly once.

Add negative-space tests for a Blocked empty-edge stage, an unrelated dependency edit/completion, and a same-stage dependency transition; test both `publishStage` and Role-message publication if the latter permits a Blocked generation. This is independent of the P1 timestamp-order defect in `findings/01-fifo-wake-time.md`.

## Evidence boundary

This is a code-path finding in the new pure contract; no production Scheduler is running these functions. It is not proof of an observed live unsafe dispatch.