# P2 — project/account source-target validation is skipped, so malformed authority is classified as Incomplete

**Task:** work-c-original-git-reader-reinspection-negatives-20261118  
**Claimed review:** e20cd3ecdb3b4b88b6097b4870c18936 (Reviewer 1)  
**Frozen source:** c2fb80500c42bf6afcd0a36eace7ff3363f371bb  
**Location:** Server/WorkLegacyImportAuthenticatedReadDiagnostics.fs:116-158, especially 130-139; Server.Tests/WorkLegacyImportAuthenticatedReadDiagnosticsTests.fs:44-51,183-189  
**Canonical comparison:** Server/OriginalGitPublisherAdmission.fs:94-140; Server/WorkLegacySourceWitness.fs:251-287  
**Severity / disposition:** P2 correctness and typed-classification gap; blocks acceptance of this negative diagnostic. Not a production-positive Verified bypass.

## Observed issue

The corrected checkJournal path now applies OriginalGitPublisherAdmission.validateTarget to source-effect journal targets and checks ordered receipt sequences, distinct effect UUIDs, SHA-256 shape, Git OID shape, durable operation correlation and digest. That resolves the previously reported journal-specific omission.

However, the *parent account/project ledger* path still does not call validateTarget. checkLedger merely requires nonempty target.CanonicalRepositoryId, ApprovedRemoteFingerprintSha256 and GitRef, with equal ledger/epoch/heads; it also never requires target.CanonicalRepositoryId = fence.Binding.TrustedRemoteId, a matching fence Binding.Scope, or a valid Git OID for CurrentRemoteHead. Canonical SourceWitness.checkLedger explicitly validates these properties (lines 262-287).

This gives a concrete misclassification in the submitted tests themselves: originalCapture() constructs AccountLedger at refs/heads/account and ProjectLedger at refs/heads/project (tests lines 44-51). OriginalGitPublisherAdmission.validateTarget permits only refs/heads/jarvis-account-admission for AccountLedger and refs/heads/jarvis-orchestration for ProjectLedger (lines 98-99, 130-140). Both fixture refs therefore fail the canonical validator. But the diagnostic's checkLedger never checks them, and the fixture-only echo test (lines 183-189) asserts that all eight kinds, including Project, yield Incomplete. The exact frozen .NET 11 Release diagnostic suite passed 15/15; that test result demonstrates the current classification on those fixtures. For Project the subject exists, so the result reaches the generic terminal Incomplete path at diagnostics lines 261-265 instead of rejecting the known malformed ref.

Additional static variants are an invalid nonempty approved fingerprint, a target repository differing from fence.Binding.TrustedRemoteId, or a non-Git CurrentRemoteHead mirrored in fence.FencedHead; the diagnostic does not reject these shapes in its account/project gate. No new independent executable malformed-ledger regression was added by the reviewer: these variants are source-inspection counterexamples, not claimed observed test outcomes.

## Why it matters

The canonical P1 task requires precise Incomplete/Unknown/Rejected negative classifications for authenticated project/account binding and remote effect evidence. Incomplete should mean a required independent authority is not established, not that a *known invalid* account/project Git authority/ref has silently passed shape and scope checks. The C1 source-witness path already distinguishes these. The probe remains default-off and never emits production Verified, so this is a diagnostic correctness gap, not writer STOP removal or live cutover risk.

## Required correction

1. Reuse the canonical publisher target validator for every account/project ledger target before accepting its binding as coherent. Also align the minimal remote identity, fence Binding.Scope / TrustedRemoteId and fenced-head OID checks with existing SourceWitness invariants; do not introduce an alternate weakened validator.
2. Change the test fixtures to canonically allowed AccountLedger/ProjectLedger ref names. Add red-first negatives for malformed ledger ref, invalid SHA-256 fingerprint/repository ID, swapped trusted remote, wrong binding scope, and invalid remote Git head with otherwise coherent fences.
3. Assert Rejected for known invalid account/project target/ref identity and the appropriate Unknown for uncertain external observations. Retain Incomplete for absent independent authentication and the terminal default-off no-production-auth result. Preserve all original-Git writer STOP, live import, scheduler and cutover HOLDs.

**Evidence limits:** static inspection plus independently executed focused and adjacent Release tests; no new negative test or code patch by this reviewer.