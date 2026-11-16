# WQ10 canonical Prepared binding: default-off, fail-closed

Status: RECOVERY_BLOCKED, not Prepared. This is an isolated typed
assessment over the accepted WQ07 account and WQ02 project Git CAS
authorities. It is not a writer, a migration, or a production cutover.

Source: Client/Modules/WorkQueueCanonicalMigrationPrepare.fs
Tests: Client.Tests/WorkQueueCanonicalMigrationPrepareTests.fs
Read-only inspector: tools/Wq10MigrationPreview/Program.cs
Disposable staging sidecar: tools/Wq10MigrationPreview/prepare.py

The existing prepare.py sidecar returns ADAPTER_ONLY_NOT_PREPARED and writes
only separate staging refs. Those receipts are never authoritative canonical
Preparing/Prepared proof.

## Boundary

The module requires a stable operation UUID and actor, explicit attempt mode,
one account and exactly two distinct project Git authorities, pinned account
and project SHA heads, project versions and original high-water, a SHA-256 of
the offline report, and an approved disposable directory with the exact
.wq10-disposable-fixture marker. All repository paths and local fetch/push
authorities must be under that root with no traversal or symlink ancestors.
Every supplied clone and bare authority has its Git metadata recursively
checked for nested symlinks/junctions, not merely the top-level .git/objects
or refs directories. Linked descendants and object alternates fail before fetch.

InspectOnly is default-off. AttemptOnDisposable still always returns a typed
blocked assessment. An operator-supplied report hash, permission boolean or
migration phase string is never independently authenticated authorization.

The module uses AccountCapacityLedger.fetch, LedgerStore.fetch, and
WorkQueueGitLedger.loadAt to re-read trusted identities and canonical
project state. Fetches and trusted-cache update-ref operations run exclusively
in fresh, unpredictable, private temporary clone metadata within the approved
root, made from bare authorities without Git hardlinks or installed templates.
Caller-supplied clone metadata is never a fetch target, avoiding the
check-then-fetch symlink race against the original clone. On Unix the scratch
must be private to the current user (mode 0700); a group/other-writable
disposable root is refused. Clone metadata is discarded after each assessment.
The assessment reports separate typed gaps for pinned heads, versions,
high-water, migration phase, held global tokens, outstanding launches,
queue/graph/message/source coverage and remote validation failures.

No canonical transaction is attempted. The module does not call reserve,
release, setCapacity, execute, LedgerStore.transact or git push.
The existing fetch readers can internally invoke update-ref, but only against
the fresh private ephemeral clones; they never update the supplied clones
or any canonical account/project authority.
Account/project authoritative before/after diffs are empty for this adapter,
including repeated original UUID, competing head changes and read failures.
A competing writer can move its authority; the adapter observes conflict,
never rewinds, forces, or fabricates a repair. Old Agent/Manager/task, review,
WIP and operation histories remain inspectable.

## Missing typed authorization and transaction contracts

1. WQ07 AccountCapacityState.MigrationPhase recognizes the strings
   LegacyActive, Frozen, Preparing, Validated, Activated and Aborted, but
   the accepted writer API has no authenticated Freeze/Preparing epoch CAS
   or immutable migration receipt. The state lacks fixed participant set,
   pinned project heads, min connected-client protocol and operator approval.
2. WQ02 MigrationEpoch is Frozen, Preparing, Validated or Activated. It
   lacks Prepared(epoch, baseHead, indexCoverage, conversionReceipt);
   WorkMutation has no PrepareProjectEpoch transaction. A new transaction
   must bind original history and inert conversion in the SAME project CAS.
3. No independently verified old-writer fence currently spans connected
   clients, old assignment/reviewer/integration APIs, Manager heartbeat and
   old scheduler launch authority. A report boolean cannot supply one.
4. WQ04 canonical binder still identifies incomplete source-effects and
   role joins: exact legacy event/index high-water, immutable operation
   hashes, WQ03 source and browser journals, versioned role provenance,
   frozen source refs/SHAs and conversion collision checks need a real
   same-CAS authenticated producer. Unsupported cases must stay blocked.
5. Both project histories and the account cannot push atomically. A real
   adapter needs immutable original-UUID before/after receipts, partial
   crash journal, exact remote head verification, unknown acknowledgment
   reconciliation and exact-ref CAS repair/abort without losing any original
   ref. Symlinked committed receipt files and source-altering jobs must
   block rather than being overwritten or treated as finished.
6. A valid Prepared proof, once implemented, does NOT authorize activation.
   Separately reviewed operator authorization, compatible client handshake,
   participant-wide validation and an account Activated CAS are required
   before the Scheduler can dispatch and the Manager can be retired.

## Local verification

Run with .NET 11 Release, not GitHub Actions:

    dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~WorkQueueCanonicalMigrationPrepareTests
    dotnet build tools/Wq10MigrationPreview/Wq10MigrationPreview.csproj -c Release
    python3 -m unittest discover -s tools/Wq10MigrationPreview/tests -v

The native fixture uses a bare account and two bare project repositories,
each with two clones, and asserts no extra authoritative ref movement for
default-off, explicit attempted, repeated-UUID, stale-head, old-writer,
incomplete-high-water, missing-marker, traversal and symlink cases,
including nested clone objects/refs and bare authority metadata links
to outside directories. The negative tests compare external-directory
contents before/after the attempt and verify remote refs never move.

These private scratch and recursive metadata checks are defense in depth for
an approved disposable fixture, not a production trust system or a substitute
for a host-level sandbox against malicious code running as the same OS user.
A future canonical writer still requires authenticated authority binding,
fencing and reviewed transaction receipts plus process-level isolation.
No live migration is authorized.
