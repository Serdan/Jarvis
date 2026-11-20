# Feature: Resolve consequential decisions

- **Identity:** `telos.feature.operator-decisions`
- **Systems:** [Intent](../systems/intent.md), [Work](../systems/work.md), [Execution](../systems/execution.md)
- **Status:** Proposed enduring Feature contract

## Desired behavior

Telos should ask the Operator only when a meaningful product, authority, budget or irreversible-action decision cannot be made within existing delegated responsibility. An agent may encounter an ambiguous requirement, conflicting System contract, unavailable resource or action outside its project; these are reasons to create a **precise, durable Challenge**, not reasons to ask the Operator to investigate ordinary technical details.

A Challenge is linked to the affected contract, Work or transition, the current versions, the missing decision, known options and actual evidence. It blocks exactly the action that needs the answer. It must remain discoverable even when a notification is not delivered. Unrelated authorized execution should continue.

The Operator can clarify intent, accept an explicit proposal, grant or withhold a **scoped** exceptional permission, or defer. These are different kinds of decisions; granting authority to perform one action must not silently rewrite the Feature's normative behavior. An answer becomes an attributable, versioned Decision at the owning authoritative boundary, not an interpreted sentence in a forgotten chat.

The intelligence responsible for outcomes should first investigate feasible alternatives and prepare a concrete recommendation. It should not dump a routine programming problem on the Operator simply because a tool or dependency was initially blocked.

## Observable satisfaction

An Activity needs a cross-project permission it lacks. Telos records a Challenge identifying the proposed exact effect and owner; only that transition waits. The Operator approves a bounded permission and its provenance is recorded, while other projects do not inherit that grant. In a separate case, the Operator changes Feature intent through an approved revision rather than a permission grant; Telos preserves the distinction and re-evaluates the affected Work.

**Not implied:** operator approval through notification acknowledgement, unlimited delegated authority, repeated routine interruptions, or informal text overriding current access controls.
