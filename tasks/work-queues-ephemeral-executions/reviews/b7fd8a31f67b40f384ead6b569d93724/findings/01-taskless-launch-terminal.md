# B01 — P1 — Taskless Manager launches lack a complete terminal contract

**Frozen source:** `36155553147705748c90d73a5aaf497b511768ef`, `docs/specs/work-queues-and-ephemeral-executions.md`, lines 55–57, 149, 173–183, 207–219, 225, 305–317; acceptance 17, 30–31.

**Finding.** A scheduled Manager is expressly *not* a work-item claimant (line 149), while `YieldWork`, `CompleteWork`, and `BlockWork` require an active work item/activation. Section 9 nonetheless requires normal turn end to use one of those three work-scoped operations. The special `TerminalManagerSlotHandoff` only covers transfer of the Manager's token to a child. There is no defined canonical terminal transition for an ordinary Manager that has dispatched children and then exits, a Manager that wakes to demand that has since disappeared, or an unclaimed child with `NoWork`. Section 8 says a `NoWork` launch becomes terminal, but the proposed write API provides no launch-level terminal transition that creates the `terminalReceipt` required by `ReleaseGlobalAgentSlot`.

**Concrete failure.** With `maxConcurrentAgents=8`, M reserves token #1, launches two workers, and finishes management without claiming any work item. `YieldWork` is inapplicable; no handoff is needed. Either M's token remains reserved indefinitely, or a client releases it based on prose/tab/elapsed time without first fencing M's still-live launch. A later M call can then schedule additional work after capacity is reused. A cancellation between scheduler preflight and Manager execution creates the same gap.

**Required design change.** Define an idempotent **launch-level** terminal operation and state machine for *all* launches, including zero-claim Manager, `NoWork`, predispatch cancellation, and error/recovery. Require a stable launch ID, expected launch version/generation, exactly one terminal reason/receipt, and rejection of any subsequent dispatch/claim after terminalization. Distinguish project launch-terminal commit from later account-global token release: the token is not released until that project terminal receipt is independently verified. Replays, retries, and competing terminal outcomes must converge on one receipt. Specify who calls this operation when a scheduled Manager's session ends normally.

**Acceptance fixtures.**
1. Manager dispatches two children, ends normally and releases exactly its own token once; child tokens remain.
2. Manager wakes after demand disappears: canonical `NoWork` terminal record and one release, no fabricated work item.
3. Late `ClaimNextWork` or Manager dispatch after terminal receipt is rejected.
4. Two clients race `NoWork` versus handoff/cancel on one Manager launch: one terminal state, no lost/duplicated token; restart/replay returns the same result.

**Severity:** Blocking architecture contract. No source code changes requested by this review.