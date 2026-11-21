# P2 — Required lock-hold and retry measurements are absent

**Frozen source:** `6e9ed2c74c720087dd80b53cd8a51958647b76d6` (base `dcac884fc061869572b12a03befc12f11c009f2f`). **Original task:** `performance-trust-binding-contention-20261122`, which explicitly requires capture of transient failure rates, p50/p95/max, **lock hold time and retry counts**, and tracing the two locked trust-binding phases.

## Exact evidence

- `docs/testing/trust-binding-contention-perf3-20261122.md`, Phase B, states: "the fixture reports per-call latency distributions and contention outcomes but does not directly instrument exact microsecond lock-hold time or per-call retry counters." The final section likewise states "Total Git fetch count and per-acquisition retries are not instrumented."
- `Client.Tests/ReviewWorkflowTests.fs` `ParallelReadOnlyTrustBindingCrossProcessContentionProfile` records only `Stopwatch` around each entire `loadAtRootWithSnapshotUsing LedgerStore.fetch`, plus typed contention totals. These timings include independent Git fetch/verification and process scheduling, and cannot isolate either exclusive critical section.
- `Client/Modules/OrchestrationApi.fs` `withConfigLock` limits retries to at most 32 and tests 75ms after sharing exceptions; it does not record actual retry count or time spent holding the lock. The two lock entries at initial binding read and post-fetch CAS cannot be separated from the current measurements.
- The committed final TRX confirms reported pass counts and 1/2/8/16 latency values, not these omitted measurements.

## Impact and requested narrow correction

The implementation plausibly lowers visible `TrustBindingContended` rates (matched synthetic 27/27 baseline to 0/27 final, with a separate 2/27 candidate run), but cannot answer the task's explicit diagnostic question: whether duplicated reads/anchor persistence or critical-section duration cause avoidable contention, or how many retries account for the new synchronous latency. A source-level upper bound is not a measured distribution.

Add **fixture-local** instrumentation or source-bound trace probes that directly report acquisition retry counts and lock-hold time for both critical sections under the same disposable 1/2/8/16 runs, and record exact-source Release evidence. Keep aggregate latency informational; do not introduce runtime logging of secrets or weaken the trusted-head CAS. There is no need to change the retry design unless measurements expose a defect. This finding does not require main edits, deployment, or production ledger access.

**Severity:** P2, original performance-evidence acceptance gap; no verified correctness/security exploit is asserted.