# P1 — Accepted review/integration proofs lack Work-generation binding

**Frozen source:** `a7a835a85ff79667e54fed0ccc17e75dd4de2a21`
**Paths:** `Server/WorkRecovery.fs:29-57,291-332,409-444`

`SourceProof` carries `Generation`, which `sourceReceipt` compares to `work.Generation`. But `FrozenReviewProof` and `IntegrationProof` carry no stage/claim generation, Work version, graph/role lineage or exact review-to-integration authorization link. `reviewReceipt` and `integrationReceipt` require only matching project/Work ID, exact SHA, a positive verdict and caller-provided `CanonicallyVerified` booleans.

A *genuine, canonically verified* accepted review from an **earlier stage generation** can therefore be reused for a new Work generation if the source SHA has not changed (e.g., role/graph/spec revision invalidates authorization without changing the code commit). Construct `RecoveryWorkSnapshot` for generation 3 with a generation-3 valid `SourceProof` and an old accepted review/integration receipt originally issued for generation 2 at the same SHA; all checks pass. After the current generation's `ReleaseAuthorization` is constructed, `ReleaseAuthorized` can be true even though the required independent review/integration for that generation never happened. A verified historical receipt is **not** automatically an authorization for a new generation.

This violates G0's exact frozen provenance, replay and generation fence. Extend typed review/integration proof with independently inspected original stage/generation and relevant source/graph/role binding, and require exact match to the current Work before computing positive receipts or Recovery authorization. Integration must also bind its explicit authorization and accepted review receipt, not just a coincident SHA. Add negative tests where SHA is identical across generations but the accepted review/integration belongs only to the older generation, and a positive current-generation case.

The code is pure/default-off. No live cutover is implicated; this is a correctness requirement on the G0 authority proof itself.