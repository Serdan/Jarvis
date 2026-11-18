# Verification — guarded-delete UNKNOWN / settled-stage replay correction (no blocking finding)

**Independent reviewer:** Reviewer 1. **Request:** `c021a6a214194e859e6f527f33b5cf08`. **Frozen head:** `1a6a21419e859e6f527f33b5cf08b59016dc5ead`; **base:** `ae58c255078a6f5319559f65b598623533c5fcc8` (verified upstream `Serdan/Jarvis refs/heads/main`). Previous Reviewer 2 ChangesRequested `c21b5eae24114cd8866bf7092e0d8268`, decision **7902**, was acknowledged by Worker 21 and its complete summary/finding inspected.

## Previously blocking control-flow flaw

In predecessor `06642d2718f6a1f992f4e86c2b632ca202ab58b1`, `Client/Modules/LedgerStore.fs` `deleteReservedReviewRefForAction` returned `Ok "AlreadyAbsent"` when a physically absent ref was observed *before* entering the parent `withSourceRefEffectStage` admission, bypassing both Unknown and settled-stage replay fencing.

At the frozen corrected source, `Client/Modules/LedgerStore.fs:2545-2592` checks for a wrong-OID lease first (a rejection), but then enters `withSourceRefEffectStage` for `Ok None` **and** `Ok(Some expectedOid)`. Its parent/stage/ref/OID/request payload commitment is constructed at lines 2467-2502; the journal's `withPublicationClassified` at `Client/Modules/OriginalGitWriterAdmissionJournal.fs:642-676` refuses Unknown admission and settled replay before running publication. A genuine first-time absent-ref case reconfirms absence and returns `AlreadyAbsent` as proven no physical effect, not an authenticated delete; concurrent disappearance or unacknowledged delete remains `OutcomeUnknown`. The legacy delete leaf lines 2098-2117 does not settle a failed/unobserved push based on physical ref absence.

## Independently examined regression and positive evidence

- `Client.Tests/LegacyWriterProtocolFenceTests.fs:1630-1721`: disposable two-clone, real bare-Git guarded-delete cases for `lost-ack` and `concurrent-delete`, each repeating the identical parent/ref/expected-OID after physical absence. Both require `ExistingEffectRequiresReconciliation`, unchanged journal high-water/pending Unknown, and precisely one actual guarded-delete push.
- `Client.Tests/LegacyWriterProtocolFenceTests.fs:1723-1765`: successful deletion requires canonical receipt for settled replay, binds same parent against mutated request/ref/OID, and distinguishes a fresh no-push absent-ref rejection.
- `Client.Tests/LegacyWriterProtocolFenceTests.fs:1767-1780`: distinct never-activated host verifies legacy default-off absent-ref no-op.
- Other source-stage tests cover create-only source refs, orphan after later CAS conflict, changed parent payload, unacknowledged push, integration stage and STOP. The production `ReviewWorkflow.fs` diff is exactly three narrow call-site replacements passing the original `action`; PR #6 baseline deserialization compatibility is retained.

**Independent execution on registered clean Worker21 worktree:** `dotnet --version` = **11.0.100**. `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter 'FullyQualifiedName~ManagedSourceStage|FullyQualifiedName~ManagedIntegrationStage|FullyQualifiedName~StoppedSourceStage|FullyQualifiedName~ManagedGuardedDelete' --no-restore`: **9 passed, 0 failed, 0 skipped**. `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --filter FullyQualifiedName~LegacyWriterProtocolFenceTests`: **37 passed, 0 failed, 0 skipped**. `git diff --check ae58c255..1a6a2141`: exit 0. `git rev-parse HEAD`: exact frozen head; status clean, branch three ahead of origin/main. GitHub compare confirms exactly `Client/Modules/LedgerStore.fs`, `Client/Modules/ReviewWorkflow.fs`, `Client.Tests/LegacyWriterProtocolFenceTests.fs` modified, with 3 commits from the pinned base.

## Acceptance boundary

No blocking defect found in this bounded isolated local/default-off source-effect seam. This is **not** a physical hosted-Git actor/authentication attestation, full Client+Server .NET 11 Release eligibility, Windows test result, integration acceptance, STOP release, live publisher authorization, Work Scheduler activation, deployment, migration or cutover. Those remain UNKNOWN/HOLD and require independent gates. No implementation/source branch/merge/agent changes were made by this review.
