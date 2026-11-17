# Reviewer 5 — Accepted: corrected P0 original-Git publisher admission contract

**Review:** `e3eab74cbdf04b12833f7ae7285dc016`  
**Frozen source:** `70ed7631b9a0f25e5cba3f6822b453607d65e3ad`  
**Correction base:** `bfab99d09e221a2661453dce7bb5fad13bfaf743` (Reviewer 1 `ca4582f5ea744b9cb71ac82ee94130d7`, ChangesRequested)  
**Original integrated base:** `2cf1aa3625661aa24aba5c2118764afafe31e6ca`  
**Decision scope:** Corrected P0 source-only, explicitly default-off typed contract, audit and disposable negative-only pre-receive fixture. No P1/P2 authenticated gateway, positive quiescence witness, C3 promotion or production cutover acceptance.

## Independently verified

1. Exact correction `git diff bfab99d..70ed7631 --stat` changes **only three authorized files**: `Server/OriginalGitPublisherAdmission.fs`, `Server.Tests/OriginalGitPublisherAdmissionTests.fs`, and `docs/specs/work-original-git-publisher-admission-audit-2026-11-17.md`. Worktree HEAD was exactly `70ed7631b9a0f25e5cba3f6822b453607d65e3ad`, clean, and `git diff --check 2cf1aa3..70ed7631` exited 0.
2. **Reserved-ref alias fixed:** `validateTarget` refuses `OriginalGitRefPurpose.SourceEffect` with both `refs/heads/jarvis-orchestration` and `refs/heads/jarvis-account-admission`. New regression checks both `validateTarget` and `validateCas` negative cases.
3. **Physical ref collision fixed:** `validateFenceRequest` uniqueness is now by `(CanonicalRepositoryId, GitRef)`, excluding caller-disputed epoch/ledger/project/fingerprint; tests reject every conflicting variant and continue accepting the legitimate account+project fixture.
4. **Trusted-inventory consistency:** the newly added `validateTargetAgainstTrustedInventory`, `validateCasAgainstTrustedInventory` and `validateFenceRequestAgainstTrustedInventory` require exact registered record equivalence for a physical ref, fail missing and conflicting entries closed, and reject unregistered alternate source refs and spoofed project, ledger, epoch and remote fingerprint. This is intentionally **not** a source-authenticated inventory or live enforcement: the trusted list is an argument to a pure helper, so no future caller may treat self-supplied inventory as a remote witness.
5. **Git ref shape:** dot-prefixed path components (including `refs/heads/jarvis-review/.shadow`) are rejected with regression coverage.
6. Independently ran `dotnet --version`: **11.0.100**; `dotnet test Server.Tests/Server.Tests.fsproj -c Release --filter FullyQualifiedName~ServerOriginalGitPublisherAdmissionTests --nologo -v quiet` on clean frozen worktree, **6 passed / 0 failed / 0 skipped**, net11.0, with a fresh build. The existing disposable Git pre-receive test verifies stale clones fail at the bare remote and heads stay unchanged; this is a negative property only. Author reports full Server.Tests 441/441 and Client focused 11/11 + 19/19, but those broader results were not independently rerun here. Full Client.Tests remains unverified (author timeout).

## Explicit hard hold

**HARD NO-GO for production:** this commit neither implements nor authenticates a shared remote publishing authority, credential/channel inventory, serialized CAS+barrier acquisition, all-client original pending/source-effect high-water, GitHub rule provisioning or a positive C3 all-client witness. A blanket disposable pre-receive deny hook cannot provide those guarantees. Retain existing Git-ledger production authority and default-off behavior. Any P1/P2 implementation, verified all-client quiescence, C3 promotion, remote-rule/credential change, deployment or operator cutover requires separate design, evidence, independent review and explicit authorization.

**Verdict: Accepted for the frozen corrected P0 contract only.** No new blocking finding within that bounded scope.