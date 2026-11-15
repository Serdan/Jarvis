# P1 — A definitely blocked pre-launch reservation never releases capacity

**Location:** `scripts/manager-heartbeat.mjs:14,150-173,347-355`

The documented preflight requires the Manager to mark a reservation `blocked` when the queue, challenge or agent state changes *before* `begin`, meaning **no browser effect occurred**. However `blocked` is included in `LIVE_RECORDS`. Every later `decide` adds it to `reserved` and consumes a worker/reviewer capacity slot. A newly eligible agent remains suppressed indefinitely until a manual operator `release` on a known-safe no-launch record.

**Independent reproduction on frozen commit `94e3c1e`:** with one Ready worker and worker budget 1, `decide` reserves it; mark the record `blocked` to model a known-safe pre-launch stop. Running the same eligible snapshot again returns no reservations, count `worker:1`, reason `BudgetFull`. There is no live or uncertain tab.

This undermines unattended heartbeat recovery following a transient preflight change and wastes a hard budget slot.

**Required correction:** distinguish terminal, auditable `blocked-before-dispatch` from uncertain `dispatching/submitted` records. Retain the history but exclude definitively blocked records from live reservations/capacity accounting; require manual recovery only for genuinely uncertain effects. Add a block -> later eligible -> new reservation regression, including scheduler restart.
