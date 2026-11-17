# P1 — Cancelled ancestor does not suppress descendant eligibility

**Frozen source:** `2d1b1a07c44cd420eb2a418fcc2895343ebc72c1`
**Affected:** `Server/WorkBlocking.fs:42-56, 89-94, 122-145`; `Server/WorkComposition.fs:348-354`
**Task acceptance:** cancelled descendants excluded; blocked/unready candidates never dispatchable; stale graph state fails closed.

## Finding
`WorkCompositionMutation.Cancel(parent)` marks only the parent's `CancellationReason`; child nodes remain otherwise unchanged. `WorkBlocking.unfinished` checks only the **candidate node's own** cancellation, and `unitMayRun` checks its own unfinished state and hard prerequisites, but never a cancelled ancestor.

Construct a valid graph `Project(root) -> System(B) -> Feature(C)` with C linked to canonical task `task-C`; cancel B, leaving C uncancelled. Supply an otherwise complete queue snapshot containing an `Available`, clear-gate stage with `SubjectId=task-C`. `Contracts.orderAvailable` includes the stage; `unitMayRun graph C` is true (C is unfinished, no hard prerequisites); `WorkBlocking.project` therefore returns C in `Eligible`, despite its enclosing System being cancelled. The test `optionalAndCancelledDescendantsDoNotInheritBlockerRank` covers **direct** child cancellation, not cancelled ancestors.

The projector also checks the graph and queue's expected versions independently, without an enforced common graph/queue binding, so the caller cannot rely on `project` itself to filter a stale Available descendant after ancestor cancellation. An eligible projection must never present execution from cancelled/out-of-scope containment as runnable.

## Required correction
Define and implement the cancelled-ancestor eligibility rule in this pure projector (or return a typed stale/inconsistent-state rejection for an Available stage under a cancelled ancestor). Ensure blocking propagation likewise does not elevate cancelled subtrees. Add a direct negative fixture with cancelled System B, uncancelled child C and Available C stage; include an accepted positive after explicit permitted reopen/reconciliation. Preserve separate optional-child semantics, actual dependency blockers and FIFO ranking.