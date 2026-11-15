# WQ03 host source-effect journal continuity (inert, default-off)

## Scope and acceptance boundary

This is an isolated correction descended from the frozen ChangesRequested source
`f85c288d45981aef5502ce953f7e135e2fd9e965`. It fixes the P1
SQLite-loss/replay reset for the **opt-in host-owned WriteFile/PatchFile diagnostic**.
It is **not** operational source succession, verified worker-origin authorization,
Scheduler activation, or permission to release a work/launch/token/source claim.

## Explicit trusted enrollment, never in dispatch

The operational `SourceEffectJournal.openAtHostRoot` is **open-existing-only**.
It uses the host account's fixed `LocalApplicationData/Jarvis/
wq03-source-journal-continuity-v1.anchor` path (outside the selected journal
root). No root selected by a source request can select another anchor. Existing
unanchored SQLite databases, missing/corrupt anchors, unknown schemas, and
missing/replaced databases block with `RecoveryBlocked`; they are never
upgraded or recreated during source execution.

`provisionTrustedHostRoot root anchorPath` is a separate code API, **not**
exposed through SignalR/MCP or called by the operational dispatcher. Its use
requires a trusted host-operator provisioning path that creates/approves an
absolute journal root and a separate durable anchor parent. The provisioner
refuses if either the SQLite file or anchor already exists (including partial
enrollment), writes and fsyncs an independent anchor FIRST, and initializes
a new SQLite schema with a journal UUID and exact root identity. Do not enable
the opt-in diagnostic on a host without an explicitly reviewed provisioning
process. No automatic legacy database/schema migration is permitted.

## Write ordering and recovery rule

For an original caller-stable operation UUID, each SQLite serializable writer
transaction verifies that its journal UUID, canonical root, and **entire
original-operation key inventory** match the independent anchor. For a new
effect it creates a Prepared row, appends `op|UUID|fingerprint` to the external
anchor with `Flush(true)`, checks agreement, and only then commits SQLite.
Only a committed FirstDispatch can reach an awaited file handler.

The anchor is written **before** SQLite commit. A crash at any point can
leave an extra anchored operation without a committed DB row. That state is
intentionally RecoveryBlocked; it must not repair itself or dispatch again.
SQLite deletion, an empty replacement, an older valid SQLite snapshot,
changed journal UUID, root switch, marker loss/torn write, and malformed
schema likewise block. Replays against intact original state remain
AlreadyRecorded, never a new dispatch. Persistent anchor mismatch is a durable
fail-closed recovery fence even when the SQLite DB is unavailable for storing
a separate fence row.

File-only SQLite original UUID and scope checks do not establish that an RPC
caller is authenticated to the canonical registered launch, nor that an
upstream source UUID was issued/owned durably by an authoritative admission
service. No cryptographic/immutable protection is claimed against a
privileged host operator capable of replacing **both** anchor and DB.
Process trees, detached jobs, actual Git push/merge/remote acknowledgements,
legacy and out-of-band source mutations, host protected
`refs/heads/jarvis-wip/**` create-only/nondelete/retention policy, WQ04/05
producer coverage, actor binding and future WQ03 source succession are
still **unproven**. YieldSource, Scheduler, source handoff and production
routing remain disabled and RecoveryBlocked.

## Reproduction / isolated validation

.NET 11 Release `Client.Tests` SourceEffect fixtures use disposable root
and **independent sibling** anchor only. They cover explicit no-auto-enrollment,
A's original UUID host write then B's same-UUID replay, two-client concurrency,
SQLite deletion, valid old-snapshot rollback despite same journal identity,
root switch, anchor corruption, payload/source changes, intact restart,
and corrupt SQLite. The tests make no hosted GitHub Actions or production
host/ref mutation.
