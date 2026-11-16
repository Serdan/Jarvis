# Reviewer 2 — WQ10 successive-stage identity re-review: ChangesRequested

**Frozen source:** `18d333d41b7646ef90b16b4056855798300b60bd`, base `289e32754b6e2bd59b157d51c3fd79a16e881e98`.

## What is corrected

The previous `199baf8b` false `MULTIPLE_CANONICAL_STAGES` blocker on completed implementation + successor implementation or completed implementation + frozen review has been fixed. Existing stages are now preserved in the JSON and Markdown preview with immutable work IDs, kind, generation, state, and source lineage. The earlier omitted-evidence and account/project migration phase blockers remain fixed.

The new frozen Release `tools/Wq10MigrationPreview` CLI's disposable Git fixture suite passes **15/15**. Inspected the precise 3-file correction diff and authoritative `Server/WorkQueueContracts.fs:575-601`.

## One remaining P1 false blocker

**Separate FrozenReview stages with same kind/generation, no source lineage are incorrectly classified as duplicates.** Details and independently reproduced fixture: `findings/repeated-review-stage-identity.md`.

The new dictionary identity tuple is `(Kind, StageGeneration, RepositoryId, SourceTaskId, TargetRef, LineageId)`, which is not an enforced unique key. `publishStage` permits distinct frozen review work IDs and checks only same-ID collision plus concurrent unfinished **Implementation** source-writer lineage collision. Two historical/sequential review stages for one task can each have generation 1 and no source lineage.

In an independent disposable two-clone repro with `review-first: FrozenReview/gen1/Completed` and `review-second: FrozenReview/gen1/Available` for `T1`, explicit empty evidence inventories, all four coverage flags and a real origin fixture: **actual frozen CLI returns `NO_GO` with sole blocker `DUPLICATE_CANONICAL_STAGE`**. This is ordinary staged review history, not a duplicate. The 15 passing tests miss this case.

**Requested minimal correction:** separate immutable work identities and domain/review identities from source-writer lineage collision checks; preserve multiple legitimate review stages even if stage-generation numbers coincide. Add one focused positive fixture, keep the existing true-conflict negative. No new production gate or general duplicate-proof infrastructure needed.

**Verdict: ChangesRequested** for this one bounded false-positive correction. No actual migration, deployment, Scheduler activation, epoch changes or source integration authorized.
