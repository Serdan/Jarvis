# Confirmed pre-existing intermittent NUnit assertion — not repaired by this test-only review

**Classification:** P1 test reliability issue, original `Client.Tests/WorkQueueSchedulerHostLoopTests.fs:318`; source `25c03f82b6b5257f4feb3904527211e1833383b4` leaves it unchanged. **Not a defect in the three new frozen test/report files.** No silent scope expansion.

**Original obligation:** task explicitly requires preserving and reproducing the original Worker14 broad 28/29 RED, distinguishing fixture nondeterminism from production deadlock, and proposing a separately authorized fix without editing existing tests.

**Independent reproduction:** six individual Release --no-build --no-restore testhost invocations of `StopJoinsConcurrentManualStepWithoutBackgroundLoops`; r4 and r6 exit 1 at line 318 (2/6 RED), four pass. Exact source .NET 11.0.100; full TRX at `/tmp/jarvis-reviewer2-hostloop-r4/review2-r4.trx` and `...-r6/review2-r6.trx`. Initial independent clean build/run passed. 500 source-bound real-Host adversarial cases pass while observing 184+127 instances where the host-owned StopAsync join precedes caller-owned Task.Run wrapper completed flag.

**Root cause and boundary:** production `RunOnce` sets its internal `stepFinished` TCS under lock in `finally` before the method returns to the external Task.Run wrapper. `StopAsync` awaits pump, timer and TCS, not the external caller-owned wrapper task; the original test assumes stronger external task completion and samples `IsCompletedSuccessfully` immediately after Stop. This is a scheduling race/invalid test assertion, not evidence of a hung in-host Wake or production loop leak.

**Proportional candidate remediation (requires separate authorization/adjudication):** preserve the assertion that Stop is incomplete while Wake remains blocked, then separately `do! manuallyRunning.WaitAsync(timeout)` before asserting the manual result and no duplicate callback. If StopAsync is intended to await arbitrary caller-owned tasks, the public API ownership contract needs a separate explicit design; this task is not that authorization.

**Disposition within this review:** Record confirmed known RED without hiding it, and **Accept** the bounded, truthful test-only diagnostic. No scheduler activation or broad-suite clean assertion.