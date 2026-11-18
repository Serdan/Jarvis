# P1: Local B1 scope accepts a different original ledger ID

**Frozen source:** `b9e0d5fab7a3d2906c3d71b0f44537e8ba2263ae`, `Server/WorkSqliteDomain.fs:21-25,71-76,280-321`. Compare `Server/WorkLegacyImportProjection.fs:29-36` (original project evidence includes `LedgerId`).

The closed B1 body requires `ledgerId`, but validates it only as any nonempty UUID. `Expectation` has `AccountId`, `ProjectId`, `RepositoryId`, `SourceRef` — **no expected original ledger ID**. `normalizeStructure` compares account and project body scope, and source repo/ref, but never ties `ledgerId` to the expected canonical project ledger.

Concrete source-only counterexample: take a valid Project (or other kind) fixture with matching account/project and source, replace only `body.ledgerId` with `99999999-9999-4999-8999-999999999999`; `checkProperties` accepts the UUID, `checkSpecific` does not inspect it, and the function returns `Ok`. The test `original_source_scope_and_missing_receipt_never_promote_caller_strings` covers wrong account/project/repo/ref, not a wrong ledger.

**Why blocking:** The bounded task calls for fail-closed original project/account/source scope and cross-project origin invariants. A structurally valid B projection should not silently normalize a row labeled as a different original ledger within the same project. This is not a claim of a presently active ingress path; source/receipt authenticity remains blocked independently.

**Required correction:** Add the expected canonical original project ledger ID to the *local* expectation/context and enforce equality with `body.ledgerId`, rejecting a wrong-but-well-formed UUID. The upstream C owner must eventually authenticate that expectation; a caller-supplied equality is never production Verified. Add positive/negative ledger identity tests on the closed parser.