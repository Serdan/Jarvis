# Independent review — ChangesRequested

## Scope and provenance

Reviewed only the exact frozen Stage A submission:
- source commit: 2bb370139db1be3eb3edcb0c9aa5be15e8662805
- pinned base: cba405f32ecbf27161d7838fff9cebb69ffc79f1
- review request: bf6383d055c9482b9f4e6297d813d64b

The source worktree is clean and matches the frozen commit. The patch adds only three new files (the standalone test project, its ingress tests, and the qualification note); no application source, existing project file, shared test or live-agent state is changed. git diff --check passes.

## Fixture fidelity

The fixture is credible for the narrow registered-ingress question. It starts the real Server Program configuration and registered MCP endpoint, authenticates synthetic HTTP calls, establishes a real registered SignalR client, captures the command delivered to that client, and invokes the actual authenticated client dispatch/promotion path. It uses disposable Git ledger clones and a disposable OptionalFeedbackStore. The subject activity watermark is seeded and read back through the authenticated Hub rather than inferred from missing cache data. The tests do not replay the live W12 operation or query a live actor's private job.

## Independently verified test evidence

The recorded TRX SHA-256 was independently verified against the file:
e3735f3fe9212938a57822b3e9e65edc6513f62304b8402dfc8f0b8cc5023877

Counters: 22 total/executed, 7 passed, 15 failed, zero errors, timeouts or aborted tests.

The seven passing registered-route controls cover all seven action shapes (RegisterAgent, AssignTask, ReissueAssignment, AdoptTaskSpec, ReissueQueuedAttempt, RecoverAgentAssignment and TransferOwner). Five are successful controls; AdoptTaskSpec and ReissueQueuedAttempt are deliberate invalid-state controls. The latter assert exact StaleEntityVersion and AgentBusy errors, matching terminal ToolFailed responses, no applied receipt, unchanged canonical snapshot, and no additional alert/activity effect after the pre-dispatch baseline. Duplicate operation IDs return the same pending handle; changed canonical requests are rejected on the mutable routes; the fixture verifies one dispatch.

The fourteen intended RED cases are seven legacy subject-as-transport-actor failures and seven explicit initiator/subject wire mismatches. The captured logs support the attribution defect: legacy calls label the transport and promoted job with SyntheticSubject, update its activity, and expose its seeded alert; the explicit AssignTask sample labels the job with SyntheticInitiator but serializes OwnerId as SyntheticInitiator instead of the expected SyntheticSubject. These are valid red-first reproductions on this frozen baseline.

One of the fifteen failures is not valid evidence: project_scoped_job_read_must_not_require_owner_label. It expects cross-label GetJobResult access, but the source correctly denies it. See findings/get-job-result-privacy.md.

## Findings

1. High — findings/get-job-result-privacy.md: remove the unsupported project-read override expectation and assert the unrelated-label denial. The original task explicitly preserves actor-private GetJobResult; do not weaken production authorization.
2. High — findings/explicit-negative-controls.md: the explicit branch unconditionally expects ToolCompleted for AdoptTaskSpec and ReissueQueuedAttempt, even though those same fixtures are deliberately invalid and must remain StaleEntityVersion and AgentBusy. After the actor/subject fix, the suite would still fail. Preserve exact terminal negative assertions and no-mutation checks in explicit mode.
3. Medium — findings/explicit-target-state.md: the explicit branch does not verify the post-terminal canonical ledger effect (for example, AssignTask owner/Ready state and unchanged initiator state) or an explicit successful receipt. The legacy control's state assertion does not prove the corrected explicit path.
4. Medium residual — findings/remaining-coverage.md: the broader original task's quick/slow/terminal/unknown GetJobResult matrix and AsyncLocal isolation are not exercised by this Stage A project. This is not a production defect finding, but those obligations remain unqualified and must not be reported as completed by this submission.

## Decision

ChangesRequested for this exact source SHA. The disposable fixture and the core legacy/explicit defect reproductions are useful and well-founded, and the two invalid-state control paths have good terminal evidence. However, the false privacy RED and the explicit-path negative expectations prevent this test project from serving as a reliable future-green regression gate. Add the missing explicit target-only postconditions before treating the Stage A contract as complete. Keep the broader lifecycle/isolation items open or explicitly deferred; this review is not production correction, integration acceptance, deployment approval or completion of the original task.
