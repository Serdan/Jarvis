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
