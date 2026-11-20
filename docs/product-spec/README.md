# Telos product specification — experiment

**Status: proposed product intent, 20 November 2026.** This is a worked example of the enduring specification Telos should eventually maintain for **itself**. It is not the live Jarvis/Telos Work ledger, a task assignment, an implementation plan, an assertion of current capability, or permission to activate or deploy anything.

The content deliberately separates a **Project** promise, four **System** contracts, and six **Feature** contracts. These are enduring descriptions of what should be true about Telos, even after the Work to implement them is finished. Work, Activities, Sessions, Jobs, Reviews, Evidence and Decisions are related identities, **not** substitutes for the specification.

## Project

- [Telos — purpose, priorities, authority and outcome](project.md)

## Systems

These are durable responsibility boundaries, **not** a required file hierarchy, service split or formal `Subsystem` taxonomy. Their relationships below are candidate directed *architectural* dependencies; ordinary back-and-forth events need not constitute a dependency cycle.

| System | Responsibility | Depends on |
| --- | --- | --- |
| [Intent and specifications](systems/intent.md) | Effective Project/System/Feature contracts, decisions and revisions | — |
| [Execution and authority](systems/execution.md) | Authenticated, bounded tool/model actions on approved resources | — |
| [Work and coordination](systems/work.md) | Turn desired change into progressing, schedulable Work | Intent; Execution |
| [Evidence and acceptance](systems/evidence.md) | Explain and verify what implementation actually establishes | Intent; Work; Execution |

An actual implementation may find a better dependency direction or an existing abstraction to reuse. That should be proposed as a reviewed **System-spec change**, not silently derived from a table.

## Features

Features specify observable capabilities. They may span several Systems, persist after implementation, and are **not** children or types of Work.

| Feature | Systems | Desired outcome |
| --- | --- | --- |
| [Evolve product intent](features/evolve-intent.md) | Intent; Work; Evidence | Operator intent changes become authorized, traceable contract revisions |
| [Carry a goal into useful Work](features/progress-work.md) | Intent; Work; Execution | The system finds and advances the next useful authorized step |
| [Verify and integrate outcomes](features/verify-outcomes.md) | Work; Execution; Evidence | Results are source-bound, independently evaluated and explicitly accepted |
| [Continue safely after interruption](features/recover.md) | Work; Execution; Evidence | Progress survives interruption without repeating uncertain effects |
| [Resolve consequential decisions](features/operator-decisions.md) | Intent; Work; Execution | Operator answers precise questions without managing routine implementation |
| [Develop Telos with Telos](features/self-development.md) | All four Systems | Telos can demonstrably contribute to its own independently checked development |

## How to read and evolve this experiment

Read the [Project](project.md) for the outcome and non-goals, then only the Systems and Features implicated in the question. Do not concatenate every note into every execution prompt.

The **current effective contract**, **its old versions and decisions**, **Work arising from gaps**, and **actual source/test/review receipts** must remain separate. A change to one Feature should lead to a visible proposal and impact analysis, not an append-only paragraph added to every task.

Prose is intentional: these documents are not a homegrown requirements DSL or a list of opcodes. The compact IDs and links are only an illustration of the stable entities and relationships Telos will eventually own. Acceptance examples describe observable behavior, not preapproved test results.

This experiment uses the newer Datum model—Feature as enduring behavioral specification, Systems linked by typed dependencies, Work as distinct execution obligations—rather than treating a Feature as a transient work unit in the current [composable-work-units prototype](../specs/composable-work-units.md). That difference requires deliberate future reconciliation; **this document does not migrate or supersede running records**.

Design basis: [Datum self-executing specifications](https://github.com/Serdan/Servitor/blob/main/datum/notes/self-executing-specifications.md), [Work/Activity model](https://github.com/Serdan/Servitor/blob/main/datum/notes/work-activity-domain-model.md), [Feature-linked intent revision](https://github.com/Serdan/Servitor/blob/main/notes/feature-linked-intent-revision.md). Specific architecture and cutover rules for current implementation live in [Work stateful orchestration](../specs/work-stateful-orchestration-2026-11-17.md).
