# P1 resolved — commit-time same-UUID receipt replay after Recovery

**Classification:** Verified resolved; no blocking issue in the frozen default-off correction.
**Source:** `c2c1e371b148fbf664353b0d8d7f5e66f3e0311a`.
**Previous finding:** Reviewer 5 `3c8939a40a5b43d0baf5feba5e861c90`, `findings/commit-time-replay-admission-race.md`.

## Failure and closure

`WorkSqliteAuthority.Commit` checks `work_receipts(operation_id, command_hash)` inside the transaction and returns `Ok { Replayed=true }` for an exact prior command at `Server/WorkSqliteAuthority.fs:598-613`, **before** evaluating project guards or CAS mutations. Since `ReadReceipt` and `Commit` are separate calls, the old Scheduler could return `Ok SqliteSchedulerOutcome` for a second caller after its receipt preflight missed the original, the original committed, and a Recovery fence changed the authoritative project row.

In the frozen patch, `reserveFresh` `Server/WorkSchedulerSqlite.fs:492-503` and `claimFresh` `:577-584` both match `Ok receipt when receipt.Replayed -> Error HistoricalReceiptNotAdmission` before constructing any positive outcome. Initial matching historical `ReadReceipt` is also refused in `reserve`/`claim` (`:646-663`). These four exits jointly cover the sequential and interleaved same-UUID cases. Changed identities/hash remain `OperationIdReused` in the storage layer; storage failure remains an error.

## Test-contract inspection

`Server.Tests/WorkSchedulerSqliteTests.fs` adds:
- `post_recovery_same_uuid_two_project_reservation_never_renews_admission` and corresponding claim case: receipt visible at preflight following a higher-version Recovery fence, refused without new capacity/receipt mutations.
- `commit_time_reservation_replay_after_recovery_is_not_new_admission` and corresponding claim case: call the fresh commit paths directly using the original snapshot after original commit and Recovery. These produce the previously missed `Replayed=true` at `Commit`, and demand `HistoricalReceiptNotAdmission`.
- Reopen and original same-UUID tests now read immutable audit receipts via `ReadReceipt` rather than equating replay with new admission.

The race regressions assert unchanged durable sequence, unchanged account `Reserved`, preserved original receipt and claim generation, and no new external effect. Their direct fresh-path invocation makes the TOCTOU case deterministic instead of relying on timing. I inspected the implementation and tests without independently executing the test suite. Author claims focused 2/2 and full Server Release 443/443 PASS.

## Scope constraints

No modification to `WorkSqliteAuthority` or `WorkRecoverySqlite` in this frozen review. Approval covers only the Scheduler interpretation of historical receipts and its tests, **not** production Recovery, the raw C1 importer mapping, verified old-Git-writer handoff, live dispatch, or operator cutover. Historical receipts remain audit-only; a retry needs current authoritative admission rather than treating the old receipt as renewed authority.