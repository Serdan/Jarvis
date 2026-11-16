# Reviewer 3 — typed integration diagnostics / BN-042/043 independent review

**Verdict: Accepted** for frozen commit `d0164e0d2057c8b7e15484b59987b300db052db0`, base `b277cd0d1b850b97ad81d889e2278aaef74e74ae`, review `83e9d1e51d22414dac4bf5cdfa5b0229`.

## Historical root cause identified from exact feedback

Historical feedback `53c5fe17` (BN-042, **2026-11-05**) and `9be558bb`/`94f90b3c` (BN-043, **2026-11-06**) recorded the same rejected payload pattern: a normal source branch, exact committed SHA, `refs/heads/main` target, and **hyphenated D-formatted review IDs** (`40c72f08-66f3-4788-890b-3f45585d1d1b`, `c21ed646-0b7b-4e64-9c4b-402ec6c51f3d`). Error: `InvalidBinding "Invalid integration submission."`, before entering ledger/Git checks.

The historic integration code introduced in `1c47f730` on **2026-11-03** has an exact matching error string and a preceding predicate:

```fsharp
not (String.IsNullOrWhiteSpace reviewId)
&& not (match Guid.TryParseExact(reviewId, "N") with true, _ -> true | _ -> false)
```

This rejects both captured **valid D-format UUIDs**. These fields otherwise satisfy the syntactic predicates shown in that historic validator. Therefore the historical BN-042/043 *early generic InvalidBinding* is **explained by the N-only reviewId parser**, not by Git ancestry, source commit publication, task closure, or a review verdict. The generic message existed in source from Nov 3 until Nov 14. Commit `0e43b68d` on **2026-11-14** already replaced it with `Guid.TryParse` and normalization `Guid.Parse(...).ToString("N")`. Thus **the primary incident fix predates this current patch**. Deployment binary provenance at the moment of Nov 5-6 still was not independently attested, but the exact error string, predicate and captured arguments support this root cause directly; do not claim this patch introduced the D/N fix.

## Scoped source review

- This patch changes the broad preflight error to field-specific `taskId`, `actorId`, `submissionId`, `sourceRef`, `submittedCommit`, `reviewId`, `targetRef` diagnostics and distinguishes missing task, unregistered actor, review nonacceptance and mismatched accepted source/target/commit. D- and N-format UUIDs remain normalized in canonical request, and replay identity remains stable.
- Immutable review provenance, pinned SHA, target fast-forward ancestry, managed-ref creation, remote review-ref verification, and same-UUID retry behavior remain enforced. No integration or deployment is performed by the tool or this review.
- The new BN-042/043-shaped fixtures exercise **before-merge**, **after-merge and closed task**, D/N replay, wrong branch/ref/SHA, withdrawal, missing task and actor. These are isolated **shape reproductions**, not writes to Favn nor an independent reconstruction of the historical deployed binary.

## Independent evidence

- Reviewed frozen two-file delta, original historical validator, Nov14 migration, exact three feedback records, and existing MCP argument route (`Server/McpTools.fs` `SubmitIntegration`: `reviewId` passes via `TaskWriteCommand.Reason`).
- Independent Linux .NET 11 Release hermetic filter `CapturedBn042AndBn043ShapeCanSubmitBeforeAndAfterIntegration|IntegrationSubmissionDiagnosticsAreFieldSpecificAndTyped|IntegrationRejectsUnacceptedReviewsAndRetainsRewrittenIndependentSnapshots|AcceptedSnapshotCannotBypassAdvancedRemoteTarget`: **4/4 passed**, exit 0, disposable scratch removed.
- Worker reports 26/26 integration-focused client tests and 277/277 server Release tests; not independently rerun wholesale here. The complete unfiltered Client Release exceeded its 240s cap, so **full-suite passing evidence remains unavailable**.
- `git diff --check b277cd0d..d0164e0d` exit 0, Worker 9 frozen checkout clean. No source edits, main merge, production ledger activity, or deployment.

**Recommendation:** accept the diagnostic and regression patch as source correction; explicitly record the N-vs-D historic cause and Nov14 fix in the parent task/incident resolution. Do not present synthetic fixture success as proof of the exact Nov5/6 runtime version or claim full-suite acceptance.
