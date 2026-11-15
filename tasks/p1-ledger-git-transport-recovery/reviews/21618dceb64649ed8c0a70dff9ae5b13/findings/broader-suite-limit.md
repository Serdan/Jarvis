# Independent broader Client.Tests Release run — no full-suite pass

**Exact frozen commit:** `5a24d3da479a2a934e3898e7add99858e394e565`; no source edits.

Command: `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --logger 'console;verbosity=minimal'` in the registered `p1-ledger-transport-worker6` worktree, .NET SDK 11.0.100, local Jarvis sandbox only.

The run returned **ExitCode=-1, TimedOut=true** at its bounded 180-second timeout. Console output also reported a failing *unmodified* `BrowserBridgeTests.snapshotExportDeadlineStopsSlowChunksWithoutPublishing` (`Client.Tests/BrowserBridgeTests.fs:797`): expected `SnapshotDeadlineExceeded`, observed `SnapshotTransportTimeout`; additional activity-persistence permission/invalid-data warnings appeared. This is not evidence that the P1 transport changes caused the unrelated browser test failure; it is evidence that a full-suite pass is **not established**. Do not claim complete Client.Tests validation. The seven focused new Release fault-injection tests **did** pass independently, as recorded in summary.md.

This note supplements, but does not change, the **ChangesRequested seq3260** verdict, which is based on the independently traced P1 pre-journal public API counterexample and P2 diagnostic ambiguity.
