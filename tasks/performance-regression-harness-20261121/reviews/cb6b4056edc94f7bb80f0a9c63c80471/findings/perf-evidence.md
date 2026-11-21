# Independent Reviewer 3 findings — performance regression harness

**Frozen review:** `cb6b4056edc94f7bb80f0a9c63c80471`; task `performance-regression-harness-20261121`; source `917391422de23b060c58cfcf7811eb89243c272d`, base `2222f6e6bffc6c3b02403d79b54e3fa94cb64f47`. Verified clean isolated worktree `perf-regression-harness-20261121` at that exact SHA before and after tests. Commit `91739142` only adds docs/baseline over implementation commit `d3839c8e53dcf83563f24382f0bd073b049fc86e`.

## Independent execution (2026-11-21)

- `dotnet --version`: `11.0.100`. `dotnet build Client.Tests/Client.Tests.fsproj -c Release --no-restore`: **exit 0**, zero errors, four warnings in unchanged Client/other tests.
- `dotnet vstest Client.Tests/bin/Release/net11.0/Client.Tests.dll --TestCaseFilter:FullyQualifiedName~AgentInboxBulkLedgerBenchmarkAndSnapshotCorrectness --logger:console;verbosity=normal`: **3 discovered, 3 passed**, including 0, 220, and 2,200 synthetic historical records; 27.67s suite. On frozen `91739142`, each produced 30 warm samples:
  - 0 records: cold 11 Git starts, warm max 12, changed-head 13; one fetch; warm p50/p95/p99 **31.47/39.67/43.86 ms**.
  - 220 records: same 11/12/13 work counts; one fetch; warm p50/p95/p99 **116.97/158.83/197.97 ms**.
  - 2,200 records: same 11/12/13 work counts; one fetch; warm p50/p95/p99 **383.17/511.96/515.08 ms**.
  - Warm samples used 0 batch blob reads and 1 individual blob read; cold and head-change phases used at most 1 batch read. All byte-amplification, canonical equality, independent-clone, remote-head invalidation, and new-notification assertions passed.
- `dotnet vstest ... --TestCaseFilter:FullyQualifiedName~thirty --logger:console;verbosity=normal`: **2 discovered, 2 passed** (targeted stalled-operation test plus an unrelated reconnect test). The 32 dispatch operations completed before release of the simulated held worker; measured cheap-path p50/p95/p99 **1.0625/1.0681/1.0706 ms**, held for 1000 ms.
- Reviewed `Client.Tests/PerformanceRegression.fs`, `ReviewWorkflowTests.fs`, `SignalRDispatchTests.fs`, `docs/PerformanceRegression.md`, and the checked-in nine-report baseline. All 9 baseline reports are explicitly synthetic, .NET 11 Release, implementation SHA `d3839c8e`, with 30 warm samples and 12 starts / 1 fetch; three historical runs per cardinality. No production test data, main edits, deployment, or scheduling.

## Scope limitations / nonblocking follow-ups

1. **Test scope:** The negative test holds a synthetic `CommandDispatchQueue` callback, not a real slow Git or ledger transaction. It proves dispatch-worker concurrency but not end-to-end contention under ledger locks, remote network, or transports. Add an injected Git/ledger delay and cancellation/child-process negative test as a follow-up before claiming system-level tail guarantees.
2. **Named workload coverage:** Only AgentInbox is covered by the new deterministic Git-count fixture. Dashboard, get_task, reviewer inbox, task overview, challenge list, result polling and send preflight, plus two-device contention, remain unmeasured; documentation appropriately discloses this. They should get separate source-exact fixtures.
3. **Platform and metadata:** Windows/KRUSO parity has not run. Reports give OS, architecture, runtime and logical processor count, but not CPU model. Add CPU model to future same-host comparison metadata, without exposing private host identifiers.
4. **Interpretation:** 12 Git starts and 1 fetch are *bounded*, not evidence of optimality. Local cold/warm timings are not cross-host CI gates. In particular the historical 2,200-record p95 reached ~1,080 ms under contention; treat it as a measured tail, not an acceptable latency target.

**Verdict: Accepted for the scoped, lightweight source-only regression harness.** Independent exact-source Release tests and bounded-work evidence pass. These findings do not certify system-wide performance, production network latency or Windows parity.