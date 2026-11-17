# Blocking — plaintext scheduled bearer can pass through an allowed field (F0-1)

**Frozen source:** `ecabbf023a6b750c2c15c92711048712de40f590`  
**Location:** `Server/WorkSnapshotCodec.fs:70-120`, `Server/WorkSnapshotCodec.fs:187-203` (field serialization)  
**Severity:** High — confidentiality / backup safety

`WorkSnapshotRecord.Fields` is a free-form `Map<string,string>`. The closed key vocabulary does not prevent a plaintext scheduled UUID bearer from being assigned to a *permitted* string field. For example, create a `ScheduledVerifier` record with `verifierSha256` set to a valid 64-hex hash and `reservationId = "d9f1234e-2e50-4f44-8b8e-d1d798292263"` (this value represents the plaintext scheduled bearer). `allowedFields` accepts both keys; `validField` only applies `legalId` to `reservationId`, so `exportSnapshot` succeeds and `writeRecord` writes the bearer verbatim into the readable project JSON. Analogous errors are possible in other unconstrained permitted fields. The existing negative test only tries an *unknown key* (`bearerToken`) or a non-hash `verifierSha256`, so it does not exercise this failure.

**Required before acceptance:** Make the scheduled credential backup model a typed, explicit nonsecret projection from authoritative state, with distinct verifier vs bearer representations and no way for a caller to substitute a bearer into the serialized projection; establish/verify this producer boundary and add a regression proving that a plaintext token supplied through an otherwise allowed field cannot be exported. A mere field-name allowlist is not proof of the no-plaintext-bearer requirement. At minimum, accurately narrow the F0 security claim and gate integration on a validated nonsecret mapping.

No production backup/deployment is authorized by this review.