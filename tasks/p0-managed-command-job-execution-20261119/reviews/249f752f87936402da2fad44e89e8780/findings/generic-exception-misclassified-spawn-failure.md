# P1 integration blocker: generic ExceptionError can conceal an already-started process

**Review:** `249f752f87936402da2fad44e89e8780` of exact `249f752f87936402da2fad44e89e878064140c9f`.

## Source proof

- `Client/Modules/JobManager.fs:859-860` maps *every* `Client.ExceptionError` received from `receiveCommand` to `CommandSpawnFailed` (a definite failure without an `OutcomeUnknown` marker).
- The actual production `Program.fs:209-212` calls `executeScheduledRunCommand` with `Client.receiveCommand`. The RunCommand dispatcher `Client/SignalR/Client.fs:856-857` calls `JobManager.runManagedCommandIn` and then `serialize'`; `Client/SignalR/Client.fs:295-315` catches a failure while serializing a successful result and returns `Client.ExceptionError`. Serialization happens **after** `runManagedCommandIn` can have admitted an OS process. Consequently a post-admission response-serialization failure can be labeled `CommandSpawnFailed` even though the process may have executed.
- The correction does correctly type `ProcessOutcomeUnknown` inside `startProcess` and on a missing registered job; this finding targets the *remaining untyped generic exception boundary*, not those fixed paths.
- `Client.Tests/ScheduledCommandConsumerTests.fs:172-191` explicitly asserts a generic `ExceptionError` maps to a definite spawn failure. It never proves that this error type can only originate before process admission.
- `Client/Modules/BrowserSchedules.fs:709-715` recognizes uncertainty by `OutcomeUnknown` in the reason. Without that marker it persists `failed`; the recurring branch at lines 244-252 can re-arm a failure. This incorrectly permits another execution when the first one was uncertain. The separately owned Worker 18 correction can fence *correctly classified* `outcomeUnknown` but cannot repair this mislabeled input.

## Required correction

Use an explicit typed, provenance-supported **pre-start** spawn failure (e.g. `ProcessSpawnFailed`) instead of equating all `ExceptionError` values with failure to start. Treat unproven exception origins, especially post-start dispatch/serialization failures, as `ProcessOutcomeUnknown` with a stable marker and no automatic replay. Add a deterministic regression injecting a failure after an admitted command and demonstrate exact consumer -> schedule `outcomeUnknown`; retain tests for verified pre-start validation/permission/spawn denials. Do not modify Worker 18's BrowserSchedules lease.

**Evidence limit:** Source-traced reachable conditional path; not a claim of an observed production serialization failure. Existing Release suites passing does not refute this untested safety classification.