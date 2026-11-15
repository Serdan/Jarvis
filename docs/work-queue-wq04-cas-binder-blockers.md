# WQ04 canonical-source / versioned-role same-CAS binding: blocked boundary

Status: **inert audit and negative-test seam only**. This work does not authorize a new producer, admission, claim, Scheduler, manager, host source operation, account reservation, migration, or deployment.

## Exact examined parents

* Current main `e837d2d67277af8eb4b634ff58d5ba9e1b59d19e`, containing independently Accepted, integrated WQ04 fail-closed correction `dc71085090e4c914a3a336baba248907a3fb3030` (review `62a2380dfe8a40b2a06fc15fb8fea552`, Accepted seq3725).
* Worker10 `0ba335368d6f621f186fbc0e062833da8e0b8625`, source-event evidence reader independently Accepted (review `d82cb165f1a845939448f6d2ac601e9c`, seq3746). Its code is copied via a local cherry-pick into this isolated review branch; its original branch is untouched.
* Worker11 `1cd0b88db631dc4258601e5e531f95c02a34ebd4` versioned historical role-catalog candidate, review `ed89f13401cd4c18bce62b189af79f73` still undecided at inspection. Its module is **not imported as approved authority**, and its branch is untouched.

## Proven vs missing

The accepted source-evidence reader proves the original canonical Git ledger **operation UUID, actor, action, original request/result hash, immutable event blob, introducing commit, entity path and exact observed high-water**. A fabricated event ID, review acceptance status, source SHA/target, role membership, or matching caller counter cannot replace the corresponding original receipt.

The reader intentionally returns **only `IncompleteCoverage`**. It does *not* prove the original source-mutating job/push journal, externally protected and retained WIP refs, completeness across all source/index families, or their atomic relationship to a project `WorkQueueGitLedger` CAS commit. The accepted WQ04 project writer has an unconditional `ProduceEvent` hold and ID-independent semantic guards in `PublishStage`, `PostMessage`, `AdmitProject` and `ClaimNext`. Those protections must remain in force.

Worker11's frozen role code can verify a historical catalog version and calculate a role preflight from a pinned project snapshot, but the exported preflight explicitly flags `RequiresCanonicalSourceAndAccountProof`. Its review is pending and the positive historical role predicate is **not** integrated with the WQ04 `LedgerStore.transact` project writer or account-global launch-credit proof. A matching role ID, revision, or persisted catalog blob alone is not authorization.

The missing invariant is a **single authenticated, optimistic Git CAS transaction** that derives all writable outputs from one verified ledger head, including:
1. the exact canonical operation/origin receipt and all task/review/challenge/integration/message high-water coverage, with no gaps or caller-selected truth;
2. the immutable source-host side-effect/terminal proof and protected source lineage, or an explicitly separate recovery block where a cross-ledger atomic join cannot be shown;
3. the historical versioned role catalog and same-CAS compatibility for new demand, admission, unclaimed-credit rematching and claim, including v1/v2 disable races;
4. account-global launch credit authority and project admission/terminal receipt, consistently fenced against concurrent clients;
5. exactly-once terminal IntegrationPolicyResolved and honest WQ04 stage-generation correspondence.

The current trusted inputs do not satisfy that invariant. Joining two **read-only** preflights after the fact is not a substitute for the writer's original CAS planner. No server-local SQLite authority or alternative counter is acceptable.

## This branch

`Client/Modules/WorkQueueCanonicalCasBinder.fs` exposes a typed original-event request and role scope, returning **only `RecoveryBlocked of MissingAuthority list`**. There is deliberately no `Authorized` case. An explicit `attemptUnderCas` regression seam calls the project's real Git transaction planner, re-reads the verified source event and project at its pinned snapshot, then **always returns `IncompleteCoverage / RecoveryBlocked` without any changes**. This is not called from any production route.

`Client.Tests/WorkQueueCanonicalCasBinderTests.fs` runs disposable two-Git-clone negative fixtures: authentic event still cannot produce, forged original UUID/request hash/event selector rejected, caller high-water/stale head rejected after another clone advances, mismatched or historical role assertions denied, and raced CAS attempts produce neither operations nor project changes. The accepted source-reader suite remains unchanged.

## Criteria before any positive implementation

Receive an **independently Accepted** role-catalog module, then produce a new, independently reviewed transaction writer with authentic canonical origin/complete source and index coverage, stable frozen session role history, real account credit proofs, and negative tests for two-clone stale/forged/raced replays. Include original source host effects and WIP-ref protection; do not infer their success from Git ancestry or a valid review. No live cutover follows automatically from passing tests.
