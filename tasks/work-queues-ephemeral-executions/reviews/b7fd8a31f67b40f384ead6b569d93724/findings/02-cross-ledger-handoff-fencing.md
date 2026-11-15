# B02 — P1 — Global slot handoff lacks a proven project-fence-before-transfer ordering

**Frozen source:** `36155553147705748c90d73a5aaf497b511768ef`, lines 117, 173–183, 198–219, 227–231, 314–317; acceptance 30–33.

**Finding.** The design correctly rejects fictitious atomicity across the project Git ledger and account-global admission ledger. But the last-slot handoff says one atomic *global* CAS replaces Manager M's token with child C's and thereby "fences" M, while M's launch/demand and work-scoped eligibility are canonical **in the project ledger**. A global CAS cannot simultaneously update the project launch. The spec does not require a verified project-side terminal fence for M before the global token swap, nor does it define replay/reconciliation for every partial handoff state. This is a real two-ledger race, not a demand for a distributed transaction.

**Interleaving.** M has the last global token and project launch still permits dispatch operations. A Manager/project operation checks M's eligibility. Another device performs global M→C CAS. Before M's project record becomes terminal, the older project operation commits (or a late session invokes a new one), despite M now being logically terminal. The global account record alone cannot invalidate a project-ledger CAS already in flight. If the browser child C is submitted before project demand is durable and M is fenced, a crash can leave a running child without a unique project demand/launch receipt. The opposite ordering can strand a reserved child demand after an uncompleted global swap.

**Required design change.** Define an **ordered, idempotent multi-ledger handoff protocol**. One safe shape is: (1) CAS-prepare child demand and terminally fence Manager's project launch in the project ledger while M's global token is still held; (2) verify that receipt and atomically swap M's token for C's in the global ledger using caller-stable handoff/launch IDs; (3) permit exactly one browser submit only after both receipts and dispatch journal state are reconciled. Ambiguity in either ledger retains capacity and prevents a new launch ID/retry. Specify compensating rollback/reconciliation for project-prepare-only, global-swap-only, and submitted-but-unobserved states. For normal completion/recovery, project launch/work claims must be fenced **before** account token release. A stale pre-handoff Manager operation must fail project CAS.

**Acceptance fixtures.**
1. Two clients race an in-flight Manager project mutation against last-token handoff; the project fence serializes them, and no post-terminal dispatch wins.
2. Crash after project prepare but before global swap; replay/compensation yields exactly one outcome without overdispatch.
3. Crash after global swap but before browser submission; C can be dispatched once using the original IDs/receipts.
4. Uncertain browser submission, cancellation and independent reconciler race; token never duplicated/freed early, nor permanently lost without an explicit RecoveryNeeded record.
5. `maxConcurrentAgents=1` and two-project contention: every admitted browser launch has a durable compatible project and global receipt.

**Severity:** Blocking concurrency/recovery invariant. Does not require any agent-ID permission checks.