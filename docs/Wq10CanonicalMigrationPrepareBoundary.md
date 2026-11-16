# WQ10 canonical Prepared binding: default-off, fail-closed

Status: project RECOVERY_BLOCKED, not Prepared. The read-only canonical
assessment remains inert. A separate, explicitly invoked WQ07 account-only
Preparing CAS now exists for host-signed private disposable fixtures; it is
not configured on a production host and does not establish project Prepared,
account Validated/Activated, a Scheduler permit or a real migration.

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

The original `inspect` and `attemptCanonicalPrepare` entrypoints remain
read-only; they do not call reserve, release, setCapacity, execute,
LedgerStore.transact or git push. The separate opt-in
`attemptAccountPreparing` uses the same private clone checks but can perform
one WQ07 account-only canonical lease push after independent signature checks.
The read-only entrypoints only update Git caches in private ephemeral clones
and never update supplied clones or account/project bare authorities.
Account/project authoritative before/after diffs are empty for this adapter,
including repeated original UUID, competing head changes and read failures.
A competing writer can move its authority; the adapter observes conflict,
never rewinds, forces, or fabricates a repair. Old Agent/Manager/task, review,
WIP and operation histories remain inspectable.

## Missing typed authorization and transaction contracts

1. WQ07 AccountCapacityState.MigrationPhase recognizes LegacyActive, Frozen,
   Preparing, Validated, Activated and Aborted. WQ07 now has an assembly-internal
   pinned-head Preparing CAS and immutable original-operation receipt for
   independently signed **private disposable fixtures only**. No operational
   authenticated Freeze writer, deployed signer/trust-anchor provisioning,
   connected-client/fence evidence producer, or production migration approval
   yet exists. No production Preparing credential or protocol is configured.
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
The account Preparing lease is confined to the approved disposable bare
account authority, but filesystem checks alone are not an OS sandbox against
malicious same-user races. A production migration still requires authenticated
authority binding, an independently operational old-writer fence, full project
same-CAS Prepared writers, reviewed transaction receipts, cross-remote crash
recovery, and process-level isolation. No live migration is authorized.

## WQ07 account-only Preparing CAS (default-off fixture port)

`WorkQueueCanonicalMigrationPrepare.attemptAccountPreparing` is a distinct,
typed account-only transaction port. It is never called by
`inspect`/`attemptCanonicalPrepare` and has no Scheduler, Work dispatch,
Manager-retirement, deployment, or project-CAS integration.

A candidate is bound to the exact original UUID and actor, frozen account
head and StateVersion, account ID/ledger/epoch/fetch-push identity, SHA-256 of
the offline preflight report, fixed **two-project** participant roster
(project/ledger/epoch/authority digest/frozen Git head/version/source
high-water), and minimum connected-client protocol. These are canonicalized
in a length-prefixed, sorted, deterministic statement SHA-256; no raw remote
credential URL is persisted. Three independent ECDSA public keys, provisioned
outside the command, verify role-separated signatures of that exact statement
and the distinct protocol report, old-writer fence report, and operator
decision evidence hashes. A caller boolean, report string or generic source
name cannot authorize the transaction.

The accepted path is restricted to the same exact-marked, private temporary
disposable root with bare account and two bare project authorities, no
traversal/symlink/object-alternate redirects, a fresh private clone, and
explicit `AttemptOnDisposable` mode. It checks current canonical Frozen
account/project heads, full zero-held-slot account capacity, zero outstanding
project launches and available queue/graph/message/source coverage. WQ07's
existing immutable operation receipt records the full signed request and
account-only result in the same exact-parent lease CAS that moves the
account's `MigrationPhase` to `Preparing`. A different concurrent UUID
cannot silently rebase on the winning epoch. The identical original UUID
recovers its receipt after lost transport acknowledgment or replay; a reused
UUID with different signed payload is rejected. No second account token
is manufactured and old account tokens/releases/refs are retained.

The result always reports separately `AccountPreparing` or
`AccountRecoveryBlocked`, and `ProjectPreparedNotEstablished`. Even a
successful disposable account-only CAS is **never** joint Prepared,
Validated or Activated. A stale original, mismatched roster or signatures,
foreign/duplicate trust key, unknown source/fence proof, phase change, held
global slot, or unapproved authority remains blocked, with no fallback to
unspecified credentials. The preexisting WQ10 assessor remains default-off
and read-only.

**Unresolved production dependencies:** an authenticated Frozen account epoch
writer; separately administered operator signer and approval records; a
connected-client protocol reporter; a verified old-API/Manager/legacy scheduler
write fence with continuous high-water; independently reconciled project WQ02
Prepared conversions (including legacy source and role index proof); and
cross-remote partial-crash journals plus separate activation approval.
Fixture-generated signing keys demonstrate transaction semantics but are
not real operational approvals. The private fixture API must not be wired
to production, even if an operator supplies a plausible signature bundle.
