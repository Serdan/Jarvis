# Independent review — Accepted

## Frozen source and scope

- Reviewed immutable source ref `refs/heads/jarvis-review/automation-git-call-optimization-ledger-20261126/0b488c25b20e47d4af6f61cd126c79d1/r1`, source version 1, exact head `ca4823ad1f198416ef2f96588f9e47b5ea87647a`, against base `9dc95184896118c96f34493da28ce40bf5b3d8d6`.
- GitHub compare confirms two commits ahead, no commits behind, and exactly two changed files: `Client/Modules/LedgerStore.fs` and `Client.Tests/LedgerMutationStagePerfTests.fs`.
- The registered candidate worktree `ledger-perf-worker4-20261126` is clean at the reviewed head. The unrelated dirty selected primary checkout was not used or modified.

## Implementation assessment

No blocking correctness defect found.

- Batching is restricted to 2–64 disjoint ordinary exact paths with a bounded total argument length. Non-batchable, overlapping, or ambiguous path sets fall back to the previous per-path lookup.
- The batch reads from the same immutable `head` already fetched for the transaction, uses NUL-delimited `ls-tree` output, requires exact returned paths and `100644 blob` entries, and accepts only the same 40/64-character object IDs as the previous lookup.
- Results are keyed by ordinal path and consumed in the original `changes` order, preserving first-error precedence. Missing paths, stale versions, mode/type errors, and Git lookup failures retain the prior result semantics.
- The check remains before local tree/commit creation and publication. The existing fetch, authoritative revalidation, push/CAS, confirmation, receipt, retry, and unknown-outcome paths are unchanged.

## Validation

- Owner's frozen candidate validation: 14/14 passed, exit 0 (job `bc12487e74de4d6bb7a9822288fedbcd`). The owner's baseline report records 8/8 passed (job `4cae224d835a46e0b606ec9db88fa75b`; its job record was no longer retrievable during this review).
- Independent Release run of `LedgerMutationStagePerfTests`: 5/5 passed, exit 0 (job `6fc439edae59425abc3b6c890be6bab9`).
- Independent cross-caller Release run: 12/12 passed, exit 0 (job `4aa326190c5d48e58fee42dcc81afb42`). It covered credential/foreign-credential checks, cross-clone stale-writer replay, lost-push uncertainty, non-fast-forward/CAS recovery, review lifecycle/document writes, and concurrent/two-clone challenge state-machine behavior.
- All reviewed test fixtures use disposable local repositories; no live ledger, deployment, or activation was performed. Detailed terminal evidence is in `findings/validation-run.md`.

## Performance evidence and caveats

- Reported runner invocation counts: CreateTask 38→37, RegisterAgent 35→35, AssignTask 47→44, CheckpointAttempt 44→43. Each retained 2 fetches, 1 push, and 1 commit-tree invocation. This is a deterministic reduction in Git runner fan-out for the multi-path writes; single-path RegisterAgent is unchanged as expected.
- Reported wall times were 97.63→90.61 ms, 121.46→94.55 ms, 135.17→125.29 ms, and 121.93→108.55 ms respectively. Treat these as indicative single-run observations, not statistically established latency gains.
- The reported process metric is source-attributed runner invocations, not independent OS child-process/Trace2 evidence. In the successful no-fault timing fixtures each invocation proceeds to one bounded Git process launch, but the instrumentation increments before the fault seam/process start.
- The 30-sample mutation-distribution test is marked Explicit and was excluded from the 14/15 non-explicit candidate run. Run the same explicit distribution fixture against both frozen baseline and candidate before making stronger latency claims. The 2200-record inbox p50 comparison is a useful read-path guard, but this mutation change does not target that path.

## Residual non-blocking coverage

- The new direct batch test exercises multiple record paths and stale-version ordering. There is no dedicated test for a failed batched `ls-tree` command or the 64-path/16-KiB/overlap fallback boundary. The implementation fails before publication on lookup failure and conservatively delegates those fallback cases to the unchanged per-path implementation; adjacent stale/fetch/index negative controls and the additional caller tests passed.
- No deployment, integration, or source changes were made by the reviewer.

**Verdict: Accepted.** The optimization is narrowly scoped, preserves the transaction's pinned-snapshot and publication semantics, and demonstrates deterministic reduction in Git runner fan-out. The measurement caveats above should remain attached to any latency claim.
