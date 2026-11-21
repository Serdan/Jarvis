# External Git publication observation (Worker 21, Phase A)

Source baseline: `dcac884fc061869572b12a03befc12f11c009f2f`, task `integration-external-publication-observation-20261121`.

## Problem and exact boundary

`Client/Modules/LedgerStore.fs:2607` owns a single managed non-force publisher effect with an original-Git writer-admission journal. It deliberately rejects an already published integration tip without its *own* original before/after witness. `Client/Modules/ReviewWorkflow.fs:1343` routes modern `CompleteIntegration` through that managed publisher. `Client/Modules/WorkCompositionAcceptanceAdapter.fs:78-151` accepts only an original immutable canonical completion with `VerifiedNonForceGitPush/v1` and origin checks; no observation may satisfy that gate.

An ordinary authenticated Operator may have used normal Git tooling to push an independently Accepted, exact mechanical two-parent merge. Git can later prove only current branch inclusion and topology, NOT publisher identity, historical absence of force-pushes, or who performed the push. Those are distinct facts.

## Proposed contract

Create a **separate append-only** entity under `tasks/{taskId}/integration-observations/{submissionId}/{operationId}.json`, not a mutation to `IntegrationSubmission`, `CompleteIntegration`, original Git writer admission or WorkComposition. The entity carries: project-bound canonical operation identity and authenticated Operator actor; submission ID/task; exact submitted SHA; frozen Accepted review ID, review ref, version, reviewed head; exact two-parent integration merge commit, its first-parent target SHA, verified tree SHA; target ref, trusted submission-time target SHA; independently read remote advertised SHA and UTC observation time; `evidenceClass=ObservedExternalGitInclusionUnattributed/v1`, `publisherAttribution=Unattributed`, `managedPublisherProof=false`. This is a **current remote state observation** and no more.

A separate `ExternalObservations` read projection in `GetIntegrationSubmissions` exposes records while `Status=Pending` remains unchanged for the original submission. No managed completion operation is manufactured. The original compatibility reader does not need to accept new members on old IntegrationSubmission entities.

## Authority and source verification

Route the mutation via the existing TaskWrite `withAdministrativeActor` under the same configured project OAuth/trust binding. Actor is server-derived, not supplied by the caller. The canonical `LegacyDirectWriterFence.transact` provides a scoped immutable operation UUID, request hash, compare-and-swap journal, and explicit retry/unknown-result behavior. An unsuccessful/unknown operation must be inspected by original UUID; never generate a new UUID to replay an uncertain write. Source verification itself performs *no push*.

Before writing, verify the configured source binding, original Git history integrity (no shallow/graft/replace/alternate overlays), exact accepted review identity and independent reviewer, pinned `ReviewRef` and `IntegrationRef` fetched from source remote, immutable submitted SHA, and canonical Pending submission. `LedgerStore.verifyMechanicalIntegrationMerge` must check exactly two parents: verified first-parent target history, exact Accepted source as second parent, and Git ort merge-tree equality without conflict. Verify integration is an ancestor of the independently fetched remote head via `readRemoteTarget`; remote ahead is fine only by ancestry. Capture the actual commit tree and first parent from original source, not caller strings, into the immutable entity. If an observation for the same submission already exists, require its observed head to be an ancestor of the new remote head so later detected rewrites fail closed.

The recorder cannot attest that an unseen intermediate remote ref was never force-rewritten before the first observation. Therefore even a perfectly verified observation remains **publisher unattributed**. A force rewrite that removes any already recorded observed head fails the subsequent monotonicity check; one that preserves ancestry can still only produce a new observation, not a publisher receipt.

## Compatibility and noninterference

Never set `IntegrationSubmission.Status=Integrated`; never write any publication* fields; do not add an alternative accepted mechanism to `WorkCompositionAcceptanceAdapter`, any scheduler/launch gate or the legacy pre-v1 exception. Historical all-absent field groups in `ReviewWorkflow.validEntity` remain unchanged. New observation reader rejects mixed-case duplicate members, truncated/partial evidence, mismatched paths/IDs and noncanonical evidence classification. No untrusted caller claim about remote state is accepted.

Worker 5 owns the managed publisher-provenance/authenticated receipt boundary; this task touches no issuer trust root, `OriginalGitWriterAdmissionJournal` publisher code, nor managed receipt acceptance. Existing source may continue to be published through ordinary Operator Git, but this pathway records observation only.

## Disposable bare-Git adversarial fixture plan

Two work clones and one bare remote, with a separate canonical Git ledger. Start with target T, an independently Accepted source S and pinned remote review/integration refs. Build I=merge(T,S) with exactly two parents and Git merge-tree equality, publish I by ordinary Git in clone A, observe from clone B. Assert that the append-only observation records I/tree/T/remote SHA/actor, pending integration remains pending, managed acceptance remains rejected, and the remote is unchanged by observation.

Matrix:
1. Valid I at remote; remote advances to J descendant of I: accepted as observed J with I still exact. Repeat same UUID yields original immutable receipt; distinct UUID creates another observation only with monotonic ancestry.
2. Missing/incorrect review, unaccepted review, contributor posing as reviewer, mutable review/ref, source SHA, integration ref or target ref: reject; no record.
3. Unreviewed extra edit hidden in I, wrong second parent, wrong first-parent target, octopus merge, conflict or wrong tree: reject.
4. Remote target absent, ahead without I, rewritten to unrelated history, rewritten after prior observation to remove observed SHA: reject.
5. Grafts, replace refs, shallow and alternate object overlays: reject history proofs.
6. Mixed-case duplicate JSON, partial/mismatched evidence shape, malformed UUID/OID, cross-project identity or actor replay: reject; no managed acceptance.
7. Concurrent reconcilers on one operation ID: one ledger CAS outcome and identical replay receipt; different IDs can each observe but never modify submission.
8. Unknown remote transport: no inferred success. Unknown canonical write: inspect original UUID, never replay as a new managed publication.
9. Existing managed publisher positive and negative suites stay green. No absolute-time assertions.

## Phase B bounded source lease

`Client/Modules/ReviewWorkflow.fs`: new observation entity, path, read projection and mutation. `Client/Modules/LedgerStore.fs`: only a small read-only verified Git parent/tree extraction helper (no publisher modifications). `Client/Modules/OrchestrationApi.fs`: authenticated new TaskWrite action and read projection. `Client.Tests/ReviewWorkflowTests.fs`: disposable two-clone positive/negative regression tests, including managed acceptance remains unavailable. Avoid `Client/Modules/WorkCompositionAcceptanceAdapter.fs` and Worker 5 files. Source-bound `.NET 11 Release` tests and independent frozen review are required before any integration; no main edits, deployments, ledger rewrites or schedules.
