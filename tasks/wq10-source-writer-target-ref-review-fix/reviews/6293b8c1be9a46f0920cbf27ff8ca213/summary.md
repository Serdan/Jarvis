# Reviewer 2 — WQ10 source-writer target-ref identity correction

**Verdict: Accepted (no blocking findings)**  
**Frozen source:** `d035701dbfd7ed5d9c7b33e82cf7a9349afe501c`  
**Review:** `6293b8c1be9a46f0920cbf27ff8ca213`  
**Baseline:** `6fcd0f8812b3957b2d4dd6c9baf754c9dd00d303`

## Independent findings
The previous P1 false-green target-ref mismatch is corrected. `provenance.py:53-126` reads original per-task attempt JSON at the **same pinned project Git head**, requires matching canonical `sourceLineage.targetRef` and requires `targetRef`, WQ08 candidate `sourceRef`, and `sourceCommit` to coexist on **one** original attempt. It no longer treats separately aggregated refs/commits as a source-writer identity proof. Missing/invalid attempt files, mismatched targets, split tuples, missing repository evidence, and mismatched source identity produce explicit `SOURCE_PROVENANCE_UNVERIFIED` / `NO_GO` rather than `PREPARED_FOR_REVIEW`.

I independently cross-checked the hash formulas against `Client/Modules/LedgerStore.fs:1504-1529` (legacy source repository ID) and `Client/Modules/WorkQueueGitLedger.fs:283-288` (WQ03 canonical repository ID). Both are calculated from the originally approved fetch/push endpoints and compared with pinned evidence; an independently present `lineageId` must also agree. The existing pinned WQ04 source-origin Git blob, source-operation/ref/commit and role-version checks remain. Candidate bypass is still disallowed, and required inert `DispatchDisabled` / `AccountNotActivated` diagnostics are preserved.

## Reviewer-run validation
- `git show d035701d`: reviewed the exact source correction and added regressions (two-project Alpha canonical `refs/heads/unrelated` vs legacy `refs/heads/main`, matching two-project positive case, missing target, split attempt tuple, repository mismatch/missing evidence).
- `python3 -m unittest discover -s tools/Wq10MigrationPreview/tests -v`: **32/32 passed**, exit 0, 18.556 s.
- `dotnet build tools/Wq10MigrationPreview/Wq10MigrationPreview.csproj -c Release --no-restore --nologo`: **exit 0**, 0 warnings/errors, .NET 11 Release.
- `dotnet fsi scripts/test-client-hermetic.fsx -- --timeout-seconds 240 --filter FullyQualifiedName~WorkQueueSchedulerHostPreviewTests`: **12/12 passed**, exit 0, no timeout, disposable test environment removed; pre-existing F# compiler warnings outside this correction.
- `git diff --check 6fcd0f8812b3957b2d4dd6c9baf754c9dd00d303 d035701dbfd7ed5d9c7b33e82cf7a9349afe501c`: **exit 0**.

## Evidence boundary
Accepted **only** as the frozen, offline/read-only WQ10 preflight correction. The disposable fixtures and WQ08-shaped previews are not authenticated live captures. `PREPARED_FOR_REVIEW` is local consistency, not production authorization; `productionCutover=NOT_AUTHORIZED` remains unconditional. No Manager retirement, Scheduler/browser activation, epoch mutation, deployment, GitHub-hosted Actions, production cutover, or integration is authorized by this decision. Lead alone controls exact-SHA integration.