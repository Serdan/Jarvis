# Independent Work/B B1 correction re-review — Accepted

**Review:** `e7f1d83b5b7c4b81a7a49ce0a5ef0931` (Reviewer 2)
**Exact frozen source:** `d603204d1305279dd3408832f24febbcde283d3b`
**Base:** `16dd6b1d717a3e24bb5450f58ca6ecefffcc9086`
**Prior ChangesRequested:** `8e457aad916b4a6da4b3d9987f09f7ef` at `b9e0d5fab7a3d2906c3d71b0f44537e8ba2263ae`, decision sequence 7598.

## Independent findings

1. **Original-ledger identity binding corrected.** `Server/WorkSqliteDomain.fs` extends `Expectation` with `OriginalLedgerId`, rejects malformed expectations and binds `body.ledgerId` to that exact expected original ledger in `normalizeStructure`. The added eight-kind test accepts the matching value and rejects a different valid UUID for each kind. This is a **caller-supplied structural equality**, not a trusted source attestation.
2. **Review decision states corrected.** `checkSpecific` now uses explicit disjoint cases: Pending requires sequence 0 and no receipt; Accepted/ChangesRequested require positive sequence and present receipt. The new regression test covers Pending with sequence only, receipt only, both, and final verdicts with either missing element, plus complete positive final cases.
3. **Boundary preserved.** Frozen commit contains only the B1 domain and contract-test correction; entire branch is four additive files (domain, tests, two compile includes). `git grep normalizeStructure` over the frozen Server/Server.Tests source reports the validator definition and only two test call sites—no production invoke or SQLite writer path.

## Independently executed checks

- `git diff --check 16dd6b1... d603204...`: exit 0, no output.
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~WorkSqliteDomainContractTests`: **9 passed, 0 failed, 0 skipped**; Release build succeeded (unrelated pre-existing compiler warnings).
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-build --no-restore`: **552 passed, 0 failed, 0 skipped**.
- Post-test registered worktree remained clean and `git rev-parse HEAD` returned the exact frozen `d603204d1305279dd3408832f24febbcde283d3b`.

## Decision boundary

**Accepted** for the frozen, default-off local typed/secret-safe B1 structural validator and its two requested corrections. No outstanding correction finding within this review scope. No authenticated original-Git producer/receipt provenance, external-effect authority, production Verified eight-kind ingestion, F restore, integration, deployment, Scheduler/API activation or cutover is established or approved by this verdict. Those remain subject to their separate upstream gates.
