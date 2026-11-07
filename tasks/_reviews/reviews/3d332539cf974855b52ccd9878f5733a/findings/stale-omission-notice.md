# [P2] An omission can become invisible after an intermediate complete delivery

**Location:** `Client/Modules/AgentInstructions.fs:120-147,309-354`.

The all-or-nothing resolver represents an oversized file using `Scope = "instruction-warning"`, but a successfully delivered file has `Scope = "project"` or `"workspace"`. `DeliveryCache.Filter` stores fingerprints separately under `document.Scope + ":" + document.Path` and never retires the other scope's entry.

**Reproduction on one path within a Working turn:**
1. Write oversized file A; resolve it; a warning with full source SHA(A) and budget is emitted and cached as `instruction-warning:path`.
2. Replace with small file B; resolve it; full instructions are emitted and cached as `project:path`.
3. Replace with the *same* oversized bytes A; resolve in the same budget; the warning text and fingerprint are identical to step 1, so the cache suppresses the warning, although B has ceased to be applicable and A is not delivered.

The agent receives neither new instructions nor a renewed omission notice. This is a stale instruction-state error, not just redundant-message handling. The same issue exists for workspace scope.

**Request:** Model the *latest observed delivery state per logical document path* (source hash, delivered/omitted state, relevant budget) rather than independently retaining warning vs content fingerprints. Add A-oversized -> B-delivered -> A-oversized regression coverage.
