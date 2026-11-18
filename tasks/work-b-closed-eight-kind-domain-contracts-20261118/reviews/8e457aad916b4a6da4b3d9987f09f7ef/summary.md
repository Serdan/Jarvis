# Independent Work/B B1 review — ChangesRequested

**Frozen request:** `8e457aad916b4a6da4b3d9987f09f7ef`  
**Exact source:** `b9e0d5fab7a3d2906c3d71b0f44537e8ba2263ae`  
**Base:** `16dd6b1d717a3e24bb5450f58ca6ecefffcc9086`  
**Boundary:** independent, default-off eight-kind structural-domain parser and negative-space tests. Does **not** imply a trusted original-Git producer, authenticated remote effect, Scheduler/host activation, verified eight-kind production ingress, F restore, acceptance or cutover.

## Verified observations

- Examined the exact clean frozen worktree, four-file diff (additive `WorkSqliteDomain.fs`, `WorkSqliteDomainContractTests.fs`, two compile includes), canonical task spec, B v2 authority/provenance, C1 original project/ledger identity, G recovery and unregistered negative-only source ingress.
- `git diff --check 16dd6b1d... b9e0d5f...`: exit 0, no output.
- Independent .NET 11 Release focused `WorkSqliteDomainContractTests`: **7 passed, 0 failed**.
- Independent complete `Server.Tests` Release `--no-build`: **550 passed, 0 failed**.
- Exact frozen `normalizeStructure` has no non-test call site and does not write SQLite. Existing positive fixtures are not trusted remote-source evidence.

## Blocking source-level findings

1. [P1 Pending review-decision inconsistency](findings/pending-review-decision-consistency.md): `Server/WorkSqliteDomain.fs:241-245` accepts Pending with positive sequence but null receipt, or Pending with a receipt but zero sequence. Required two-branch review decision invariants and negative tests.
2. [P1 Original ledger identity not bound](findings/ledger-identity-unbound.md): `Expectation` excludes expected `ledgerId`; the closed body accepts any well-formed ledger UUID even when account/project/source match. Required original project ledger equality in the local structural context, plus wrong-ledger test. That equality alone cannot produce Verified source evidence.

These are precise source-derived counterexamples; no new failing test was injected into the author's worktree. Passing existing tests does not cover them.

## Decision scope

**ChangesRequested for this frozen B1 source only.** The requested corrections are local to Worker20's domain/test lease; no other source owner, integration, deployment, live writer STOP, migration, Scheduler/API, Recovery, or cutover is authorized. Resubmit a new exact frozen head to the shared review queue after fixes and tests.