# P1 — Recognized logical records need required fields and valid lifecycle states

**Frozen source:** `ecabbf023a6b750c2c15c92711048712de40f590`, `Server/WorkSnapshotCodec.fs:72-120`.

`validateRecord` checks that `Fields` is nonempty and each supplied key is allowlisted, but only `ScheduledVerifier` has mandatory fields. A `WorkStage` with only `priority=1` is accepted despite lacking `workId`, `queueId` and `status`; an `EffectJournal` with only `status=Unknown` is accepted without `operationId` or effect identity. The `status` validator accepts arbitrary ASCII letters rather than known lifecycle values. Both pass `exportSnapshot` and the corresponding import path if the manifest is canonical.

Restoring such a snapshot cannot reconstitute schedulable work or preserve the one-shot external-effect fence. This is a completeness/safety failure, not a whitespace or formatting concern.

**Required:** Define mandatory fields and per-kind state/enum invariants for the F0 logical record types (or reject records until typed/validated input is available). Add negative export and rehashed-canonical-import fixtures for incomplete WorkStage/Reservation/Receipt/EffectJournal and impossible statuses, plus successful representative round-trips. Keep F1 SQLite integration separate.
