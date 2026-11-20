# System: Work and coordination

- **Identity:** `telos.work`
- **Depends on:** [Intent and specifications](intent.md); [Execution and authority](execution.md)
- **Status:** Proposed enduring System contract

## Responsibility

This System transforms gaps between intended behavior and demonstrated outcomes into manageable Work. It maintains the relationships needed for intelligence to plan and for a deterministic Scheduler to dispatch work that is genuinely eligible.

**Work** describes a desired change, investigation, maintenance obligation or validation goal. **Activity** describes doing a coherent piece of that Work. **Session** is one bounded period of execution; multiple Sessions can advance the same Activity. A **Job** is a concrete command/build/test operation with its own outcome. None of these is the Feature's permanent specification.

## Behavioral contract

When a user requests a capability, Telos finds applicable Project/System/Feature intent and existing mechanisms before making Work. Work has a real scope, current authority, dependencies, acceptance obligations and enough context to be resumed. It may require several Activities: source implementation, test authoring, review, integration, investigation and correction. A current Work item must not become an append-only journal of every previous attempt.

Planning uses intelligence for problem decomposition, architectural judgment, prioritization and blocker resolution. Scheduling uses authoritative facts for eligibility, resources, capacity, fair competition among compatible demands and duplicate prevention. Mechanical dispatch must not require a continuously active conversational supervisor. Short-lived role-specific executions can perform planning, implementation, reviews or other Activities as needed.

Telos should pursue **useful parallel progress** rather than introducing avoidable dependencies or filling all idle slots regardless of purpose. When the highest-priority path has an unmet prerequisite, Telos should make that condition explicit and identify a bounded, relevant, authorized action where one exists. An investigation can advance the objective when it resolves a meaningful uncertainty. Reporting unchanged state is not itself evidence of progress.

Execution scope should usually be one cohesive, independently inspectable step, not a long checklist inside one fragile model turn. Results and checkpoints belong to the enduring Work identity, with source and spec versions pinned, so another authorized execution can continue. Arbitrary fragmentation into microtasks is also undesirable.

Work priorities and readiness are explainable from current dependencies, decisions and resource facts. A Work item may be completed even while the Feature it affects has unmet acceptance criteria. The graph should expose that distinction to the Operator.

## Evidence that this System behaves correctly

Two independent eligible Work items can execute concurrently under available capacity without touching the same exclusive resource. A blocked Work item does not prevent independent safe progress. An available execution or passing historical test cannot make an ineligible change authorized. If a review requests changes, the review Activity finishes with a real verdict while the implementation Work remains unresolved. Restarting a model session does not erase the Work's identity or create a duplicate deliverable.

**Related Features:** [Progress Work](../features/progress-work.md), [Verify outcomes](../features/verify-outcomes.md), [Recover safely](../features/recover.md).
