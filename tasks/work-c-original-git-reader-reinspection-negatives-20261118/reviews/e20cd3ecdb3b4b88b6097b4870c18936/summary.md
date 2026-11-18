# Reviewer 1 — independent Server C journal correction re-review

**Verdict:** ChangesRequested (P2 typed-classification issue outside the repaired journal-specific cases)  
**Task:** work-c-original-git-reader-reinspection-negatives-20261118  
**Review:** e20cd3ecdb3b4b88b6097b4870c18936  
**Frozen commit:** c2fb80500c42bf6afcd0a36eace7ff3363f371bb  
**Base:** 5db32e1f73b0c5b2c4988a002910832fbc3d174c; **target:** refs/heads/main  
**Spec blob:** 48cfba4045b7ee139e0913a57659e03c99b0d3f2

## Source and provenance
The independently queried remote review ref refs/heads/jarvis-review/work-c-original-git-reader-reinspection-negatives-20261118/e20cd3ecdb3b4b88b6097b4870c18936/r1 resolves exactly to the frozen commit. Worker 4's registered worktree HEAD matches and its porcelain status is empty. Diff from approved base affects only the allowed two new Server C diagnostic/test files and additive Server.fsproj and Server.Tests.fsproj includes. git show --check returned exit 0. No integration submission was recorded.

## Independently executed .NET 11 Release
SDK 11.0.100 on the exact frozen worktree, all exit 0 and not timed out:

- Focused WorkLegacyImportAuthenticatedReadDiagnosticsTests: 15 passed, 0 failed, 0 skipped; built net11.0 Release with --no-restore.
- Adjacent WorkLegacy filter: 74 passed, 0 failed, 0 skipped; Release --no-build --no-restore.
- OriginalGitPublisher filter: 24 passed, 0 failed, 0 skipped; Release --no-build --no-restore.

Compiler warnings came from unchanged Program.fs, ClientService.fs and older tests, not the diff. Fixture/mock and disposable Git coverage does not prove native production authority or physical effect observation.

## Judgment
The five new malformed-journal regressions and the corrected checkJournal (source-effect target validation, contiguous sequences, one-shot IDs, hash/OID shape, operation correlation and digest) resolve the specific Reviewer 2 finding at decision 7856. The negative-only probe never returns production Verified; no source reader registration, live migration, original-Git writer STOP release or cutover occurs.

**Blocking P2:** The account/project ledger path in WorkLegacyImportAuthenticatedReadDiagnostics.checkLedger (lines 116-158) still omits OriginalGitPublisherAdmission.validateTarget and canonical trusted-remote/scope/OID checks. This permits known-invalid original Git account/project authority to be classified as Incomplete rather than Rejected. The existing fixture actually uses refs/heads/account and refs/heads/project, which canonical validateTarget disallows for AccountLedger and ProjectLedger; the executed echo test expects Incomplete across all eight kinds, including Project. The production SourceWitness.checkLedger (lines 262-287) rejects invalid target/ref and checks additional remote/fence invariants. See findings/ledger-target-classification.md for precise counterexample, evidence boundaries and corrective tests.

**Decision:** ChangesRequested, without treating this P2 diagnostic defect as a live authorization bypass. Canonical ref validation, trusted remote/fence checks, valid fixture refs and red-first malformed-ledger negative classifications are required. Original Git writer STOP, production source-authenticity gate, integration, scheduler, deployment and operator-only cutover remain HOLD. No code edits or lifecycle actions beyond review documentation/decision were performed.