# P2 — initial source lineage can be admitted/claimed with untrusted repository identity

**Frozen SHA:** `4f0ac4df3f3f437501c8da054c262706ec3f5e64`.

`Client/Modules/WorkQueueGitLedger.fs:338-347,562-569` requires initial source-lineage identity fields only to be nonblank and continuation to be absent. `verifySourceContinuation` at `:266-275` returns `Ok()` unconditionally for `(ReachableRef=None, ContinuationSha=None)`. Consequently `PublishStage` followed by `ClaimNext` can admit and assign a first-generation `WorkKind.Implementation` source stage whose `RepositoryId` disagrees with `sourceRepositoryId(binding)`, or whose `PinnedBaseCommit` is not a real reachable commit. Only later `verifySourceHandoff` (`:278-329`) rejects it, after worker capacity and an exclusive claim are already consumed.

This is a poisoning/fail-late availability problem rather than evidence of a forged continuation commit. A caller can create a source stage that cannot be safely handed off without manual canonical repair. Validate initial source identity, pinned base commit type/reachability and approved source binding at publication and before first claim (or stage it as incomplete/unrunnable), with a two-clone negative test that ensures an invalid initial repository/base never consumes a source claim.
