# Independent P0 historical-integration compatibility review

**Original brief:** Independently review exact `7fc5440f`; repair historical schema-v2 integration reads without treating missing publisher proof as validated publication. Check partial proof, provenance and negative tests. No main push/deployment.

**Review:** `c6f08e124fd84a90b123202611211103` · `_reviews` · Reviewer 1  
**Source:** `7fc5440f6905718285403c92e62ce2f99dd25957`  
**Base:** `2222f6e6bffc6c3b02403d79b54e3fa94cb64f47`  
**Verdict:** **ChangesRequested** — `findings/case-variant-publication-evidence.md`.

## Reproducibility
- Claimed exact unclaimed request version 1; now claimed version 2; source matches frozen review snapshot and review-details head.
- Registered source worktree `operator-legacy-integration-read-fix-20261121` was clean, HEAD exactly matched source, and base was ancestor.
- Source diff only changes `Client/Modules/ReviewWorkflow.fs` and `Client.Tests/ReviewWorkflowTests.fs`; reviewer made no implementation/main edits.
- Initial `agent_inbox` reproduced production `LedgerIntegrityError` on `tasks/agent-next-response-alerts/integrations/1ed9df3829b04a1e98cc27a0584a4a81.json` for missing required JSON member. Independent `reviewer_inbox` worked.
- Pinned .NET 11 Release source compiled; focused NUnit fixtures `HistoricalSchemaTwoIntegrationsAreReadableButPartialManagedEvidenceIsRejected` and `HistoricalIntegrationPublisherFieldsMustBeAllPresentOrAllAbsent` **passed 2/2**, no skips/failures, 2 seconds execution (`--no-build --no-restore`). The wider `FullyQualifiedName~ReviewWorkflowTests` run hit a 120-second timeout after successful compilation; do not claim wider suite success.

## Security and provenance
- Genuinely absent historical publication-proof members are left null and read-only. The managed `completeIntegration` publisher still executes bounded non-force Git publication and receipt verification. Composition acceptance still cross-checks original immutable CompleteIntegration operation and rejects blank managed publisher proof.
- **Blocking defect:** `validEntity` tests missing publication JSON keys with exact-case `TryGetProperty` (lines 519–537), whereas serializer/deserializer uses `PropertyNameCaseInsensitive=true` (line 426). Thus `PublicationMechanism` (PascalCase) alone is treated as an entirely absent group by the validator, then materializes as populated partial proof. The new test only inserts camelCase `publicationMechanism`, so it misses this bypass. Existing five-field managed-ref legacy group has analogous casing exposure.
- Add a negative fixture inserting a PascalCase proof field (and ideally each mixed-case variant) into an otherwise all-absent record. Require `LedgerIntegrityError`, then implement a consistent case-aware check or reject noncanonical evidence-key casing. Preserve genuine all-absent legacy compatibility and existing managed acceptance fences.

**Release gate:** Do not integrate source `7fc5440f` until the mixed-case partial-proof negative fails on current source and passes on corrected, independently reviewed source.