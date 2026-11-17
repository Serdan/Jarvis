## Major: competing WQ07 account-CAS race is not exercised

**Source:** `Client.Tests/WorkQueueTwoProjectEphemeralSchedulerVerticalTests.fs:391-410,440-450` at exact frozen `be31eb9e749948a2dea2e04b80f1c03b6828421a`.

The positive test admits Project B first and only *after* its token is Held invokes Project A's host, a second `prepareExplicit`, and duplicate credential preparation. This verifies serial exclusion after a committed reservation, not the specified two-clone competing admission at the same original account capacity-one head. The only stale-client transition is a WQ02 same-project `Claim`, not a WQ07 cross-project reservation race.

Add a deterministic two-client/one-account original CAS contention case, using independently fetched pre-admission evidence for competing eligible projects or same original operation, and prove one winner, one stale/losing original WQ07 outcome, no extra WQ02 four-path credential/host send, and convergence of both project/account readers. If this cannot be composed through an accepted interface, return and assert the corresponding typed default-off NO-GO instead of presenting sequential exclusion as a race proof.