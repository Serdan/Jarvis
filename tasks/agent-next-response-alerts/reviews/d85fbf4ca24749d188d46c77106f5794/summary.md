# Independent frozen review — Changes requested

Snapshot: `3e55b04161acc216131d6d56aad09c32eb3a0195` (base `a709027e9cf97d0dde0518887e6bd61a4c77800b`).

The follow-up change correctly preserves **newly consumed poll-time** response alerts alongside a completed tool job's original mutation/activation evidence. Focused frozen-branch checks passed: Server.Tests 2/2 (`polledToolJobsRestoreExactInstructionsAndMutationEvidence`, `pollResponseAlertsRemainVisibleOnPendingAndErroredJobLookups`); Client.Tests ResponseAlert 5/5, both Release .NET 11.

**Blocking P1 finding:** An alert consumed during background completion of a promoted tool job is stored in the replayable original `ToolResponse.Instructions`. Every subsequent `get_job_result` poll reattaches the same STOP, violating one-shot delivery; an unpolled completed job can also strand the consumed STOP instead of showing it on the next unrelated agent response. See `findings/promoted-job-alert-replay.md` for exact paths, deterministic sequence, and required regression coverage.

Resolve the delivery boundary before accepting the feature. Do not alter or lose canonical job error/mutation evidence or the newly fixed poll-time instruction path.