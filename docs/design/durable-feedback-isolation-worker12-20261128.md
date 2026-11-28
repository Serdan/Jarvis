# Durable feedback isolation — source-only design and audit

Task: robustness-feedback-durability-telemetry-isolation-20261128.
Audited remote main: `89d9e38531869ca6f4fa4e514f9d2f4ed13e51a1`.
Pinned source-only worktree: `feedback-durable-w12-20261128`.
No production deployment, database migration, maintenance, replay, or
retention-policy change is authorized by this document.

## Root cause and evidence boundary

* `Server/Services/OptionalFeedbackStore.fs` is a single physical diagnostic
  worker. Its default 200 ms caller deadline never releases an in-progress
  SQLite operation. New diagnostics are denied while the worker is active;
  activity and maintenance share that worker. Both `TryExecute` and
  `TryRead` share admission, even though read-only history bypasses hard
  write quota. Thus read-only list/get/summary can fail while an optional
  operation, activity write or maintenance pass is active.
* The default combined diagnostic budget is 32 MiB (main + WAL + SHM +
  rollback journal), and the main page budget is at most 16 MiB. At 80%
  pressure drops expendable posts; hard write admission rejects all remaining
  diagnostic writes, including feedback. A SQLite FULL error has the same
  quota classification. A larger shared quota only postpones recurrence.
* `Server/McpTools.fs:1361–1538,1729–1776` routes all five explicit feedback
  methods through the optional store. Authentication is established before
  dispatch and repeated in the worker's request context. No local client
  approval is required.
* `FeedbackStore.fs:203–216` stores an operation foreign key with
  ON DELETE SET NULL. Routine age pruning (272–285) deletes expired
  operations regardless of links, whereas pressure pruning in
  OptionalFeedbackStore excludes linked operations. Existing tests explicitly
  expect loss of the link on normal expiry. This is separate from losing
  feedback rows, and changing shared retention is outside this lease.
* Submit resolves an explicit or recent same-user operation, then inserts
  feedback using a separate connection and server-generated ID. Retention
  can race linkage resolution. On timeout, the worker may still commit;
  retry without a caller-retained key can create a second report. Update
  commits first and reads through another connection, so a response can
  reflect a later write or fail after commit.
* No direct production server filesystem or SQLite data was inspected.
  Prior observed MCP failures and the exact source establish the causal
  paths, not the current physical database size, lock holder or deployment
  version. The full task overview exceeded the tool response limit; the
  canonical tasks tree at snapshot `4bf6e68b37c6ec5d7519c1d8381ef123dbf2077a`
  contained no separate feedback/telemetry task by ID. Lead challenge
  `d6d8a03f630649ef9601cde995346593` requests explicit shared-file ownership
  before composition.

## Proposed separation

The feedback data is durable operator-authored application data, unlike
optional diagnostic rows. Use a distinct SQLite file and a bounded feedback
admission domain. No shared telemetry budget, worker, retention transaction,
or foreign key may gate feedback. The existing diagnostic store, activity
proof, connection history, ingress and notification/admin surfaces are
unchanged.

The isolated prototype has one physical writer and two physical reader
slots, immediate rejection at lane capacity, a three-second default response
deadline, one-second SQLite busy timeout and two-second SQL VM progress
budget for runtime connections. A timed-out caller does not release its
physical slot. An uninterruptible filesystem stall remains possible and must
not cause unbounded replacement workers. Reader failures are unavailable, not
empty history. The SQLite runtime uses WAL, private cache, no pooling and
FULL synchronous writes. It must not create a replacement database on a
missing/malformed/unmarked path. The default proposed budget is 64 MiB main /
128 MiB combined, independent of telemetry; these are admission/page limits,
not a filesystem quota. Host-wide disk exhaustion can still affect both.
Capacity values are proposed defaults, not measured production requirements.

Feedback uses tenant-scoped IDs and a separate tenant-scoped immutable
request receipt. A caller-retained UUID plus a SHA-256 digest of normalized
request inputs binds a submit or update. The feedback change and receipt are
one SQLite transaction. Same ID and same input returns the original response;
same ID with different input or mutation kind conflicts. An update replay
cannot undo a later distinct update. Derived operation evidence is intentionally
not part of request identity, so optional history changing between retries
cannot alter an already committed result. Receipt lookup precedes write
quota admission; reads and replay remain available above the write limit.
Receipts are retained with feedback and have no automatic purge.

A response timeout or SQLite exception after admission is conservatively
outcome-unknown. Retry the exact mutation with the same UUID; do not generate
a fresh ID, infer non-commit from elapsed time, or replay with changed inputs.
If no response or request key was retained, reconcile using bounded list/get
and operator judgment; do not claim exact deduplication. Composition must
expose an optional caller-retained request ID or require one, document how it
is returned before/after failures, and preserve authenticated user isolation.

Optional operation metadata is evidence, not a precondition. A successful
same-tenant explicit or recent matching diagnostic lookup can supply a
snapshot of operation ID and success; unavailable or expired telemetry
cannot establish linkage. Preserve a supplied but unverified operation ID
separately from verified linkage and never count it as a failed operation.
Do not disclose whether an operation belongs to a different tenant.
The adapter must not pass arbitrary caller-supplied values as verified
evidence. The prototype explicitly rejects evidence with a different user
or a mismatching explicit ID. Verified links are no longer mutable by
diagnostic retention; failure counts use the captured observation. This
is a semantic change from previous explicit-ID rejection on missing
telemetry and requires the composition decision recorded by Lead.
Unverified does not mean that the operation was absent, and verified is
not proof of a canonical command receipt.

## Offline preparation and recovery

The prototype's initialization/import is an explicit method, never a
startup side effect. Require operator approval, a quiesced legacy feedback
writer and a verified consistent backup before use. Preserve the legacy
database and all its sidecars. Do not copy only the main file of an active
WAL database and do not manually delete sidecars.

The importer reads one SQLite snapshot using a separate read-only
connection; it streams all legacy feedback into a newly reserved
destination transaction, preserving ID, tenant, timestamp, project/tool,
category/severity, text, workaround, status and resolution fields. Older
schemas without status use Open. Preserve surviving operation IDs as
requested evidence, and mark as verified only when a same-user operation
row exists; snapshot its known success. Null links already cleared by old
retention cannot be reconstructed. Source rows are not changed. Validate
source/target counts before commit. Distinct application ID and schema
version are written atomically with the new schema and rows. A failed
import leaves an unmarked destination for inspection; runtime refuses it
and initialization refuses to overwrite an existing main/sidecar. Use
a new path for a separately authorized retry, never silently delete
partial output or accept it as empty history.

Before cutover, verify source and target per-tenant counts and record
identity/content/resolution equality, operation-link classifications, SQLite
integrity, backup/restore and sufficient independent disk headroom. The
prototype currently verifies total count in its transaction; detailed
operator verification and a reviewed offline invocation mechanism are
required before activation. Retain the source backup and record the
migration evidence. Switch exactly one feedback writer authority after
validation. Never start the new writer while legacy feedback can still
be written, never fall back to writing legacy on a new-store error, and
do not roll back to stale legacy state after new writes without explicit
reconciliation. Ordinary telemetry can continue using its existing file
after approved cutover; it does not own the new feedback history.

On durable quota pressure or SQLite failure, preserve all files and use
the durable feedback read/reconciliation path if available. Investigate
physical size (main, WAL, SHM, journal), filesystem space, lock owners,
and integrity with approved offline procedures. No automatic archival,
receipt expiration, deletion, or compaction is implemented. A full
durable store requires an explicit budget/archival decision, separate
from diagnostic quota maintenance. Do not silently drop closed reports
or replay uncertain writes. Back up the independent file with a
SQLite-consistent method and verify restoration before trusting it.

## Scope and verification

Source-only isolated prototype at commit
`97ae900a2e811380280e81356d735e2b8d0cbed1`; its independent linked-source
test project does not modify Server.fsproj, Program.fs, McpTools.fs or
shared test composition.

```sh
dotnet test tests/DurableFeedback20261128/DurableFeedback20261128.fsproj -c Release
```

Engineering job `4c13a373377744db823022a4058d92d7` was started against that
commit. Its terminal result was not yet observed when writing this document.
The fixture covers diagnostic quota, separate durable admission, idempotent
submit/update, tenant isolation, preserved evidence after pruning, legacy
migration and failed import, durable quota read/replay, and a real competing
SQLite writer with uncertain outcome. Compile/test claims must use terminal
job evidence, not this planned list.

Production composition, optional evidence acquisition, request-ID schema
contract, and activation/import tooling are pending Lead's exact ownership
decision. No existing feedback tool is changed by the prototype.
