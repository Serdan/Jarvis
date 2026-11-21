# Jarvis Perf 3 — source-exact Git-ledger mutation stage attribution

Source baseline: `2222f6e6bffc6c3b02403d79b54e3fa94cb64f47` (isolated `perf/ledger-mutation-stages-20261121`).
Scope: `CreateTask`, `RegisterAgent`, `AssignTask`, `CheckpointAttempt`.
All test remotes are freshly created local bare repositories; **no canonical live ledger writes or deployment**.

## Source boundaries and latency classification

| Stage | Exact source path | What is necessary / what may be redundant |
| --- | --- | --- |
| Before mutation | `LedgerStore.transact` `withPhase "prepublication-fetch"` | A fresh authoritative remote fetch is necessary for optimistic CAS; `validate` executes two `git remote get-url` subprocesses on each fetch. |
| Inspect existing UUID and plan | `readOperation`, `plan snapshot`, `blobAt`, `changedAgentStates` | Pinned-commit reads and entity-version guards are necessary. `blobAt` invokes `git ls-tree` per change; an existing agent blob is also fetched for state-change receipts. This is a source-exact N+1 risk, not proof that guards can be skipped. |
| Prepare tree | `writeTree`, `commitTree` | One isolated `git read-tree`, **two child processes per written file** (`git hash-object -w --stdin` and `git update-index --cacheinfo`), one `git write-tree`, and one `git commit-tree`. Since each mutation writes ledger header + operation record + N domain files, local tree construction costs **7+2N** Git child processes. Index isolation and exact content blobs must remain intact. |
| Publication | `publishAndConfirm`, `git push --porcelain origin <oid>:refs/heads/jarvis-orchestration` | One normal non-force push per attempt is necessary. A rejected CAS retry is only allowed when Git explicitly proves non-fast-forward rejection. An ambiguous outcome is fenced and reconciled by original UUID. |
| Confirmation | `withPhase "confirmation-fetch"` then `readOperation` and `isAncestor` | Confirmation after push is necessary, because a successful-looking subprocess alone cannot establish the durable remote receipt. Includes another fetch and local verification processes. |
| Client/UI | MCP queue, SignalR and response delivery | Not measured by the local test. End-to-end tool duration is not equivalent to mutation wall time. |

The source-bound `LedgerStore.measureMutationStages` opt-in scope collects elapsed time for top-level named phases, Git process/fetch/push/commit/blob counts and stdout bytes. Planner processes outside a `withPhase` wrapper are labelled `unscoped` rather than falsely attributed to network latency. `TotalElapsedMilliseconds` includes that residual. Nested remote probes are excluded from phase elapsed accumulation to avoid double-counting.

### Reproduction

`dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~LedgerMutationStagePerfTests --logger "console;verbosity=detailed"`

The standard test selection exercises four real workflow mutations, count bounds, a slow injected prepublication failure with zero pushes, and two competing clients against one disposable Git remote. The 30-per-operation sample method is marked **Explicit**, because its latency distribution should not gate CI. Select it directly for machine-local p50/p95/p99 baselines. Compare before/after subprocess counts and same-machine wall time; do not treat Linux-to-KRUSO absolute times as a regression.

### Narrow source optimization and remaining candidates

The isolated branch implements one **NUL-delimited batch `git update-index -z --index-info`** to replace per-file `update-index --add --cacheinfo`, keeping the isolated temporary index, individual immutable blob hashes, explicit checked update failure, and unchanged commit/push/confirmation paths. No additional remote operation is eliminated. The local per-transaction Git-process reduction was independently counted in the same fixture, before a full same-machine latency comparison; an independent reviewer must still inspect exact source and fail-closed negatives.

1. Batch resolution of pinned domain entity paths/blob contents only after tests prove no stale read, missing entity, unexpected object type or receipt-state regression.
2. Retain remote fetch and postpublication proof; instrument hosted remote RTT/transfer separately from local fixture counts.
3. Preserve `OutcomeUnknown` after ambiguous publication; no blind retry or new UUID may replace original-operation reconciliation.
4. Record same-machine pre/post p50/p95/p99 and run Windows KRUSO fixture when available. Do not assert cross-machine absolute latency regression.

### Verified local baseline

.NET 11 Release, disposable local bare Git remote, test fixture on Serdan-Home. Source-bound single-sample invocation values (not hosted timings):

| Mutation | Wall ms | Child Git processes | Fetches | Pushes | Commit-tree |
| --- | ---: | ---: | ---: | ---: | ---: |
| CreateTask | 110.07 | 41 | 2 | 1 | 1 |
| RegisterAgent | 120.19 | 37 | 2 | 1 | 1 |
| AssignTask | 142.29 | 52 | 2 | 1 | 1 |
| CheckpointAttempt | 138.32 | 47 | 2 | 1 | 1 |

The normal offline test selection completed **3/3 passing**: four write-path count bounds, a slow injected prepublication fetch failure with no push/commit, and two independent concurrent client writers with no lost task. The 30-per-operation distribution remains deliberately non-gating.

The assignment's 3.3–9 s timings are client execution observations, not measured by this local fixture. These local 110–142 ms operations establish that 37–52 local Git subprocesses are significant and reducible, but **do not by themselves account for multi-second production calls**. Real hosted transport RTT/transfer, per-client queueing, SignalR and response delivery must be measured independently.

### Same-machine prechange 30-sample distribution

Explicit NUnit test, n=30 **per operation**, 120 successful local bare-Git writes. Cold is the first sample of that operation and not an overall test-process cold-start metric.

| Mutation | Cold ms | p50 ms | p95 ms | p99 ms | Mean Git processes | Mean stdout bytes | Mean current-thread allocation bytes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| CreateTask | 190.86 | 115.94 | 172.10 | 190.86 | 41 | 4936 | 5,090,785 |
| RegisterAgent | 118.23 | 104.82 | 130.22 | 137.32 | 37 | 3897 | 4,562,300 |
| AssignTask | 152.54 | 137.53 | 171.66 | 176.31 | 52 | 8661 | 6,487,910 |
| CheckpointAttempt | 143.60 | 129.57 | 199.62 | 282.71 | 47 | 7783 | 5,857,260 |

All 120 operations used two fetches, one non-force push, and one commit-tree call. Mean current-thread allocations are diagnostics only; child-process and other threads' memory is not included.

| Mutation | Pre-fetch p50 ms | Local-prep p50 ms | Push p50 ms | Confirmation-fetch p50 ms |
| --- | ---: | ---: | ---: | ---: |
| CreateTask | 25.67 | 25.71 | 18.40 | 25.70 |
| RegisterAgent | 25.45 | 21.75 | 17.90 | 25.94 |
| AssignTask | 25.31 | 33.55 | 19.00 | 26.18 |
| CheckpointAttempt | 25.57 | 26.22 | 19.27 | 25.37 |

### Batched-index after-change first local sample

| Mutation | Prechange Git child processes | Batched-index processes | Processes eliminated | After-change wall ms |
| --- | ---: | ---: | ---: | ---: |
| CreateTask | 41 | 38 | 3 | 119.65 |
| RegisterAgent | 37 | 35 | 2 | 116.40 |
| AssignTask | 52 | 47 | 5 | 136.31 |
| CheckpointAttempt | 47 | 44 | 3 | 122.85 |

The batched-index source change passed **4/4** .NET 11 Release normal-selection tests, including exact process upper bounds and injected `update-index` failure with **zero commits and zero pushes**. The failing original operation UUID had no remote receipt or created task. The concurrent clients also left both tasks present, while the failed prepublication fetch did not publish. Source-only `git diff --check` passed.

### Batched-index after-change 30-sample distribution

Explicit non-gating NUnit, same Serdan-Home machine, n=30 per operation. This was a separate time window, not load-controlled or randomized/interleaved A/B.

| Mutation | Cold ms | p50 ms | p95 ms | p99 ms | Mean Git processes | Mean stdout bytes | Mean current-thread allocation bytes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| CreateTask | 530.33 | 183.48 | 554.90 | 718.63 | 38 | 4936 | 4,743,684 |
| RegisterAgent | 396.95 | 197.61 | 503.68 | 520.57 | 35 | 3897 | 4,336,320 |
| AssignTask | 186.94 | 246.83 | 810.78 | 925.59 | 47 | 8661 | 5,901,541 |
| CheckpointAttempt | 173.26 | 216.70 | 674.32 | 691.05 | 44 | 7783 | 5,508,600 |

All 120 postchange writes retained two fetches, one push and one commit-tree. The percentiles **worsened in the postchange run**, including *unmodified* remote fetch and publication phases: prepublication fetch medians rose to 41–51 ms from ~25 ms; push medians rose to 30–38 ms from ~18–19 ms. This indicates a materially different execution environment during the second run; the measurements do **not** establish either a latency speedup or a regression attributable to the index optimization. The count reduction of 2–5 child processes per write is reproducible and independent of CPU/network contention. For causal wall-time evidence, repeat interleaved A/B on the same machine under equivalent load, isolating this change while holding ledger sizes comparable.

| Mutation | Post pre-fetch p50 ms | Post local-prep p50 ms | Post push p50 ms | Post confirmation-fetch p50 ms |
| --- | ---: | ---: | ---: | ---: |
| CreateTask | 43.21 | 30.80 | 33.23 | 49.84 |
| RegisterAgent | 51.21 | 34.45 | 38.35 | 51.48 |
| AssignTask | 43.00 | 46.10 | 30.31 | 44.21 |
| CheckpointAttempt | 41.77 | 37.63 | 30.14 | 39.29 |



### Final Release regression evidence

- Normal Perf 3 fixture: 4/4 passing, exact postbatch process ceilings, real disposable Git writes, prepublication remote-failure and failed batched-index negatives, and concurrent client preservation.
- Explicit diagnostic fixture: 1/1 passing with 120 postchange mutation samples, non-gating p50/p95/p99 and stage distribution.
- Existing `LedgerStoreTests`: 5/5 passing with .NET 11 Release `--no-build --no-restore`.
- `git diff --check`: clean. No live canonical project/account ledger was used for any sample.
- The immutable historical integration missing-field issue still prevents the normal agent inbox; it is an independent coordination blocker, not a reason to bypass assignment or corrupt records.
