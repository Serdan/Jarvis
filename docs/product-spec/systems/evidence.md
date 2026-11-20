# System: Evidence and acceptance

- **Identity:** `telos.evidence`
- **Depends on:** [Intent and specifications](intent.md); [Work and coordination](work.md); [Execution and authority](execution.md)
- **Status:** Proposed enduring System contract

## Responsibility

This System keeps claims about engineering outcomes distinguishable from the actual evidence supporting them. A test run, an independent review decision, integration into a branch and satisfaction of the current Feature contract are different facts.

Telos should make it possible to ask: **What did we intend, what was built, which exact version was checked, and which part of the current promise is supported?**

## Behavioral contract

Evidence carries the identities necessary to interpret it: applicable Feature/System/Work contract versions, source revision, relevant SDK/toolchain/environment, test/profile inputs, actual outputs, and the decision or observation that was made. A PASS is not transferable to different source or a newer, materially changed requirement merely because the label matches. Authored tests, skipped tests, stale receipts, uncertain operations and expected RED experiments must not be presented as successful acceptance.

Verification should include meaningful negative and boundary cases. Test quality itself can be Work; the absence of a failing test does not automatically prove that the Feature was implemented correctly. Where semantic behavior cannot be mechanically proven, Telos should preserve the limits of independent evaluation rather than invent certainty.

Source review must inspect an exact frozen candidate by an independent Reviewer who did not author the change. A request for changes records a valid completed *review Activity* but does not accept the candidate. Revisions produce new source identities and require appropriate fresh review. Integrating accepted source is a separately controlled transition that checks the current target and required evidence; a review verdict alone does not grant permission to merge.

Telos distinguishes historical acceptance from **current conformance**. If intent changes, it re-evaluates exactly which criteria, tests, integrations or proofs remain applicable; it does not discard unaffected evidence or silently pass changed criteria. The system should be able to describe partial, stale, contradicted and unknown status alongside verified claims.

Verification has a purpose and a stopping rule. A source-only prototype may be useful despite incomplete release gates; final protected integration or deployment may require substantially stronger proof. A proposal to add another check should explain which decision the result can change instead of making a recursive chain of hypothetical blockers.

## Evidence that this System behaves correctly

Two source candidates with identical test counts cannot share a receipt unless its exact provenance actually applies. A `ChangesRequested` review prevents acceptance of that frozen candidate, and a fixed descendant receives its own review. A successful integration receipt identifies what reached main; it does not establish all Project/Feature behavior by fiat. After a Feature revision, evidence for an untouched criterion remains valid, while the changed criterion is correctly marked as needing re-evaluation.

**Related Features:** [Verify outcomes](../features/verify-outcomes.md), [Evolve intent](../features/evolve-intent.md), [Develop Telos](../features/self-development.md).
