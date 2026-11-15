# Independent architecture review — ChangesRequested

**Reviewer:** Reviewer 2
**Frozen review:** d4fbb56e2d4c451eab1ef23c281dc647
**Task:** work-queues-ephemeral-executions
**Exact source:** e737c2da901ae4073f7ecc066781f428020ffdd1
**Inspected document:** docs/specs/work-queues-and-ephemeral-executions.md (333 lines; docs-only)
**Comparison base:** 1481bfa03aea4def721d2b24835bff9b9076160a
**Scope:** design consistency and existing canonical contracts; no implementation, tests executed, source edits, deployment or scheduling.

## Verdict: ChangesRequested

The chosen architecture is directionally sound: work-item/activation separation, project-scoped Git orphan ledger as sole coordination authority, versioned CAS and immutable checkpoints, role subscriptions as conventions rather than agent permissions, immutable frozen review and exact integration evidence, fail-closed uncertain browser launch/recovery, and an explicit migration/authorization gate should be retained. The spec is strong on avoiding fabricated lifecycle transitions and stale-session recovery based on elapsed time or tab closure.

The current revision is **not yet a sufficiently precise implementation contract** at several concurrency and handoff boundaries. The findings below are blocking for spec acceptance before Phase B implementation; they do not authorize live changes.

## Findings

1. **F01 (P1) — Launch demand reservations:** available-item counts are not reduced by pending launch *demand*, so consecutive Manager passes can overspawn sessions for a single unclaimed item. Specify canonical cross-device CAS reservations or a provable single-leader/demand-credit equivalent. See `findings/01-launch-demand-reservations.md`.
2. **F02 (P1) — Session versus activation end:** CompleteWork/YieldWork terminates an activation, not necessarily its ChatGPT session. The Manager releases session capacity on an insufficient signal; NoWork has no explicit durable launch terminal path. Define the lifecycle and fencing. See `findings/02-session-end-capacity.md`.
3. **F03 (P1) — Source continuity:** an optional source SHA and mutable/local branch do not guarantee another worker can reconstruct exact unfinished source. Define durable, reachable, pinned source handoff and dirty-job handling. See `findings/03-source-handoff-continuity.md`.
4. **F04 (P1) — Review/queue transitions:** independent canonical review claim and queue claim need a fully specified atomic/reconciliation contract; a post-verdict generic yield could reopen already decided review work. Retain independent verdict/integration semantics and explicit policy variants. See `findings/04-review-canonical-transitions.md`.
5. **F05 (P1) — Related source stages:** exclusive claim per item does not prevent concurrent correction items for a shared task/source lineage after multiple independent review verdicts. Define stage generation, serialization or explicit branched source policy. See `findings/05-concurrent-source-stages.md`.
6. **F06 (P1 migration / P2 projection races) — Claimed reviewers and dependency/index coverage:** migration cannot treat a claimed independent review as unclaimed or as a live activation without evidence; pending intents/jobs/launches and old writers require explicit fencing. Define block-versus-wake and queue coverage watermark checks. See `findings/06-migration-index-and-dependency-races.md`.

## Required acceptance before re-review

Revise the specification at a **new exact pinned SHA** (do not mutate this frozen snapshot) with explicit state/transition tables for launch/session/activation, review domain + work projection, source handoff, source-stage concurrency, and migration/coverage. Update negative-space acceptance fixtures to include repeated and concurrent Manager ticks before claims, two Manager devices, no-work and late-session claims, finished review then YieldWork, dirty/unreachable/rewritten WIP source across clients, two ChangesRequested reviews on one task, claimed reviewer at cutover, partial conversion/pending intents, and BlockWork versus last-dependency satisfaction. Preserve all existing positive invariants around account-level permissions, exact reviewed SHA, audit history, and explicit task closure.

Review performed against exact frozen documentation and related checked-out project contracts (orthogonal-agent-state, orchestration-architecture, ManagedReviewIntegration, OrchestrationMigration, ManagerHeartbeat, ExecutionScheduling); no tests were run because this is a docs-only design review. No request to implement, merge, deploy or schedule follows from this verdict.