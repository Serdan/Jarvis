# Blocking: commit-time idempotency replay can still return actionable Scheduler success

**Frozen review:** `f9976ff931520994bc7dc216f07f301f0c49b29c` vs `2a51920bcedf3fed185a2956b7dc812474107881`
**Severity:** P1 / ChangesRequested

The new `replayOutcome` branch rejects a receipt already visible during the initial lookup. That closes the sequential tests, but leaves a time-of-check/time-of-use gap at the actual SQLite commit.

- `Server/WorkSchedulerSqlite.fs:490-496` (`reserveFresh`) and `:570-576` (`claimFresh`) convert **every** `Ok receipt` from `store.Commit` into `Ok SqliteSchedulerOutcome`; neither checks `receipt.Replayed`.
- `Server/WorkSqliteAuthority.fs:598-613` resolves an exact `operation_id`/`command_hash` match to `Ok { Replayed=true }` **before** checking any project guards, STOP/Recovery coverage, record versions or account capacity. This is the intended immutable idempotency receipt, not new admission.
- The initial `ReadReceipt` lookup in `replayOutcome` is a separate transaction/read, not atomic with the eventual `Commit`.

**Reachable interleaving:** Two concurrent callers start `reserve` (or `claim`) with the same operation UUID and unchanged initial snapshot. Caller B sees no receipt in `replayOutcome` and pauses. Caller A commits the original command. Recovery installs a durable fence (project authority `coverage=Recovery`, higher record version). B resumes with its original snapshot and identical command hash. Its `Commit` finds A's receipt and returns `Replayed=true` without checking newly installed guards; `reserveFresh`/`claimFresh` return a positive `Ok` admission **after** Recovery. No duplicate capacity debit is necessary for the violated invariant: a positive Scheduler admission can authorize downstream behavior.

**Required:** In *both* fresh commit success arms reject `receipt.Replayed` as `HistoricalReceiptNotAdmission` (or implement an equally strong same-transaction authority/fence recheck before positive admission). Do not reconstruct a positive historical result merely because the stored receipt matches. Preserve read-only receipt/provenance reconciliation and same-UUID immutable audit evidence; keep default-off/no G1a merge. Add a regression that covers the receipt-lookup/commit race, or deterministic evidence exercising the commit-time replay path after a project Recovery/STOP fence. The added sequential post-Recovery tests do not cover this interleaving.

The frozen review's claimed Release results (focused 21/21, full 441/441) are compatible with this missed concurrency path; I did not rerun the build.