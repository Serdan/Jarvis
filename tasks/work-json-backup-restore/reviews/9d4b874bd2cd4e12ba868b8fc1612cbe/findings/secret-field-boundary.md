# P2 — Field-name allowlist does not itself prove bearer exclusion

**Frozen source:** `ecabbf023a6b750c2c15c92711048712de40f590`, `Server/WorkSnapshotCodec.fs:20-24,70-105` and `Server.Tests/WorkSnapshotCodecTests.fs` credential test.

The comment claims the closed vocabulary makes bearer plaintext unrepresentable. In reality `Fields: Map<string,string>` accepts arbitrary strings for allowlisted identifiers (`claimId`, `reservationId`, `operationId`, `workId`, etc.), including a scheduled bearer UUIDv4. The credential negative test only adds a forbidden field name or replaces `verifierSha256`; it does not establish that a secret cannot enter an allowed value.

A UUID-shaped bearer is indistinguishable by string syntax from a legitimate UUID-shaped record ID, so a regex filter is not an appropriate fix.

**Required:** Constrain or document the trusted typed projection boundary that constructs F0 records and guarantees secret material cannot be supplied as a generic logical field; do not claim the generic string codec alone proves this. Add a negative test or a type-level guarantee for mistaken bearer-as-identifier mapping before enabling F1 export. Ephemeral manual grants must likewise be absent at the source boundary.
