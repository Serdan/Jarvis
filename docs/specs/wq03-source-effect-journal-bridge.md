# WQ-03 trusted source-effect journal (isolated foundation)

Status: **inert and fail closed**. This branch adds a durable client-side
pre-dispatch journal, not a replacement for WQ-03's source handoff blocker.
Worker 5's WQ-03 source implementation remains untouched.

## Boundary and provenance

The host constructs SourceEffectScope from the canonical LedgerBinding using
fromTrustedLedgerBinding. Identity includes project name and ID, ledger ID and
epoch, approved fetch/push fingerprint, repository ID, source ref, isolated
checkout ID, work ID, launch UUID and claim generation. Every request also
requires its original caller-stable operation UUID and exact request SHA-256.
Never generate a new UUID merely because an earlier reply timed out.

openAtHostRoot requires a pre-provisioned absolute host directory outside a
source checkout. The shared SQLite database uses rollback transactions with
synchronous EXTRA and a POSIX parent-directory fsync on first creation. A
missing, corrupted or inaccessible journal fails closed; there is no fallback
to the in-memory job list. This is a **host-local** store, not a network-
distributed authoritative registry. Deployment requires reviewed ACL,
retention, backup and cross-device coverage arrangements.

beginBeforeDispatch transactionally records the immutable original UUID and
a sticky RecoveryBlocked fence **before** any dispatch. dispatchOnce executes
only for the first committed record. Crash/reconnect, replays and duplicate
same-UUID calls never redispatch. Changed request, scope or ledger binding is
rejected. Prepared after a crash and Uncertain after a dispatch exception are
not positive terminal proof, even with a clean checkout. In-flight original
pushes, merges, jobs and retries cannot be silently treated as stopped.

runSynchronousFileEdit records a terminal observation only after its own
host-side synchronous callback returns, but that observation does not claim
all mutations are covered. The journal exposes NO method to fabricate a
terminal proof for detached processes, original pushes, merges or retries.
inspectForHandoff is read-only and reports per-UUID evidence, missing/wrong
identities, incomplete route coverage and sticky blocked reasons. It always
reports RecoveryBlocked until separate audited host-wide coverage exists.

## Explicit outstanding activation gates

- Legacy RunCommand, StartJob, RunProjectTask, WriteFile, PatchFile, ImportFile,
  GitCommit, Git push/merge/retry and out-of-band OS/Git mutations are **not**
  automatically enrolled in this standalone module. The journal does not
  assert their absence from an empty row set. Complete enforced dispatch
  coverage must be integrated and separately reviewed.
- Host-local journal cannot prove remote device/process mutations terminated.
  Production needs trusted process-tree terminal receipts and immutable
  original remote-push results bound to the same original operation UUID.
- WQ-03 must persist its canonical MarkSourceRecoveryBlocked gate transition
  when integrating the host bridge. The local journal fence is an additional
  durable safety record, not a replacement for canonical project state.
- Git host create-only, no-update/no-force, no-delete and retained
  refs/heads/jarvis-wip/** policy for actual credentials, bypass accounts and
  object pruning remains an external operator gate. No unprotected fixture
  is accepted as a substitute for ruleset proof.
- Source-successor claim issuance, token release, positive YieldSource,
  Scheduler activation, migration, production deployment or main branch
  integration remain forbidden pending their independent proof and review.

## Release regression boundary

Client.Tests/SourceEffectJournalTests.fs verifies pre-dispatch persistence,
original UUID replay over restart, concurrent two-client single admission,
two actual Git clones with distinct checkout IDs, a lost remote reply,
still-in-flight old process despite clean checkout, terminal local edit
without complete host coverage, wrong project/ledger/payload, missing legacy
routes and inventory, durable blocked fences and corrupt journal behavior.

Run locally:

    dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~SourceEffectJournalTests

These tests validate the primitive and its conservative negative-space
behavior, **not** a production-capable source handoff or a host policy proof.
