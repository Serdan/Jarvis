# Independent Reviewer 1 — WQ10 correction

**Verdict: Changes requested (one blocking source-lineage finding)**  
**Frozen source:** `62d9b0f400a1e1c01519f73af1a6faeae963b2e7`  
**Baseline:** `b061ca525358f8616562e8911e3fbd6158c0b6d1`  
**Review request:** `6d59b6f4ad284d2eb17a46c990e88a6c`

The correction addresses the two findings from the rejected `3bf142f2` review substantially: `DispatchDisabled` is accepted only in inert `ReadOnlyDryRun/CanDispatch=false` mode at the matching project head, with missing/duplicate/mismatched diagnostics and adverse issues rejected. `requireCandidate=false` no longer waives the positive candidate requirement; no candidate is a non-green `NO_GO`. Local immutable WQ04 origin blob OIDs and core candidate fields are compared against the pinned project ledger. Cutover remains `NOT_AUTHORIZED`.

**Reviewer-run checks:** .NET 11 `dotnet build tools/Wq10MigrationPreview/Wq10MigrationPreview.csproj -c Release --no-restore` succeeded (0 warnings, 0 errors). `python3 -m unittest discover -s tools/Wq10MigrationPreview/tests -v` passed **27/27** tests in 13.0s, including the original two reviewer regressions and two-clone scenarios. `git diff --check` passed. WQ08 source at the reviewed head independently confirms the mandatory `DispatchDisabled` project-head issue and `AccountNotActivated` account-head issue. The host-shaped fixtures are not a live authenticated capture.

**New blocking finding:** [P1 — contradictory source-writer target ref still yields green](findings/source-lineage-target-ref-not-verified.md). The candidate validator checks that most canonical `sourceLineage` fields are nonempty but does not reconcile the queued writer's `targetRef` with independently inventoried legacy attempt refs. In a reviewer-run disposable two-project adversarial case, Alpha's queued lineage target was `refs/heads/unrelated` while its pinned legacy attempt target was `refs/heads/main`. WQ10 still emitted `PREPARED_FOR_REVIEW`, no blockers, and `sourceProvenanceVerified=true`. This violates the task's positive source-writer lineage consistency gate. Regression tests currently miss the case.

**Required before acceptance:** reconcile the source-writer identity tuple against independently inspected original attempt evidence, fail closed on mismatch/insufficient provenance, and add a targeted mismatch regression. Do not integrate this frozen branch until the finding is corrected and independently reviewed. This decision does not authorize production cutover, Manager retirement, Scheduler/browser activation, or mutation of ledgers.