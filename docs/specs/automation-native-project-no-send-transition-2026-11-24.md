# Native project effect exclusion: isolated CAS contract

Scope: source-only preparation at base `08994560914ac473c4c9d935bf584c0e3cc7b3cb`. This module is not registered in the running Client, does not authorize an external action, and does not release account capacity. It deliberately does not claim account proof from the unaccepted W12 implementation or production wiring from W16.

## Invariant and ordering

All participants contend on the same canonical project ledger ref and same original effect key. A verified account reservation alone is not send authority. A missing project fence alone is not a no-send result.

| Current project state | Claim | Begin send | Exclusion |
| --- | --- | --- | --- |
| Absent | Commit original HeldBeforeSend + immutable launch intent/args | Denied | Commit original-bound tombstone + immutable evidence |
| Exact original HeldBeforeSend, unchanged from verified claim origin | Denied | Commit Dispatching | Commit original-bound tombstone + immutable evidence |
| Dispatching, terminal, uncertain, unknown, malformed or any other occupied state | Denied | Denied | Denied |
| VerifiedNoSend | Denied | Denied | Only exact operation replay; never another transition |

The one winning commit provides the ordering. A caller must never send from a cached claim result, an applied-operation replay, a stale proof, or uncertain publication. The begin-send commit must be fresh and acknowledged by the canonical Git transaction, before the original provider call. A failed or uncertain send cannot be converted back to HeldBeforeSend. No rollback from Dispatching is supported here.

## Composition contract

- W12 is the owner of authenticated original reservation resolution. Read the actual current original context, candidate, immutable reservation receipt and its origin, account ledger/epoch, status/generation, and current verified project heads. Exclusion is allowed only from an explicitly no-session pre-send account state; unknown/submitted/active/uncertain stays occupied. Revalidate during the project CAS plan. Do not accept a caller boolean, absent intent, browser observation, timeout, closure or fake provider session as proof. The original project fence, if account-recorded, must be the same verified claim origin as the transition witness. The pre-fence case must establish the tombstone on the very key used by ingress.
- W16 is the owner of the authenticated project claim and the provider callsite. Use `checkClaim` within the existing claim/intent/arguments transaction. Use `planBeginSend` within a distinct request-hashed transaction on that same project ref, binding the original claim, effect and exact argument evidence; call the provider only if the transaction reports fresh success (`AlreadyApplied = false`) and all other existing transport and account checks succeed. Every native send route must pass this transition. A read-only original intent is not send permission.
- `planExclusion` is a project transaction planner. The enclosing authenticated caller constructs the W12 typed evidence from the verified context and uses its exact serialized bytes as the `NativeVerifiedNoSend` intent request. The plan writes those same bytes to the canonical fence and `records/native-scheduler/no-send/{operationId:N}.json`, using the verified head and exact predecessor blob. For a held predecessor, the witness includes the verified claim operation ID, request hash, origin commit and original fence blob; the claim actor and all original identity fields must agree. No other project record is overwritten. Preserve immutable original launch intent and argument paths.
- The current isolated source validates the existing worker fence key and the matching observed W12/W16 reviewer draft key; use the accepted common account-side key helper directly at composition, not a new parallel key. A distinct attempt/review claim has a distinct key. Reopening an excluded key under a different dispatch or reservation UUID is forbidden.
- Verify immutable operation origin and the exact evidence blob at both origin and current verified head before release. Use `LedgerStore.verifyIntegrationOperationIntent` with the expected intent, **not** raw string comparison with the envelope's `request` member: LedgerStore recursively canonicalizes JSON keys (including nested F# records). Compare the actual evidence blob bytes separately. Release remains an account CAS against the original receipt, generation and exact current occupancy; never rely on capacity already being released.

## Evidence boundary

The standalone test harness uses real disposable project Git remotes and two clones, with fixture original context (not an authenticated account reservation). It exercises same-key preclaim exclusion, exact held-fence exclusion, begin-send consumption, stale and duplicate rejection, operation replay, immutable origin/blob evidence, successor key isolation, foreign fence path, wrong claim hash, changed assignment generation, unknown fence rejection and simultaneous two-clone CAS conflicts. No actual provider call or account release occurs. Composed tests using accepted W12 authority, W16 ingress, exact release evidence, real worker and reviewer claims and all send paths are required before any enablement. The old Stage B fake effect/no-send helper is not the producer.

The independent account/host source and operational acceptance remain prerequisites; this module alone is not proof of end-to-end scheduler dispatch or safe live activation.
