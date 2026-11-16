# Independent review — optional review-dependent blocking

**Frozen source:** `da256ddbddb2e9b601edc878f1b4fa05d43e71e4` versus base `944bca7fa38cb5e973b04ce7829966cca351202b`.

**Verdict: ChangesRequested (P1).** See `findings/legacy-client-review-wait-bypass.md`: new review-only wait remains schemaVersion 2 with optional `reviewIds`; pre-change clients accept the unknown member and can manually requeue a review-only blocked attempt because its known challenge dependency list is empty. A compatibility/version gate is required before deployment to mixed-version clients. Add a two-version negative-space test to prove fail-closed behavior.

**Verified positives:** independent inspection of new typed block operation, common dependency CAS/reverse index, terminal review/withdrawal wakeup, owner-slot reconciliation, old challenge-only request-hash preservation, no notification or implicit integration, and new two-client/race cases. Independently ran .NET 11 Release `FullyQualifiedName~ChallengeWorkflowTests`: **46 passed, 0 failed**, and full `Server.Tests`: **277 passed, 0 failed**. Full `Client.Tests` run exceeded the bounded 240-second execution window, so it has **no complete pass verdict** (not a proven test failure); the author must supply complete full Client Release evidence to satisfy task acceptance.

**Nonblocking diagnostic observation:** when a review-only Eligible wait is reconciled after a busy owner's slot is released, `ReviewWorkflow.reconcileEligibleOnRelease` still records `ChallengeUnblock` and a challenge-specific reason. Preserve review-specific causality if possible, although the ordinary inbox `AwaitedReviews` keeps the terminal outcome discoverable.

**Integration recommendation:** do not integrate this frozen commit. Repair old-client compatibility and rerun targeted negative-space tests plus complete full Client Release validation; then submit a new frozen review.