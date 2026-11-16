# WQ03 original browser one-shot journal (client-local, default off)

This module records the original browser submission identity, not source-effect
mutations or a project/account CAS. It does not install a browser provider or
enable WQ08 dispatch.

## Explicit enrollment

A trusted host chooses an absolute private local directory and creates it first.
Call Client.OriginalBrowserJournal.OriginalBrowserJournal.provisionTrustedHostRoot
exactly once during offline enrollment, then call openExisting for normal use.
No dispatch/replay path automatically creates the journal. This enrollment
creates original-browser-v1.anchor before original-browser-v1.sqlite.
Incomplete enrollment must be inspected; do not reset it on retry. Keep both
files together in backups and restrict directory access to the client account.
Each original UUID and intent hash is also appended and fsynced to the anchor
before SQL commit. Missing or rolled-back database rows block further use.

The journal uses a deterministic UUID as its database key derived from the
exact WQ08 wq08:dispatch:<sha256> original operation ID; the original ID is
preserved verbatim in a unique column and returned by read operations.
Intent SHA-256 covers project, launch, work/generation, role/version, request
fingerprint, account/project heads, source ref/commit and exact operation ID.

## Write and recovery contract

1. Construct the pinned OriginalBrowserIntent only after the separate WQ04/WQ07
   account/project admission checks. tryMarkOriginal commits the complete
   immutable intent via SQLite (synchronous=EXTRA) before it returns Marked
   and before any injected provider callback may be invoked.
2. Existing means NEVER call the provider again for this original, regardless
   of whether the row has an outcome. Another process can race the first
   caller, but only the first writer receives Marked. Changed identity or an
   operation/launch collision is IdentityConflict.
3. A genuine provider response may be persisted once through
   recordOriginalOutcome: Accepted requires its receipt; DefinitiveNoEffect
   requires an authoritative no-effect reason; Unknown preserves ambiguity.
   A timeout, thrown exception or lost response is not evidence of no effect.
   Missing outcome is also unresolved. Conflicting later outcomes require
   manual reconciliation, not overwrite.
4. findOriginal looks up the exact original operation ID; findByLaunch looks
   up the unique original launch ID. Every read reconstructs and checks the
   original intent SHA and derived UUID against the independent anchor
   inventory. Missing, rolled-back or replaced SQLite rows, continuity
   mismatches, corrupt schema and inaccessible storage fail closed.
5. A read-only preview adapter can be supplied with
   WorkQueueSchedulerHostPreview.realReadPortsWithOriginal account project
   (Some openedStore). The default realReadPorts and existing CLI remain
   unbound, report the missing provision, and keep CanDispatch = false.

## Operator decision on uncertainty

Preserve the original anchor/database and the precise operation/launch IDs.
Inspect the provider's original response or other verified receipt, and the
independent account/project Git heads. Do not resend under the same or a new
ID merely because the journal or response is unavailable. A restored copy
must be reconciled with the complete original history before authorizing any
new attempt. If a journal file disappears, do not call provisioning as a
repair or interpret an empty store as no effect.

Unit tests use disposable directories and an injected fake provider only;
none of these paths send real browser requests.
