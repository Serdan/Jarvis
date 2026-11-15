# Independent frozen architecture re-review — ChangesRequested

**Reviewer:** Reviewer 1
**Review:** `f0ec0c91bb1049aa9b40bf9ea7adc751`
**Task:** `work-queues-ephemeral-executions`
**Exact frozen commit:** `ef486f4617d211db0719924626ae3b05bc1f40ca`
**Branch:** `refs/heads/docs/work-queues-design-corrections`
**Reviewed document:** `docs/specs/work-queues-and-ephemeral-executions.md` (469 lines; 86,909 bytes; registered worktree at the exact SHA, clean)
**Predecessor:** `b7fd8a31f67b40f384ead6b569d93724` (Reviewer 2; ChangesRequested; B01–B05), source `36155553147705748c90d73a5aaf497b511768ef`; original F01–F06 review `d4fbb56e2d4c451eab1ef23c281dc647`.
**Scope:** Static documentation and state-machine/concurrency/recovery review only. No code execution, implementation, tests, merge, deployment, schedule, or frozen-source changes.

## Verdict — ChangesRequested

The revision **substantially fixes all five prior B01–B05 architecture issues** and preserves the earlier F01–F06 corrections. In particular it now has launch-level taskless termination, an actual project-first Manager fence, injective role-compatible demand-credit matching with priority-feasible rematching, an account migration epoch with per-project read/write gates, and account CAS-serialized cap changes. Its core work-centric direction is internally coherent.

**Two remaining P1 protocol edges prevent accepting the document as a complete implementation contract:**
- **C01:** `findings/01-unbound-global-admission-abort.md` — the **global-token-success / project-demand-reservation-failure** interval lacks a defined safe terminal/abort receipt path when no project launch record exists (lines 176–188, 225–232). The spec directs release on definitive project failure but otherwise requires a verified project terminal receipt for every release.
- **C02:** `findings/02-prepared-handoff-abort-race.md` — a prepared Manager handoff's **abort** is not serialized against a late account token swap (lines 175–183, 240–248). An immutable prepare receipt can remain valid after project child cancellation; the specified state machine lacks an explicit prepared->aborted/terminal transition and account-level swap-versus-abort decision.

Neither gap calls for extra agent-ID permissions, a distributed transaction, or changing the chosen account-global capacity model. They require explicit CAS state transitions and negative-space fixtures for the two identified intervals.

## B01–B05 re-review matrix

| Prior item | Revised evidence | Assessment |
| --- | --- | --- |
| **B01** Taskless Manager / NoWork terminal | §7 lines 175–188, `TerminalizeLaunch` idempotent launch-level receipt, project fence before account release, competing terminal outcomes; tests 34 | **Fixed for launches with a project record.** C01 covers the omitted pre-project admission-failure interval. |
| **B02** Manager last-slot project fence | §8 lines 240–248, `PrepareManagerHandoff` under project CAS **before** account swap, independent receipt verification, one-shot child journal; tests 31, 35 | **Normal handoff fixed.** C02 covers abort/replay after project prepare and before/against global swap. |
| **B03** Priority/demand credits | §§6–8 lines 149–153, 217–221, canonical injective compatible matching across live unclaimed launches, feasible highest-priority selection, atomic rematch, invalidated-credit fencing; tests 20, 36 | **Addressed.** A/B reversed start and overlapping A-only vs A+B subscriptions behave correctly at the specified state-machine level. |
| **B04** Cross-ledger migration | §14, fixed project membership, `LegacyActive/Frozen/Preparing/Validated/Activated` epoch, per-project Prepared receipts, minimum-protocol old-writer fence, verified account + project read gates, staged abort and no ref rewrites; tests 16, 23, 29, 37 | **Addressed at architecture level.** Implementer must prove gates apply to in-flight old-client writes and lost Git receipts, as the negative-space fixtures require. |
| **B05** Capacity resize | §8 line 223 and §12 `SetGlobalAgentCapacity`, same account CAS/version domain for cap and token reservations, reject shrink below active/uncertain occupancy; test 38 | **Addressed.** No implicit eviction, timeout release, or unreviewed drain-only mode. |

## F01–F06 and adjacent guarantees

- **F01 launch demand:** Canonical demand credits, project Git CAS and one-to-one feasible rematching prevent duplicate demand from two Managers and out-of-order worker claims. Partial/incomplete coverage fails closed.
- **F02 execution/capacity:** One claim per launch, exact claim-reply replay, logical terminal receipts independent of physical provider exit, uncertain browser submit retention, scheduler skip-when-full, one account-wide `maxConcurrentAgents` including Manager. Physical provider-session concurrency is expressly **not** guaranteed by the logical cap and must not be advertised as such.
- **F03 source continuity:** Pinned source repo/base/lineage; remote verified immutable WIP commit/ref before yield, isolation per activation, protected reachability/retention, and no requeue with dirty/unpublished source or mutating jobs (§9).
- **F04 canonical review:** Review claim and queue item use one CAS, verdict does not terminalize reviewer launch, post-verdict YieldWork/BlockWork cannot reopen a decided review, exactly-once downstream producer (§10, tests 22, 27).
- **F05 writer serialization:** One current source-writing stage per lineage/generation; concurrent ChangesRequested findings converge, stale review findings are preserved but cannot rewrite current source, distinct lineages require explicit merge plan (§§4, 10).
- **F06 cutover/dependencies/indexes:** Claim/integration/pending-intent refusal, block-vs-satisfaction CAS, continuous high-water/index coverage and explicit account migration gates (§§5, 11, 14).
- **Operator conversation:** Task/subject-addressed immutable Question/Response/Decision events, challenge-backed blocking response, cursor-based continuation and operator attention; no dependence on numbered Worker inbox (§11, tests 24–26). Decisions do not silently mutate specs or accept reviews.
- **Exact SHA:** Review binds immutable frozen source/ref; Accepted enqueues integration pinned to review ID, source SHA and target policy, with no merge of modified/unreviewed source or assumption that verdict is integration (§10).

## Required re-review gates

1. Specify and test how the first failed project demand/launch registration releases an already-reserved **global** token without violating the terminal-receipt proof requirement; distinguish definite failure from an ambiguous pending Git operation (C01).
2. Add explicit prepared Manager **abort** transition and one globally serialized swap-or-abort decision, including late swap after apparent absence, concurrent cancellation, and child-token cleanup/replay (C02).
3. Extend §16 with those four negative-space fixtures per finding; retain tests 34–38 and the prior F01–F06 acceptance tests unchanged in intent.
4. Create a **new exact pinned source commit and review** after the remedies. This verdict is not authorization for Phase F cutover, deployment, unattended Manager schedule, or integration.

**Final canonical verdict:** **ChangesRequested** for this frozen documentation snapshot.