# System: Intent and specifications

- **Identity:** `telos.intent`
- **Depends on:** None of the other Systems in this experiment
- **Status:** Proposed enduring System contract

## Responsibility

This System preserves the Operator's intended software behavior as effective, versioned Project, System and Feature specifications. It is the place where enduring promises and authorized decisions are understood; a temporary Work task or conversation is never a substitute for it.

A Project expresses purpose, quality priorities, non-goals and delegated authority. A System describes a durable architectural responsibility, interface obligations and directed dependencies on other Systems. A Feature describes observable capability and acceptance expectations, often involving more than one System. Features are not a subtype of Work; a feature still exists when implementation work has ended.

The normative specification is written in **ordinary language**. Telos may keep stable criterion anchors, versions, typed relationships and provenance, but should not make the Operator translate their intentions into a comprehensive formal requirements language.

## Behavioral contract

Every effective contract has an identifiable current version, an inspectable history and the attributable decision that made it effective. Conversations, generated drafts, notes, source code and tests may suggest a change; none may silently become a new requirement. A conflict or consequential ambiguity should be exposed at the affected contract boundary rather than guessed away.

An Operator can revise a Project, System or Feature through an authorized decision. Intelligence should understand the existing model, reuse participating mechanisms and propose coherent scope before the Operator approves a change. Servitor Work specifically presents **Feature text as read-only** and uses a Feature-linked proposal conversation; the UI implementation belongs to Servitor, while Telos owns the authoritative versioned mutation.

A revision should identify the contracts, Work and evidence potentially affected. It must not retroactively change what an already-started Activity was authorized to do or pretend an old receipt proves a new behavior. Unaffected evidence and operations remain valid.

System dependencies are explicit and typed. Telos should reveal genuine architectural cycles and their meaning without confusing architectural dependency with ordinary bidirectional communication or Work scheduling order.

## Evidence that this System behaves correctly

A Feature change approved against version N becomes a new attributable version, while an unapproved proposal remains inert. A stale concurrent approval cannot overwrite version N+1. An Activity already executing against version N retains that context until an authorized reconciliation; the current Feature can nevertheless show that its old acceptance evidence is now incomplete or stale. An unrelated Feature's accepted evidence is not invalidated without a causal relationship.

**Related Features:** [Evolve intent](../features/evolve-intent.md), [Operator decisions](../features/operator-decisions.md).
