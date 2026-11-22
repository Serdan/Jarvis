# Automation Client Release diagnostic — Worker 22 (2026-11-22)

## Authority and source boundary

Task `automation-client-release-red-diagnostic-20261122`, pinned spec blob `2559cd5bb1928725a7d3d5b69a537c2e79a60aeb`. Fresh clean main source `dcac884fc061869572b12a03befc12f11c009f2f`, branch `test/automation-client-release-diagnostics-worker22-20261122`, registered worktree `automation-client-release-diagnostics-w22-20261122`.

Integrator checkpoint **10992** explicitly places 32-path `cef6a59c` on NONPUBLICATION HOLD; it includes a cancelled ancestor and semantic overlaps. It is **not** the source of this baseline. Old jobs `e54db7a2a89f4a5e9125dad34907d3b1` and `5ee0987fc6124aebaee55a83e81d39c0` are UNKNOWN, not terminal green or retried.

Only this directory and this Markdown file are in the source lease. No existing `Client.Tests`, production `Client`, shared `*.fsproj`, main ref, remote or integration were edited. Targeted custody request to Lead was sent via notification (operator conversations are disabled). The disjoint source path is separate from Integrator/Worker14/Worker20 leases.

## First-party contract audit

- `Client.Tests/PerformanceRegression.fs:38-54`: measured Git starts per **single** canonical inbox read, capped at **12** for cold/warm and **14** after changed head. This is *not* an OS-wide simultaneous child-process ceiling.
- `Client/Modules/LedgerStore.fs:196-228`: `measureGitReads` counts starts in an `AsyncLocal` scope, not a sampled `ps` process tree. The external /proc sampler only reports concurrency, observed pid count, threads, and RSS; short-lived Git children may evade 150 ms sampling. Never substitute these metrics.
- `Client.Tests/ReviewWorkflowTests.fs:6242-6382`: real production `getAgentInbox` over Git fixtures, NUnit cases 0/220/2200 with 30 warm samples each, verified snapshot parity and changed-head contract; the expected budget is asserted in this first-party source.
- `Client/Modules/ActivityLog.fs` and `Client.Tests/ActivityLogTests.fs`: intentional negative tests for symlink escape and malformed/overlarge activity state invoke `LoadAgentSeen`/`SaveAgentSeen`, which log `UnauthorizedAccessException`/`InvalidDataException` while the oracle asserts rejection. **Stderr text alone is not a failed test.** Final TRX/outcome is the arbiter; genuine failing permission tests must not be dismissed.
- `activity-permission-control.fsx` is an additional source-bound oracle against the actual compiled `JarvisClient.dll`, validating a successful private valid read/save and rejected symlink read/write with intact outside sentinel. Execute only after the full baseline to avoid interfering with performance tests.

## Disposable Release run (one unfiltered invocation)

Managed Jarvis job: `d56b2adf580945dca42e6508a4f0c68b`. Source exact: `dcac884fc061869572b12a03befc12f11c009f2f`. SDK exact: **11.0.100**. Entrypoint `launch_main_release.py`; no shell runner permitted by Jarvis policy. Profile, `HOME`, `USERPROFILE`, XDG, `TMP`, NuGet, Git global config, and dotnet CLI home are private. Git credential helpers are disabled, terminal prompts disallowed. Single project `Client.Tests.fsproj`, Release, VSTest unfiltered, NUnit worker count 0 for diagnosis, `-m:1`; wall bound 900 seconds, kill-after 30 seconds. Native testhost against exact current main.

Evidence directory: `/tmp/jarvis-client-release-w22-main-u8mba82w` (locally disposable; not pushed). It holds `manifest.json`, `terminal.log`, `proc-samples.jsonl`, `process-summary.json`, and `results/client-full.trx` if completed. `analyze_release.py` requires source/exit/TRX consistency and never manufactures green from stderr.

Diagnostics parser negative controls: `python3 scripts/tests/automation-client-release-diagnostics-20261122/test_analyze_release.py`, managed job `0933e9dba40142f88cc70dfa3de3afbb`, **6/6 PASS exit 0**. Covers corrupted source identity, missing exit, missing TRX, 13-versus-12 Git counter failure, failed TRX despite zero process exit, and intentionally expected activity permission log with a passing TRX.

## Terminal outcome and first event (not a full test verdict)

**UNKNOWN / TIMEOUT, not PASS and not a proven NUnit RED.** The one authorized source-bound **unfiltered** Release job `d56b2adf580945dca42e6508a4f0c68b` finished with managed runner **exit 124**, **900.126s** elapsed, `timed_out=true`, and **zero TRX** files. The child `dotnet test` exit field in `manifest.json` is **0 after SIGTERM**, but cannot establish completion; the launcher explicitly returns 124 on timeout. No second full run, replay of old unknown jobs or partial-green claim was attempted. The source was `dcac884fc061869572b12a03befc12f11c009f2f`, GA SDK `11.0.100`; project was `Client.Tests/Client.Tests.fsproj` Release, `-m:1`, no filter, NUnit workers 0. This only covers **main**, not Integrator's held 32-path composite or any later approved composition.

The real order **visible in `terminal.log`** is: native restore/build and testhost startup, then line **14** `Jarvis activity persistence: agent-seen read failed (InvalidDataException)`, followed by five further activity diagnostics (`append`/`agent-seen read`/`agent-seen save`, all `UnauthorizedAccessException`), then two **Skipped** test lines. No completed test totals, test case failures, stack trace, per-test timing or TRX appeared before termination. These log lines are **not sufficient** to attribute the timeout to ActivityLog, distinguish an expected negative test from a genuine failing one, or identify the currently running NUnit case. The earliest *logged exception* is the `InvalidDataException`; the earliest observable terminal event is build/test startup.

**First-party correspondence (not an inferred case verdict):** `Client.Tests/ActivityLogTests.fs:378-387` explicitly tests refusal of an oversized `agent-seen.json` with `InvalidDataException`. Its symlink-escape tests at `:216-231`, `:234-249` and `:426-439` expect or record denied read/save/append. `Client/Modules/ActivityLog.fs:309-370` emits these persistence log labels from the real Store. These tests are plausible sources of the six lines, but **without TRX we do not assert their outcomes or exact chronological attribution**. The standalone production-bound FSI oracle below narrows behavior, not the full test result.

## Original process trace and stall boundary

Read-only analysis of `proc-samples.jsonl` (5,349 snapshots at nominal 150 ms over 899.928s) and `process-summary.json`:

- **Git:** 30 unique sampled PIDs across 15 minutes; peak **3 concurrent** Git processes. First sampled Git at **163.714s**, last at **789.640s**. A last window of approximately **110.3s** had no sampled Git process. Short-lived Git processes can escape 150 ms samples.
- **Process resources:** peak **7 descendants**, **182 threads**, **2,087,396 KiB RSS** (~2,038 MiB). Near timeout the trace still had **3 descendants**, **60-61 threads**, about **838 MiB RSS**. No evidence here of a currently exploding Git child count or an OOM kill; this does not prove absence of a CPU/lock/I/O stall.
- **Contract distinction:** `Client/Modules/LedgerStore.fs:196-228` counts Git *starts within one `AsyncLocal` measured operation*. `Client.Tests/PerformanceRegression.fs:38-54` expects at most **12** for normal cold/warm inbox operations, **14** after head advance. **Thirty PIDs across the whole suite and peak three simultaneously are not that metric.** This run neither reproduces nor rules out Integrator's earlier *per-operation* 13-over-12 failure on a different, unaccepted composition.
- **Hang classification:** bounded **uncompleted NUnit testhost** at the 900s deadline. Continued Git observations as late as 789.64s contradict a single 15-minute Git-child deadlock, but no test-level event/timestamp pinpoints which case was active for the final 110s. Do **not** label the entire suite PASS, RED, or definitively deadlocked based on these observations.

Original disposable evidence is retained without alteration under `/tmp/jarvis-client-release-w22-main-u8mba82w` (not pushed). SHA-256 digests:

| File | SHA-256 |
| --- | --- |
| `manifest.json` | `129831899ef81578e262ea2edd8d0a94a6f91ccbba7d1eb92ea0751acdb1384e` |
| `terminal.log` | `292151e0e589739a6064f96b44694f7915973e09257d10b1b44076182f64158a` |
| `proc-samples.jsonl` | `1038d943c40c66cfbc6b447d77118a5c1b07bbe152ac4718eac921d5f5cdc040` |
| `process-summary.json` | `3d460408825eca71e48cdf0f3471ddc0d79be09c0f1a3cfceeca8c7d7cf48a2f` |

## Bounded direct source-bound controls (not a full-suite substitute)

After the timeout, with exact unchanged main source and SDK `11.0.100`, each direct `dotnet fsi --exec` was run under a fresh disposable HOME/USERPROFILE/TMP and a 45-second OS timeout. These oracles import the **actual compiled first-party Release assemblies** from the pinned source:

1. `git-budget-assertion-control.fsx`: `Client.Tests.PerformanceRegression.boundedInbox` **PASS** for metric GitProcesses=12 and explicitly **REJECTED** a mutant with 13. `boundedAfterHeadAdvance` **PASS** with GitProcesses=14. Initial FSI attempt failed **before assertions** (`FS0039: NUnit not defined`) due to missing explicit FSI `#r` reference; the *only* correction was adding scoped `nunit.framework.dll` reference, followed by successful bounded run **exit 0**. This directly verifies the existing NUnit assertion but **does not execute the actual Git-backed benchmark** or prove its measurements.
2. `activity-permission-control.fsx`: actual compiled `Client.ActivityLog.Store` **PASS**, including valid private read/save roundtrip and strict private Unix modes, then **read rejected** and **write rejected** for a symlink pointing outside the activity directory, with sentinel **unchanged**; bounded FSI **exit 0**. Its logged `UnauthorizedAccessException` is the **intended** negative. Unix-only: **not** a native Windows ACL test or a full-suite ActivityLog verdict.

`analyze_release.py /tmp/jarvis-client-release-w22-main-u8mba82w` produced **exit 1** intentionally with `verdict=UNKNOWN`, `reason=bounded timeout; no complete test verdict`, SDK/source preserved, six activity log matches. The classifier is fail-closed for wrong source/SDK, missing terminal result/TRX, any timeout even if child exits 0 or a synthetic green TRX exists, filtered invocation, or an actual red TRX. Its separate parser-only mutation/control suite is **10/10 PASS**, not native Release acceptance. `__pycache__` is excluded from committed source.

## Root-cause disposition and smallest ownership boundary

**Established:** the original earlier 13-over-12 finding belongs to a different uncertain/composed run and cannot be reproduced from this main-only whole-run process sample; this run had an **unlocalized full-suite timeout** rather than a completed failure verdict. Activity permission log strings are compatible with intentional test negatives; the direct Store oracle confirms the expected behavior in disposable Unix scratch. **Unestablished:** a real Git fanout regression, genuine profile-permission defect, exact slow/hung NUnit test identity, and acceptance of any mixed source composition.

The current authorized **test-only new-directory and doc correction is self-contained**. Its reviewer should validate that no source-contract guarantee is overstated, that the two real-assembly oracles are meaningful but bounded, and that wrong SDK/filtered/timeout results cannot be promoted to green. A new **separate narrow scoped lease** from Lead is required *only if* later source-exact per-test events/complete TRX establish an issue in `Client.Tests/ReviewWorkflowTests.fs`, `Client.Tests/ActivityLogTests.fs`, `Client/Modules/LedgerStore.fs`, `Client/Modules/ActivityLog.fs`, or testhost scheduling. Suggested next owner is Lead/Integrator: plan one separately authorized **test-case-level timestamp/duration diagnostic** on the independently accepted composed source and distinguish a genuinely failing case from intentionally logged negatives; do not indiscriminately replay the 15-minute job. Full unfiltered Client Release acceptance remains **HOLD** until an actual complete source-bound TRX and exit and independent review.

No existing Client/Client.Tests source or fsproj, current main, live server, browser, credentials, deployment, migration, scheduled work, or integration status was changed. Source is prepared for frozen independent review; acceptance/publication are separate decisions.


## Adjudicated narrow classifier repair (2026-11-22)

**Authority:** Reviewer4's immutable `ChangesRequested` source `1d73d13d7c735ca1ea91acd2079ba8f7563473bb` / review `3c95ad7f`; Adjudicator2 decision sequence **11214** NARROW on both findings. Lead's binding exclusive scripts/docs-only lease is present in task spec revision 2; no change to production, Client.Tests, shared fsproj, current main, or other agents' work. The original review verdict is not retroactively accepted.

- **F1 — complete TRX semantics:** `analyze_release.py` now parses a single terminal `ResultSummary` with required numeric nonnegative `total/executed/passed/failed/notExecuted` counters and reconciles declared results with every `UnitTestResult`. Missing, duplicated, malformed, contradictory, or zeroed summaries fail closed as **UNKNOWN**, not PASS. Genuine complete failed outcomes remain **RED**. The classifier separates `NotExecuted` result rows from the native adapter's `notExecuted` counter: the real NUnit/VSTest adapter may emit skipped `NotExecuted` results while keeping that counter at zero. The observed real TRX from Worker24's **distinct** completed Release source had 1,362 result rows, 1,360 Passed, 2 NotExecuted, `ResultSummary outcome=Completed`, `executed=1360`, `notExecuted=0`; it parsed successfully. This is **schema compatibility evidence only**, not evidence of acceptance for Worker22's baseline or Integrator's composite.
- **F2 — exact Release invocation:** classifier requires `dotnet test Client.Tests/Client.Tests.fsproj` with exactly one `-c Release`, `--configuration Release`, or `--configuration=Release` switch before test-runner arguments. Missing, Debug, duplicate/conflicting, unsupported configuration properties, malformed switches, test filter, wrong SDK, and wrong source cannot produce PASS. Actual installed `dotnet --version` was **11.0.100**. Read-only verification of original pinned baseline manifest confirmed a valid `-c Release` argument and the same original source hash; **timed_out=true, zero TRX** remain binding.

**Bounded repair checks:** `python3 -B scripts/tests/automation-client-release-diagnostics-20261122/test_analyze_release.py -v` **22/22 PASS, exit 0**, covering positive formats, counter conflicts, absent/failing summaries, malformed XML, Git 13-over-12 RED, activity-log intentional negatives, source/SDK/timeout safety, and Release invocation positives/negatives. Additional real-adapter negative mutation: a **disposable copy** of Worker24's real completed TRX with `passed` artificially incremented was independently rejected as `summary counters contradict individual outcomes` (**exit 0**, expected rejection); the original TRX was untouched. None of these focused checks is the missing full source-exact Client Release gate. The previous **10/10** in the original report refers to the earlier frozen source, not the repaired suite.

**Immutable boundary:** full unfiltered main Client Release job `d56b2adf580945dca42e6508a4f0c68b` remains **TIMEOUT / UNKNOWN (900.126 s; no TRX)**. Do not rerun, classify as PASS/RED, conflate sampled peak 3 concurrent Git children with 13 cumulative Git starts, or infer final NUnit case outcomes. A new exact SHA and frozen independent review are required after the scripts/docs-only correction.
