# Reviewer 2 — Changes requested

**Frozen reviewed source:** `4a35fd3849bf2f46b70e9aa4b04571c4b7032205`  
**Task:** `composable-work-units`  
**Review:** `f339da67c89d44bcbd59e4e68710cceb`

The submitted fix improves the earlier stale-parent-acceptance replay boundary. `WorkCompositionAcceptanceAdapter.reviewPurpose` now binds the effective direct-child and prerequisite acceptance closure; the existing managed-review/integration receipts are validated inside the composition Git CAS, and no operator/scheduler activation was added. Focused Release client composition tests passed **3/3**.

**Blocking P1:** `findings/cancelled-child-acceptance-resurrection.md`. The underlying `WorkComposition.currentAcceptance` model records cancelled children only as `-1`. A parent accepted with a cancelled child becomes stale when the child is reopened, but the *same historical parent acceptance becomes current again* when that child is cancelled a second time—even if its spec version and cancellation reason changed. The new review-purpose hashing cannot prevent this silent resurrection because no acceptance operation is invoked. Record and validate a cancellation revision/identity in parent proof snapshots; test reopen -> re-cancel and revisions of cancelled children.

**Decision:** Request changes. Existing tests passing does not resolve this evidence-boundary violation. All observations refer to the frozen reviewed commit, not the contributor's mutable branch.
