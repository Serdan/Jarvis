# Independent review — Perf 3 ledger-mutation stages

**Verdict: Accepted (bounded source optimization; no wall-time speedup assertion).**

- **Review:** `c91e597e1f3d4c2e89bcbd3e20d54a61`
- **Task:** `performance-ledger-mutation-stages-20261121`
- **Frozen source:** `12b738d576d4db3c28b6ae77401d733f5ffa7345`
- **Base:** `2222f6e6bffc6c3b02403d79b54e3fa94cb64f47`
- **Independent worktree:** `perf-ledger-mutation-stages-20261121`, HEAD verified identical to frozen SHA, clean after tests.
- **Scope:** `Client/Modules/LedgerStore.fs`, dedicated fixture `Client.Tests/LedgerMutationStagePerfTests.fs`, project inclusion, stage report; `git diff --check` exit 0.

## Source audit

1. `writeTree` hashes the same individual immutable contents, initializes an isolated temporary Git index with `read-tree`, and now writes one NUL-delimited `update-index -z --index-info` batch. Exact `100644 <oid>\t<path>\0` framing avoids path argument parsing and preserves the prior object/tree semantics. A failed batch returns before `write-tree`, `commit-tree`, or publication; temporary index is deleted in `finally`.
2. `gitIndexInput` uses existing `execute`/bounded subprocess transport, `GIT_INDEX_FILE` isolation and UTF-8 stdin. `BoundedProcess.TextInput` writes the full memory including NUL bytes. No changes to remote fetch, non-force push, postpublication confirmation, trust roots, or outcome-unknown fencing.
3. Opt-in `measureMutationStages` stores an AsyncLocal scope, attributes top-level elapsed phases and records unscoped child counts without double-counting nested phase time. No opt-in, no new Git work. Diagnostic caveat: `ProcessStarts` counts attempted `execute` invocations, including fault-injected commands that do not spawn an OS process. For successful non-faulted fixtures it equals actual starts.
4. Concurrent independent-client regression confirms both task records survive competing CAS publication. Positive path retains exactly two fetches, one normal push and one commit-tree; attempted push count is bounded (<=8). Explicitly rejected CAS can retry; ambiguous publication is not blindly replayed with a new UUID.

## Independent .NET 11 Release verification

SDK `11.0.100`; normal fixture rebuilt from this frozen checkout. `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~LedgerMutationStagePerfTests` **4/4 passed**:
- Four routine successful writes with deterministic process ceilings and required phase buckets.
- Slow injected prepublication timeout: no push and no commit-tree.
- Injected batched index-info failure: no commit, no push, no remote operation receipt and no created task.
- Two concurrent Git clients: no lost task, bounded publication attempts.

Existing `LedgerStoreTests` with Release `--no-build --no-restore`: **5/5 passed** (concurrency, history, idempotency).

Explicit 30-sample-per-mutation Release diagnostic separately rerun: **1/1 passed**, 120 successful disposable bare-Git mutations:
| Mutation | Git process starts | Fetch | Push | Commit-tree | p50 ms | p95 ms | p99 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| CreateTask | 38 | 2 | 1 | 1 | 111.83 | 142.83 | 182.49 |
| RegisterAgent | 35 | 2 | 1 | 1 | 102.40 | 123.92 | 139.44 |
| AssignTask | 47 | 2 | 1 | 1 | 128.96 | 138.95 | 143.85 |
| CheckpointAttempt | 44 | 2 | 1 | 1 | 121.40 | 136.35 | 147.26 |

Prior reported unbatched process counts 41/37/52/47 versus independently observed 38/35/47/44: **3/2/5/3 fewer child Git commands**, supported directly by replacing per-file index update commands with one. The new independent run's phase p50s were approximately 18–26 ms per network/preparation phase against a local bare Git remote, not hosted remote timings.

## Decision and limitations

**Accept** the narrow correctness-preserving process-count optimization and source-attributed diagnostic fixture. No verified blocking defect found. This is not proof of multi-second end-to-end tool latency reduction. The submitted report's initial postchange latency tails worsened even in unchanged remote stages under a separate load window; appropriately disclosed as confounded. Causal wall-time gain needs interleaved same-host A/B under comparable load. Hosted network RTT, queueing and SignalR response stages were not measured. Windows KRUSO parity remains follow-up when connected. These are nonblocking limitations, not grounds to overstate performance. No main edits, deployment or schedules during review.