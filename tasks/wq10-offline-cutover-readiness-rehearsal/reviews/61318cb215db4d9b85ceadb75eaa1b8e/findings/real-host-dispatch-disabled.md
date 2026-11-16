# P1 — Real WQ08 host evidence can never yield PREPARED_FOR_REVIEW

**Frozen source:** `3bf142f22fc7a54b5e4c0b69e42f3b10e29f5376`.

**Location:** `tools/Wq10MigrationPreview/rehearsal.py:15,124-133`; WQ08 reference commit `f1ad0e89`, `Client/Modules/WorkQueueSchedulerHostPreview.fs:375-376`.

WQ08's real `previewWith` unconditionally appends `DispatchDisabled` because all host preview reports are non-dispatching. WQ10 only permits `AccountNotActivated` in `EXPECTED_PREVIEW_ISSUES`; any `DispatchDisabled` is turned into a blocking `SCHEDULER_PREVIEW_ISSUE`. Therefore a captured **genuine** WQ08 preview cannot produce the advertised green `PREPARED_FOR_REVIEW` rehearsal. The green test fixture fabricates the host-shaped issue list without the mandatory `DispatchDisabled` issue, and the PascalCase test only rewrites its casing.

This contradicts the deliverable's requirement to connect to the actual modern Scheduler preview while demonstrating a prepared case, and gives operators a green report only from evidence that a real WQ08 host cannot emit.

**Required fix:** Classify `DispatchDisabled` as a documented expected inert diagnostic only when `mode=ReadOnlyDryRun` and `canDispatch=false`; preserve blocking for all truly adverse issues. Add a regression/contract test that consumes output from the actual WQ08 host preview (or an exact source-backed contract fixture including mandatory issues). Keep `productionCutover=NOT_AUTHORIZED` regardless of rehearsal verdict.
