# P2 — Cancelled operator request remains counted and replyable in TUI

**Frozen source:** `f7c5598aed774712fbfa6edab901a194e6541023`.

## Reproduction
1. Registered Agent 3 creates a single-condition challenge with `OwnerKind="Operator"`, `OwnerAgentId=""`. The condition is `Pending`.
2. Agent 3 cancels the challenge using `CancelChallenge` at exact version 1. The durable record correctly becomes `ChallengeStatus="Cancelled"` while its condition retains `ConditionStatus="Pending"` for audit.
3. `ChallengeWorkflow.listOperator` (`Client/Modules/ChallengeWorkflow.fs:788-814`) still returns the row with those statuses, appropriately retaining the history.
4. `ConsoleTui.writeNavigation` (~211), `renderChallengesUnsafe` (~1224), and `beginChallengeReplyUnsafe` (~1213) use **only** `item.ConditionStatus = "Pending"`. The cancelled request is displayed as a pending operator action, inflates the Challenges count, and accepts Enter to start a reply.
5. Submitting that reply necessarily fails `StaleEntityVersion`: `ChallengeWorkflow.update` (~508) refuses any mutation when challenge status is `Cancelled`.

**Independent negative test:** Reviewer-only worktree `reviewer1-operator-cancelled-pending`, based on exact frozen source, adds `Client.Tests/ChallengeWorkflowTests.fs:1019` `CancelledOperatorInputMustNotAppearAsPendingActionInOperatorUi`. .NET 11 Release test job `f95646c7fe93471ebafa79209d8db71b`: **FAILED 1/1 as expected**, showing a `Cancelled` challenge with historical `Pending` condition returned by the exact UI count/reply predicate. Owner branch untouched.

## Required
Define a shared presentation/actionability predicate requiring **an active challenge** (`Pending` or `Blocked`) **and** a `Pending` operator condition. Apply consistently to counter, row badge, selection and reply eligibility. Keep cancelled challenges available for history but never show them as waiting on the operator. Add TUI-facing regression for cancelling a pending request. Do **not** rewrite historical pending condition metadata to hide this bug.

**Impact:** False urgent pending work, dead-end response action, and operator attention diverted to a request the agent explicitly withdrew.
