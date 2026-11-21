# Independent Perf 2 frozen-source review — Reviewer 2

Review `73bb94e1cb504c73a513fa77ac68c994`; task `performance-task-overview-batch-git-20261121`; source SHA `be661813de3a78d08a2cf72e0fcac101feb32d14`; base `2222f6e6bffc6c3b02403d79b54e3fa94cb64f47`. The registered `perf-task-overview-batch-20261121` checkout was clean and `git rev-parse HEAD` exactly matched the frozen source. `git diff --check` passed. No source edits, main mutation, deployment or schedules performed by reviewer.

## Source inspection
`ReviewWorkflow.listProjectTasks` fetches one verified snapshot, batches primary task/review records, decodes them through the existing `requiredFromRecords -> decodeEntity -> validEntity` path, derives current attempt paths and reads these in a second source-pinned batch. Final TaskId and CreatedSequence/ReviewId sort orders remain. Selected canonical path patterns are a subset matching existing `ledgerPath` canonical entity families; unrelated history/integration receipts are not decoded. The existing `readSelectedRecordsAt` validates Git modes `100644 blob`, canonical OID and selected paths; malformed or missing selected entities propagate errors. No path membership is cached across calls; head advancement is re-enumerated.

## Independently executed tests
`dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~TaskOverviewBatchPerformanceTests --logger console;verbosity=normal` exited 0 on the exact frozen worktree, 7/7 NUnit cases passed. Coverage includes cold/warm/remote-head advances, serialized old-reader equivalence, missing linked attempt, malformed canonical task, invalid attempt path, symlink `120000`, executable `100755`, and gitlink `160000` mode failures.

281 task + 281 attempt + 530 review corpus observed:
- Cold: 257.31 ms; 20,104,104 allocated bytes; **12 Git process starts**, 1 fetch, 1 individual blob, 2 batches, 884,151 output bytes.
- Warm: 144.92 ms; 17,033,960 bytes; **12 starts**, 1 fetch, 1 individual blob, 0 batches, 500,228 output bytes.
- Advanced remote head: 198.58 ms; 17,343,848 bytes; **14 starts**, 1 fetch, 1 individual blob, 2 batches, 502,209 output bytes.
- Small clean: 76.27 ms; 10 starts, 1 fetch, 0 batch reads.

The legacy source path reads 281 task + 281 current attempt + 530 review entities = 1,092 required records, each using `LedgerStore.readAt`'s `blobAt` and `showBlob` (2 Git commands): **2,184 per-entity Git starts**, plus fixed fetch/list overhead. Contributor's separately reported baseline was 2,193 starts / 9,030 ms / 276.6 MB. The old wall-time and allocation baseline was not rerun by this reviewer; the process-count explanation is corroborated from frozen source.

## Nonblocking observation
The requirement says *target* single-digit Git process starts. This implementation does not reach that aspiration: measured 12 cold/warm and 14 after head advance, although all deterministic <=14 and <=1-fetch gates pass and the per-entity scaling has been removed. A future fixed-overhead optimization is worthwhile if it preserves verified fetch, source pinning and safety; this is not a correctness regression and must not justify bypassing those guards.
