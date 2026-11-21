# P1 BLOCKING — fabricated modern v1 publisher receipt accepts force-rewritten target

**Review:** `08fdc04c145540b29d6920bd38f946f1` (Reviewer 2). **Exact frozen source:** `2f478f79deb1fc4d1be3416550844692657d7ed7`. **Task:** `integration-accepted-source-diverged-target-20261121`. **Author:** Worker 5. This finding is **conditional on canonical-ledger write capability plus target-ref write capability**; it is not an unauthenticated MCP exploit.

## Observed defect

The source fixes the previous freshly forged *blank* legacy completion by unconditionally rejecting `legacyDirect` at `Client/Modules/WorkCompositionAcceptanceAdapter.fs:98-112`. It also parses and binds exact canonical operation arguments at `LedgerStore.fs:943-986`. But its replacement trust rule for modern completions merely checks **self-asserted** fields of the original `IntegrationSubmission`:

- adapter lines 114-124: `PublicationMechanism = "VerifiedNonForceGitPush/v1"`, `PublicationOperationId = completion.OperationId`, and well-formed `PublicationPreviousHead` / `PublicationObservedHead`;
- lines 125-134: recorded `previousHead` descends from captured submission target; reviewed S descends from recorded `previousHead`; recorded observed head contains S and remains on current target;
- lines 93-94: the original completion's canonical request matches its exact task, submission, and integration commit.

These are important but they **do not authenticate that** `ReviewWorkflow.completeIntegration` actually called `LedgerStore.publishIntegrationTarget` for the claimed operation or that the recorded `previousHead` was the authoritative *immediate pre-publication remote head*. A fresh generic `LedgerStore.transact` can originate a canonical `CompleteIntegration` operation (with authentic hash/trailers and exact correct canonical request), while writing arbitrary v1-looking publication fields. Origin immutability and canonical request binding establish *which arbitrary claim was first made*, not that its publication mechanism executed.

## Independent executable repro: disposable bare Git two clones

Independently executed source-linked **F# / .NET 11** scratch harness, loaded from the exact frozen Release-built libraries and the repository's actual `withTwoClones` helper. Generated only under OS temporary directories, removed afterward; did not edit the author's source. The real bare Git remote had two clones, a genuine independent Accepted review pinned to S, a managed pending integration, and a WorkComposition unit linked to the canonical task.

1. Capture base target B at submission. Produce accepted immutable source S from B.
2. In clone 2, commit an unrelated independent target advance U from B and push U to disposable remote `main`. Thus U is **not** an ancestor of S.
3. In clone 1, deliberately **force-push S over U** on that disposable remote; remote now advertises S. This is not the protected managed non-force publication.
4. Use a fresh, generic `LedgerStore.transact` of `Kind="CompleteIntegration"` to write a new Integrated receipt for S. Set `PublicationPreviousHead=B` (not U), `PublicationObservedHead=S`, `PublicationMechanism="VerifiedNonForceGitPush/v1"`, and `PublicationOperationId` equal to the fresh canonical operation UUID. Supply *correct* taskId/submissionId/integratedCommit request arguments, so the new request-binding gate passes. Backdate `CompletedAtUtc` to emphasize that timestamp is not trust.
5. Call `WorkCompositionAcceptanceAdapter.acceptIntegrated` from the other clone. **It returns Ok with `AlreadyApplied=false`**, accepting forged publisher provenance and the force-rewritten target.

Actual Jarvis job `a59588e2d00b4937aa1ba8f8abcc6155` (scratch-only, no production publication) printed:

```
INNER_OS_EXIT=51
SECURITY_RED: forged v1 accepted AFTER FORCE REWRITING U to S; AlreadyApplied=false
```

**OS exit 51 was intentionally emitted only on incorrect acceptance; exit 0 would have signaled rejection.** No F# compile/runtime error or fixture assertion failure occurred. A simpler real-bare-Git two-clone case with S already on remote, an externally non-force-pushed S, and a fresh fabricated v1 receipt also returned `Ok`: job `62b0356f82954b2f929901701f131009`, intentional OS exit 51.

## Required correction

Do not treat self-declared `VerifiedNonForceGitPush/v1` and caller-supplied before/after heads as an independently authenticated publisher receipt. Bind a managed completion to a **verified, durable, source-of-authority publication journal/receipt for the exact operation, target ref and B->S (or T->I) transition** produced by the protected non-force publisher, with immutable operation/ledger provenance. The journal must fail closed if missing, uncertain, contradictory, or copied from another operation, and must not be issuable merely by a generic canonical `transact` recording claims. If authentic publication provenance is unavailable, do not accept the managed receipt. Preserve bona fide modern direct and mechanical clean merge positives.

Add a **committed adverse real-bare-Git two-clone test** matching this exact U->force-S + fresh forged v1 receipt; demand `Error` and unchanged WorkComposition graph/CAS, plus a direct fresh self-assertion attempt without a managed publisher. Continue testing B->S legitimate direct and T+S mechanical merge, immutable reviewed source, graph CAS, and no replace/grafts.

**Trust boundary:** This attack fixture deliberately assumes the ability to originate a canonical ledger operation and perform an external push on a disposable bare remote, like the prior independently reviewed threat model. It establishes an insufficient *consumer acceptance predicate*, not a production credential compromise, remote-host branch-protection bypass, or the absence of legitimate publisher protections on the real `ReviewWorkflow.completeIntegration` path.
