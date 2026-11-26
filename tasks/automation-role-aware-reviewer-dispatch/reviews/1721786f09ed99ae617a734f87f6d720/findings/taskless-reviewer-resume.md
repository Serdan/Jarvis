# Taskless reviewers cannot resume a claimed review after yielding

**Severity: High**

## Contract

The task requires a reviewer with a runnable claimed review to resume it, and permits reviewer dispatch without a standing assignment.

## Evidence

- The taskless lifecycle is explicit: `YieldTasklessWork` changes an unassigned Working agent to `Parked` (ReviewWorkflow.fs lines 2763–2780). `StartTasklessWork` can start an idle unassigned agent from either `Parked` or `NeedsWork` (lines 2740–2761).
- The planner's `REVIEWABLE_AGENT_STATES` contains only `Ready`, `NeedsWork`, and `Free` (scripts/manager-heartbeat.mjs line 20). Its claimed-review resume path requires one of those states and execution `Stopped`; `Parked` is rejected as `NoRunnableClaimedReview`.
- The Manager instructions do not unpark a reviewer before dispatch or provide any distinction between an operator-parked reviewer and one parked by the required taskless yield. The role's description of an available taskless reviewer as `NeedsWork`/`Stopped` (Manager.role.md lines 37–38) is not true after `YieldTasklessWork`.
- I reproduced the planner behavior with a canonical-shaped snapshot containing a Reviewer in `Parked`/`Stopped` and a `Claimed` review owned by that Reviewer: no reservation is produced; the reviewer is suppressed as `NoRunnableClaimedReview` and `ClaimedReviewAlreadyOwned`.

## Impact

If a taskless reviewer reaches a session boundary while its claim remains open, the required yield operation parks it. The next Manager pass cannot resume that claimed review. The same reviewer also remains ineligible for subsequent unclaimed demand. Without manual unpark intervention, taskless reviewer automation stops making progress after a yield.

## Required correction

Define and implement the authorized lifecycle for a taskless reviewer that has yielded with an outstanding claim. The scheduler must be able to resume that exact claimed review without overriding an intentional Operator park; distinguish scheduler-yielded taskless sessions from manually parked agents, or introduce an equivalent durable disposition. Add tests for a claimed review after `YieldTasklessWork`, plus reuse after a completed taskless review. Simply adding `Parked` to the eligible-state set is unsafe because it would erase the existing meaning of Parked.
