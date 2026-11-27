# Preserve actor-private GetJobResult authorization

**Severity:** High — blocks this review  
**Classification:** Incorrect test expectation; conflicts with an explicit original requirement

## Reproduction

tests/AdminActorSubjectIngress20261127/IngressTests.fs:299-308 creates a synthetic ToolOperation under SyntheticInitiator and polls it with SyntheticUnrelated. The ReadOverride case at lines 303-304 expects that cross-label poll to succeed. The immutable TRX records the resulting failure:

- Test: project_scoped_job_read_must_not_require_owner_label
- Actual response: ValidationFailed: Tool job belongs to another initiating agent.
- The test expected otherDenied = false, so this is a false-positive RED, not a product defect.

The ordinary control path already observes the same denial and logs it at lines 305-308, but does not assert it.

## Requirement and source evidence

The original task explicitly requires unrelated/wrong-project reads to be denied and says “no weakening of actor-private GetJobResult.”

The frozen source implements that contract:
- Client/Modules/JobManager.fs:525-533 (ToolOperations.authorize) rejects a ToolOperation when the requesting actor label differs from entry.Handle.AgentId.
- Client/Modules/JobManager.fs:54-59 (Core.authorized) applies the same actor-private check to process jobs.
- getJobResultForProjectAsAgent at lines 688-697 passes the actor through to those checks.

There is no project-scoped operator override in this path. The test uses an unrelated agent label, not a separately authorized override identity.

The qualification note docs/reviews/admin-actor-subject-ingress-20261127/qualification-scope.md also describes this as an “independent project-scoped job-read expectation” and says an Operator override permits it. That claim is unsupported by the original task and contradicts the frozen authorization implementation.

## Impact

This test adds a spurious RED and misclassifies correct privacy enforcement as a defect. It risks expanding the correction into a weakening of the very job-ownership boundary the task requires preserving.

## Required correction

1. Remove the ReadOverride test/branch and the unsupported override claim from the qualification note.
2. In the ordinary control path, assert that polling with SyntheticUnrelated is denied, with the expected actor-private error.
3. Keep the existing wrong-project, wrong-principal and cancellation-denial assertions.
4. Do not change production GetJobResult authorization to satisfy this test.

The seven legacy-route controls and the seven explicit-shape RED cases are otherwise distinct from this invalid expectation.