## Moderate: stale-client negative cannot distinguish CAS conflict from preflight failures

**Source:** `Client.Tests/WorkQueueTwoProjectEphemeralSchedulerVerticalTests.fs:440-450` at `be31eb9e749948a2dea2e04b80f1c03b6828421a`.

The second clone's `transitionDisposable` is declared the stale-claim race if it returns **any** `Error _`. This can succeed for an unrelated session/host/evidence failure, without ever reaching the original project Git version-CAS check. The later idempotent same-operation replay does not identify the rejected contender's failure cause.

Assert the specific expected stale-version or original CAS conflict and verify no additional claim/receipt/credential effect was created; keep non-CAS negative proofs separately classified. This is particularly important because the fixture advertises two-clone CAS evidence.