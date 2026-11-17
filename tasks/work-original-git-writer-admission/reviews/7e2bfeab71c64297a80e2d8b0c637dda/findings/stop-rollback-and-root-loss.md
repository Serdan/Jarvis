# P1 — STOP can be bypassed by a stale same-identity journal or full binding-root removal

Frozen corrective head: `9f5b32ad1cf0dde8ba34da7fbdc294327b6f9d93`; original prior reviewed head: `ab6ecdb0d952f348ecaea504c59a4ed0c875aba9`; base: `35f675cc1564710d10fdb2f50a03edff39368a28`.

## Independently verified source path

In `Client/Modules/OriginalGitWriterAdmissionJournal.fs`:

- `provisionDisposableJournal` creates one `journal_identity.journal_id` UUID. `stop path` updates only `control.closed=1` in this same journal; it does not advance any independent host-owned STOP watermark.
- `bindingText` pins just `OriginalWriterHostBinding/v1`, journal path, and `journal_id`. `readHostBinding` validates the binding files and reads `journalId` from the currently named file, but does not verify `closed`, its epoch, a monotonic high-water mark, or anti-rollback evidence outside that journal.
- `configuredPath` returns `Ok(Some pinned)` when that path and UUID still match. `admit` then reads `control.closed` from the supplied file and permits a fresh operation when it reads zero.
- Separately, `hostRootState` returns `Ok false` when the entire host-binding directory is missing. With the environment unset, `configuredPath` returns `Ok None` and `withPublication` directly calls `publish()`, even for a host previously activated and stopped. Only deletion of the two files *while leaving the directory* is denied.

## Concrete failure sequences — source-established, not dynamically reproduced

**Same-identity rollback with intact binding:** (1) Provision journal A and activate, retaining a durable pre-STOP backup of A. (2) STOP A, persisting `closed=1`. (3) Restore the pre-STOP A at the same path, preserving its `journal_id` but restoring `closed=0`; keep the separate host binding unchanged. (4) On restart, `readHostBinding` accepts the same path and identity, and `admit` sees open control and permits a new Git publication. The persistent host binding thus prevents substitution by *different* UUID, but does not make STOP monotonic against a stale journal rollback. This is directly relevant to stale restore/Recovery fencing requirements.

**Full binding-root loss:** (1) Activate and STOP A. (2) Remove the entire `original-git-writer-admission` host-binding directory (not merely the two files). (3) Restart with the journal environment variable absent. (4) The code classifies the host as never activated, returns `Ok None`, and resumes the original direct-push path. Local directory loss cannot silently act as authorization to re-enable original writers.

## Required correction

Persist and independently verify a monotonic, restart-stable STOP/activation authority that cannot be rewound by replacing the journal with an older same-ID copy, and fail closed when the host binding root itself disappears after prior activation. If loss of all independent authority is inherently unprovable, surface an explicit mandatory Recovery/NO-GO state rather than treating absence as proof of never-activation. Preserve the explicitly genuine preactivation default-off path. Add hermetic remote-ref negative controls for (a) same-ID pre-STOP journal rollback and (b) entire host-binding-root deletion with environment loss, showing no post-STOP Git effects. Keep route coverage and migration cutover NO-GO.

The new tests do cover different-ID journal substitution and deletion of the *files* in a surviving binding directory. They do not cover either sequence above. No source edits, merge, cutover, deploy or scheduler activation were performed by the reviewer.