# P2 — Claimed Work under a recovery gate is incorrectly classified Active

**Frozen SHA:** `2ade4efef0c9bae199d65dada75dbc306d7a65cc`

**Location:** `Client/Modules/WorkQueueToolFacade.fs:100-115` (especially lines 107-112); compare `Client/Modules/TaskFirstUiProjection.fs:285-288, 434-450`.

`classification` matches `WorkState.Claimed launch` before considering `blocked.Contains stage.Id`. If the launch has matching `PrimaryWorkId`, it returns `Category = "Active"` for *any* launch state and gate. In the canonical WQ09 projection, a claimed stage with a non-Clear gate appears in `Blocked` as a `RecoveryHold` (for `IntegrationGateHold`, `SourceRecoveryBlocked`, `ExternalEffectObservedPendingGate`, etc.). The new facade silently replaces that recovery classification with `Active`; similarly a `DemandInvalidatedPendingTerminal` or `TerminalPendingRelease` launch can be labeled Active from the matching ID alone.

**Repro:** Construct a covered WQ02 project with a claimed Work stage, a matching launch/PrimaryWorkId and `Gate = SourceRecoveryBlocked "unreconciled push"`. `projectDashboard` includes the Work row in `Blocked` with `RowState.RecoveryHold`, but `WorkQueueToolFacade.inspectAt` returns `Category="Active"` (its `Reasons` mentions the gate but the category is wrong). There is no negative-space test for claimed-but-held work.

**Impact:** `ListWork(category="Blocked")` omits a Work item that is explicitly blocked pending recovery; `ListWork(category="Active")` includes it despite terminal/stop or source-recovery uncertainty. This violates the promised coherent blocked/active view and can misdirect operators or future task-first consumers. The fact that claims are presently default-off does not remove the incorrect read-only status.

**Suggested correction:** Treat canonical WQ09 blocked/recovery holds with precedence over a plain claimed state. Preserve the distinction between `Claimed` and verified-live `Active` and surface stop/terminal/reconciliation uncertainty instead of inferring liveness from the primary Work link. Add fixture tests for non-Clear claimed gates, a stopped/terminal-pending launch, and the `Blocked`/`Active` filters.