# Independent G1a review — Changes requested

Frozen source `4d6d6f07b43a3641b0b6162ecf91411868ef9045` on base `9c290e299faf7ee40061f262f682c36b30d0771a`. Four changed files: new `Server/WorkRecoverySqlite.fs` and `Server.Tests/WorkRecoverySqliteTests.fs`, plus two F# compile includes. Scoped to default-off, disposable SQLite Recovery seeding and E0 admission, **not** F1 verified release, live restore or cutover.

The additive module mostly follows the desired safety pattern: a single guarded SQLite CAS downgrades project coverage, blocks stages and increments the account capacity version; it stores original stage bodies, external effect states and captured launch operation identities. The tests exercise stale/fresh E0 snapshots, project-set mismatch, same-seed replay, unknown effects and self-attested release refusal. Submitted evidence reports .NET11 Release Server.Tests 443/443 passing; I inspected source and tests but did not independently rerun the suite.

**Two blocking findings (P1):**

1. [`findings/prepared-effect-fence-gap.md`](findings/prepared-effect-fence-gap.md) — `validate` rejects a `Prepared` stage effect *before fencing*. The project remains `Verified`; E0 can still reserve a different settled Available work in that project. A normal uncertain effect must cause a durable project-wide Recovery veto, not prevent it.
2. [`findings/replayed-admission-bypasses-fence.md`](findings/replayed-admission-bypasses-fence.md) — E0 `reserve`/`claim` return successful historical same-operation receipts before checking current Recovery coverage. A post-fence retry can receive actionable positive admission. The G1a tests check fresh UUIDs but not original lost-response operation IDs. Preserve read-only original receipt reconciliation separately from permission to dispatch.

Please address both in source and hermetic negative tests, coordinate with E Scheduler owner for any required E0 API change, rerun .NET11 Release, and submit a fresh frozen SHA. **No host registration, live restore, F1/G1b release, migration or Scheduler cutover is authorized by this review.**
