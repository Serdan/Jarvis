# Taskless reviewer launches never start taskless execution

**Severity: High**

## Contract

The task spec explicitly permits reviewer dispatch without standing reviewer assignments. The new Manager instructions identify such a reviewer as `NeedsWork` + `Stopped` and make it eligible for an unclaimed review (Manager.role.md lines 37–38, 69–72).

## Evidence

- The generated Reviewer launch prompt only says to read `reviewer_inbox`, claim eligible frozen work, and act after a successful claim. It never instructs the reviewer to call `start_taskless_work`.
- `StartTasklessWork` is an explicit, self-attributed lifecycle transition. Its tool contract says an idle agent's inbox does not start it. The implementation accepts only an unassigned `Parked`/`NeedsWork` agent at the exact version and transitions it to `Working` (ReviewWorkflow.fs lines 2740–2761).
- The automatic inbox activation path only transitions `Ready` agents with a non-null assignment (Client/SignalR/Client.fs lines 419–421). It does not activate an unassigned `NeedsWork` reviewer.
- Claiming a review only changes the review record's status, reviewer, and version; it does not change the agent's execution state (ReviewWorkflow.fs lines 4585–4591).
- The existing regression test explicitly verifies that taskless `AgentInbox` does not activate the agent and that `StartTasklessWork` is required (Client.Tests/ReviewWorkflowTests.fs, `TasklessSignalRRequiresExactActorAndExplicitStartYieldWithoutInboxActivation`, lines 3751–3815).

## Reproduction

Using the planner with one taskless reviewer and one review:
1. The first tick reserves the unclaimed review for the reviewer.
2. The reviewer claims it and produces attributed tool activity, but—without `StartTasklessWork`—its canonical state remains `NeedsWork`/`Stopped`.
3. The first tick observing that activity is suppressed, but the following tick emits a new `ClaimedReviewResume` reservation for the same reviewer and review.

I reproduced this sequence directly against the frozen `decide` implementation. The observed result sequence was `UnclaimedFrozenReview`, no launch, no launch, then `ClaimedReviewResume`.

## Impact

The taskless session is invisible to the scheduler's active-agent/capacity accounting. Successive ticks can launch duplicate sessions for the same claimed review, violating the once-only reservation and configured reviewer-capacity guarantees. Concurrent sessions can race to write review documents or decide the same claim.

## Required correction

Wire the taskless lifecycle into the actual Reviewer launch instructions: the reviewer must read its own inbox to obtain the exact current agent version, call `start_taskless_work` as itself before doing review work, then inspect/resume/claim the review. Add a regression covering the full launch/claim/activity sequence across successive planner ticks and prove that the reviewer is counted as Working until it yields. Do not have Manager impersonate the reviewer; the taskless start API is actor-bound.
