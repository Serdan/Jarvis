# WQ-04 independent frozen review — **ChangesRequested**

**Reviewer:** Reviewer 2 · **Review:** `d289b8ac73b749a99cdfc0eefce0bbd1` (task-bound, not `_reviews`)  
**Exact source:** `6f0686720e56e198e085111cd8f22f5c6a6d7b83`  
**Verified immediate parent / integrated base:** `8ed9671543f05073f7d453728b50772138dd87f2`  
**Branch:** `feat/wq04-queue-producers-worker4`; isolated worktree `worker4-wq04-queue-producers`, clean before audit. No source edits, main push, Scheduler activation, deployment, GitHub Actions or Codex.

## Independent verification

- `git rev-list --parents --max-count=1 6f068672...` proved the exact 8ed96715 parent. `git show --stat` showed 9 files, 932 insertions / 1 deletion, including pure producer, role selection, client Git-CAS route and focused tests.
- Executed **.NET 11 Release**: `dotnet test Server.Tests/Server.Tests.fsproj --configuration Release --no-restore --filter FullyQualifiedName~ServerWorkQueueProducerTests --verbosity minimal`: **9 passed, 0 failed** (net11.0).
- Executed **.NET 11 Release**: `dotnet test Client.Tests/Client.Tests.fsproj --configuration Release --no-restore --filter FullyQualifiedName~WorkQueueProducerGitTests --verbosity minimal`: **3 passed, 0 failed** (net11.0). These use disposable local two-clone Git CAS fixtures. One pre-existing F# FS0760 warning in Client/Program.fs; no failed tests.
- Positive audited behavior: deterministic source-token/generation work IDs; task operator message passive vs one role-message work generation; review-before-ordinary ordering and blocked-stage exclusion; graph cycle warnings and fresh FIFO position after edge removal; CAS loser/retry; historical role configs in pure selector; integration-policy origin refuses unresolved GateException. WQ-04 is still inert: the tests are necessary, **not** proof of canonical production producer integration.
- Bounded ad-hoc FSI negative probe was **not run** because Jarvis denies shell execution. The findings below follow directly from the reviewed code and are not claimed as separately executed failures.

## Blocking findings

1. **F01 — Versioned role-selection/admission mismatch.** `WorkQueueProducers.selectRoleDemand` uses versioned `RoleCatalog`; `Contracts.admitProject`, `compatible` and `reconcileCredits` use the old `Project.Subscriptions` with no role version. New subscription grants may be selected but fail admission; revoked queues may still receive stale credits. Operator Role messages also use the old map. See `findings/versioned-role-admission-drift.md`.
2. **F02 — Producer provenance/high-water not authoritative.** `WorkQueueGitLedger.ProduceEvent` accepts supplied source identity and candidate origin with only project-ID binding and implementation-lineage check; no canonical review decision/source event proof for AcceptedIntegration, review, challenge etc. Coverage compares caller-supplied counters and booleans rather than a pinned source/producer index, and publication leaves ProducerCount unchanged. The comment requiring a future authoritative adapter acknowledges the missing safety proof. See `findings/unverified-producer-source-coverage.md`.

## Explicit decision

**ChangesRequested**, even though all 12 focused Release tests pass. The review may not authorize exact-source canonical WQ-04 integration: role demand and existing admission can disagree, and producer authenticity / complete coverage is not established. Correct at the same snapshot/CAS boundary, add negative two-clone tests, freeze a **new exact source** and request independent review. Continue to block host WIP handoff, Scheduler activation, real token reservation, migration and deployment.