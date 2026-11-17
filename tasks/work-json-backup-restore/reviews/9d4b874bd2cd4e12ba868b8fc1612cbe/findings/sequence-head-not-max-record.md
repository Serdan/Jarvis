# Blocking — committed DB sequence is incorrectly required to equal highest retained record sequence (F0-2)

**Frozen source:** `ecabbf023a6b750c2c15c92711048712de40f590`  
**Location:** `Server/WorkSnapshotCodec.fs:131-146`, including exact equality at line 146  
**Severity:** High — restore availability / semantic fidelity

The snapshot contract carries a *global committed sequence*. That sequence is a database revision; it need not equal the maximum `record.Sequence` of rows surviving in an export. A committed deletion, compaction, or transition that removes the latest row can advance the global revision without leaving any retained record at that revision. The codec rejects a coherent state such as an empty project with `CommittedSequence=2` after records were deleted, or retained records all at sequence 8 after a committed delete at sequence 9, because of `maxSeq = snapshot.CommittedSequence`. The existing `dishonest` negative case tests only an assumed invariant, not an actual transactional inconsistency.

**Required before acceptance:** Preserve `CommittedSequence` as an independent authoritative snapshot transaction value. Check retained row sequences do not exceed it; avoid inferring equality unless the authoritative schema explicitly guarantees a permanent event/anchor for *every* committed revision and the exporter proves that guarantee. Add round-trip tests for an advanced sequence with no surviving row at that sequence, including an empty-after-deletions database. Keep restore's torn-file sequence comparison.

No SQLite integration/F1 restoration is being approved here.