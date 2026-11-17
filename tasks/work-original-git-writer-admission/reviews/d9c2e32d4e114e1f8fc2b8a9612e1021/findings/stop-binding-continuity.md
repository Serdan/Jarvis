# P1 — STOP is not durable across missing or replaced journal binding

Frozen head: `ab6ecdb0d952f348ecaea504c59a4ed0c875aba9`; base: `35f675cc1564710d10fdb2f50a03edff39368a28`.

## Source evidence
- `Client/Modules/OriginalGitWriterAdmissionJournal.fs`: `configuredPath` takes only the process environment variable `JARVIS_ORIGINAL_GIT_WRITER_ADMISSION_JOURNAL`; unset or blank returns `Ok None`.
- `admit` treats `Ok None` as journal disabled.
- `withPublication` on `Ok None` immediately invokes `publish()`, even if a previous database recorded `closed=1` via `stop`.
- A changed environment variable pointing to a separately provisioned, open journal is likewise accepted without any immutable journal identity binding or monotonic STOP attestation.

## Failure sequence (source-proven, not independently executed)
1. Configure journal A and call `stop A`; its persisted `control.closed=1` is durable.
2. Restart a publisher without the environment variable, or point it at a new open journal B.
3. An original project/account CAS or source-ref publish enters `withPublication`.
4. The original Git push can now run without consulting journal A. An already-issued STOP is therefore not an enforceable host-wide barrier, and a current/restart witness cannot prove all writers stayed excluded.

The implementation's `RouteCoverageProven=false` and NO-GO comments correctly prevent treating this as migration permission, but they do not close the source-level admission bypass. The task requires durable STOP/restart fencing before any future pinned import.

## Required correction
Introduce an independently authenticated, monotonic enabled/stopped binding to the original writer journal (including journal identity and authority scope), such that missing, changed, replaced or unreadable configuration after activation fails closed rather than reverting to default-off publication. Keep the genuine not-yet-activated default-off behavior unchanged. Add hermetic tests for STOP followed by restart with missing binding, substituted journal, and verified resumption/explicit reset denial. No cutover or live activation is authorized by this review.