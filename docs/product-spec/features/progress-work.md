# Feature: Carry a goal into useful Work

- **Identity:** `telos.feature.progress-work`
- **Systems:** [Intent](../systems/intent.md), [Work](../systems/work.md), [Execution](../systems/execution.md)
- **Status:** Proposed enduring Feature contract

## Desired behavior

Given an authorized Project, System or Feature objective, Telos should organize and perform **the next useful engineering steps** with minimal operator micromanagement. It should understand the existing software and contracts before proposing changes, identify independent lines of progress and preserve the distinction between choosing Work and executing it.

Work is meaningful because it closes a real evidence or implementation gap. Planning can establish priorities, feasibility, source ownership, constraints and the smallest relevant producer for a blocker. A deterministic Scheduler should dispatch eligible Activity to a compatible short-lived execution within capacity and authority. The Scheduler does not invent technical GO; an idle model is not proof that new work is authorized.

Execution should be coherent and bounded: an implementation slice, a verification question, a review or a tightly scoped correction may each be valuable, without requiring one model to remember the entire Project roadmap. The current Work identity, spec versions and evidence survive across Sessions. Lead-like technical judgment remains accountable for the larger outcome without taking over every Worker implementation.

When a dependency is blocked, the system should attempt a genuinely allowed, useful investigation or disjoint parallel task if one exists. If no safe next step exists, it should identify the precise unavailable source, resource or decision and the owner who can supply it. It must not invent work merely to keep agents busy or spin indefinitely between unchanged HOLD reports.

## Observable satisfaction

A Feature requiring two independent components yields two separately eligible Activities that progress concurrently within capacity. A third task with a conflicting source lease waits, with a meaningful reason. After one Activity finishes, Telos can resume the same Work from its evidence and checkpoint without creating another copy. A blocked critical path leads either to a bounded evidence-producing step or to a precise outstanding decision—not a status-only cycle.

**Not implied:** unlimited automatic spending, permanent named Worker slots, bypassing source/review gates, or equating task closure with Feature acceptance.
