# Feature: Carry a goal into useful Work

- **Identity:** `telos.feature.progress-work`
- **Systems:** [Intent](../systems/intent.md), [Work](../systems/work.md), [Execution](../systems/execution.md)
- **Status:** Proposed enduring Feature contract

## Desired behavior

Given an authorized Project, System or Feature objective, Telos should organize and perform **the next useful engineering steps** with minimal operator micromanagement. It should understand the existing software and contracts before proposing changes, identify independent lines of progress and preserve the distinction between choosing Work and executing it.

Work is meaningful because it closes a real evidence or implementation gap. Planning can establish priorities, feasibility, source ownership, constraints and the smallest relevant producer for a blocker. A deterministic Scheduler should dispatch eligible Activity to a compatible short-lived execution within capacity and authority. Eligibility and authorization come from authoritative Work and resource state; available execution capacity does not create permission to act.

Execution should be coherent and bounded: an implementation slice, a verification question, a review or a tightly scoped correction may each be valuable, without requiring one model to remember the entire Project roadmap. The current Work identity, spec versions and evidence survive across Sessions. Accountability for the larger outcome persists across Activities and Sessions without requiring one execution to perform every step.

When a prerequisite prevents progress, Telos should represent the precise missing dependency, resource, evidence or decision as structured state. It should keep independently eligible Work progressing and expose permitted investigations or corrective actions where they exist. If none are eligible, the unresolved condition and the path to resolving it remain explicit. It must not create filler Work merely to occupy capacity.

## Observable satisfaction

A Feature requiring two independent components yields two separately eligible Activities that progress concurrently within capacity. A third task with a conflicting source lease waits, with a meaningful reason. After one Activity finishes, Telos can resume the same Work from its evidence and checkpoint without creating another copy. A constrained critical path identifies its exact unmet condition; if an authorized investigation or decision can advance it, that action is visible and actionable.

**Not implied:** unlimited automatic spending, permanent named execution slots, bypassing source/review gates, or equating task closure with Feature acceptance.
