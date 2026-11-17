# Reviewer 1 — ChangesRequested (P0 source-only publisher admission)

**Review:** `ca4582f5ea744b9cb71ac82ee94130d7`  
**Frozen source:** `bfab99d09e221a2661453dce7bb5fad13bfaf743`  
**Base:** `2cf1aa3625661aa24aba5c2118764afafe31e6ca`  
**Scope:** P0 audit, default-off typed contract, disposable negative remote rejection only. **No approval** for P1/P2 live gateway, C3 importer, production host controls, or cutover.

## Positive evidence

- Diff is strictly additive: `Server/OriginalGitPublisherAdmission.fs`, its Server.Tests fixture, compile includes and the audit document. No existing `LedgerStore`, `AccountCapacityLedger`, Scheduler, Recovery, importer, backup, credential, or production publishing path changes.
- Audit enumerates project/account ledger initialize/record/transaction and review/integration source-ref publication/deletion, and explicitly identifies old-client/credential/remote-host gaps. It correctly refuses to equate local receipts or Git HEAD equality with authenticated all-client quiescence.
- Independent rerun on frozen worktree: `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-build --filter FullyQualifiedName~ServerOriginalGitPublisherAdmissionTests`: **5/5 PASS**, including actual remote rejection on three disposable bare repos. `git diff --check base head`: **PASS**. Author reports Server.Tests 440/440 and focused Client.Tests 30/30; full Client.Tests unverified/timed out, so those broader claims are not independently asserted here.
- The test's blanket disposable pre-receive deny hook is explicitly labeled negative-only evidence. It does **not** show atomic barrier installation, authenticated privileged publisher, complete credential inventory, or post-fence live GitHub enforcement; the audit retains a hard NO-GO.

## Required P0 typed-contract corrections

1. **`findings/source-effect-reserved-ref-alias.md`** — `SourceEffect` targets can name the exact reserved project/account ledger CAS ref when a nonempty ProjectId is supplied. Do not allow purpose/ref aliases that could change future admission classification. Bind the approved class to a trusted per-remote ref registry and add negative tests.
2. **`findings/conflicting-physical-ref-fence-targets.md`** — Fence target deduplication includes disputed ledger epoch/identity, so it accepts two inconsistent ledger epochs for one physical repository/ref. Reject contradictory mappings using the authoritative remote/ref key and test this collision.

**Small shape-validation follow-up:** `validGitRef` accepts `refs/heads/jarvis-review/.shadow` although `git check-ref-format` rejects it (observed exit 1). Check dot-prefixed components, and add a Git-compatible invalid-ref test; do not mistake syntactic validity for authorization.

**Re-review:** Submit a fresh frozen SHA after narrowly fixing the P0 contract/tests; preserve default-off isolation and the explicit NO-GO. Actual remote enforcement, positive all-client high-water witness, barriers, and production rule provisioning remain separately gated P1/P2/C3 work.
