# Git-process-count regression diagnostic — 2026-11-22

## Scope and immutable evidence

Task: `automation-client-perf-git-fanout-diagnostic-20261122`. Source pinned to
`d948e2cdc8aceba5236f621f62d99f3422dfe4fc` (parent
`dcac884fc061869572b12a03befc12f11c009f2f`). Source's only changed paths
are the six Worker 14 Challenge composition paths; this diagnostic changes none
of them.

Previously completed **unfiltered** Release Client job
`b8442490469e4e758cb2a99288d2a7cc` is terminal **exit 1**. Immutable
`/tmp/jarvis-w14-challenges-20261122/full/challenges-full.trx` reports
**1385 passed / 3 failed / 1 skipped**. All three failures occur at
`Client.Tests/ReviewWorkflowTests.fs:6326` on the *first cold inbox* operation:
`AgentInboxBulkLedgerBenchmarkAndSnapshotCorrectness(0|220|2200)` measured
`GitProcesses = 13`, exceeding the unchanged 12-start ceiling
(`PerformanceRegression.fs:39,49`). Durations before the failure were
0.3718679s, 0.7594919s, and 3.6817330s respectively.
No warm-sample reports were written by these failing cases; do not infer a
subsequent metric or falsely mark the suite green.

## What the metric measures

`PerformanceRegression.sample` calls `LedgerStore.measureGitReads`.
`LedgerStore.fs:498-500` increments `ProcessStarts` for **each call to
the in-process Git runner**, before even attempting `BoundedProcess.run`.
This is a cumulative attempt counter within a single logical inbox call,
**not** an OS concurrency gauge. External unrelated Git processes cannot
increment this scoped `AsyncLocal` counter. `Worker 22`'s separate 900s
Client timeout with peak **3 simultaneous OS Git processes**, no TRX,
therefore neither explains nor contradicts these failures. Likewise, the
three fixture values do not exhibit per-historical-record process scaling:
all fail at the same constant count of 13, including **zero** historic records.

## Source-exact causal delta and command accounting

At the pinned source, `ReviewWorkflow.getAgentInboxAt` calls
`verifyOwnerWaitIndexesAt` at line 3419 **before** the existing batched
inbox-record reader. The parent commit has no such call.

`verifyOwnerWaitIndexesAt` calls
`LedgerStore.listCompleteWaitIndexesAt` for
`challenge-waits/by-owner/<encoded owner>`. That helper is newly added in
the six-path commit. It **unconditionally** runs exactly two Git commands
for an authorized prefix, including the empty-prefix case:

1. `git ls-tree -r --name-only <pinned ledger commit> -- <owner-prefix>`
   through `listAt`.
2. `git log --first-parent --diff-filter=A --format= --name-only
   <pinned ledger commit> -- <owner-prefix>`.

It is not a platform process leak: these are deliberate product calls added
to verify that a historical reverse index has not disappeared. Subsequent
indexed-record verification can add further commands when indexes exist,
but the empty fixture's extra two come from this mandatory scan alone.

The source-verified diff includes the expected six paths and leaves both
`PerformanceRegression.fs` and `ReviewWorkflowTests.fs` unchanged.
The extra call is in the product's inbox execution path, not in test setup
or the test process-count instrumentation. Original first-cold measurement
is independent of test-runner background Git activity.

## Correctness and proportional correction

The new historical `git log` is a **security/integrity witness**: naively
skipping the log whenever the current owner prefix appears empty would make
an issued-then-deleted reverse index indistinguishable from never issued.
Do **not** remove this verification, turn off coverage, weaken the assertion,
increase the budget above 12, or bypass the negative reverse-index mutants.

The smallest separately authorized production correction should target
unnecessary round trips in the **same verified-snapshot inbox read path**,
e.g. amortize the owner-index tree inventory against already-required
batched ledger tree traversal, preserving the historical-completeness
witness, immutable block-operation validation, owner/Challenge reverse
edges and error semantics. Establish exact per-command before/after
accounting; prove warm/cold and 0/220/2200 workload correctness as well
as deleted-index, future-generation, wrong-owner, and stale-snapshot
negative controls before independent review. The explicit source lease
**does not permit** any such production or shared-test edits here.

## Reproduce the evidence safely

Run the read-only audit against the frozen Git objects and immutable TRX
without building or invoking the full suite:

```sh
python3 scripts/tests/automation-client-perf-git-fanout-20261122/audit.py \
  --red-trx /tmp/jarvis-w14-challenges-20261122/full/challenges-full.trx
```

The script asserts exact ancestry and six changed paths, that the extra
guard did not exist in the parent, both new Git commands, source of
cumulative counter, 12 ceiling, and the three exact original REDs.
Failure of any check is an audit failure rather than permission to relabel
test outcomes. Baseline control evidence is recorded below when terminal.

## Exclusions

No replay of original full Client job, no Worker 22 retry, no main push,
deployment, live settings/configuration change, scheduler activation,
source correction or Codex invocation. No claim of full green.

## Exact-parent isolated Release control and result

A separate, clean registered worktree was created at
`dcac884fc061869572b12a03befc12f11c009f2f` (not the live main and
not any other worker's checkout). One bounded targeted .NET 11 Release test
invocation selected only the three
`AgentInboxBulkLedgerBenchmarkAndSnapshotCorrectness` parameter cases,
set single testhost `RunConfiguration.MaxCpuCount=1`, and had a hard
240-second timeout. Jarvis job
`c95477aed0e544878c0b053a7e449e1b` terminated **exit 0** with
`Passed: 3, Failed: 0, Skipped: 0` (22s tests; TRX
`/tmp/jarvis-w4-fanout-20261122/baseline/baseline-targeted.trx`).

The isolated control wrote the canonical performance JSON for all three
counts at
`/home/serdan/Projects/.Jarvis-worktrees/client-perf-baseline-w4-20261122/Client.Tests/bin/Release/net11.0/perf-regression/`.
Each report self-identifies the exact parent SHA. For **every** 0/220/2200
fixture: cold **11** starts; first warm **12** starts; all 30 warm samples
at most **12**; after-head-advance **13**, within its *separate* unchanged
14-start limit. Source-bound baseline reproduction and evidence match
`verify_baseline.py` in this diagnostic lease.

Thus the source-diff command accounting is numerically corroborated:
the mandatory two-command completeness guard lifts the **cold** count
from 11 to 13 on the frozen Challenge source even with zero historical
records. The **warm** baseline is already at 12, so the same guard
also threatens the warm budget, but the frozen tests fail at cold and
**did not measure/report frozen warm**. Do not claim an observed frozen
warm value.

Run the paired read-only checks:

```sh
python3 scripts/tests/automation-client-perf-git-fanout-20261122/audit.py \
  --red-trx /tmp/jarvis-w14-challenges-20261122/full/challenges-full.trx \
  --baseline-trx /tmp/jarvis-w4-fanout-20261122/baseline/baseline-targeted.trx
python3 scripts/tests/automation-client-perf-git-fanout-20261122/verify_baseline.py \
  /home/serdan/Projects/.Jarvis-worktrees/client-perf-baseline-w4-20261122/Client.Tests/bin/Release/net11.0/perf-regression
```

Conclusion: **product-side fixed Git subprocess-count regression introduced
by the Challenge completeness guard**, not proof of concurrent Git fanout
or a Worker22 timeout cause. The integrity feature itself remains
necessary. This diagnosis does **not** authorize changes to the
six-path production source; a separate narrow owner/lease and regression
proof are required to reduce Git invocations without weakening
completeness. Frozen full Client Release stays **RED**.

## Fail-closed audit negative

A disposable copy of the original TRX was altered to replace the `(0)`
assertion's observed `13` with `12`. The source-pinned `audit.py`
rejected that tampered evidence with **exit 1 / AssertionError**. Both
diagnostic Python files passed syntax parsing. The immutable original TRX,
parent fixture reports, source worktrees and shared benchmark tests were
left unchanged. This is a diagnostic-script mutant, **not** a substitute
for future production integrity mutants.
