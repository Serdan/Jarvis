# Reviewer 4 independent frozen review — ChangesRequested

**Review:** `3c95ad7f927d46a582bd932e5fb02547`
**Source SHA:** `1d73d13d7c735ca1ea91acd2079ba8f7563473bb`
**Task:** `automation-client-release-red-diagnostic-20261122`
**Original spec:** `2559cd5bb1928725a7d3d5b69a537c2e79a60aeb`
**Baseline main:** `dcac884fc061869572b12a03befc12f11c009f2f`.
**SDK observed by reviewer:** `11.0.100`.

## Independently confirmed evidence / good boundaries
- Frozen branch HEAD equals exact requested source SHA. Diff is seven new files entirely inside the permitted `scripts/tests/automation-client-release-diagnostics-20261122/**` and `docs/testing/automation-client-release-diagnostics-20261122.md`; no production, existing Client.Tests, fsproj, or main changes.
- The prior one-shot full unfiltered Release Client.Tests job `d56b2adf580945dca42e6508a4f0c68b` ended at **900.126s timeout**; no TRX. Running the frozen `analyze_release.py` against original locally retained evidence returns `UNKNOWN` / timeout, exit 1, source `dcac884...`, SDK 11.0.100, observed 30 unique Git PIDs and peak 3 concurrent. Neither the 30 suite-wide PID count nor the six activity diagnostics establishes a 13-vs-12 per-inbox regression or a genuine NUnit failure. Do **not** promote to full Client acceptance.
- Independently reran Python parser controls: **10/10 PASS** (exit 0). Independently reran `dotnet fsi --exec` on `git-budget-assertion-control.fsx`: 12 PASS, 13 rejected by the actual first-party NUnit assertion, changed-head 14 PASS (exit 0). Independently reran `activity-permission-control.fsx` against real compiled `Client.ActivityLog.Store`: normal roundtrip, private Unix modes, symlink read/write rejection, intact outside sentinel (exit 0). These are **bounded native .NET 11 controls**, not a complete NUnit suite, and the Git FSI does not execute the real measurement.
- Did not rerun the original uncertain/900s full suite, activate production, write source, deploy, integrate, or schedule work.

## Actual review findings
1. `findings/trx-counter-false-green.md` — **P2**: parsed results are never reconciled with TRX counters/summary outcome. Independent mutated TRX with one Passed result but summary Failed and counters total 2 / failed 1 was incorrectly classified `PASS`.
2. `findings/debug-evidence-false-green.md` — **P2**: a synthetic valid Debug `dotnet test ... -c Debug` receipt, with pinned source/SDK and positive TRX, was also classified `PASS` despite the required Release gate.

Both concrete negatives contradict the original fail-closed **full unfiltered Release** test-evidence contract; they are in the new classifier, not a discovered production defect. Suggested narrowly scoped script/test corrections are recorded separately and need independent bounded adjudication under review policy; do not silently widen source custody.

**Verdict: ChangesRequested** on this frozen source. The current timeout classification is correct and remains UNKNOWN. The independent review rejects the classifier's unsafe potential future PASS decisions, not the documented honest timeout findings.
