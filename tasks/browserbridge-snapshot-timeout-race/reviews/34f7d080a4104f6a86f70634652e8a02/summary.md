# BrowserBridge snapshot deadline classification (Worker 10)

## Observed and root cause
The task records a .NET 11 Release failure on 2026-11-15 in `BrowserBridgeTests.snapshotExportDeadlineStopsSlowChunksWithoutPublishing`: expected `SnapshotDeadlineExceeded`, observed `SnapshotTransportTimeout` after about 499 ms for a 400 ms caller budget. One isolated baseline Release execution passed here; I did **not** reproduce the original failure deterministically.

`Client/Modules/BrowserBridge.fs:249-265` originally used `pending.Task.WaitAsync(timeoutMs)` and mapped its timer expiry to `SnapshotTransportTimeout`. `CaptureDom` supplied that wait with the caller's *remaining* budget (initial capture ~line 562, chunk transfer ~line 627) and then checked `remainingMs() <= 0` **after** the exception to decide whether to relabel it `SnapshotDeadlineExceeded`. Timer callback/stopwatch sample scheduling near zero therefore determined the public error even though the initial wait had no independent transport budget. This is a timeout-classification defect in the bridge, not evidence that the unrelated JobManager test probe caused it.

## Minimal correction
Commit `c1ac0d00ac5fd7add442d8ed16dafa0ad8ec3d04`, branch `refs/heads/fix/browserbridge-snapshot-deadline-worker10`:

- `requestSnapshotFrame` accepts the semantic timeout error selected **before** starting the timed wait.
- Initial capture uses `SnapshotDeadlineExceeded`; chunk waits use it when the overall caller deadline is the limiting bound (`remaining <= 10000`). An independent 10-second transport cap remains `SnapshotTransportTimeout` when shorter than caller time left. At equality, caller deadline wins.
- `Client.Tests/BrowserBridgeTests.fs` adds `snapshotCaptureDeadlineClassifiesUnansweredRequest` (silent extension); the existing slow chunk/late reply test remains unchanged. All checksum, permission, authorization, temporary-file cleanup, and pre-publication deadline gates remain intact.

## Validation
- Baseline focused Release run (before patch): 1/1 passed; intermittent fault not reproduced in isolation.
- Patched .NET 11 Release focused tests (new silent-capture and existing slow-chunk test): 2/2 in initial build, then 4 further runs of 2/2 = **10/10 test executions**.
- Patched BrowserBridge Release suite: **45/45 passed**.
- Baseline full Client.Tests Release run with 240-second ceiling timed out; output showed unrelated `ActivityLog.fs` activity-persistence `InvalidDataException`/`UnauthorizedAccessException` diagnostics. Full patched suite is separately running; its status must be recorded without attributing unrelated failures to BrowserBridge.

No integration, deployment, or restart performed. Review this exact frozen commit; integration belongs to Lead only after an Accepted verdict.
