# Reviewer 2 — independent P0 integration-protocol re-review

**Verdict: ChangesRequested (one independently executed P1 blocking publication-provenance failure; one P2 historical-compatibility gap).** This review exclusively concerns frozen source `2f478f79deb1fc4d1be3416550844692657d7ed7` and review `08fdc04c145540b29d6920bd38f946f1`. Prior review `a5e0f973c25e4b68b53406c151cafa69` on `51cafa698a21f903c32f3a951aca43324b9fc636` remains immutable ChangesRequested decision 9783.

## Provenance and scope

Independently claimed canonical review at seq 9806. `git ls-remote origin refs/heads/jarvis-review/integration-accepted-source-diverged-target-20261121/08fdc04c145540b29d6920bd38f946f1/r1` **OS exit 0, exact SHA match** (repeated at completion). Author source ref also resolves to exact SHA. The new commit is an immediate child of rejected `51cafa698...`; original task base `d702352ef705f9991ac1cb14dc15a13cce78aa44` is an ancestor (**git merge-base --is-ancestor OS exit 0**). Rejection-to-frozen delta changes exactly three files: `Client/Modules/LedgerStore.fs`, `Client/Modules/WorkCompositionAcceptanceAdapter.fs`, `Client.Tests/WorkCompositionLedgerTests.fs` (**+130/-24**). Base-to-frozen delta spans nine source/test/docs files, including ReviewWorkflow, LedgerStore, acceptance adapter and managed-integration runbook. `git diff --check` from base to frozen **OS exit 0**. Registered isolated reviewer worktree `reviewer2-integration-p0-2f478-20261121` remains clean and HEAD exact frozen SHA.

## Verified fixes from earlier review

- `LedgerStore.verifyCompletionOperationRequest` (`LedgerStore.fs:943-986`) parses the immutable `CanonicalRequestJson` envelope and its embedded request, requiring exact schema/operationId/projectId/actor/kind and exact taskId, submissionId and integratedCommit with no extraneous fields. Independently inspected, and positive + malformed-argument negatives exercised by the focused suite.
- `WorkCompositionAcceptanceAdapter.requireManagedCompletionAt` (`WorkCompositionAcceptanceAdapter.fs:78-140`) obtains the original canonical `CompleteIntegration` operation and immutable receipt and rejects later altered/missing completion; newly manufactured *blank* legacy-shaped managed receipt now rejects unconditionally. A fresh timestamp/backdated date is not used as an epoch predicate.
- Ordinary `ReviewWorkflow.completeIntegration` still uses `publishIntegrationTarget`: checks current remote, source and merge ancestry and, for distinct merge I, exact T first/S second parent and mechanical Git merge tree; ordinary non-force push, independent readback, operation/publisher receipt, and CAS are retained. `GIT_NO_REPLACE_OBJECTS=1` and graft/shallow/alternate guards remain. These facts are about the legitimate writer's code path and its existing tests—not proof that an arbitrary v1-looking consumer receipt was written by that path.

## P1 BLOCKING: fabricated modern publisher receipt passes after force rewrite

Detailed reproduction and remediation: `findings/forged-modern-v1-publisher-receipt.md`.

The new discriminator treats the **original recorded v1-looking fields as trustworthy publication evidence**, despite no independent cryptographic/authoritative binding to `publishIntegrationTarget`. In an independently executed scratch-only two-clone real-bare-Git test, source S from B was independently Accepted; remote target advanced to unrelated U; a separate external `git push --force` replaced U with S; a *fresh generic* canonical `LedgerStore.transact` inserted a `CompleteIntegration` with exact valid canonical request and self-asserted `PublicationMechanism="VerifiedNonForceGitPush/v1"`, operation UUID, `PreviousHead=B`, `ObservedHead=S`. Adapter `acceptIntegrated` returned `Ok(AlreadyApplied=false)`. It **accepted the force-rewritten target without the protected publisher ever running**. Job `a59588e2d00b4937aa1ba8f8abcc6155` **OS exit 51 intentionally indicates security RED** and printed:

```
SECURITY_RED: forged v1 accepted AFTER FORCE REWRITING U to S; AlreadyApplied=false
```

Separate newly fabricated v1 receipt with external ordinary push also accepted: job `62b0356f82954b2f929901701f131009`, intentional **OS exit 51**. These scratch scripts live under OS temporary directories only and were deleted; they use the exact built source assembly and repository's `withTwoClones` bare-Git fixture, without author-source edits. This demonstrates a **conditional producer-authenticity gap requiring canonical-ledger write capability and an external target push**, not an unauthenticated production exploit or independent proof of compromised Git-host rules. It is nevertheless a blocking breach of the task's trust contract: acceptance must validate the publisher provenance, not self-asserted fields.

Required: an independently authenticated durable publisher operation/journal binding exact operation, target and before/after heads (and exact I/S/T), not mintable by generic `transact`; fail closed without provenance. Add committed two-clone U->force-S/forged-v1 adverse test asserting rejection and unchanged graph/CAS, retaining legitimate direct and mechanical merge positives.

## P2: historical managed pre-v1 positive not supported

`findings/authenticated-pre-v1-compatibility-unproven.md`: the new code unconditionally rejects *all* managed no-publisher-field direct completions, even a truly authentic old one, because there is no independently verified pre-v1 anchor/allowlist. This correctly prevents fresh legacy-shaped spoofing, but the instructed authentic pre-v1 positive is absent. No affected production record is established. Require immutable historical proof + positive regression, or explicit documented operator waiver/migration; do not reenable shape-only compatibility. Older *branch-only* entries are separately supported and are not the same class.

## Independent executed tests and limits

Exact isolated source, `dotnet --version` = **11.0.100**, Release `net11.0`:

1. Job `df4556491a844cc1b5614b046ae29222`: `dotnet test Client.Tests/Client.Tests.fsproj -c Release --framework net11.0 --filter 'FullyQualifiedName~WorkCompositionLedgerTests|FullyQualifiedName~WorkCompositionQueueTransactionTests|FullyQualifiedName~ReviewWorkflowTests' --verbosity minimal` — **OS exit 0, 171 passed, 0 failed, 0 skipped, 4m02s**. Restored and actually built `Client.Tests.dll`, then ran real tests; not a no-op.
2. Job `f0f1d0764d3c47ed938631110d6e815b`: same frozen Release build, `--no-build --no-restore`, filter `LegacyWriterProtocolFenceTests|WorkQueueReviewIntegrationAdapterTests|WorkQueueIntegrationSourceAuthorityTests|WorkQueueCanonicalSourceEvidenceTests|WorkQueueCanonicalCasBinderTests` — **OS exit 0, 79 passed, 0 failed, 0 skipped, 2m14s**.
3. Independent scratch F#/.NET11 real bare Git two-clone adversarial cases above: **OS exit 51 intentionally signals incorrect acceptance**, not compilation/test-discovery failure.

These 250 in-repo tests are distinct focused/adjacent groups. The **unfiltered full Client.Tests suite was not run** here. No production host branch-protection/credential separation was independently verified; local disposable bare Git can force-update its test branch by design. No author source edit, production/main push, source merge, deployment, scheduler activation, work-state cutover, uncertain-operation replay, or Codex through Jarvis was performed.

**Decision: ChangesRequested on exact frozen SHA.** Retain immutable rejection and request a fresh source commit and a new independently claimed review after authentic publisher proof, adverse fixture, and pre-v1 disposition. This is not approval to publish `main`.
