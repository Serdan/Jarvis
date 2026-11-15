# P1 — dependency wake corrupts two-tier FIFO by replacing server time with sequence

**Frozen source:** `143a73a35336305afaab7d796f16f70782982709`  
**Files:** `Server/WorkQueueContracts.fs:330-342, 495-513, 811-826`; `docs/specs/work-queues-phase-a-contracts.md:15-22, 39`  
**Accepted design:** `docs/specs/work-queues-and-ephemeral-executions.md` §3 (`availableAtUtc` **and** per-project `availableSequence`), §16 cases 4, 5, 7, 9.  
**Severity:** P1; newly released work can incorrectly jump ahead of older Available work, breaking the central FIFO scheduling contract.

## Reproduction from the pure transition semantics

Use one project, one compatible Worker queue, and three ordinary (non-blocker-tier) work stages:

1. Publish `B`: Available, `AvailableAt=1000`, `AvailableSequence=1`.
2. Publish `C`: Available, `AvailableAt=2000`, `AvailableSequence=2`.
3. Publish `A`: Blocked by B, `AvailableAt=1500`, `AvailableSequence=3`. Existing `normalizePublishedStage` retains the correct Blocked state. `NextAvailability=3`.
4. A registered Worker claims B (oldest ready work); after C was available, B completes with `completeOrdinary`. This calls `projectDependenciesChanged -> wakeBlockedStages`.
5. The wake sets A's `AvailableSequence=4` **and `AvailableAt=4`**, rather than a new witnessed availability time after B's completion. `orderAvailable` sorts A before C because `4 < 2000`.

Expected: C precedes A, because C was Available before A was woken; the wake moves A to the back of its tier. Actual: A precedes C. Neither has an unsatisfied dependent that changes its tier. This is a legal typed input: `AvailableAt` and `AvailableSequence` are independent `int64` fields with no invariant equating their units.

`wakeBlockedStages` cannot witness a fresh timestamp: it has neither a `now` parameter nor an injected clock. Its `AvailableAt=sequence` assignment is inconsistent with other valid transitions, e.g. `resolveDependencyGate` uses a caller-supplied `now` (§line 1116). The Phase A note that the *tests* use sequence as their virtual time does not make sequence a valid timestamp in the typed contract, nor fulfill the accepted independent timestamp/sequence requirement.

## Required correction

Provide the canonical server-witnessed `now`/virtual clock value to dependency projection (or an explicit pure time-witness argument to its callers). On each winning Blocked→Available CAS, set `AvailableAt` to the witnessed time and `AvailableSequence` to the next monotonic sequence. Multiple wakes in the same CAS may share a timestamp but must have distinct sequences; replay must reuse the original receipt without changing either. Ensure all triggering paths—ordinary/message/ancillary completion, waiver, cancellation, gate resolution—use the same mechanism.

Add a negative fixture with timestamp values intentionally unlike small sequence values, two ready stages in the same tier, and a wake later than their publication. Assert older ready C precedes newly woken A. Verify the existing requeue/replay, stale two-client CAS, and blocker-tier tests still pass.

## Evidence boundary

This finding is derived from exact frozen source/accepted spec and deterministic code path inspection; it is not a claimed runtime production repro. The .NET 11 Release targeted 41 and full Server.Tests 176 tests pass, but none tests mixed time/sequence domains. Phase A remains side-effect-free and does not operate the production Scheduler.