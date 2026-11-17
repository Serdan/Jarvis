# P1 — Committed sequence is incorrectly inferred from surviving records

**Frozen source:** `ecabbf023a6b750c2c15c92711048712de40f590`, `Server/WorkSnapshotCodec.fs:131-146`.

`validate` requires `snapshot.CommittedSequence = max(record.Sequence)`, or exactly zero when there are no surviving records. A consistent database snapshot after a committed delete/removal at sequence 9 with newest surviving record at sequence 8 is rejected. An empty logical database after nonzero committed history is also rejected. No F0 contract guarantees that every committed sequence leaves a permanently retained record or audit event.

The global committed sequence comes from the authoritative transaction snapshot; it need not equal a sequence on a live row. The current equality check can prevent backup precisely after a legitimate mutation, and could force misleading sequence changes.

**Required:** Validate that every record sequence is at most the authoritative committed sequence and all manifest/files agree, but do not infer the global sequence from the live-row maximum unless the model explicitly introduces a guaranteed, retained commit marker. Preserve the dishonest-sequence negative test by testing inconsistent manifest/data or a durable marker invariant. Add post-delete and empty-after-history success fixtures.
