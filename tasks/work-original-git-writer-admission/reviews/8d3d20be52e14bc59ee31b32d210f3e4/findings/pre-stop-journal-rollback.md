# P1 — pre-STOP journal rollback erases admitted/UNKNOWN remote effects and fabricates a drained witness

**Frozen source:** `10df861cf096cf5b4733fb1178ebe5000ccb697b` (review `8d3d20be52e14bc59ee31b32d210f3e4`, base `35f675cc1564710d10fdb2f50a03edff39368a28`).

## Source-established failure

`Client/Modules/OriginalGitWriterAdmissionJournal.fs` creates independent host state with `Open, epoch=1, highWater=0` at first activation (line 373). `admit` inserts a durable `effects` row (lines 400–455), and `settle` updates only the same SQLite database (lines 472–487). **Neither operation advances or seals the independent host high-water or unresolved-effect identity.** The first high-water update occurs *only at STOP* (lines 536–549), where STOP computes `SELECT MAX(sequence)` from whichever SQLite contents currently occupy the path and then writes that observed value to the independent state. `inspect` subsequently accepts that same value (lines 490–535).

Consequently a stale same-identity SQLite restore **before STOP**, rather than the post-STOP restore covered by the new tests, can erase an admitted effect without any host-state mismatch. Precise reproduction sequence:

1. Provision journal A and activate its host binding, then take a copy of A while it is open with no effect rows (same `journal_id`). Independent host state remains `Open / 1 / 0`.
2. Through `withPublication`, actually push a new project/account or immutable source ref to a disposable bare Git remote and intentionally lose confirmation. The operation is recorded as `Unknown` in A; the remote effect exists.
3. With all SQLite connections closed, replace A with the saved pre-admission database at the same path, retaining the binding files and independent host-state file. UUID, epoch and open state still match; its independent high-water was never advanced by the lost effect.
4. Call `stop A`. It reads the restored SQLite's old `MAX(sequence)` (e.g. 0), writes `Stopped / 1 / 0` to host state, closes the SQLite control row, and `inspect` reports `PendingOrUnknown=[]`, `AllRecordedEffectsSettled=true`, `SettledHighWater=HighWater=0`. This is a **false clean STOP witness** despite a real, unresolved remote effect.

The current `StoppedHostCannotPublishAfterSameIdRollbackOrWholeBindingRootLoss` test backs up the pre-STOP DB but restores it **after** STOP, when the host high-water has already been pinned; it does not exercise this pre-STOP loss. The successful 21/21 focused Release run therefore does not rule out this sequence.

## Required correction

Persist an independent, monotonic effect admission/effect-identity commitment **before any corresponding irreversible Git publication can begin**, and make `stop` and `inspect` prove that the currently present journal includes that full committed admission history (or fail closed to Recovery). A mere high-water written at STOP is insufficient. Crash ordering must not create a fail-open interval; a committed external watermark ahead of SQLite is acceptable only as an explicit UNKNOWN/Recovery fence, not as proof of settlement.

Add a hermetic regression with a real bare-remote ref change, a lost Git acknowledgment, stale **pre-STOP** same-ID database restoration, and assertion that STOP/inspect cannot report a clean drained witness (or admit further effects). Keep genuine preactivation default-off unchanged, `RouteCoverageProven=false`, and all import/cutover/deploy/scheduler holds.

**Evidence limit:** independent source reasoning for this new sequence; no test source was modified and this precise negative was not dynamically executed in this review.