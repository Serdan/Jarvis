# Independent correction review — Accepted (Reviewer 1)

**Task:** work-proposal-priority-projection  
**Review:** c3b18b974d8846a4a6f4a30e7b9d52c1  
**Frozen SHA:** 6a173e838b6a9b790ffbe1c720a6ddcee3952ab0  
**Prior rejected SHA:** 2d1b1a07c44cd420eb2a418fcc2895343ebc72c1  
**G0 base:** 7712bc547c264873036975e30194da03a05cc0ba

## Prior findings independently checked

Read Reviewer 5's exact-frozen summary and three findings from review 72cb37e14f6244b9a16e8298d2b70f8e (ChangesRequested seq5806). Verified changed source and regression fixtures:

1. **Lead priority/reason authorization (P1):** `VerifiedLeadProposalDecision` now binds `AuthorizedPriority` and `AuthorizedReason`; `verifyDecision` compares both to caller-supplied values along with Work ID, version, spec, action and verified receipt. Explicit internal original-producer bypass also binds `AuthorizedPriority`. Tests reject a mismatched approval priority (1 vs 999), mismatched SetPriority value/reason, and mismatched internal producer priority while retaining positive paths.
2. **Cancelled-ancestor eligibility (P1):** `WorkBlocking.activeAncestry` recursively checks the unit and all containing ancestors for cancellation, missing nodes and cycles, and `unfinished` gates both blocker propagation and `unitMayRun`. Negative test uses an Available child and grandchild beneath a cancelled System; neither is eligible or blocking. Explicit reopening restores the eligible path.
3. **Return-for-revision (P2):** `LastReturnedSpec` records the returned pin; unchanged Draft-to-Proposed submission yields `RevisionRequired`. `ReviseDraft` requires a new blob and next pin version; positive revised resubmission and fresh approval test remains.

## Independent evidence

- Exact SHA was present in clean registered worktree `work-proposed-priority-worker2-20261117`. G0-to-frozen diff touches only pure `Server/WorkProposal.fs`, `Server/WorkBlocking.fs`, paired tests and the two project references; no live Scheduler or database integration.
- `git diff --check 7712bc547c264873036975e30194da03a05cc0ba 6a173e838b6a9b790ffbe1c720a6ddcee3952ab0`: **exit 0, no output**; `git status --porcelain=v1`: empty.
- Independently rebuilt and ran `dotnet test Server.Tests/Server.Tests.fsproj --configuration Release --no-restore --filter 'FullyQualifiedName~WorkProposal|FullyQualifiedName~WorkBlocking|FullyQualifiedName~WorkQueue'`: **151 passed, 0 failed, 0 skipped**.
- Independently ran `dotnet test Server.Tests/Server.Tests.fsproj --configuration Release --no-build --no-restore`: **318 passed, 0 failed, 0 skipped**.
- No reviewer source edits, live Scheduler/cutover, hosted Actions or Codex.

## Decision

**Accepted.** The three prior findings are addressed in the frozen source with targeted negative fixtures and passing adjacent/full Release tests. Review applies only to exact SHA 6a173e838b6a9b790ffbe1c720a6ddcee3952ab0; integration and production cutover require separate gates.
