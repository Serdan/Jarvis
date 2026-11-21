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
