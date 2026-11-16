# High — Claimed Work with no positive liveness evidence renders Active

**Location:** `Client/Modules/WorkDashboardView.fs:121-124` (frozen `681958aa9233d33ebec13d3b8ef8e15bbb1b3093`).

The `Active` branch treats every `row.Liveness` value *except* a string containing `UNKNOWN` as proof of `Active (observed)`. No positive session signal, bound launch, or verified session state is checked. For example, the test fixture's default `row "claim" "Active"` carries `Liveness = "Not claimed"` and both `ClaimLaunchId = None` and `SessionUuid = None`, yet `workStatus` returns `Active (observed)`. Other nonpositive values such as `Stopped`, `Timed out`, `Unbound`, or empty string also pass this test.

This violates the explicit fail-closed claimed-but-uncertain/unknown acceptance criterion: a claim state is not evidence of a live execution. It could mislead an operator even though the detailed pane says account admission and original effect are unknown.

**Required correction:** classify as active only from explicit positive, trusted liveness evidence in a typed/closed mapping aligned to the bound facade; every absent, stale, contradictory, unrecognized, or unverified value must be displayed as claimed/uncertain (not actively running). Add table-driven negative-path tests including an `Active` row with `Liveness="Not claimed"`, absent session IDs, and stale/stopped values.