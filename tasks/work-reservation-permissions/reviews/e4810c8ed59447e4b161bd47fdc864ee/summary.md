# Independent Reviewer 2 — Accepted, narrowly scoped WQ11 P1 source-provenance remediation

**Review:** `work-reservation-permissions/e4810c8ed59447e4b161bd47fdc864ee`  
**Exact frozen source:** `efa4bea200bee5316cd848b6c409c8a1da9d5195`  
**Previously rejected source:** `58d4223c6c5e0ff20e41d61c170a15d2ab668fac` (Reviewer 1 finding `findings/source-scope-pinning.md`, decision seq 5245).  
**Original task base:** `dfb73853bac06e3076a4e546517a2a640f493418`.

## Independent source review

- Used isolated, clean `review/reviewer2-wq11-source-efa4-20261116` worktree, independently confirmed `HEAD=efa4bea200bee5316cd848b6c409c8a1da9d5195`, and verified rejected `58d4223` is an ancestor. The correction is confined to `Client/Modules/WorkQueueReservationPermissions.fs`, its NUnit test file, and `docs/work-reservation-permissions.md`; no main push.
- `reviseWithSourceAt` uses the **winning original Git CAS** to resolve WQ02 original credential verifier/admission, canonical credited or claimed Work, account, project, launch and current stage. The grant stores source `ReachableRef` separately from approved destination `TargetRef`, and `ContinuationSha` (falling back to first-generation `PinnedBaseCommit`) separately from source repository ID, lineage generation and claim generation. These are not caller-selected source authority.
- `pinSource` constructs the original `wq03-{workId}-{launchId}-g{claimGeneration}` identity, checks bound ledger/approved fetch and push identity, then invokes WQ03 `verifySourceClaimCheckout`: registered linked detached worktree, exact expected HEAD, approved remote and continuation provenance, clean status, worktree name and physical selected path. The absolute path is persisted in the grant and checked again at source authorization. A different valid/same-name worktree in another clone cannot substitute for the granted physical checkout.
- `authorizeWithSourceAt` compares the canonical source/target/repository/lineage/claim/path against **both** the stored grant and the operation's requested source identity; checks original bearer SHA-256 in fixed time, registered non-stopped launch, lease, Work, account, grant revision, expiry and family. Plain `authorizeAt/checkAt` cannot approve a source operation without a trusted source checkout. Planning/non-source revisions and revocation still work without a source checkout. The requested source family is not inferred from a caller role.
- Code and fixture audit: bearer does not enter the grant/revision intent or serialized record; source and old Planning fixture inspect the ledger without exposing the raw bearer. Existing public route is not wired to these new internal functions; `git grep` found source helper calls only inside this module, and tests are the consumers.

## Independent .NET 11 Release evidence

SDK `11.0.100`; `dotnet restore Client.Tests/Client.Tests.fsproj --ignore-failed-sources` exit 0; `dotnet build Client.Tests/Client.Tests.fsproj -c Release --no-restore` exit 0 (3 pre-existing warnings; 0 errors).

Actual `dotnet vstest` executions on `Client.Tests/bin/Release/net11.0/Client.Tests.dll`:
- `FullyQualifiedName~WorkQueueReservationPermissionsTests`: **7/7 passed** (real independent bare Git remote and two clones; no skipped).
- Combined `WorkQueueGitLedgerTests | WorkQueueCredentialTests | WorkQueueCanonicalSourceEvidenceTests | WorkQueueReservationPermissionsTests`: **44/44 passed**.
- Additional `WorkQueueReviewIntegrationAdapterTests | WorkQueueToolFacadeTests | WorkQueuePracticalAdmissionTests`: **24/24 passed**.
- `git diff --check 58d4223c efa4bea2`: exit 0; final checkout clean and at exact SHA.

Source-stage fixtures assert an initial source Implementation approval and CAS claim, a real WQ03 yielded successor WIP ref and SHA rather than TargetRef, and denial of foreign valid worktree, same-name checkout at another physical path, valid foreign source ref, wrong continuation SHA, swapped target ref, wrong claim generation and prior/foreign credential. Planning, stale revision/lease, revocation and nonrunnable blocker-proposal checks remain positive/negative as appropriate.

## Explicit evidence boundary — NO production GO

- No genuine approved **Integration stage** with original WQ05/WQ04 admitted source lineage and authority is exercised. The new source Integration gate is fail-closed; the review does **not** pretend the positive path is proven by toggling `IntegrationAuthorized` in an artificial fixture. A separate end-to-end original Integration-source authority test remains required.
- No OAuth-authenticated operator grant host bridge, trusted production clock, public Work-native/source-job mutation adapter or live scheduler is connected. `operator:[hex]` is an internal actor assertion, **not** OAuth evidence. `checkSourceAt` is a preflight, not an OS sandbox or an atomic Git+filesystem transaction. Actual source operation adapters must bind the physical checkout and reverify at execution.
- **Additional pre-cutover workflow concern:** the reused WQ03 `verifySourceClaimCheckout` requires a clean working tree on every source authorization. An in-progress Implementation may deliberately be dirty after edits, so per-operation host integration will need a separate claimed-worktree integrity policy/test rather than treating the claim-time clean preflight as unconditional permission for all subsequent edits/test calls. This is not evidence of an existing live bypass because there is no live source operation adapter.
- Prerequisite proposals are evidence-only (`ProposedOnlyNotRunnable`), not graph-linked runnable work. Default-off, no Scheduler, production migration/cutover, live source job, hosted GitHub Actions or Codex.

**Verdict: Accepted for the exact frozen P1 correction to the internal, default-off source-scope-pinning contract only.** This does not accept production operational permission isolation, a real Integration positive, host launch/approval, or cutover readiness.
