# P1: Pending review accepts contradictory decision metadata

**Frozen source:** `b9e0d5fab7a3d2906c3d71b0f44537e8ba2263ae`, `Server/WorkSqliteDomain.fs:241-245`.

The predicate compares `decision <> "Pending"` only to the conjunction `decisionSequence > 0 && decisionReceiptId.IsSome`. It therefore accepts both of these structurally contradictory states:

- `decision="Pending"`, `decisionSequence=17`, `decisionReceiptId=null` (false <> false).
- `decision="Pending"`, `decisionSequence=0`, `decisionReceiptId=<nonempty UUID>` (false <> false).

Both are returned as `Ok(canonical body)`, provided the otherwise ordinary fixture is valid. This is a source-level counterexample, not an executed added failing test. Existing `Server.Tests/WorkSqliteDomainContractTests.fs:207-218` tests a completed verdict with a missing receipt, but neither malformed pending case.

**Why blocking:** The task explicitly requires closed review state and stale decision rejection. A body flagged Pending must not carry an earlier decision sequence or decision receipt; accepting either destroys the intended review decision/receipt consistency at the new typed boundary.

**Required correction:** Use explicit disjoint invariants: Pending requires sequence 0 and absent receipt; Accepted/ChangesRequested require positive sequence and present receipt. Add both negative cases and positive decision cases to the frozen contract tests. Preserve the structural-only/never Verified boundary.