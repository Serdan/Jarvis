# WQ04 canonical-source / versioned-role same-CAS binding: blocked boundary

Status: **inert audit and negative-test seam only**. This work does not authorize a new producer, admission, claim, Scheduler, manager, host source operation, account reservation, migration, or deployment.

## Exact examined parents

* Current main `d3b7ab73f4616050e7f7cd54381267f63a519895`, containing independently Accepted WQ04 fail-closed correction `dc71085090e4c914a3a336baba248907a3fb3030`, canonical inert binder `76ca7864002157cde6901a562ea64dba8306e68c`, and Worker10 source evidence (reviewed/integrated separately).
* Worker10 `0ba335368d6f621f186fbc0e062833da8e0b8625`, source-event evidence reader independently Accepted (review `d82cb165f1a845939448f6d2ac601e9c`, seq3746). Its source was separately reviewed and integrated onto current main; this follow-up uses the mainline module without modifying Worker10's branch.
* Worker11's corrected historical role catalog `d44f40e029a2321beaaf5faa46347bc52cec67c4` is independently **Accepted** (review `d44f40e029a2321beaaf5faa46347bc5`, seq3893), composed with current main `9a09028ad0c4e2392ca755634a7a3c7328574924` and independently Accepted again (review `4c0e76df4bfb44408345d67b4ce97311`, seq3922). The earlier 1cd0/d2dac iterations were ChangesRequested; no positive role launch has been authorized.

## Proven vs missing

The accepted source-evidence reader proves the original canonical Git ledger **operation UUID, actor, action, original request/result hash, immutable event blob, introducing commit, entity path and exact observed high-water**. A fabricated event ID, review acceptance status, source SHA/target, role membership, or matching caller counter cannot replace the corresponding original receipt.

The reader intentionally returns **only `IncompleteCoverage`**. It does *not* prove the original source-mutating job/push journal, externally protected and retained WIP refs, completeness across all source/index families, or their atomic relationship to a project `WorkQueueGitLedger` CAS commit. The accepted WQ04 project writer has an unconditional `ProduceEvent` hold and ID-independent semantic guards in `PublishStage`, `PostMessage`, `AdmitProject` and `ClaimNext`. Those protections must remain in force.

The now-Accepted historical role module proves original role-issuance history, rejects retroactive legacy launch authority, and exposes versioned preflight which explicitly flags `RequiresCanonicalSourceAndAccountProof` and `RequiresSameCasChosenWorkAndCreditProof`. It is still **not** an executable joint producer/admission/claim writer with the WQ04 `LedgerStore.transact` project mutation and account-global launch-credit proof. A valid role ID, revision or original role receipt alone is not authorization.

The missing invariant is a **single authenticated, optimistic Git CAS transaction** that derives all writable outputs from one verified ledger head, including:
1. the exact canonical operation/origin receipt and all task/review/challenge/integration/message high-water coverage, with no gaps or caller-selected truth;
2. the immutable source-host side-effect/terminal proof and protected source lineage, or an explicitly separate recovery block where a cross-ledger atomic join cannot be shown;
3. the historical versioned role catalog and same-CAS compatibility for new demand, admission, unclaimed-credit rematching and claim, including v1/v2 disable races;
4. account-global launch credit authority and project admission/terminal receipt, consistently fenced against concurrent clients;
5. exactly-once terminal IntegrationPolicyResolved and honest WQ04 stage-generation correspondence.

The current trusted inputs do not satisfy that invariant. Joining two **read-only** preflights after the fact is not a substitute for the writer's original CAS planner. No server-local SQLite authority or alternative counter is acceptable.

## This branch

`Client/Modules/WorkQueueCanonicalCasBinder.fs` exposes a typed original-event request and role scope, returning **only `RecoveryBlocked of MissingAuthority list`**. Its role check now calls the Accepted `WorkQueueRoleAuthority.loadAt` at the *same pinned snapshot*, rejects missing/fabricated/disabled/stale version and unadmitted launch identities as `RoleEvidenceRejected`, but explicitly retains `RoleAdmissionUncovered` and the unconditional `SameCasProducerUnproved`. There is deliberately no `Authorized` case. An explicit `attemptUnderCas` regression seam calls the project's real Git transaction planner, re-reads the verified source event and project at its pinned snapshot, then **always returns `IncompleteCoverage / RecoveryBlocked` without any changes**. This is not called from any production route.

`Client.Tests/WorkQueueCanonicalCasBinderTests.fs` runs disposable two-Git-clone negative fixtures: authentic event still cannot produce, forged original UUID/request hash/event selector rejected, caller high-water/stale head rejected after another clone advances, authentic enabled role revision assessed read-only but denied under the real CAS planner, missing/unadmitted/disabled/stale role assertions rejected, and raced CAS attempts produce neither operations nor project changes. The accepted source-reader suite remains unchanged.

## Criteria before any positive implementation

Use the already **independently Accepted** historical role catalog and produce a separately reviewed transaction writer with authentic canonical origin/complete source and index coverage, stable frozen session role history, real account credit proofs, and negative tests for two-clone stale/forged/raced replays. Include original source host effects and WIP-ref protection; do not infer their success from Git ancestry or a valid review. No live cutover follows automatically from passing tests.
