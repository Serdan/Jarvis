# Lightweight Jarvis performance regression lane

This source-only test lane intentionally **does not** use BenchmarkDotNet, touch a production ledger, enable scheduling, or deploy anything. It extends the original `AgentInboxBulkLedgerBenchmarkAndSnapshotCorrectness` synthetic Git fixture in `Client.Tests/ReviewWorkflowTests.fs`, reusing `LedgerStore.measureGitReads` and the production `CommandDispatchQueue`.

## Release execution

Restore an isolated checkout first; `dotnet test` with missing test assets can exit successfully without running any NUnit tests on some .NET 11 SDK configurations. Require a positive test count from the actual test runner, not just exit code 0.

```sh
dotnet restore Client.Tests/Client.Tests.fsproj --ignore-failed-sources
dotnet build Client.Tests/Client.Tests.fsproj -c Release --no-restore
dotnet vstest Client.Tests/bin/Release/net11.0/Client.Tests.dll '--TestCaseFilter:FullyQualifiedName~AgentInboxBulkLedgerBenchmarkAndSnapshotCorrectness' '--logger:console;verbosity=normal'
dotnet vstest Client.Tests/bin/Release/net11.0/Client.Tests.dll '--TestCaseFilter:FullyQualifiedName~thirty' '--logger:console;verbosity=normal'
```

If the runner's filtering syntax differs, use `dotnet vstest ... --ListTests` before running, and retain explicit counts and TRX. A test process that produces no discovery or execution evidence is not a pass. The fixture has three cardinalities: 0, 220, and 2,200 canonical historical task/attempt/review/integration/notification records plus unrelated 16 KiB task specs. All Git remotes are under a disposable temporary directory. Each fixture reads one immutable head cold, then warm, checks the canonical serialized result and independent clone, samples 30 repeats (configurable to 30–100), advances the remote head with one new canonical notification, and rejects stale reuse.

For repeatable local distributions, run three times on the **same machine**, pinned source SHA, fixture cardinality, and configuration:

```sh
for n in 1 2 3; do
  JARVIS_PERF_RUN_INDEX="$n" JARVIS_PERF_SAMPLES=30 \
    dotnet vstest Client.Tests/bin/Release/net11.0/Client.Tests.dll \
    '--TestCaseFilter:FullyQualifiedName~AgentInboxBulkLedgerBenchmarkAndSnapshotCorrectness'
done
```

Set `JARVIS_PERF_REPORT_DIR` to a local scratch directory to preserve JSON. The default is the NUnit working directory's `perf-regression/` folder. Each report is machine-readable and includes source SHA, OS, architecture, .NET runtime, processor count, run number, fixture count, cold/warm/changed-head snapshots, p50/p95/p99/max, max allocated bytes, Git starts, fetches, per-blob/batch reads, Git stdout bytes, and response bytes. No command arguments, credentials, repository paths, source payloads, agent IDs, or host names are included.

## Deterministic CI contracts

- One remote fetch per canonical inbox; at most 12 Git process starts in steady-state and 14 across a verified remote-head change (additional ancestry checks), at most one batch and one individual blob read.
- Git stdout amplification at most 32 times the canonical response size plus 64 KiB; historical spec payloads must not leak into inbox.
- Cold/warm serialization and independent-clone views equal byte-for-byte at the same verified head; one new canonical notification must advance the head and appear in the new result.
- 32 cheap command-dispatch reads must complete **before** releasing one held operation, with the held lane kept blocked for one second; p50/p95/p99 and queue/execution timings are informational. This proves no serial head-of-line dependency without flaky wall-time gates.

These tests are not load tests of a real network, and do not yet cover all named tool paths. `listProjectTasks`, dashboard refresh, `get_task`, challenge list, send preflight, result polling, cancellation, and Windows KRUSO parity remain distinct follow-up probes. Existing `BoundedProcessTests` and `SignalRDispatchTests` cover several fault/cancellation boundaries; those are not reclassified as new evidence. Future parity runs should compare Git counts and canonical correctness before interpreting machine-specific latencies. Treat >60 s tails as incidents needing queue, Git, network, serialization and scheduling-stage attribution, not as a reason to loosen deterministic bounds.

## Baseline provenance

The original historical Linux baseline is in `docs/AgentInboxScaling.md` (November 8, .NET 10: 220 records, 10 Git processes, one fetch, one cold batch, 495 ms cold, 228.5 ms warm; later bounded-process run reported about 108 ms and 74 ms). Those numbers are **not** a .NET 11 baseline or cross-host threshold.

Three verified .NET 11 Release runs on EndeavourOS x64 (12 processors), implementation commit `d3839c8e53dcf83563f24382f0bd073b049fc86e`, each passed all three cases. The complete nine per-run JSON reports, including cold/warm/head-change counters, allocations, tail latency and response sizes, are retained in `docs/perf-baselines/2026-11-21-endeavouros-x64-net11.json`. The local TRX receipts are named `TestResults/perf-run-{1,2,3}.trx`.

| Records | Cold range, 3 runs | Warm p50 range | Warm p95 range |
| ---: | ---: | ---: | ---: |
| 0 | 60–102 ms | 30–46 ms | 38–130 ms |
| 220 | 162–188 ms | 114–126 ms | 143–204 ms |
| 2,200 | 604–727 ms | 345–470 ms | 507–1,080 ms |

Every warm sample used 12 Git starts, one fetch and zero blob batches; the changed-head phase used 13 starts. The 2,200-record warm p95 variation is evidence that local contention affects latency tails, **not** a 1.08 s correctness threshold. A Windows baseline remains unmeasured and must be paired with the same implementation SHA, fixture sizes and Release configuration before making parity claims.
