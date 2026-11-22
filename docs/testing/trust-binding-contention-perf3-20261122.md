# Trust-binding read contention: disposable two-client baseline (2026-11-22)

## Provenance and boundaries

- Base: `dcac884fc061869572b12a03befc12f11c009f2f`, `.NET 11` Release, Linux.
- Production `Client/Modules/OrchestrationApi.fs` is unchanged for the baseline.
- Fixture: `Client.Tests/ReviewWorkflowTests.fs`, `ParallelReadOnlyTrustBindingCrossProcessContentionProfile`.
- The test creates a temporary bare Git remote and two independently bound Git checkouts. Two independent `dotnet fsi` processes hold the exact `FileStream(FileShare.None)` OS locks for those random checkout-specific binding paths. The Jarvis execution sandbox redirected temp and config paths to a private fixture; no live Jarvis ledger files were written.
- The test executes real `LedgerBindingConfig.loadAtRootWithSnapshotUsing LedgerStore.fetch` and `verifyTrustedHead`; one writer advances the disposable remote head through `ReviewWorkflow.createTask` first.
- Counts/latency are **informational**, not thresholded in CI. Results below deliberately force overlapping lock ownership; they are not production traffic rates.

## Unmodified source baseline

Command:
`dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~ParallelReadOnlyTrustBindingCrossProcessContentionProfile --logger 'console;verbosity=normal'`

| Concurrent read-only loads | TrustBindingContended | p50 ms | p95 ms | Max ms |
|:--|--:|--:|--:|--:|
| 1 | 1/1 | 28.69 | 28.69 | 28.69 |
| 2 | 2/2 | 0.63 | 1.32 | 1.32 |
| 8 | 8/8 | 0.38 | 0.64 | 0.64 |
| 16 | 16/16 | 0.41 | 1.41 | 1.41 |

Result: **1/1 NUnit test passes**, because typed transient contention is currently an allowed result. All 27 contended calls fail promptly, and post-release retries converge to the newer remote trusted head. This demonstrates that the status failure is not evidence of a changed binding, remote rollback or unauthorized mutation.

## Source analysis

`withConfigLock` takes an exclusive OS lock before bounded JSON read/validation. `loadAtRootWithSnapshotUsing` takes the same lock again for a compare-and-swap on `HighestTrustedHead` **after** the remote fetch and history check. Network Git I/O is not done within the critical section; on a newer concurrent anchor the load refetches and verifies, and it never writes a stale lower anchor.

The acquisition currently makes one nonblocking attempt, and a recognized sharing violation becomes `TrustBindingContended` immediately. Normal parallel read-only calls thus surface a transient synchronization detail as an error. A bounded retry only around FileStream acquisition is a safe candidate; no retry of the callback or unknown-effect mutations is authorized.

## Follow-up

Measure the same fixture after an isolated narrowly scoped acquisition-only retry change, including 1/2/8/16 loads, final trusted head and negative controls (indefinitely held lock, malformed binding, symlink, rollback). Publish the exact commit and independent review. Absolute timings are host-specific.


## Phase B (candidate before dangling-symlink hardening)

The modified `withConfigLock` retries only recognized file-sharing violations during **FileStream acquisition**, at most 32 retries and approximately 75 ms of elapsed budget, with 1–4 ms jitter. It never retries the callback or ambiguous mutation outcomes; `loadAtRootWithSnapshotUsing` retains its original trusted-head validation and locked CAS. No Git fetch occurs inside the lock.

After correcting the independent two-process release-order fixture, the .NET11 Release candidate returned:

| Read-only concurrent loads | Contention | p50 ms | p95 ms | Max ms |
|:--|--:|--:|--:|--:|
| 1 | 0/1 | 185.07 | 185.07 | 185.07 |
| 2 | 2/2 | 81.69 | 82.27 | 82.27 |
| 8 | 0/8 | 168.87 | 174.48 | 174.48 |
| 16 | 0/16 | 176.21 | 184.13 | 184.13 |

**8/8** NUnit regressions passed (including negative remote endpoint and deleted-ledger behaviors, stale delayed fetch, nonregressing trusted-head CAS, and symlink protections). Synthetic latency includes Git fetch/verify and process scheduling, not isolated lock-hold durations. The 1/2/8/16 samples are not inferential statistics; no absolute timing threshold is asserted. Two forced-lock reads still returned recoverable contention; this is intentional fail-closed behavior when the budget expires.

An independently prepared **same-fixture unmodified-runtime** baseline is also being run to separate fixture-release effects from lock-policy effects. The post-hardening candidate additionally recognizes dangling symbolic links at the lock path before attempting FileStream open, guarding against unexpected target creation. The source for the corresponding final Release run must be recorded separately from this earlier candidate trial.

Current telemetry limits: the fixture reports per-call latency distributions and contention outcomes but does not directly instrument exact microsecond lock-hold time or per-call retry counters. The retry upper bound is source-verifiable; it is not a measured count. No production or live-ledger outcome was inferred from the synthetic profile.


## Exact two-client harness baseline matched to Phase B release sequencing

A separate worktree `test/trust-binding-perf3-comparable-baseline` at committed Phase A `d4be2f17d38cec3036fc5e79777ea959b0e4f94c` ran **unmodified production `OrchestrationApi.fs`** with the identical simultaneous holder-release correction. Command: `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~ParallelReadOnlyTrustBindingCrossProcessContentionProfile --logger 'console;verbosity=normal' --logger 'trx;LogFileName=trust-binding-perf3-baseline-comparable.trx'`. Result: **1/1 passed**.

| Concurrent loads | Contended / total | p50 ms | p95 ms | Max ms |
|:--|--:|--:|--:|--:|
| 1 | 1/1 | 77.95 | 77.95 | 77.95 |
| 2 | 2/2 | 1.10 | 1.10 | 1.10 |
| 8 | 8/8 | 0.34 | 0.50 | 0.50 |
| 16 | 16/16 | 0.35 | 0.51 | 0.51 |

Matched synthetic fixture summary: baseline contention 27/27, revised candidate 2/27. The revised method spends additional bounded waiting on confirmed sharing violations, so contended-call latency predictably rises; the numbers should **not** be used for a cross-host p95 performance claim. For ordinary bursts and user-visible error rates, this is a correctness/availability tradeoff, not a measured throughput gain.


## Final exact-source security hardening and verification

The final candidate also detects and rejects both existing and **dangling**
symlink lock paths via `FileInfo.LinkTarget`, including after a delayed
sharing-violation retry. This closes the `File.Exists` false-negative for
dangling symlinks without allowing an untrusted target to be created.
Its negative test also checks the binding file symlink and unchanged decoy
and binding bytes.

Command: `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter '<8 source-bound trust-binding tests>' --logger 'console;verbosity=normal' --logger 'trx;LogFileName=trust-binding-perf3-final.trx'`

Result: **8/8 Passed**, complete raw TRX in
`docs/testing/trust-binding-contention-20261122/final-release-8-tests.trx`.
Final same synthetic fixture results:

| Concurrent loads | Contended / total | p50 ms | p95 ms | Max ms |
|:--|--:|--:|--:|--:|
| 1 | 0/1 | 88.96 | 88.96 | 88.96 |
| 2 | 0/2 | 107.30 | 112.04 | 112.04 |
| 8 | 0/8 | 137.93 | 150.06 | 150.06 |
| 16 | 0/16 | 172.35 | 176.29 | 176.29 |

The matched original unmodified production baseline was **27/27 contention failures**; the final candidate was **0/27** in this run. Another corrected test candidate run observed 2/27 failures, which establishes that remaining typed contention errors are possible when the finite budget expires. These variations also demonstrate why the CI assertions are correctness/state invariants only, with latency percentiles informational.

The synthetic delayed locks were deliberately held and released across *two external OS processes*; no main or live user config path was changed. In addition to that fixture, the exact-source regression suite verifies delayed Git fetch does not hold the lock, independent concurrent trusted-head advance preserves the newer anchor, invalid remote change and remote ledger loss fail closed, sharing classifier distinguishes permanent errors, and symlink fences reject forbidden paths. The lock acquisitions do not encompass network I/O. Total Git fetch count and per-acquisition retries are not instrumented in this test; only finite source retry bounds and observed user-call failures were established.

## File SHA-256 for independent inspection

- `Client/Modules/OrchestrationApi.fs`: `2f88f7b86e2f07fdad4bdfddcbabdc6cf45660dc26c216ac255a75399d2d9e05`
- `Client.Tests/ReviewWorkflowTests.fs`: `22540c263e7745d029c274cfd69ede032f897abec4b0bc7f760b8602b66a63ae`
- `Client.Tests/OrchestrationApiTests.fs`: `4add0c7adfb476ba6bb59128e354264bb6001a095c47c7b201d1b0bc8e7bbd21`
- `docs/testing/trust-binding-contention-20261122/final-release-8-tests.trx`: `15194219ba5a8ea5f077f1366b2ed37d62b38c85c439f687a0e6320a74d0923c`

## P2 frozen-review correction: phase-separated lock instrumentation

Independent ChangesRequested review b3631ab3bdf245edb54d0a373feb245c
identified missing **lock acquisition retries and lock-hold durations**, not a failed
security gate. This correction uses an opt-in AsyncLocal test sink to sample
each lock acquisition; the ordinary runtime has no registered observer or
telemetry output. Sampling is performed *after FileStream disposal* and cannot
change the result even if the diagnostic callback throws. The original
recognized-OS-sharing-only retry budget (32 retries / approximately 75 ms),
trusted-head CAS, symlink and rollback checks, and fetch-outside-lock
semantics are unchanged.

ParallelReadOnlyTrustBindingCrossProcessContentionProfile measures
initial-read and post-fetch-cas separately, reporting successful
acquisitions, exhausted acquisitions, total/max failed-open retry counts,
and held p50/p95/max for each phase. The fixture has two disposable Git clones
and two **external** FSI OS lock holder processes; reader loads are concurrent
tasks inside the test process, *not* two separate reader processes. The same
concurrency groups contain post-release recovery probes for both clones, to
ensure both phase measurements are present even if every contended reader
exhausted its retry budget.

First measured run: .NET SDK 11.0.100, Linux, Release,
dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore
 --filter FullyQualifiedName~ParallelReadOnlyTrustBindingCrossProcessContentionProfile
 --logger 'console;verbosity=normal'
 --logger 'trx;LogFileName=trust-binding-perf3-measured.trx'.
**1/1 passed, exit 0**.

| Readers | Typed contention | Whole-read p50/p95 ms | Initial retries total/max | Initial held p50/p95/max ms | CAS retries total/max | CAS held p50/p95/max ms |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 0/1 | 85.13 / 85.13 | 12 / 12 | 3.800 / 3.800 / 3.800 | 0 / 0 | 1.791 / 1.791 / 1.791 |
| 2 | 0/2 | 94.00 / 98.28 | 47 / 24 | 0.109 / 0.251 / 0.251 | 0 / 0 | 0.122 / 0.274 / 0.274 |
| 8 | 0/8 | 115.10 / 122.30 | 123 / 18 | 0.074 / 3.679 / 3.679 | 2 / 1 | 0.072 / 0.132 / 0.132 |
| 16 | 0/16 | 231.04 / 255.50 | 230 / 19 | 0.056 / 3.426 / 3.426 | 1 / 1 | 0.091 / 0.756 / 0.756 |

At every n, both clones recovered successfully to the exact newer trusted head
after holder release. The separately sampled post-release probes each acquired
the initial and post-fetch locks twice, with zero retries, zero failures.
Their initial held p95 values by n=1/2/8/16 were
0.118 / 0.120 / 0.059 / 0.090 ms; CAS p95 values were
0.260 / 0.245 / 0.128 / 0.178 ms. The 27/27 pre-retry baseline and
0/27 measured run are synthetic deliberately overlapped lock schedules,
**not estimates of production MCP error rates or p95**.

The forced initial-lock contention produces most retry attempts; measured
post-fetch-CAS hold times are short. These observations do not justify
removing the second trusted-head read/CAS, caching a trust anchor, moving Git
I/O under the lock, or expanding the retry budget. No production binding,
GitHub main, deployment or live schedule was touched.


### Repeated Release run and immutable evidence

A second source-exact .NET11 Release suite selected trust-binding, stale
head/remote, delayed fetch, rebind and dashboard recovery controls:
**10/10 passed**; raw TRX is
docs/testing/trust-binding-contention-20261122/trust-binding-perf3-measured-regressions.trx.
The repeated fixture recorded typed contention 0/1, 0/2, 0/8,
**12/16** under the n=16 forced locks. The n=16 initial-acquisition trace
measured 394 failed opens total (max 32 prior retries for a failed acquisition),
with only 4 successful initial-lock and 4 successful CAS acquisitions. This
confirms the bounded policy **still fails closed** and is not a promise of
zero errors or an unconditional latency improvement. All recovered
head observations and independent negative controls passed.

A separate source-exact .NET11 Release negative-space suite covered held
locks, sharing classifier vs permanent I/O, missing/malformed/mismatched
binding, and valid/dangling symlink lock and binding rejection:
**4/4 passed**. The raw Release TRX files were copied intact into the
isolated review source and parsed for test/counter counts and embedded
phase samples.

SHA-256 evidence:

| Frozen source or raw Release evidence | SHA-256 |
|:--|:--|
| Client/Modules/OrchestrationApi.fs | 62dd0820476427d558cf2c43e2129086a446f3920938d1f7f82cf54353358846 |
| Client.Tests/ReviewWorkflowTests.fs | 400f5864d248b5c36f47a4e002abc3301138885e2927d3a00ef1f440f790cf33 |
| Client.Tests/OrchestrationApiTests.fs (unchanged) | 4add0c7adfb476ba6bb59128e354264bb6001a095c47c7b201d1b0bc8e7bbd21 |
| docs/testing/trust-binding-contention-20261122/trust-binding-perf3-measured.trx (1/1) | 41adac17786df5adfbd85c39f157227cba26e6e026cf6fe8a69f47fc7efac025 |
| docs/testing/trust-binding-contention-20261122/trust-binding-perf3-measured-regressions.trx (10/10) | 8be4fd94a616bb2cc865c7cdf9478682cd4cf02d21843083137de4fd51178baf |
| docs/testing/trust-binding-contention-20261122/trust-binding-perf3-measured-negatives.trx (4/4) | b680d516d73f7c30c74ddf792d43a8fe92ed9d33a1cd42c34fae925df4a5e593 |

Original source commit 6e9ed2c74c720087dd80b53cd8a51958647b76d6
and base dcac884fc061869572b12a03befc12f11c009f2f
remain unmodified immutable provenance. The correction only updates the
isolated contributor branch and will require a new independent frozen review.
