# P1 — A different launch can discharge the captured in-flight launch fence

**Exact frozen source:** `46b7c9da90d755a8c7f1f85c4ca16b7eeb9838e1`  
**Location:** `Server/WorkRecovery.fs:160-169,421-463,643-692`; existing coverage `Server.Tests/WorkRecoveryTests.fs:204-225`.

## Finding

`RecoveryWorkSnapshot.CapturedInFlightLaunch` is only a `bool`. In `effectAssessment`, a captured launch is considered accounted for when **any** `ExternalEffect` row has `Kind=Launch` and the same `WorkId` and `Generation` (lines 434-437). The row's `OperationId` is never tied to the actual launch that was in flight at the snapshot. A canonical terminal observation or definitive-no-effect proof for an unrelated launch therefore removes the `InFlightLaunchReconciliation` requirement, even though the captured launch remains unsettled.

Concrete fail-open state permitted by the current types:

1. Snapshot for `W`, generation 2, records `CapturedInFlightLaunch=true` for launch `L-active`; its original job may still be live. The snapshot has no field carrying `L-active`'s operation/session identity.
2. Recovery has a **genuinely verified** terminal/no-effect journal for an *earlier/different* operation `L-other`, with `WorkId=W`, `Generation=2`, `Kind=Launch`; `L-active` has no terminal proof. Project/account gap seals and STOP clearance can independently be valid; those seals do not identify the launch captured in the snapshot.
3. Supply `Effects=[L-other]` and `Goal=Unchanged`. The existence test discharges the captured-launch fence; the outcome loop accepts `L-other`'s own terminal proof. The model emits only `RecoveryAuthorizationRequired`. A canonically issued authorization matching the resulting `EvidenceDigest` then yields `ReleaseAuthorized=true` and `NormalDispatchAllowed=true`, despite `L-active` still being unknown.

This is a safety property failure at the G0 pure decision boundary, not a claim of observed production behavior. Matching *Work and generation* is insufficient to prove the original external launch is terminal when retries, multiple operations or a substituted journal can exist. A digest faithfully binding the **wrong** launch evidence does not fix the missing identity.

## Required correction

Capture exact immutable original launch operation/session identity or a set of captured launch IDs (including Work/generation) in the recovery input. Require reconciled, canonical terminal/no-effect evidence for **each captured identity**, never merely any matching launch row. If original identity is unknown, continue to emit `InFlightLaunchReconciliation`; never authorize normal dispatch. Add negative tests with `L-other` terminal and `L-active` unresolved in the same Work/generation, as well as a positive matching-original-terminal test. Keep G0 default-off; no G1 integration or live Scheduler work.

## Independent review evidence

The prior two P1 corrections (generation/role/graph proof lineage and content-addressed release evidence) are substantively implemented. Frozen worktree was clean; four files within G0 scope, `git diff --check` passed. Independent `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore` on .NET 11 Release: **321 passed, 0 failed, 0 skipped**. The current tests exercise a single launch journal but not an unrelated terminal launch paired with a captured unresolved one.
