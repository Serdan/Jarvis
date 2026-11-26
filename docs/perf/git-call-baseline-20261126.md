# Git-call baseline and end-to-end equivalence contract

Date: 2026-11-26

## Frozen baseline

The baseline is the freshly fetched remote `main` commit:

```text
9dc95184896118c96f34493da28ce40bf5b3d8d6
```

The registered isolated worktree is `perf-git-call-baseline-20261126`, branch
`refs/heads/perf/git-call-baseline-20261126`, at exactly that commit. Its
attempt binding pins `BaseCommit=9dc95184896118c96f34493da28ce40bf5b3d8d6`
and `TargetRef=refs/heads/main`. The worktree was clean at binding. The
primary checkout was already dirty and behind; it was not edited or used as
the source checkout.

The repository remote is `https://github.com/Serdan/Jarvis.git`. Its hosting
visibility/protection is not verified by Git transport; do not infer it.

## Reconciliation of accepted historical measurements

These commits are retained as historical evidence, not silently treated as
the current baseline:

- `72618844318944079550eb898e128b11c03039a7` (parent
  `0fab55163bcc1f08ca7ea061d6e4ae0427e30e9b`) changes only
  `Client.Tests/TaskOverviewBatchPerformanceTests.fs`,
  `Client.Tests/WorktreeTests.fs`, and `docs/McpEndToEndLatency.md`.
  It adds populated 0/220/2200 read-path and worktree-parser goldens, including
  semantic comparisons and opt-in trace-based measurements. It is not an
  ancestor of the frozen baseline. The new hot-path golden is not present in
  this baseline checkout, so its results must be rerun before comparison.

- `92927b7d9ef59d744cc0bfa4f71fc54278e464f0` (parent
  `d948e2cdc8aceba5236f621f62d99f3422dfe4fc`) is a diagnostic-only commit
  containing a report and audit scripts. Its report compares source
  `d948e2c` with parent `dcac884fc061869572b12a03befc12f11c009f2f`.
  It found the inbox completeness guard added two Git runner calls even for
  an empty owner prefix: cold inbox counts rose from 11 to 13; the frozen
  source failed the unchanged 12-call ceiling at 0/220/2200. The exact
  negative-index integrity witness is required and must not be removed,
  bypassed, or hidden by raising the threshold. This is not a measurement of
  the 2026-11-26 baseline.

Metric caveat: the historical `PerformanceRegression.sample` counter is a
scoped count of calls to the in-process Git runner before process launch. It
is not an OS concurrency gauge. New measurements must distinguish runner
attempts, actual Git child starts, remote fetches/pushes, and elapsed time.

A material source drift was found: the accepted 726 report describes bulk
`ListWorktrees` as one `git worktree list --porcelain` call with
`IsClean=None`. In the frozen baseline, `Client/Modules/Worktrees.fs`
still launches `git status --porcelain` for each existing worktree
(lines 91-96). Therefore historical worktree timings/counts are not
comparable to this baseline without rerunning.

## Measurement contract

Every before/after comparison must freeze and report both exact source SHAs.
Use the same test binaries' target framework, .NET SDK/runtime, OS, fixture
seed, fixture topology, and commands. Use clean, separate registered
worktrees; never use the dirty primary checkout. Do not compare different
fixtures or use one run's warmed caches as the other's cold run.

For each scenario, preserve:

- source SHA, parent/base SHA, target ref, runtime/SDK, OS, test filter and
  terminal exit code;
- fixture seed, canonical fixture head, and exact scenario/population;
- end-to-end elapsed time and per-phase timings where available;
- actual Git process starts, fetches, pushes, commit-tree operations, and
  bounded stdout bytes (plus the in-process runner counters where exposed);
- result class, normalized payload digest, canonical ref names, resulting
  tree/entity digests, and durable effects;
- negative-control outcome and exact error class/message where observable.

Compare semantic payloads and state, not commit timestamps or incidental
presentation fields. Canonical refs must retain the same names and equivalent
target tree contents. Writes must produce the same durable records/effects.
A performance win is invalid if it changes permissions, snapshot freshness,
fencing, CAS, cancellation, error classification, or uncertainty/recovery
semantics.

### Representative coverage

- Reads: task overview; AgentInbox at 0/220/2200; task-with-spec; pinned
  review details; challenge list; notification inbox.
- Writes: CreateTask, RegisterAgent, AssignTask, CheckpointAttempt, including
  their prepublication-fetch, local-preparation, publication-push and
  confirmation-fetch phases.
- Discovery: bulk ListWorktrees at empty/small/large parser populations and
  with real registered worktrees; preserve unknown cleanliness as unknown.
- Recovery: JobResult initial and incremental polling; operation-status and
  pending-intent reconciliation; uncertain publication and cancellation.

Required negative controls include malformed/missing selected records,
wrong-owner and future-generation reverse-index entries, deleted historical
reverse indexes, stale snapshots, permission denial, stale CAS, failed
prepublication fetch, failed local index preparation, ambiguous publication,
and cancellation. Keep each control tied to the operation it protects.

Use a controlled local bare Git remote and a handshake-controlled transport
delay for fetch/cancellation tests. No live project ledger, production remote
effects, deployment, or scheduler activation. Use interleaved same-host
repetitions for latency distributions; wall-clock numbers are informational,
not absolute cross-host CI gates. Deterministic process/fetch bounds and
semantic equivalence are gates.

## Repeatable focused Release commands

Run from the isolated worktree. These commands build only the Client.Tests
project and select the relevant focused cases; do not substitute a full
combined suite by default.

```sh
dotnet test Client.Tests/Client.Tests.fsproj -c Release -m:1 \
  --filter 'FullyQualifiedName~TaskOverviewBatchPerformanceTests' \
  --logger 'trx;LogFileName=task-overview.trx' \
  --results-directory /tmp/jarvis-git-perf/task-overview

dotnet test Client.Tests/Client.Tests.fsproj -c Release -m:1 \
  --filter 'FullyQualifiedName~AgentInboxBulkLedgerBenchmarkAndSnapshotCorrectness' \
  -- RunConfiguration.MaxCpuCount=1 \
  --logger 'trx;LogFileName=agent-inbox.trx' \
  --results-directory /tmp/jarvis-git-perf/agent-inbox

dotnet test Client.Tests/Client.Tests.fsproj -c Release -m:1 \
  --filter 'FullyQualifiedName~LedgerMutationStagePerfTests' \
  -- RunConfiguration.MaxCpuCount=1 \
  --logger 'trx;LogFileName=mutation-stages.trx' \
  --results-directory /tmp/jarvis-git-perf/mutation-stages

dotnet test Client.Tests/Client.Tests.fsproj -c Release -m:1 \
  --filter 'FullyQualifiedName~WorktreeTests' \
  --logger 'trx;LogFileName=worktrees.trx' \
  --results-directory /tmp/jarvis-git-perf/worktrees

dotnet test Client.Tests/Client.Tests.fsproj -c Release -m:1 \
  --filter 'FullyQualifiedName~JobManagerTests' \
  --logger 'trx;LogFileName=job-manager.trx' \
  --results-directory /tmp/jarvis-git-perf/job-manager
```

The TaskOverview and AgentInbox performance fixtures emit deterministic
Git-count bounds and informational timing/allocation output. Mutation-stage
tests emit phase counts and timings. Preserve their terminal TRX and console
output together with the source SHA. The explicit 30-sample mutation
distribution is opt-in and should be run separately when a same-host
before/after distribution is required.

This contract is the comparison boundary for the read, write, discovery and
recovery lanes. Update it only with evidence-backed changes; do not loosen
correctness assertions to accommodate a faster implementation.
