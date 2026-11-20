# P2 — add regression coverage for mutex acquisition timeout

**Location:** `Client/Modules/OrchestrationApi.fs:674-680`; test coverage in `Client.Tests/ReviewWorkflowTests.fs`.

The implementation uses `WaitOne(TimeSpan.FromSeconds(60.))` and returns a timeout validation error when the wait returns `false`. That is the correct bounded-wait shape, but no test in `Client.Tests` exercises this branch. The current named-mutex tests cover same-UUID blocking, unrelated-UUID concurrency, abandoned-owner recovery, absence of lock artifacts, and single callback execution after an IOException; none holds the mutex through the timeout.

The canonical task specification explicitly requires testing timeout when the lock cannot be acquired. Without this test, a future change could accidentally turn the wait into an unbounded block or mishandle the `false` result without failing the targeted suite.

**Requested change:** add a deterministic timeout test. Prefer an internal test seam that allows a short timeout while keeping the production default at 60 seconds. Have an independent process own the exact same binding/UUID mutex; invoke the pending operation and assert that it returns the timeout error, does not invoke the callback, does not persist a pending JSON record, and releases/disposes its local mutex handle. Then release the holder and verify a subsequent acquisition succeeds. Do not add compatibility, cleanup, migration, or lock-file machinery.

This is a test-coverage finding only; source inspection shows the production timeout branch is implemented directly and correctly.