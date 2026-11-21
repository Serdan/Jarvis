# P0 managed integration publisher provenance — fail-closed HOLD (2026-11-21)

## Scope and immutable review

Task: `integration-publisher-provenance-authority-20261121`, assigned Worker 5, generation 1. The original rejected immutable source is `2f478f79deb1fc4d1be3416550844692657d7ed7`; independently decided Reviewer 2 review `08fdc04c145540b29d6920bd38f946f1`, ChangesRequested sequence 9829. Its findings remain immutable.

A generic canonical `LedgerStore.transact` can create a genuine `CompleteIntegration` operation (correct project, operation UUID, canonical argument envelope, request hash, Git-trailer and immutable origin) while fabricating `PublicationMechanism = VerifiedNonForceGitPush/v1`, `PublicationPreviousHead` and `PublicationObservedHead`. A second process can independently force-rewrite the target from U to S. The consumer's original check accepted the forged completion even though `ReviewWorkflow.completeIntegration` and `LedgerStore.publishIntegrationTarget` never published that operation.

This is a conditional threat involving ledger-write **and** external target-push capability. It is not evidence of unauthenticated MCP access or a bypass of real GitHub protections. No production branch protection or operator permission boundary has been established here.

## Why no current durable trust root exists

- `ReviewWorkflow.completeIntegration` does check the current remote target, exact accepted SHA, review refs, mechanical merge I=(T,S), then invokes an ordinary non-force push and reads back the target. This is a safer **producer procedure**, not proof to an independent reader that that particular procedure ran.
- The canonical ledger transaction is also writable via generic `LedgerStore.transact`. Git commit immutability, request digests, actor string and publication fields can only prove which **claim** the generic writer first recorded.
- `OriginalGitWriterAdmissionJournal` is a local, default-off original-push admission/uncertain-effect journal, not a cross-client publication authority. `withPublicationClassified` deliberately calls `publish false` when journal admission is not configured. The journal does not durably capture an independently signed and externally verifiable exact remote before/after proof; `inspect` explicitly reports `RouteCoverageProven = false`.
- `Server/OriginalGitPublisherAdmission.fs` declares an `IOriginalGitPublisherAuthority` contract but states that no implementation is supplied or registered in production; `Server/OriginalGitPublisherExecutor.fs` is explicitly **disposable fixture-only**. Neither constitutes a live issuer, credential separation, or independently verifiable authorization for `ReviewWorkflow.completeIntegration`.
- A local 0600 file, a SHA-256 seal, a renamed field, a second ordinary ledger path, or an in-memory token does **not** establish a separate principal or trusted publisher identity. No protected signer, verifiable remote provider event, or dedicated append-only publisher service with separately enforced credentials was confirmed in this task. Do not pretend one exists.

## Implemented safety posture and deliberate loss of availability

The composition acceptance adapter still validates independent Accepted review, exact source SHA/ref, original immutable completion, canonical operation arguments, publisher-field shape, ancestry and mechanical merge where applicable. It then fails closed with the typed `PublisherProvenanceUnavailable` error rather than promoting unauthenticated v1 receipt fields into a WorkComposition graph CAS acceptance. The API maps this error distinctly; callers must not treat it as success, retry permission, or a transient publication failure.

This is an **adapter-level safety gate**, not a complete authorization boundary for arbitrary ledger writers. A principal permitted to call unscoped generic `LedgerStore.transact` can also attempt to rewrite WorkComposition graph bytes directly. A future authority design must separately constrain raw ledger mutations or enforce trusted graph-acceptance transitions at the actual write boundary; do not treat a guarded UI/adapter route as proof of global access control.

A genuine managed B→S direct or T→I=(T,S) non-force publisher can still complete the ordinary `ReviewWorkflow.completeIntegration` producer path. **Its completion cannot yet authorize WorkComposition acceptance** without independent publisher authentication. This is intentional; claiming end-to-end positive acceptance would be misleading. Replay of an uncertain external effect remains prohibited.

Pre-v1 managed direct receipts also remain rejected. No independently pinned historical rollout head/operation allowlist, historical positive, operator compatibility waiver or migration approval is available. Backdated dates, blank publisher fields and old-looking record shape are not origin proof. Branch-only legacy integration is a separate compatibility path.

## Minimum future authority contract (not implemented)

Lead/operator must select and explicitly authorize an independent publisher trust root, with an issuer whose authority is **separate from generic ledger mutation**. A possible design is a privileged publisher service issuing signed immutable receipts whose verifier pins the service identity outside the ledger's writable surface. That is only an architectural proposal, **not** an existing service or permission claim.

A valid receipt would have to bind all of:
- Project ID, ledger ID, epoch, canonical CompleteIntegration operation UUID and immutable original operation/request/receipt hash;
- Task ID, submission ID, accepted review ID/version/ref and exact reviewed source S;
- Approved target remote identity and ref, submission baseline B, verified immediate pre-push remote T, published I and observed post-push target;
- A publisher-authenticated, server-enforced non-force compare-and-swap/fast-forward transition T→I and independent readback, with explicit operation success rather than unknown outcome;
- Immutable historical provenance, anti-replay, cross-project/target/review binding, freshness and recovery fencing.

A Git history ancestry check alone can never establish what an independently writable remote ref pointed to *before* an external force update. The trust root must establish that observation outside both the generic ledger mutation and a caller-supplied receipt. Only after actual authentication can the fail-closed consumer gate be replaced and legitimate managed acceptance positives restored.

## Evidence and limitations

Red-before regression: `dotnet test Client.Tests/Client.Tests.fsproj -c Release --framework net11.0 --filter FullyQualifiedName~compositionAcceptanceRejectsFreshForgedModernPublisherAfterExternalTargetPush --verbosity minimal`; actual OS exit **1** with both `false` (external ordinary push) and `true` (independent U followed by force U→S) fixture variants failing as `SECURITY_RED` because forged receipts were accepted. Genuine disposable local bare Git with two clones; no live main/production push.

Green-after regression: recompiled exact isolated source with `dotnet test Client.Tests/Client.Tests.fsproj -c Release --framework net11.0 --no-restore --filter FullyQualifiedName~compositionAcceptanceRejectsFreshForgedModernPublisherAfterExternalTargetPush --verbosity minimal`; actual **OS exit 0**, 2 passed, 0 failed. The negative cases specifically require typed `PublisherProvenanceUnavailable`, unchanged graph and absent acceptance operation.

Composition ledger group: exact recompiled source with `--filter FullyQualifiedName~WorkCompositionLedgerTests`; actual **OS exit 0**, 11 passed, 0 failed/skipped. Existing genuine direct and mechanical merge **publisher** positives remain tested; their WorkComposition adapter acceptance must now return the typed HOLD. Separate transitive graph closure and CAS model assertions use an explicitly labeled test-only `executeVerifiedAcceptance` verifier callback, **not** the production adapter or claimed independent publisher authentication.

No managed historical pre-v1 positive, real authenticated managed B→S / T→I composition **acceptance** positive, or publisher signing proof is claimed. These remain blocked on the independent trust root and/or documented operator waiver.

Adjacent writer-fence/integration-source/CAS group: `--filter 'FullyQualifiedName~LegacyWriterProtocolFenceTests|FullyQualifiedName~WorkQueueReviewIntegrationAdapterTests|FullyQualifiedName~WorkQueueIntegrationSourceAuthorityTests|FullyQualifiedName~WorkQueueCanonicalSourceEvidenceTests|FullyQualifiedName~WorkQueueCanonicalCasBinderTests'`, `-c Release --framework net11.0 --no-build --no-restore`: actual **OS exit 0, 79 passed**, zero failures or skips.

A broader early run of `WorkCompositionLedgerTests|WorkCompositionQueueTransactionTests|ReviewWorkflowTests` had actual **OS exit 1** because original frozen-WQ02 tests expected a **positive authenticated managed acceptance** which cannot be shown without a publisher trust root. Seven positive-only frozen-queue test **methods** are retained unchanged but marked NUnit `Explicit(... HOLD)` rather than silently bypassing production proof or forging fixture publisher authority. Their internal Release/CAS/racing-positive expectations are therefore **NOT verified** under this source. They must be reactivated and tested with an authorized independently authenticated issuer before any production cutover.

In place of falsely accepting their success path, active `AuthenticatedPublisherAuthorityHoldMustNotReleaseOriginalWq02OrMintGraphCAS` constructs a genuine review+non-force publisher through two disposable clones and demands a typed hold, unchanged ledger head, graph, stage, and absent acceptance/release op on same-UUID retry. Other queue negative tests retain their original error oracles; do **not** broadly classify typed publisher-unavailable as success of unrelated structural rejection invariants.

Final combined targeted `--filter 'FullyQualifiedName~WorkCompositionLedgerTests|FullyQualifiedName~WorkCompositionQueueTransactionTests'`, `-c Release --framework net11.0 --no-restore`, recompiled exact final source: actual **OS exit 0, 14 passed, 0 failed, 0 skipped among selected tests** (11 composition + 3 active queue tests). The **seven Explicit methods were excluded from selection**, not counted as passing or skipped and not represented by this 14-test figure. Re-run them only after an authenticated publisher authority is implemented and operator-authorized.

The final frozen source SHA and independent review identity belong in the canonical task/review ledger, not a self-referential Git document. This is a **publisher-authentication HOLD**, not a cutover recommendation. Do not merge, deploy, activate Scheduler/Health, force-retry uncertain effects, or infer production publication authorization from this preparatory code.
