# Scoped verification evidence

Task robustness-job-handle-diagnostics-20261127, spec 39773f089a13353ef05363997af11917785ba332, generation 1. Tested source 046088925aaf1f1ca584c0f4557f6ef9ef8e32c3, tree 672f212cde78a5c64249ac63cbee836203ad890b, baseline 291b5499051fbddfff1c76308e1a0202a5d1862f. The subsequent evidence-only commit does not change compiled inputs.

## Release run

`dotnet test tests/JobDependencyDiagnostics20261127/JobDependencyDiagnostics20261127.fsproj -c Release --logger 'trx;LogFileName=job-dependency-diagnostics.trx'`

Original promoted RunCommand handle 8e51a662b42247ffb353037040f61949, checkpoint 18967. Retrieved once on next activation as the initiating Worker 12: outer ToolCompleted, inner command ExitCode=0, TimedOut=false, no truncation. 2 passed, 0 failed, 0 skipped; 291 ms test execution. Compiler warnings FS0760 (existing Client Program.fs) and FS0988 (test assembly entry point), no errors.

Retained TRX at tests/JobDependencyDiagnostics20261127/TestResults/job-dependency-diagnostics.trx (local ignored artifact), SHA-256 98570cae693c284ab1dee8b5fc9518bc90bd4232613a87c958efcfac858d7154. Verified XML counters total=executed=passed=2, all failure/error/timeout/not-executed counters zero; both named cases Passed:
- PromotedWrapperDoesNotConferChildOrObserverAuthority
- DurableStatusUsesOriginalOwnerAndProject

Verified clean source checkout and exact HEAD/tree after run. The job result is not used as a substitute for its nested command exit or retained TRX.

## Evidence boundary

These are disposable characterization tests at the registered client dispatch seam, with synthetic durable receipts and an injected runner; they are not full end-to-end authenticated OAuth/MCP server tests, live supervisor restart/eviction tests, new observer-projection tests or actual browser launch tests. No production source, authorization, project registration, wake mechanism or external effects were changed. No claim of independent review, integration or deployment acceptance. The production acceptance matrix and explicitly bounded lease sequence in preflight.md remain outstanding for the separate authorized implementation. In particular, do not infer observer entitlement, durable correlation, or success from this characterization. No private cross-agent output was retrieved.
