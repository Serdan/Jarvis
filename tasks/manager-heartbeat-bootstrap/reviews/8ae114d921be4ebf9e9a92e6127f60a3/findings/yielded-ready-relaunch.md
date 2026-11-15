# P1 — A yielded Ready assignment can never be relaunched

**Location:** `scripts/manager-heartbeat.mjs:150-155,201-202`

Once a launch has produced any positively attributed Jarvis activity, `decide` changes its journal record to `observed`. On every subsequent pass, an `observed` record with the current `assignmentKey` populates `observedAssignments` and suppresses that Worker ID, without consulting whether the previous session has since stopped.

`yield_agent` explicitly transitions a Working assignment to Ready without changing the task/attempt/generation identity. Therefore a Worker that starts successfully and then yields becomes Ready/Stopped under the *same* assignment key, but the Manager will reject every future launch with `AlreadyObservedThisAssignment`. The heartbeat cannot resume yielded work.

**Independent reproduction on frozen commit `94e3c1e`:** plan a Ready worker, record one newer `ExplicitAttributedToolCall`, call `decide` to observe it, then model a normal yield as Ready/Stopped with unchanged assignment key. The subsequent decision returns zero reservations, `AlreadyObservedThisAssignment`, while the old record is `observed`. Existing 22 deterministic tests pass but do not cover this transition.

**Required correction:** preserve no-duplicate protections while allowing a *verified* stopped/yielded Ready generation to relaunch. Use canonical lifecycle/turn-completion evidence rather than treating any past activity for the assignment as a permanent launch lock. Add a regression for observed Working -> yielded Ready -> next eligible heartbeat.
