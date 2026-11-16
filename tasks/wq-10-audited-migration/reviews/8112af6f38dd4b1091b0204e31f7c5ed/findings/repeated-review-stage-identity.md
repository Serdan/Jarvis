# P1 — Repeated distinct review stages still falsely block WQ-10 preflight

**Frozen source:** `18d333d41b7646ef90b16b4056855798300b60bd`
**Location:** `tools/Wq10MigrationPreview/Program.cs:299-335`, especially `identities` tuple and `DUPLICATE_CANONICAL_STAGE`.

The correction properly handles implementation predecessor/successor generations and implementation->review kinds. However it defines "duplicate" as *same kind + same stage generation + same source lineage*. That combination is not a unique logical stage identity in the authoritative queue: `Server/WorkQueueContracts.fs:575-601` allows distinct frozen review work IDs without requiring a source-writer lineage or globally task-unique stage generation; its collision check is explicitly limited to concurrent unfinished Implementation stages for the same source writer. An ordinary task can therefore have a completed frozen review, then a separate new frozen review with distinct work ID and each at its own generation 1, no source lineage, after a correction/re-review.

**Independent reproduction:** use the shipped `PreviewTests` two-clone fixture against the freshly rebuilt frozen Release CLI, `with_queue(covered=True, with_origin=True)`; populate `state.works` with:
- `review-first`: `subjectId=T1`, `kind=FrozenReview`, `stageGeneration=1`, `state=Completed`, no sourceLineage;
- `review-second`: `subjectId=T1`, `kind=FrozenReview`, `stageGeneration=1`, `state=Available`, no sourceLineage.

All three operator evidence inventories are explicitly empty, external evidence booleans true, account/project phases Frozen, existing legacy source attempt Finished, four queue coverage flags true, and the origin high-water record present. **Actual output:** `readiness=NO_GO` with sole blocker `DUPLICATE_CANONICAL_STAGE: Demo/T1: review-first and review-second share FrozenReview generation 1 and source lineage`. This is wrong for distinct legitimate review stages.

The frozen correction's **15/15 tests pass**, but none cover two legitimate review stages with the same kind/generation and absent lineage. The tests' true-duplicate fixture instead models two Implementation stages with same source lineage, which does not justify treating reviews the same way.

**Minimal fix:** distinguish stages by their canonical immutable work IDs and relevant actual producer/review identity. Treat separate review stages (and other non-source-writer kinds) as distinct history even when their own generation numbers happen to match. Restrict implementation source-writer duplicate checks to collisions that truly conflict under the canonical contract, or report ambiguity without turning unsupported assumptions into `NO_GO`. Add a single positive regression for completed review + successor review, alongside the existing genuine conflict negative fixture. This is an offline preview; no production ledger mutation or new global proof infrastructure is requested.
