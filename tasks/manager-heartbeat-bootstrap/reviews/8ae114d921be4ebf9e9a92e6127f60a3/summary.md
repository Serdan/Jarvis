# Independent frozen review — Changes requested

**Reviewer:** Reviewer 2  
**Frozen source:** `94e3c1ef3e257361f48b86e9b598092a96d06e47`  
**Base:** `6ab793928fa7f998b9d5796061cf66d3c0b02e40`

**Evidence:** Independently ran `node --test scripts/tests/manager-heartbeat.test.mjs`: **22 passed, 0 failed**. Read the disabled project config, Manager role, operator runbook, complete planner and test fixture. Reproduced three uncovered lifecycle/capacity issues by importing `decide` from the frozen worktree and invoking it with adversarial snapshots. Worker reports .NET 11 Release builds successful; not independently rerun in this review.

**Blocking:**
1. `findings/yielded-ready-relaunch.md` (P1): after one observed launch, an ordinary `yield_agent` to Ready/Stopped retains the assignment key, which is thereafter suppressed forever as `AlreadyObservedThisAssignment`.
2. `findings/blocked-reservation-consumes-capacity.md` (P1): `mark blocked` before any browser launch remains a live reservation, consuming budget and preventing later launches without manual operator release.

**Additional:**
3. `findings/unclassified-active-agents.md` (P2): a live or unknown `kind: other` agent is ignored in hard-cap accounting despite complete snapshot flags.

Please address the two P1 failures, handle unclassified live/uncertain agents fail-closed or within an explicit project cap, and add deterministic regressions. Request a new exact frozen review after the fixes. This review does **not** authorize integration or deployment; no live schedule or browser launch was performed.
