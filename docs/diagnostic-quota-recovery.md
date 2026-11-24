# Optional diagnostic quota recovery

This is a source-only repair to the optional FeedbackStore used by connection
diagnostics. It does not authorize deployment, production maintenance, data
deletion, database migration or a change to Health/activity-proof authority.

## Operator diagnosis

1. Check authenticated `get_connection_diagnostics`. The live in-memory device
   and selected-session fields remain available even if history fails.
   `historyUnavailable` is nonempty when the optional query failed; an empty
   event array with this field set is not evidence of no historical events.
   `storageStatus` is coarse (NotObserved, Healthy, Pressure or
   MaintenanceRequired); it contains no other user's row contents or footprint.
2. On the server, inspect the configured diagnostic DatabasePath and read-only
   file sizes of the main DB, -wal, -shm and -journal separately, available
   filesystem headroom, and server-local warnings from OptionalFeedbackStore.
   Measure over a representative interval if considering changing limits.
   Do not run write/cleanup commands against a live DB without separate approval.
3. After reviewed and explicitly approved deployment, the existing single
   diagnostic worker attempts a pressure pass at minute intervals. Normal
   retention remains hourly. Check for recovery below both 65% watermarks.
   The 16 MiB main / 32 MiB combined defaults are unchanged; soft admission
   starts at 80%, with hard new-write rejection at 100%. Main page limits
   are enforced by SQLite. These are not host-wide disk quotas.
4. A blocked checkpoint is evidence of a reader/writer retaining the WAL;
   investigate that holder rather than removing sidecars. A bounded pass
   may report incomplete reclamation and retry. If free space is inadequate,
   the file is outside the bounded legacy envelope, or protected live pages
   prevent reclamation, preserve all files and obtain a separate archival,
   export or retention decision. The server warning includes the observed
   main/aggregate/live-page footprint, and feedback table/index bytes if
   SQLite exposes dbstat. No automatic feedback or attributed activity purge
   occurs. Normal expiry can still clear an old operation link as documented.
5. Treat corrupt files or an absent main DB with sidecars as operator
   inspection cases. Do not manually delete WAL, SHM or rollback journals.
   SQLite transaction rollback, checkpoint and VACUUM provide recovery
   boundaries; an uncertain or timed-out worker remains single-flight until
   physical completion. A host I/O stall can still disable history without
   disabling core transport. No change to Health's current-process
   observation gate is made.

## Verification

Focused Release command (disposable local data only):

```
dotnet test Server.Tests/Server.Tests.fsproj -c Release --filter "FullyQualifiedName~OptionalFeedbackStoreTests|FullyQualifiedName~FeedbackStoreTests|FullyQualifiedName~AgentActivityAuthorityTests"
```

The quota cases seed a valid oversized main DB, retain feedback and linked
operation/activity rows, read history above the cap, and recover after a new
store instance is constructed. Protected-only pressure is reported without
deleting feedback. A real concurrent SQLite reader pins WAL growth beyond the
aggregate ceiling: optional posts shed, checkpoint failure is explicit, and
maintenance resumes after release. A stable main/WAL copy without SHM models
the on-disk state after abrupt loss of a process; a fresh store reconstructs
history and recovers quota. It is not a literal process-kill test. Existing
corrupt/absent, hard page-limit, hung worker, user isolation and activity
proof tests remain in the suite.

The live MCP endpoint was observed returning
`ExecutionFailed: TelemetryQuotaExceeded: diagnostic disk budget reached.`
during source investigation on 2026-11-24. The task's prior evidence reports
approximately 32 MiB combined / 16 MiB base. Direct server filesystem sizes,
write rate and retention horizon were not accessible through the assigned
client-side source lease; no production measurements or cleanup are claimed.
Consequently the defaults have not been raised. This repair does not claim
post-deployment live recovery; that requires separate authorized rollout and
measurement.
