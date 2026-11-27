# Full task coverage remains incomplete beyond the Stage A red/green slice

**Severity:** Medium — residual original-task coverage; do not claim overall task acceptance  
**Classification:** Uncovered original requirements, not an observed production defect

## Original requirement

The original task's test contract explicitly includes:
- quick, slow, terminal and unknown GetJobResult outcomes;
- AsyncLocal isolation across ingress requests;
- the explicit initiator/subject path and actor-private polling.

## Evidence in this submission

The only test file is tests/AdminActorSubjectIngress20261127/IngressTests.fs. Its runCase deliberately gates every original command before dispatch so executeOrPromoteWith promotes it to a ToolOperation. The test then observes Running and terminal ToolCompleted/ToolFailed results.

There is no test for the quick inline-response path, no process-job quick/slow/unknown status matrix, and no AsyncLocal/concurrent-request isolation exercise. A source search of the submitted test directory finds no AsyncLocal, RecoveryUnknown, Quick or Slow coverage. The current 22-case TRX therefore proves the registered-route actor/subject cases and their terminal ToolOperation controls, not this broader lifecycle/isolation matrix.

## Impact

This is not evidence that those production paths are defective. It is an evidence boundary: the Stage A fixture must not be presented as completing the full task's declared test contract or as proving request-context isolation and all GetJobResult states.

## Recommended handling

Keep this Stage A review narrowly scoped to the registered administrative ingress regression. Before overall task acceptance, add focused disposable tests for:
1. a command completing inline before promotion;
2. a delayed command promoted to a handle and polled by its initiating actor;
3. terminal and authoritative-unknown result behavior without retry/replay;
4. overlapping requests with distinct actor labels proving RequestDeviceRouting AsyncLocal context cannot leak across calls.

If Lead deliberately defers these beyond Stage A, record that deferral explicitly and keep the original acceptance items open.
