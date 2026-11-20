# Feature: Evolve product intent

- **Identity:** `telos.feature.evolve-intent`
- **Systems:** [Intent](../systems/intent.md), [Work](../systems/work.md), [Evidence](../systems/evidence.md)
- **Status:** Proposed enduring Feature contract

## Desired behavior

The Operator can change their mind about how a Feature should behave **without manually rewriting the canonical specification or constructing a task plan**. A proposed change is interpreted in the context of the relevant Project, participating Systems, other Features, existing abstractions, tests and implementation evidence.

In the intended **Servitor Work** interface, the Feature specification is read-only. Selecting a Feature and choosing to propose a change opens a conversation anchored to that specific Feature and its version. The Operator explains intent; intelligence proposes coherent revised text, a diff, rationale and any additional affected contracts. The Operator can discuss corrections and explicitly accept the proposal. Servitor owns that presentation; **Telos owns the versioned authoritative decision and mutation**.

Conversation messages, speculative drafts and unapproved diffs have no normative effect. Approval must cover the actual scope of change. A concurrent newer specification cannot be silently overwritten by an approval based on stale text. Other affected Features or Systems are not mutated without explicit included authorization.

Once approved, the new contract becomes the current intent even if implementation has not caught up. Telos identifies relevant Work, running spec-pinned Activities and evidence that needs re-evaluation. Unrelated progress remains undisturbed. Historical decisions and old results stay inspectable as history, not as evidence magically satisfying new requirements.

## Observable satisfaction

An Operator asks for changed behavior in a Feature-linked conversation and receives a proposal that recognizes an existing reusable mechanism rather than inventing an isolated special case. Rejecting the proposal leaves the current contract unchanged. Accepting it produces one attributable new Feature version and a visible implementation/evidence gap. A simultaneous unrelated Feature continues unaffected, while a stale approval cannot clobber an intervening revision.

**Not implied:** automatic code modification by the conversation, implicit cross-project authority, permission to rewrite unapproved related contracts, or a second Servitor-owned specification store.
