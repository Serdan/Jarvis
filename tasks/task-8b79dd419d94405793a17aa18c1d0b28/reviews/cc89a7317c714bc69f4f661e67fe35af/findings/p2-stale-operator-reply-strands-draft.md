# P2 — Stale operator reply cannot be rebased without losing the draft

**Frozen source:** `f7c5598aed774712fbfa6edab901a194e6541023`.

## Deterministic concurrency case
1. Agent creates an ALL-policy challenge at version 1 with a `Pending` `Operator` input and a separate `Pending` agent-owned condition.
2. The operator selects the question and types a long response. `beginChallengeReplyUnsafe` (~1211-1220 in `Client/Modules/ConsoleTui.fs`) stores **a snapshot** as `challengeReplyTarget`, including `Version=1`, and generates a stable operation UUID.
3. The agent-owned condition resolves; challenge version becomes 2, while the operator condition remains `Pending`.
4. The operator submits. The ledger correctly rejects expected challenge version 1 as stale (no evidence overwritten).
5. `submitChallengeReplyUnsafe` (~1534-1570) on `Error` **only** sets `challengeError`. The draft and UUID stay, but the UI **never refreshes `challengeReplyTarget.Version`**, so every subsequent Enter repeats version 1 and fails indefinitely. The 30-second refresh updates `challengeItems`, but not `challengeReplyTarget`.
6. To get version 2, the operator must Escape and re-enter; `beginChallengeReplyUnsafe` unconditionally clears `challengeReplyText` and generates a new operation UUID, discarding the complete unsent draft. There is no supported way to resubmit it.

**Independent concurrency evidence:** Reviewer-only .NET11 Release test `OperatorReplyAfterUnrelatedConditionChangeRequiresUpdatedExpectedVersion` at `Client.Tests/ChallengeWorkflowTests.fs:1042`, job `483da9bf88f34bd790cf1c4076e05b19`, **PASS 1/1**. It proves that the backend properly rejects the stale v1 reply but accepts the same operator answer with fresh v2, after a separate agent resolution. The broken recovery is in TUI state management, not the ledger CAS.

## Required
On a definitive stale result, reload the selected challenge, ensure the operator condition is still `Pending` and active, allow the operator to **preserve the draft** while adopting the new expected version and appropriate new operation ID, then retry. Preserve the *same* original ID/draft for uncertain-outcome idempotent retries; do not automatically submit a second intent after an uncertain commit. Provide clear UI feedback when the condition has already been answered, cancelled, or otherwise ceased to be actionable. Test concurrent changes and recoverable drafts end-to-end.

**Impact:** In a routine multi-condition ALL challenge, a completely valid operator reply becomes unsendable through the UI without manual retyping. Violates the specified retain-unsent-text and reconnect/retry behavior.
