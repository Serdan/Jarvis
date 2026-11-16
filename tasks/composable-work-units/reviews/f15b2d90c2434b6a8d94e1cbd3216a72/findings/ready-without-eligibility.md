# [P2] Work-unit `Runnable` ignores canonical scheduler eligibility

**Location:** `Server/WorkComposition.fs:368-405` (`isQueueVerified`, `phase`).

The projection returns `WorkUnitPhase.Runnable` whenever any linked stage has `WorkState.Available` and the four queue coverage flags are set. The authoritative WQ selector `Contracts.orderAvailable` additionally requires `GateState.Clear`, satisfied/waived hard-stage prerequisites, and valid dependency coverage. The launch path in `Server/WorkQueueSchedulerInert.fs:259-273` also rejects project/account migration states other than `Activated`. An initialized frozen WQ ledger with an `Available` stage and complete coverage can therefore display the linked Feature as `Runnable`, although the launch boundary rejects it. The projection also reports `Runnable` for an `Available` stage held by a non-clear gate, even though `orderAvailable` excludes it.

This is a correctness error in the promised "what is ready now?" query, not merely a missing future adapter: a caller has no indication that the claimed readiness is not actionable. It also creates a dangerous contract for later reactive wake wiring.

**Suggested correction:** Reuse the canonical WQ selection/coverage predicate to derive stage readiness and check the project migration gate. If account-global admission evidence is unavailable, distinguish "stage available/ready for consideration" from "launch eligible" rather than asserting unconditional `Runnable`. Add negative-space tests for `Available` + non-clear gate, incomplete hard-stage graph, and frozen migration. This does not require wiring the production scheduler into this first slice.

**Evidence:** Release tests for this commit passed (Server composition 11/11; client ledger 2/2); none exercise these queue admission negative cases. This finding is based on the exact frozen diff and its existing WQ eligibility contracts.
