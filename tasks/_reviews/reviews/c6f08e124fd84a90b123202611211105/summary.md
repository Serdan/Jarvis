# Reviewer 1 — independent P0 correction verdict

**Review:** c6f08e124fd84a90b123202611211105 · **Decision recommendation: Accepted**
**Frozen source:** a29ed4bbcc71d75c834a30f3303ca675023a2f19 · **Base:** 2222f6e6bffc6c3b02403d79b54e3fa94cb64f47
**Earlier ChangesRequested:** c6f08e124fd84a90b123202611211103 on 7fc5440f6905718285403c92e62ce2f99dd25957.

The source worktree was clean and at the exact frozen head. Base-to-source changes are restricted to Client/Modules/ReviewWorkflow.fs and Client.Tests/ReviewWorkflowTests.fs. Compared with 7fc5440f, both historical managed-evidence and publication-proof group absence checks now consult the shared OrdinalIgnoreCase property-name set (lines 488–490, 514–530) rather than exact-case TryGetProperty. This closes the reviewed PascalCase/mixed-case exemption despite PropertyNameCaseInsensitive=true deserialization, without relaxing required-member or duplicate-key gates.

**Independent .NET 11.0.100 Release GREEN:** focused history/partial-proof/historical review tests passed **9/9**, no skipped; security filter for forged legacy-direct completion, trusted-writer boundary (2 cases), and genuine managed non-force publisher/atomic CAS passed **4/4**, no skipped. Exact source Client.Tests/Client.Tests.fsproj net11.0; no-build/no-restore on the second filter.

**Independent Release RED:** disposable worktree reviewer1-p0-case-red-20261121 pinned at original parser 7fc5440f; only Client.Tests/ReviewWorkflowTests.fs was restored from a29ed4bb, leaving original implementation unchanged. Same 8 parameterized partial-proof tests: **6 failed, 2 passed, 0 skipped**, exit 1, as expected. Explicit failed examples: PublicationMechanism, pUbLiCaTiOnMeChAnIsM, PublicationPreviousHead, and adjacent PascalCase ReviewRef. Failures report "Partial publisher evidence was accepted: Ok" and demonstrate partially materialized provenance. Matching corrected test cases pass.

**Security source inspection:** completeIntegration still uses LedgerStore.publishIntegrationTarget (bounded non-force push) plus remote verification; WorkCompositionAcceptanceAdapter.requireManagedCompletionAt still binds original immutable completion, verified publisher UUID/mechanism, and remote source/target lineage. Unmodified genuinely all-absent legacy records remain readable and are not treated as modern publication proof. The known same-privilege trusted-writer adversary model is explicitly outside this parser fix; do not claim it is solved. No main edits, deploy, restart, schedule or ledger fixture writes.

**Verdict rationale:** previously blocking case-variant read-integrity gap is fixed with deterministic RED/GREEN source-bound negative tests and adjacent security gates. No new blocking findings within P0 scope.
