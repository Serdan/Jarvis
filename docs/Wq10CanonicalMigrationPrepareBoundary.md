# WQ10 canonical Prepared binding: default-off, fail-closed

Status: project RECOVERY_BLOCKED, not Prepared. The read-only canonical
assessment remains inert. The PUBLIC account Preparing API has no caller
trust-key argument and returns a typed no-write Blocked outcome while independently
owned host/operator trust anchors and original-journal verification are absent.
An assembly-internal private disposable test seam exercises WQ07 CAS mechanics
only. Neither path establishes project Prepared, account Validated/Activated,
Scheduler dispatch, or live migration.

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
`attemptAccountPreparing` is fail-closed without independently provisioned host trust anchors.
An assembly-internal fixture-only executor exercises the authenticated WQ07
account lease path solely on private disposable Git repositories.
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
   pinned-head Preparing CAS gated by a cryptographically verified receipt
   capability, and an immutable original-operation receipt for internal
   **private disposable fixtures only**. No operational
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

## WQ07 account-only Preparing CAS: review remediation

The PUBLIC `WorkQueueCanonicalMigrationPrepare.attemptAccountPreparing`
requires only an account Preparing command. It deliberately accepts no
caller-nominated signer keys. In this build, a trusted host/operator policy
has **not** been provisioned, so this entrypoint always returns a typed
AccountRecoveryBlocked outcome **without any account or project ref write**.
A caller-generated protocol, fence or approval key cannot authorize a CAS,
regardless of whether all signatures are internally valid.

The assembly-INTERNAL
`attemptAccountPreparingWithFixtureTrustAnchors` path is available to
the hermetic Client.Tests friend assembly to exercise the same receipt and
Git transaction logic. It is not a production entrypoint. Test-generated
signers and invented zero-event journal claims are NOT operational approval
or actual verification of legacy source history. No public host integration
calls this fixture path.

The original journal/source requirement is distinct from canonical project
ledger `NextSequence`. A complete account candidate must contain exactly
two independently signed original-source checkpoints. Each includes the
project identity, exact FROZEN project Git head, original journal UUID,
original journal high-water, and original source-effect high-water. The
coverage digest binds these two checkpoints to the exact offline report
SHA and to the signed account Preparing statement. The old-writer-fence
trust anchor separately signs the coverage digest under the domain
`original-journal-coverage`; the three independent protocol, fence,
and operator approvals sign the complete statement. Missing/unknown
original journals or high-waters remain an account-side blocking gap, even
when a caller supplies a plausible SHA or the new project sequence matches.

The low-level account CAS does not accept a raw, shape-correct receipt.
Its entrypoint requires an opaque authenticated receipt capability created
only after verifying the pinned signer signatures. On the internal fixture
path, the WQ07 original-operation receipt is fetched and compared with the
fully signed canonical request **before current-project head/Frozen checks**.
If that original UUID already committed, the WQ07 transaction returns the
original immutable result read-only; any mismatch is rejected as UUID reuse.
Only a genuinely new operation is subject to strict current project and
account Frozen/head/version/coverage/occupancy preflight. Consequently an
acknowledgment lost after account Preparing and legitimate later project
progress cannot cause a second account CAS or obscure the original result.

The internal positive fixture is confined to the exact-marked private
temporary root and distinct local bare account and two project Git authorities,
with recursive metadata-redirect checks and private clones. Its result still
reports ProjectPreparedNotEstablished, SchedulerEnabled=false and
ProductionCutoverAuthorized=false. It never writes project Prepared, activates
live scheduler/work claims, deploys, or converts the legacy Manager.

### Remaining production blockers

- A separately administered, pinned host/operator trust registry, not
  provisionable through ordinary Preparing requests.
- Real independently reconciled original journal/source-effect high-water,
  with durable report and source-bound evidence from both project authorities.
- Verified continuous old-writer/Manager/connected-client fence, project-side
  Prepared same-CAS writers, cross-remote crash recovery and separate
  activation authorization.

Until all applicable authority proofs actually exist, the public port stays
blocked. The test seam is not an authorization mechanism.

### Local verification

Use .NET 11 Release without hosted GitHub Actions or Codex:
`dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~WorkQueueCanonicalMigrationPrepareTests`.
Fixtures include forged caller-generated signers, separately signed
missing/unknown old-journal checkpoint claims, immutable UUID recovery
after a lost acknowledgment and subsequent project advance, and exact
account/project bare ref comparisons on all no-write and replay paths.
