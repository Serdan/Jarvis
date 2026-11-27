# Explicit-shape branch incorrectly expects invalid-state controls to succeed

**Severity:** High — blocks a future-green Stage A regression suite  
**Classification:** Incorrect future expectation; contradicts the task's explicit negative-control boundary

## Reproduction

In tests/AdminActorSubjectIngress20261127/IngressTests.fs:
- AdoptTaskSpec sets Scenario.ShouldApply = false at lines 88-102 and the control path expects exact StaleEntityVersion at lines 383-401.
- ReissueQueuedAttempt sets Scenario.ShouldApply = false at lines 103-110 and the control path expects exact AgentBusy at lines 383-401.
- The explicit initiator/subject branch at lines 436-445 unconditionally calls terminal ToolCompleted at line 441 for every action.

The explicit mode uses the same prepared ledger states and scenario arguments; only the initiator/subject labels change. It does not revise the canonical spec for AdoptTaskSpec or make the Ready owner idle for ReissueQueuedAttempt. Those two preconditions therefore remain invalid after the actor/subject ingress is corrected.

## Requirement

The Stage A qualification explicitly says no successful adoption or idle-owner reissue is inferred from negative controls. The original task requires preserving the underlying ledger validation and typed failure semantics while separating transport initiator from administrative subject.

## Impact

Once the explicit route correctly preserves the subject and the captured wire matches the expected canonical command, these two tests will proceed to line 441 and reject the correct ToolFailed terminal result because they demand ToolCompleted. The suite cannot become green under the correct implementation. Moreover, invalidControl is only enabled when mode = control, so the explicit negative cases do not capture a rejection baseline or assert no receipt, no canonical mutation, and no additional alert/activity effect.

## Required correction

Use scenario.ShouldApply in the explicit branch too:
- successful scenarios must require ToolCompleted, successful ToolResponse and the expected canonical receipt;
- AdoptTaskSpec must require the exact StaleEntityVersion response;
- ReissueQueuedAttempt must require the exact AgentBusy response.

For both explicit negative cases, capture the pre-dispatch canonical snapshot, subject alerts and activity watermark, then require terminal ToolFailed with the identical ToolResponse, no applied receipt, and unchanged snapshot/alerts/activity. Do not turn either negative control into a successful adoption or idle-owner reissue.
