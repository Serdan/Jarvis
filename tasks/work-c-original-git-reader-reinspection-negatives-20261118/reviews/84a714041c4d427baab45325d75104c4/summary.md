# Independent review — Server C original-Git reader negative diagnostics

**Reviewer:** Reviewer 2  
**Task:** `work-c-original-git-reader-reinspection-negatives-20261118`  
**Review:** `84a714041c4d427baab45325d75104c4`  
**Verdict:** **ChangesRequested (P2 correctness/classification)**  
**Frozen source:** `dcd640eaf25d07ed654fe61b051e505a34a61f19` from `5db32e1f73b0c5b2c4988a002910832fbc3d174c`; review ref `refs/heads/jarvis-review/work-c-original-git-reader-reinspection-negatives-20261118/84a714041c4d427baab45325d75104c4/r1`. Canonical spec blob `48cfba4045b7ee139e0913a57659e03c99b0d3f2`.

## Scope and source provenance

The review ref resolves remotely to the exact immutable source SHA. GitHub comparison proves one commit on the approved base, with only four lease-compatible paths: newly added `Server/WorkLegacyImportAuthenticatedReadDiagnostics.fs` (236 lines), `Server.Tests/WorkLegacyImportAuthenticatedReadDiagnosticsTests.fs` (342 lines), and one additive compile include each in `Server/Server.fsproj` and `Server.Tests/Server.Tests.fsproj`. Worker4's registered branch is at the same SHA, clean and Ready; no integration submission exists.

## Test evidence

Independently ran `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~WorkLegacyImportAuthenticatedReadDiagnosticsTests --nologo` via Jarvis against the exact frozen registered worktree: **.NET 11.0 Release, 10 passed, 0 failed, 0 skipped**, process exit 0, not timed out. Compiler warnings refer to unrelated existing files. Worker4's immutable checkpoint 7823 additionally reports red-first FS0039, SDK 11.0.100, adjacent C0/C1/C2/Witness/B eight-kind **78/78** Release and `git show --check` clean; this adjacent count is **owner-reported, not independently rerun**.

The diagnostic stays default-off and always returns `Incomplete` on an otherwise successful synthetic capture; no production authenticated reader registration, writer STOP release, live import, scheduler activation or cutover is implemented. Tests cover all eight record kinds, deferred/orphan review and integration refs in disposable Git, missing anchor, forged digest, pending high-water, wrong project binding/epoch/head and malformed capture. This is not production source authenticity or native remote effect proof.

## Blocking finding

See `findings/source-effect-journal-validation.md`: the newly duplicated `checkJournal` accepts malformed original-source effect journal receipts that the existing `WorkLegacySourceWitness.checkEffectJournal` expressly rejects. Its weaker validation can classify corrupted/uncertain physical-effect evidence as a benign `Incomplete`, rather than `Rejected`/`Unknown`. The task requires exact negative classifications, even though no positive result can escape this probe. Please share the existing source-witness invariant checks or restore equivalent checks and add focused negative regressions.

**Boundary:** No code edits, merge, deployment, integration, operator approval or original Git live writer STOP changes were performed. Separate full authenticated reader/import tasks remain Blocked.