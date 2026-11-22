# P1 — Terminal proof is absent from immutable operation intent

**Exact source:** `5cf8f99542a9653a013683e32e56a2c32a706164`, `Client/Modules/NativeProjectEffectFence.fs:231-259,268-277`; unchanged `Client/Modules/LedgerStore.fs:2703-2717,2721-2745`.

**Original requirement:** B1 requires a typed once-only effect journal/terminal proof and negative denial of foreign/duplicate terminal evidence, with source/effect provenance preserved. This is a **disposable fake-provider** defect, not a claim that the fake receipt authenticates a real provider.

**Defect:** `recordFakeTerminal` validates `proof` only inside `extraValidate`, but the shared `update` constructs `LedgerIntent.RequestJson` from only `candidate`, `effectId`, `expectedStatus`, and `nextStatus`. The proof's marker, status, effect ID, and provider session ID are excluded from the immutable request hash. `LedgerStore.transact` checks an existing operation ID and matching request hash **before** invoking the planner/validation closure.

**Reproduction from source (targeted mutated-proof case not executed):**
1. Claim a disposable Worker candidate; commit `beginFakeEffect`.
2. Call `recordFakeTerminal` with a valid fake `Terminal` proof and transition UUID `T`; observe `VerifiedFakeTerminal`.
3. Retry `recordFakeTerminal` with the **same** `T`, candidate and effect ID, but change proof to `Status = "Unknown"`, a foreign marker or a different `ProviderSessionId`.
4. Because the canonical intent is identical, `LedgerStore.transact` takes its `AlreadyApplied=true` path and returns success without executing `extraValidate`. The original ledger record is not overwritten, but a changed/invalid proof is misleadingly accepted as an exact replay and is not distinguishable in the operation receipt.

**Impact:** One immutable operation ID can appear to authenticate more than one terminal input. A verified provider proof cannot be source-bound to the receipt; negative-space claims about changed-payload/foreign terminal denial do not hold under same-UUID retry.

**Bounded remedy:** Include the entire canonical fake terminal proof in the immutable terminal operation intent (or implement a typed terminal-specific transaction) and add cross-clone tests for exact replay versus same-UUID changed/Unknown/foreign proof, requiring `OperationIdReused` or explicit rejection. Keep B2/live provider, account integration, deployment and schedules held.