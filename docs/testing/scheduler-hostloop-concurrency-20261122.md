# Scheduler host-loop Stop/ManualStep concurrency — 2026-11-22

## Exact scope and provenance

Canonical Jarvis task: `automation-scheduler-hostloop-concurrency-property-20261122`.
Worker 19; isolated branch `test/scheduler-hostloop-concurrency-worker19-20261122`; source lease only `scripts/tests/scheduler-hostloop-concurrency-20261122/**` and this document. No edits to production, existing tests, shared fsproj, main, scheduler activation, account or browser ports.

- Verified remote `origin/main` = `dcac884fc061869572b12a03befc12f11c009f2f` via `git ls-remote origin refs/heads/main`.
- Worker 14's independently Accepted scheduler planner source = `0f732e34c4eef9db6d40f99fbaf11730a36a6520`, direct parent `dcac884...`. `git diff --name-only dcac884 0f732e34 -- Client/Modules/WorkQueueSchedulerHostLoop.fs Client.Tests/WorkQueueSchedulerHostLoopTests.fs` was empty: the reviewed planner commit did not alter either host-loop file.
- Failing test: `Client.Tests/WorkQueueSchedulerHostLoopTests.fs:298-320`, assertion at line 318.
- Real host: `Client/Modules/WorkQueueSchedulerHostLoop.fs:110-150` (`RunOnce`) and `:209-217` (`StopAsync`).
- Linux .NET SDK `11.0.100`; `-c Release`; read-only production source.

## Independent NUnit reproduction: intermittent RED confirmed

A single exact-main Release run:
```sh
dotnet test Client.Tests/Client.Tests.fsproj -c Release \
  --filter 'FullyQualifiedName~StopJoinsConcurrentManualStepWithoutBackgroundLoops' \
  --logger 'trx;LogFileName=hostloop-baseline.trx' \
  --results-directory /tmp/jarvis-w19-hostloop-baseline --verbosity minimal
```
Exit 0, 1 passed, TRX `/tmp/jarvis-w19-hostloop-baseline/hostloop-baseline.trx`.

Three independent testhost processes using the same filter with `--no-build --no-restore`:
- `r1`: exit 0, 1/1 pass, `/tmp/jarvis-w19-hostloop-repeat-r1/r1.trx`.
- `r2`: **exit 1**, 1/1 failed, `/tmp/jarvis-w19-hostloop-repeat-r2/r2.trx`. NUnit: `Expected: True; But was: False` at `WorkQueueSchedulerHostLoopTests.fs:318`, `StopJoinsConcurrentManualStepWithoutBackgroundLoops` (67 ms). This reproduces the specific original 28/29 RED test on exact source, but not the complete original 29-case selection.
- `r3`: exit 0, 1/1 pass, `/tmp/jarvis-w19-hostloop-repeat-r3/r3.trx`.

Thus **1/4 isolated single-case invocations RED**; do not call it consistently failing or dismiss it as unverified flakiness. Adjacent Release filter (`WorkQueueSchedulerHostLoopTests|WorkQueueReactiveWakeTests|WorkQueueSchedulerHostPreviewTests`) passed 34/34, exit 0, TRX `/tmp/jarvis-w19-hostloop-neighbors/hostloop-neighbors.trx`. The neighbor filter is **not** the reporter's original 29-case selection.

## Mechanism: internal completion signal is not external Task.Run completion

Actual `RunOnce` finishes `ports.Wake`, stores outcome and invokes `ports.Report`. In its `finally`, under `sync`, it clears `inStep` and sets `stepFinished.TrySetResult(())` at line 149; only then does the method itself return `result` at line 150. `StopAsync` snapshots the internal `stepFinished.Task` and awaits it with pump/timer via `Task.WhenAll`. The test's manual caller is an **external** `Task.Run(fun () -> host.RunOnce ...)` owned by the test, not by the host.

Therefore Stop can finish after all coordinator/report callbacks, yet before that external `Task.Run` has completed its wrapper/scheduled continuation. The test incorrectly assumes `do! host.StopAsync()` implies `manuallyRunning.IsCompletedSuccessfully = true`. The evidence supports **a task-completion ordering/contract mismatch**, not a demonstrated leak of a background loop, duplicate external effect, or permanent deadlock.

This does *not* prove every possible host-loop race safe. It narrows the observed line-318 RED. If the API contract truly requires joining any arbitrary caller-owned `Task.Run`, it would require an explicit async caller/ownership protocol; the current `RunOnce`/internal TCS cannot guarantee that wrapper's task completion.

## Source-bound seeded adversarial regression

Standalone .NET 11 F# executable:
```sh
dotnet run --project scripts/tests/scheduler-hostloop-concurrency-20261122/HostLoopConcurrency.fsproj \
  -c Release -- 20261122 250
dotnet run --no-build --project scripts/tests/scheduler-hostloop-concurrency-20261122/HostLoopConcurrency.fsproj \
  -c Release -- 90317 250
dotnet run --no-build --project scripts/tests/scheduler-hostloop-concurrency-20261122/HostLoopConcurrency.fsproj \
  -c Release -- 20261120 1
```
All exits 0. First seed: **250/250** cases, **57** observations where `StopAsync` completed before the external caller `Task.Run` marked successful. Second seed: **250/250**, **69** such observations. Minified single-scenario replay: 1/1 pass, with zero early-wrapper observations in that run. The schedule-dependent early-completion count is **diagnostic, not an invariant or deterministic replay oracle**. The exact failing NUnit r2 TRX provides the separately reproduced RED.

The runner links `../../../Client/Client.fsproj`, invokes the actual production `Client.WorkQueueSchedulerHostLoop.Host`, and contains no copied/mocked host implementation. It supplies safe, deliberately inert `ReadVerifiedHeads` and `Wake` ports with gated TaskCompletionSource callback, bounded waits and cancellation-aware infinite timer. No Git I/O, scheduler sends or browser effects. Scenarios cycle through: (0) manual in-flight + stop, (1) overlapping Busy manual signal + stop, (2) Start during in-flight manual + stop and background cancellation, (3) concurrent Dispose/Stop join, and (4) same-thread reentrant RunOnce Busy + stop. Final closed-state probe ensures no post-stop reconcile or hints.

An independent minimal state model asserts: while a callback is blocked, `StopAsync` must remain incomplete; after Stop closes the host no further work/hints/start may be admitted; one blocked reconciliation must yield exactly one verified-head read, Wake and report, with the exact known outcome; repeated Stop and Dispose must join; background start/stop must not replay a pending effect. Waiting for the caller-owned Task is explicit and separate. Timeouts fail loudly and seeds/scenarios appear in errors.

### Negative oracle

```sh
dotnet run --no-build --project scripts/tests/scheduler-hostloop-concurrency-20261122/HostLoopConcurrency.fsproj \
  -c Release -- 20261122 1 --negative
```
**Expected exit 1**, observed `TRACE seed=20261122 iteration=0 scenario=2: FAIL ... unexpected number of coordinator invocations`. This injects a deliberately duplicated external `Wake` callback accounting event through the safe port and demonstrates the exact-once observation oracle is active. It is **not** a production-code mutant or proof that the production host itself duplicates effects.

## Recommended disposition and remaining gates

1. Treat existing line-318 NUnit failure as an **unstable overly strong assertion**. A narrowly authorized future *existing-test* correction could `do! manuallyRunning.WaitAsync(timeout)` before asserting its successful result; retain the stronger positive checks that `StopAsync` does not finish while `Wake` is blocked and never invokes a second effect. Do not disguise the original RED until that correction is adjudicated and independently reviewed.
2. Do not modify production on the strength of this line-318 result alone. Escalate a new exclusive source/test lease to Lead if a stronger explicit StopAsync external-call ownership contract is required. No safe default-off scheduler cutover is justified by these tests alone.
3. Independent frozen review of this test-only branch and exact Release evidence is required before integration; neither Accepted planner source nor this test report implies published mainline integration.

## Evidence limits

No production port external side effects, real account CAS, live browser-send, deployment, migration or unattended scheduler. Controlled in-process task scheduling on Linux does not prove all OS/interleaving possibilities. Testhost run outcomes and TRX paths are local ephemeral evidence, not durable integrated CI receipts. There were no source/test changes outside the assigned new scripts path and this docs file.
