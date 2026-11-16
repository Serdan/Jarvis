# Composable work units: Project / System / Feature

Status: first implementation slice, **not a production rollout**. Canonical task: `composable-work-units` (2026-11-16). The Agent-to-Work migration under `work-queues-ephemeral-executions` remains the only source for schedulable execution, review, and integration stages.

## Semantics and scope

Jarvis describes engineering objectives as durable, declarative **work units**. A Project is the root objective; a System owns a functional boundary and may contain other Systems; a Feature is a bounded implementable outcome. All three have a title, goal, scope, constraints, acceptance criteria, versioned specification, immutable acceptance history, explicit hard prerequisites, and derived status. The graph does not create permanent Lead, Worker, or Reviewer owners.

The containment model is **one structural parent per non-Project**. A Project is the single root. Independent cross-System work does **not** need multiple parents: `HardPrerequisites` may reference any other unit under the Project. Containment, blocking, source-code ownership, and Git integration ordering are distinct concepts; parentage is not an execution-order dependency. A System's independent acceptance does, however, require proof of its children.

The new pure module `Server/WorkComposition.fs` provides:

- `apply(expectedGraphVersion, mutation, graph)`: create, revise, reparent, add/remove hard prerequisites, accept, cancel, reopen. Rejects stale CAS, invalid references, two roots, Feature parents, circular containment, hard prerequisite cycles, and mixed parent/child acceptance dependency cycles.
- `currentAcceptance(graph, unitId)`: returns an accepted **target snapshot** only when the current specification, child acceptance revisions, and prerequisite acceptance revisions still match. Historical accepted targets remain in `Acceptances`; adding a corrective child or revising any upstream work invalidates current acceptance without rewriting old evidence.
- `phase` / `overview` / `allOverviews`: recursive work status, evidence-backed current acceptance, unmet prerequisite IDs, pending child acceptance, stage categories for implementation/review/integration, and derived progress buckets. Cancelled scope is counted separately instead of silently disappearing.
- Queue-backed `ReadyForAdmission` is a **project-local candidate**, selected through the canonical `Contracts.orderAvailable` predicate (coverage, clear gate, and satisfied/waived hard prerequisites) only when the project migration is `Activated`. It is **not** a claim that the account-global scheduler may launch an execution: verified role compatibility, account migration, capacity, source provenance, and admission still need independent authorization. A linked `Claimed` stage is projected as `Executing` (a ledger state, not proof of live model activity). Missing or incomplete WQ coverage returns `Unverified`; frozen migration or an excluded available stage returns `Blocked`, not invented dispatch readiness.

No manually maintained percent, implied parent acceptance, or permanent human/LLM ownership state is added. Graph readiness and stage execution readiness remain separate until the existing scheduler is explicitly connected to the graph.

## Ledger authority and concurrency

`Client/Modules/WorkCompositionGitLedger.fs` stores a single small canonical envelope at `records/work-composition/graph.json` **in the same existing project Git orphan ledger** as task specifications, reviews, integration receipts, WQ state and operation receipts. This is not a second ledger or database. It uses the same `LedgerStore.transact` CAS machinery, immutable operation ID, semantic request JSON, verified remote snapshot, per-blob compare-and-swap, canonical record serialization, project identity and state hash.

Every graph mutation reloads and validates the winner's exact ledger head. A stale `expectedGraphVersion` fails instead of rewriting a concurrent update. A retry with the same operation UUID recovers the winning receipt. `loadOverviews` reads composition and WQ state at **one pinned head**, with no split snapshot. Legacy work remains unchanged; an absent graph means no composition has been authored, not a fabricated migration.

Acceptance is a separate independently verifiable operation. The default public `execute` **rejects** `Accept` as unverified. The internal `executeVerifiedAcceptance` requires a validator supplied by the canonical task/review/integration adapter and executed **inside the same Git-CAS planner** against its exact snapshot. A caller-supplied string is not evidence. Until that adapter is wired, the domain's pure acceptance transitions are useful for tests/modeling but cannot be published through the normal ledger API.

## Readiness and remaining integration work

The pure graph is a prerequisite and acceptance **description**, not an independent scheduler or an authorization to launch LLM sessions. Existing WQ Stage generations, gate holds, source lineage, review verdicts, integration proof, capacity reservation, claims and bounded ephemeral executions stay canonical.

To complete the task's end-to-end acceptance criteria, next slices must:

1. Bind graph nodes to their authoritative Task/stage identity and validate acceptance receipts (including parent-specific verification) against canonical review, test and integration evidence. Prevent accepting a node solely because children have closed.
2. Add a reconciled, same-project-CAS bridge that derives actual WQ hard-stage prerequisites and readiness from accepted graph nodes, without double-publishing a stage or allowing a stale/incomplete projection to dispatch. Graph edits that introduce unsatisfied dependencies to claimed work must use existing WQ dependency-gate and external-push fence contracts.
3. Wire graph mutation/acceptance to scheduler wake and durable event high-water handling; prove lazy activation with **two prerequisites**, one-at-a-time completion, one bounded execution per available claim, process restart recovery, and child work created by a running execution.
4. Expose the bounded operator mutation/query commands and consistent recursive blocker/readiness overview through the existing client/MCP command surface. Cover cross-System recursion, cancelled scope, status, progress, review and integration with real repositories.
5. Validate concurrent integration promotion against actual Git heads and semantic compatibility, using existing reviews and safe atomic ref updates. No fixed branch layer and no singleton Integrator.

The current branch is deliberately *not* connected to production scheduler activation. An unverified work-unit graph must not authorize claims. No new server-owned persistent authority, agent permission framework, or project-management process machinery is proposed.

## Design review checks

- Is a unit's specification durable without allocating a model execution? **Yes.**
- Is the result of inspecting the graph predictable from one verified snapshot? **Yes.**
- Is child completion equivalent to parent acceptance? **No.**
- Does a newly discovered child erase prior release evidence? **No; it invalidates only current target acceptance.**
- Can cross-System blockers be expressed without multiple parents? **Yes.**
- Does the graph itself create a runnable session or bypass WQ claim/lease accounting? **No.**
- Is canonical acceptance validation plus reactive activation fully end-to-end? **Not yet.**

## Testing

`Server.Tests/WorkCompositionTests.fs` exercises nested containment, cross-System blocking, cross-edge cycles, independent parent acceptance, stale target invalidation, prerequisite revisions, cancellation, optimistic graph version conflicts, and missing WQ coverage. Negative-space tests also cover non-clear gates, incomplete/missing and unsatisfied hard-stage prerequisites, and frozen migration; a positive case is explicitly only locally `ReadyForAdmission`. Client Git adapter and scheduler end-to-end fixtures must be expanded as the bridges are connected.
