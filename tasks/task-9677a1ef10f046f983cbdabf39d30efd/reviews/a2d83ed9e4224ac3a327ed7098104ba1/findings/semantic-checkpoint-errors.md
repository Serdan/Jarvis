# P2 — Checkpoint semantic failures still collapse into a generic error

Frozen source: `1c1be4ecc2eb1c85081328297a02c0795b7c45f1`.

## Acceptance requirement

The task explicitly requests field-specific errors for summary, nextAction, evidence size, source SHA, **invalid versions, and ownership/phase**. It also requires stale assignment generation coverage.

## Evidence

`Client/Modules/ReviewWorkflow.fs:1511–1530` adds clear lexical/length diagnostics, including for *negative* expected versions and generation. However `checkpointAttemptWithSession` lines 1548–1554 still OR together seven distinct canonical preconditions and return just:

`StaleEntityVersion "Checkpoint requires exact current Working versions and generation."`

This is returned for lifecycle not InProgress, attempt owned by another agent, attempt phase not Started, expectedAttemptVersion mismatch, assignmentGeneration mismatch, expectedAgentVersion mismatch, agent not Working, and wrong ActiveAttemptId. These cases have different actionable remediations, but callers cannot identify which field/state failed. The new regression only asserts a generic stale error for wrong generation; it does not assert field-specific diagnostics for mismatched versions, owner, or phase.

## Required change

Within the existing single verified ledger transaction, branch on semantic predicates separately (e.g. `task.lifecycle`, `attempt.ownerAgentId`, `attempt.phase`, `expectedAttemptVersion`, `assignmentGeneration`, `expectedAgentVersion`, `agent.state`, `agent.activeAttemptId`) and return precise, safely scoped `StaleEntityVersion` diagnostics. Preserve atomicity, idempotent operation IDs and the preexisting ownership guards. Add regression assertions for stale version/generation and wrong owner/phase, not just negative numeric inputs.

Severity: **P2 acceptance gap**, not a regression in the new unbound evidence happy path.
