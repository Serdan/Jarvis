# WQ11 original Integration-stage source authority (default off)

This is a narrow, disposable-only proof for Work reservations. It does not create a public Work operation, enable Scheduler, or authorize an external Git push.

## What is authorized

The trusted host/operator may request the Integration family only when the current verified WQ02 project Git snapshot contains the original WQ04 Integration stage and its same-CAS immutable CanonicalWorkOrigin. That origin must point to a real SubmitIntegration operation, and the introducing Git commit, receipt hash and immutable original entity blob must match. The original and current WQ05 submission must be Pending and agree on submission UUID, Accepted review UUID, exact reviewed SHA, original source ref, and destination TargetRef. The original reviewer must have accepted that same frozen source before the submission and must not be the review owner.

The adapter derives temporary source lineage only from these canonical records, not from a caller-selected tree, role label, bearer claim or destination. The immutable frozen-review ref and reviewed SHA are the source, with the reviewed merge base and original target as destination. This does NOT write a fake lineage into WQ02 or invent a stage.

WQ03 still verifies the host-selected linked, detached, registered, clean checkout at the generation-scoped wq03-WORK-LAUNCH-gN path; the approved source remote and exact frozen review ref tip; HEAD and SHA; original repository identity; and base ancestry to the destination. The source and destination are distinct. WQ11 records actual checkout path, repo, ref, SHA, stage/claim generation and revision in its existing WQ02 Git grant. Each source operation and claim rechecks canonical WQ04/WQ05 proof and the WQ03 physical checkout at the winning CAS.

IntegrationAuthorized is a post-push finalization flag, not pre-claim permission. New Integration stages legitimately start false with no SourceLineage. Only an original canonical WQ04/WQ05 proof grants this default-off Integration reservation path.

\n## FrozenReview prerequisite: original Accepted decision\n\nA WQ04 Integration stage is not eligible until its original WQ04 FrozenReview hard prerequisite is completed. A generic WQ02 CompleteOrdinary on undecided review correctly fails. The default-off private fixture therefore uses FinalizeVerifiedReview: it checks the original WQ04 SubmitBranchReview source, the original WQ05 DecideBranchReview Accepted operation and reviewer independence, the currently claimed same-generation review stage, and the resulting WQ02 domain receipt inside the winning project Git CAS. Only then does existing WQ02 ordinary review completion unblock the actual original Integration stage. This is an explicit disposable bridge, not permission to invent a completed review or activate the public Scheduler.\n\nThe WQ03 generation-bound basename for a canonical hashed WQ04 Work ID plus launch UUID can be roughly 130 characters. WQ11 now permits that length only as an input syntax ceiling; it still requires the EXACT computed generation name and validates the real registered physical worktree, immutable source ref, HEAD and original repository.\n\n## Fail closed and production limitations

The operation returns IntegrationAuthorityUnavailable if the original integration/Accepted-review source join is missing, stale, mismatched or not bound to that exact WQ02 project stage. WQ03 rejects foreign, unbound, dirty, unregistered, wrong-HEAD, wrong-remote or stale-generation checkout. WQ11 rejects stale launch/lease generation, bearer, grant revisions, foreign Work and revoked authority.

This is an authorization decision inside Work reservations, not OS/shell permission, a filesystem sandbox, generic RBAC, unrestricted Git write permission, or an independent integration completion mechanism. No public MCP route calls these internal functions. No real private provider bootstrap, original source-effect terminal/unknown-send reconciliation, production Scheduler, fleet-wide old-writer fence high-water, multi-ledger atomicity or migration/cutover GO is implied. They remain separate required gates.

## Local evidence

Run: dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~WorkQueueIntegrationSourceAuthorityTests

The fixture exercises two independent clones and a disposable local remote, original review and integration WQ04 receipt production, an actual WQ03 generation-bound worktree, positive source grant and claim, and negatives for foreign checkout, credential, ref, SHA, target, claim generation, dirty/head, replay and revocation. Existing planning and implementation reservation tests should also remain green.
