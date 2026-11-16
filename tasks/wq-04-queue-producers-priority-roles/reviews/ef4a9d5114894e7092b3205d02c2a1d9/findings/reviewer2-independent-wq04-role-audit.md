# Reviewer 2: independent WQ04 historical-role binder audit

## Frozen scope and provenance
- Review `ef4a9d5114894e7092b3205d02c2a1d9`, task `wq-04-queue-producers-priority-roles`, exact frozen head `8ec10490a080d41a33be73cde86c08031f38308b`, base `d3b7ab73f4616050e7f7cd54381267f63a519895`.
- Independently checked the three-file diff (103 additions, 23 deletions), the complete `WorkQueueCanonicalCasBinder.fs`, the accepted `WorkQueueRoleAuthority.loadAt` path, test fixtures, and the source/blocker document.
- Verified the reviewer checkout at that exact SHA and checked `attemptUnderCas` call sites across the F# source tree; only the binder's own definition and `Client.Tests/WorkQueueCanonicalCasBinderTests.fs` refer to it. No new production consumer.

## Findings
**No integration-blocking defects found in this inert-only change.**

1. `NewRoleDemand` rejects invalid identity/revision and requires a versioned role that is both the current revision and enabled in the verified historical catalog. `WorkQueueRoleAuthority.loadAt` verifies historical role issuance and original launch admissions at the pinned Git snapshot; a matching caller-supplied revision/catalog path is not a permit.
2. `PinnedLaunchRole` requires a matching project launch ID, role ID, and pinned version within the authenticated `loadAt` view. Forged/missing launch identity and stale/disabled role revisions remain untrusted.
3. Every role inspection still includes `RoleAdmissionUncovered`. Every binder assessment is `RecoveryBlocked`, with unconditional `SameCasProducerUnproved`; `attemptUnderCas` returns `IncompleteCoverage / RecoveryBlocked` inside the project CAS planner and publishes no receipt. No authorization/Ready/Accepted variant exists.
4. The new two-clone fixture covers missing role catalog, genuine enabled revision as read-only evidence, fabricated revision/launch, disabled revision and stale head, and confirms no Git ledger mutation under denied attempts. Existing tests cover fabricated source event, original operation UUID/hash, high-water and concurrent denied CAS.

## Independent execution
- Local .NET `11.0.100`, isolated review checkout `reviewer2-wq04-role-audit-20261116` at frozen head.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~WorkQueueCanonicalCasBinderTests`: **5 passed / 0 failed**.
- Combined `WorkQueueRoleAuthorityTests`, `WorkQueueCanonicalSourceEvidenceTests`, `WorkQueueCanonicalCasBinderTests`: **22 passed / 0 failed**.
- Release build/test command completed with exit code 0. Working tree clean prior to test run; no source modifications performed.

## Non-authorization boundary
Acceptance of this patch **does not** accept the broad WQ04 positive producer feature or authorize Scheduler execution, launch/admission/claim, source-host terminal assumptions, account credits, WQ03 handoff, main push, migration, or deployment. Complete canonical source/index high-water, side-effect/WIP lineage, and same-CAS project/account-credit proof remain missing as stated by the implementation. Any positive writer requires a new reviewed change and end-to-end adversarial tests.

**Verdict: Accept this frozen inert historical-role evidence audit only.**