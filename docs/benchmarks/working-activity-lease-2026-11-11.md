# Working activity lease: 100-call Git-backed read baseline

**Date:** 2026-11-11
**Worktree:** `feature/admin-working-ready-correction`, based on `e8a14cb5`
**Scope:** client ingress activity admission for an already-Working agent.

## Workload

`Client.Tests/ReviewWorkflowTests.fs` runs
`OrdinarySelfReadRecordsCanonicalActivityBeforeReturningAcrossDevices`
against an isolated **local bare Git remote with two clones**. After one
`Owner` self-read establishes the canonical five-minute lease, the benchmark
dispatches 100 ordinary `GetAssignment` task reads through
`Client.SignalR.Client.processIncomingCommand`. The working assignment is
unchanged, and a before/after ledger commit OID comparison verifies that the
100 calls do **not** add any canonical writes.

The stopwatch covers the 100 command calls only, excluding fixture setup,
first lease renewal, and the final verification reads. No latency threshold
is asserted in CI because environment and Git startup cost vary.

## Results

| Run | Total 100 reads | Average per read | New ledger commits |
| --- | ---: | ---: | ---: |
| 1 | 17,449.1 ms | 174.49 ms | 0 |
| 2 | 19,008.4 ms | 190.08 ms | 0 |
| 3 (Git trace2 enabled) | 15,935.7 ms | 159.36 ms | 0 |

For the exact third run's 15.936-second UTC interval, Git trace2 recorded:

- **3,300** Git `start` events (33 per read, counting nested processes).
- **600** Git `child_start` events (6 per read; not additive to `start`).
- Frequent operations: `show` 600; `remote`, `update-ref`, `ls-tree`
  400 each; `merge-base` 300; `fetch` and its `git-upload-pack`
  process 200 each.

The Git counts are for **the full `GetAssignment` read path**, not attributable
to the lease itself. With a cached valid lease, the lease adds no canonical
transaction per normal call; the remaining high subprocess count originates
in existing ledger reads, trust/fetch and verification. The test's fixture
is local and should not be interpreted as a remote-production latency estimate.

## Reproduce

```sh
dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore \
  -v quiet --logger 'console;verbosity=detailed' \
  --filter 'Name~OrdinarySelfReadRecordsCanonicalActivity'
```

For Git process attribution, set `GIT_TRACE2_EVENT` to an absolute writable
JSONL file before starting the command. Filter event timestamps to the UTC
window emitted by the test. Count `event=start` and `event=child_start`
separately.

## Interpretation

The lease avoids adding a fetch/commit/push to **every** ordinary call, which
was the primary performance defect in the first activity-fence proposal.
However, existing task-ledger reads already incur substantial Git process
overhead (~33 starts per call in this fixture). That read-path problem is
separate from the administrative correction, and optimizing it needs its own
measurement and acceptance criteria.

## Mixed 100-call regression on durable per-operation reservations

The `7021c003` revision added a separate durable in-flight reservation per
command. The explicit diagnostic `BenchmarkHundredMixedWorkingCalls` runs
100 genuine ingress operations against a local Git-backed fixture: 34 SearchText,
33 ReadFiles and 33 same-project self NotifyAgent calls.

| Metric | Result |
| --- | ---: |
| Wall time | **44,447 ms (444.5 ms per call)** |
| Canonical commits/pushes | **233** |
| Git process starts | **13,350** |
| Git child starts | **3,199** |

The 233 commits are explained by **two reservation writes per call** (begin +
end), plus 33 actual notification mutations. Git trace2 was filtered to the
exact timed UTC interval 2026-11-11T07:04:27.7647389Z to
2026-11-11T07:05:12.2112889Z, excluding fixture setup.

Compared with 100 cheap-leased GetAssignment calls (159–190 ms/call, no
ledger writes), the per-call reservation model causes severe write
amplification. The workloads differ, so a direct latency ratio should not be
attributed solely to reservations; the **233 canonical writes** are conclusive.
A production remote ledger would also incur network latency and contention
absent from this local fixture.

**Acceptance:** amortize durable fencing across bursts of active calls without
weakening cross-device correction safety; provide explicit authenticated
orphan recovery backed by evidence that the original operation cannot still
be executing. Blind expiry would reintroduce the original race.
