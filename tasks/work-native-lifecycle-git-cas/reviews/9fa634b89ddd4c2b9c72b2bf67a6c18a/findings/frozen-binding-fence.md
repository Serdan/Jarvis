# P1 — Frozen mutation seam lacks a verified disposable-binding fence

**Location:** `Client/Modules/WorkQueueNativeLifecycleCas.fs:69-74,300-337`; `Client/Modules/WorkQueueGitLedger.fs:1061-1093,1369-1386`

The task requires frozen migration to fail closed and prohibits live Work claims. However, `transitionDisposable true` accepts a caller-supplied `LedgerBinding` and only checks the boolean, identity-field shapes and the normal WQ02/WQ03/WQ04/WQ07 witnesses. `verifyAt` explicitly requires `before.Migration = Frozen`, while `executePinnedDisposableLifecycle` calls `change true`, the fixture path that permits mutation during Frozen. Nothing in the new call chain establishes that the binding addresses an approved throwaway/scratch ledger rather than a real frozen project. A valid pre-freeze registered launch with its still-held account credit, local original journal, and historical source/role receipts can satisfy those proofs. In that case an opt-in caller can claim/checkpoint/complete that real frozen Work even though the epoch should block mutation.

Not registering a production MCP route prevents accidental external exposure today, but it does not enforce the stated invariant inside this reusable adapter. A freely supplied `enabled: bool` is not a durable environment/authority fence.

**Required:** Reject frozen real-project bindings, or make the Frozen fixture exception depend on a verified, non-forgeable disposable authority unavailable to ordinary production call sites. Keep the test-only two-clone scenario, but add a negative fixture for an otherwise-valid production-like Frozen binding proving no project-Git write occurs even with opt-in enabled. Do not enable any live route as a workaround.

This is a boundary/code-inspection finding; the existing tests do not attempt this production-like Frozen counterexample.