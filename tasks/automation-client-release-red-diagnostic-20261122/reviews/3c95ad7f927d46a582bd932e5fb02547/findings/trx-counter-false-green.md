# P2 — TRX counter/result inconsistency can yield false PASS

Frozen reviewed source: `1d73d13d7c735ca1ea91acd2079ba8f7563473bb`; `scripts/tests/automation-client-release-diagnostics-20261122/analyze_release.py` lines 12-35, 68-70, 86-90.

**Actual behavior.** `parse_trx` collects `ResultSummary/Counters` as `declared_counters`, but `classify` never compares them with parsed result counts or checks the `ResultSummary outcome`. A well-formed, contradictory report with *one* `UnitTestResult outcome="Passed"`, `ResultSummary outcome="Failed"`, and `Counters total="2" passed="1" failed="1"` returns **PASS** when the manifest has correct source/SDK, unfiltered Release command, `exit_code=0`, and `timed_out=false`.

**Independent direct reproduction.** Reviewer 4 executed a read-only `python3 -c` in the exact frozen worktree; output: `MISMATCHED_TRX_NEGATIVE verdict=PASS declared=0 counters={'total': '2', 'passed': '1', 'failed': '1'}`, terminal 0. This is a synthetic deliberately inconsistent TRX negative control, **not evidence** of a real NUnit failure or a corrupted original run.

**Contract/impact.** Original task requires a fail-closed unfiltered complete TRX/terminal gate, never promoting incomplete/uncertain runs to green. The new classifier's claimed `complete TRX` conclusion is not justified by XML parseability plus one successful result. This can cause a false acceptance decision if test output is inconsistent, incomplete, or independently reports failure.

**Proportional remedy (subject to independent adjudication).** In this test-only new script, validate `ResultSummary` and numerically reconcile `Counters` (`total`, `passed`, `failed`, skipped/notExecuted where present) to per-result outcomes; treat missing/contradictory/invalid summary evidence as UNKNOWN/RED, not PASS. Add the precise contradictory-TRX regression. Do not change Client production/tests or rerun the timed-out 900s suite as a remedy.
