# P1 — Source-effect discriminator admits reserved ledger-CAS refs

**Frozen source:** `bfab99d09e221a2661453dce7bb5fad13bfaf743`  
**File:** `Server/OriginalGitPublisherAdmission.fs`, `validateTarget` and `validateCas`.

**Reproduction (static):** `validateTarget` accepts any syntactically allowed ref for `(OriginalGitRefPurpose.SourceEffect, Some nonemptyProjectId)`. Thus changing `projectTarget.Purpose` to `SourceEffect` while retaining `GitRef = "refs/heads/jarvis-orchestration"` passes `validateTarget`; `validateCas` also passes when `SourceEffectOperationId` is present. Similarly a project-scoped SourceEffect envelope may name `refs/heads/jarvis-account-admission`. Those are reserved original-ledger publication refs, not source-effect refs.

**Why this matters:** This is a default-off contract, so it does **not** demonstrate a live bypass today. But the P0 deliverable is the typed basis for an exclusive remote publisher. A syntactically valid envelope must not permit contradictory purpose/ref identities, especially when the requirement explicitly forbids bypass by changing the command classification. Otherwise a future adapter that routes admissions by `Purpose` can accidentally admit a reserved CAS under the weaker source-effect path.

**Required before accepting the typed P0 contract:** Reject source-effect aliases of all reserved account/project CAS refs; bind purpose, account/project/ledger, repository and ref to the authority's independently trusted ref-class inventory rather than caller-selected labels. Add negative tests for both reserved-ref aliases and an unapproved alternate source ref. The authoritative remote must also enforce this mapping; fixing a client-side validator alone is not the production barrier.
