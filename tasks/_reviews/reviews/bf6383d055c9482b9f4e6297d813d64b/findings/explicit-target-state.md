# Assert explicit-path ledger effects, not only the captured wire

**Severity:** Medium — required Stage A regression assertion gap  
**Classification:** Incomplete future-success assertions; no production defect inferred from this test-only commit

## Location

tests/AdminActorSubjectIngress20261127/IngressTests.fs:436-445. The legacy AssignTask control checks owner and Ready state at lines 409-414, but the explicit initiator/subject branch has no corresponding ledger-state assertion.

## Requirement

The original task requires the explicit initiator to own/poll the promoted job while the administrative subject remains the target; it specifically calls for proving that the target transitions to Ready only, without target last-seen or alert consumption. The Stage A scope also calls out target-subject job attribution.

## Evidence and gap

The explicit branch currently checks:
- captured transport actor equals SyntheticInitiator;
- promoted job AgentId equals SyntheticInitiator;
- captured TaskWriteCommand JSON equals the expected canonical bytes;
- the seeded subject activity watermark is unchanged;
- the job eventually reports ToolCompleted and has a ToolResponse;
- the subject alert remains pending.

It does not inspect the post-terminal canonical ledger snapshot. In particular, the explicit AssignTask case never asserts that the task owner is SyntheticSubject, that SyntheticSubject is Ready, or that SyntheticInitiator remains in its pre-call state. The only owner/Ready assertion is in the legacy control branch, which exercises the old subject-only ingress shape.

The explicit branch also does not assert a successful canonical receipt or explicitly assert ToolResponse.Error is empty and ToolResponse.Result is present. It checks only that ToolResponse is Some after the job reaches ToolCompleted.

## Impact

A future route change could preserve the expected serialized request and caller job label yet apply the wrong ledger transition, mutate the initiator, or return a non-committing response. This test would not detect those outcomes. The current RED wire mismatch still reproduces the actor/subject conflation, but it does not qualify the corrected path's target-only state effect.

## Recommended assertion

After the explicit job reaches terminal state, inspect the verified disposable ledger snapshot and operation receipt. For AssignTask, assert the task is owned by SyntheticSubject, that subject is Ready, and SyntheticInitiator's state/assignment is unchanged. For RegisterAgent, assert the subject was registered and the initiator's existing record was not replaced or mutated. Add equivalent narrowly scoped postconditions for the other successful administrative actions where their state effect is part of the contract. Also assert the explicit ToolResponse is successful and its canonical receipt matches the operation.

Keep the existing activity-watermark and pending-alert assertions; they cover separate side effects.
