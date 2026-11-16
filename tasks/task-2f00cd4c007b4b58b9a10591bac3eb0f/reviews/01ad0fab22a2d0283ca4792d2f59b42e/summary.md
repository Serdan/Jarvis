# Reviewer 3 — atomic register_agent role/task setup

**Verdict: Accepted** for frozen commit `01ad0fab22a2d0283ca4792d2f59b42e2ba8c9e4`, review `01ad0fab22a2d0283ca4792d2f59b42e`, base `b277cd0d1b850b97ad81d889e2278aaef74e74ae`.

## Findings

No blocking source-level defect identified. The new `register_agent` optional `roleId` / `taskId` path builds an agent registration, optional versioned instruction role membership and optional queued task/issued generation inside one Git-ledger transaction. Task/spec/role prerequisites are read at the same pinned snapshot; missing, invalid, duplicate, already-owned or busy conditions abort without committing partial state. The new assignment path shares its validation and Ready-state transition with standalone `assign_task`, rather than copying divergent logic.

- Bare registration retains the legacy request identity and Stopped/Parked semantics. Role-only registration remains Parked; task-only and combined registration become Ready with a pinned queued attempt, without starting execution or launching a browser.
- Deterministic attempt ID from the operation UUID, agent and task gives a stable retry receipt. The additional pending-intent identity pairs include optional role/task fields when either side supplies them, preventing unsafe cross-setup receipt matches. Operation ID reuse with changed setup is rejected by the canonical transaction.
- Public MCP API appends optional arguments without displacing legacy operationId/deviceId positions. Registration response includes the role/task setup IDs and derived attempt ID. Existing attach_role/assign_task remain available.

## Independent evidence

- Inspected all six changed files and the complete new registration test class at the frozen SHA.
- `git diff --check b277cd0d..01ad0fab`: exit 0. Worker 11 checkout HEAD matches the frozen SHA, with clean Git status after tests.
- Independent Linux .NET 11 Release hermetic command:
  `dotnet fsi scripts/test-client-hermetic.fsx -- --timeout-seconds 180 --no-build --filter 'FullyQualifiedName~AgentRegistrationSetupTests|FullyQualifiedName~AdministrativeAssignedReadyAndAcknowledgedStateMatrix|FullyQualifiedName~IndependentConcurrentAssignmentsRetainSeparateGenerations'`
  **exit 0, 6/6 passed**, no timeout; hermetic disposable environment removed. Covers bare/role/task/combined setups, stale and corrupt prerequisites, idempotent replay, changed-argument rejection, conflicting concurrent task claims, and two existing assignment state/concurrency regressions.
- An initial command mistakenly passed `--scratch-parent /tmp` (supported on a newer WQ11 harness, not on this frozen branch); MSBuild rejected that argument before running tests, exit 1, and the private scratch was removed. The corrected invocation above is the valid test evidence.
- Owner additionally reports a .NET 11 Release build pass and 13 focused role/registration tests; not independently reproduced here. The owner's 120-second complete ReviewWorkflowTests run exceeded its cap and is **not** reported as a full-suite pass.

This decision approves scoped source review, not deployment. It does not claim the unrun complete suite or an end-to-end server-deployed MCP schema test. No source edits, main integration or production activity performed.
