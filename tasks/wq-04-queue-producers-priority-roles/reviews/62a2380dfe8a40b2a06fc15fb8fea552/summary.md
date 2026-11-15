# WQ04 semantic legacy-ID bypass correction — Reviewer 2 independent verdict: **Accepted (bounded inert scope)**

- **Task-bound canonical review:** 62a2380dfe8a40b2a06fc15fb8fea552, task wq-04-queue-producers-priority-roles. Claimed Reviewer 2 seq3707.
- **Exact frozen SHA:** dc71085090e4c914a3a336baba248907a3fb3030, direct child of previously rejected combined 5e2c1b4164204145653d957bd4adfcf24f597c0a; prior combined has first parent verified main e14173e116dc03b7dbdf261895fde3c893a643ee. Frozen worktree clean; git diff --check clean.
- **Independent .NET11 Release:** Server WorkQueue **98/98 pass**, Client WorkQueue + AccountAdmission + SourceEffect + AgentDashboardCache + AgentView **118/118 pass**, total **216 passed**, 0 failed, 0 skipped. Tests run in frozen worker4-wq04-semantic-fence, no hosted Actions, main push, live Scheduler, deployment or Codex.

## Decision rationale

The original seq3660 ChangesRequested finding was semantic work entering via public PublishStage/PostMessage using arbitrary non-wq04 IDs, followed by legacy AdmitProject/ClaimNext. The correction gates by **public mutation semantics**, not ID prefix: Client/Modules/WorkQueueGitLedger.fs:938-946 rejects every public PublishStage, Role PostMessage and public admission/dispatch/claim/graph transition lacking canonical source/index and versioned-role proof, while ProduceEvent stays doubly fail-closed. Public AccountAdmissionAdapter.admit returns before reserving any global token; Contracts.canDispatch always returns false, including fabricated activated/covered projects. The internal WQ02/WQ07 fixture-only mutation/credit paths require Frozen and preserve their historical two-clone CAS tests, without any runtime call sites.

New disposable two-clone adversarial tests at Client.Tests/WorkQueueProducerGitTests.fs:253-335 reject renamed FrozenReview, Integration, Planning and RoleMessage across publication/PostMessage and deny admission/dispatch/claim even after historical renamed fixture work is pre-seeded; stale concurrent CAS loses, verified state remains unchanged. This directly covers the missing negative in the earlier review. Source proof and role selector remain deliberately unavailable.

## Important scope limit

**Accepted does not authorize production activation.** This review does **not** establish canonical event/review/challenge/role source provenance, complete verified high-water/index, external WQ03 host/source protections, WQ05 accepted integration, or live token/Scheduler/dispatch readiness. Historical immutable WQ02 operation retries can return AlreadyApplied without producing a new mutation; future adapters must never count fixture receipts as canonical proof. The separate orphan da5088... is not accepted here.

See findings/reviewer2-independent-semantic-gate-audit.md for independent source-line inspection, exact ancestry, all adversarial coverage, replay caveat and Release test evidence.

**Canonical verdict requested: Accepted** for this frozen, inert/fail-closed WQ04 correction only.