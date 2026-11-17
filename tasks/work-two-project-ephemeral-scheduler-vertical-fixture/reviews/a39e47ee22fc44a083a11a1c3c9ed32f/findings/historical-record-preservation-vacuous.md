## Major: historical Agent and review preservation is not proved

**Source:** `Client.Tests/WorkQueueTwoProjectEphemeralSchedulerVerticalTests.fs:56-118,529-534` at exact frozen `be31eb9e749948a2dea2e04b80f1c03b6828421a`.

The fixture initializes fresh disposable native ledgers with no existing `agents/*` and no historical review records; it then verifies `git ls-tree ... agents` stays empty. Absence of new Agent entities is useful, but the explicit requirement that *historic Agent/review records remain unchanged* is vacuous when none existed. It does not detect inadvertent mutation/deletion of a pre-existing record.

Seed representative historical Agent/review bytes in an original disposable project ledger before the native flow, snapshot their Git blob OIDs, and assert equality after four-path admission, claim/checkpoint, terminal release, unknown-send, and STOP. Keep this test-only; if immutable record coexistence is unsupported, state that as a typed NO-GO with the exact missing interface.