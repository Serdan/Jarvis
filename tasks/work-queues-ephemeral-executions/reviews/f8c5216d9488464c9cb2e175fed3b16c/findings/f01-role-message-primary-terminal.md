# F01 — P1: Dedicated role-message claim has no coherent terminal transition

**Frozen source:** `9f1cd0fbbb65e250409e4e352d63f5c4b7dd9aa9`  
**References:** `docs/specs/work-queues-and-ephemeral-executions.md` §§7 (lines 225–246), 11 (lines 344–348), 16 (cases 28, 34–37, 44; lines 479, 485–488, 495).

## Counterexample

1. The operator posts `Role(Jarvis, Lead)` message M with no running Lead. A deterministic message work generation W is Available; Scheduler reserves launch L with its injectively matched demand credit.
2. L connects, receives its UUID, and follows §§7/11: `ClaimNextWork` takes W as L's **single primary activation**, consuming the demand credit. This is expressly required by §7 ("one primary work item") and §11 ("a launch created solely for role-message work can claim its single primary role-message work item normally").
3. L replies and calls `CompleteSubjectMessageHandling`. The specified operation marks message IDs Handled, but does **not** define completion of W, clearing its work claim, or writing L's terminal launch proof.
4. §7 nevertheless says a "taskless role-message session" finishes via `TerminalizeLaunch(Finished)`, and acceptance case 44 explicitly tests that path. Yet `TerminalizeLaunch` is allowed only **without an active primary claim**. "Taskless" is not "primary-claimless": W is an actionable message **work item**.

A faithful implementation must either reject TerminalizeLaunch (stranding W/L and capacity) or permit release while W remains Claimed (violating the atomic claim/launch terminal invariant and permitting stuck or duplicated demand). It cannot derive the missing coupling from message acknowledgement alone.

## Required correction

Define exactly one legal path for a dedicated message-primary launch. The natural path is to make canonical message handling idempotently mark the message handled, then `CompleteWork(W)` atomically complete W and terminalize L in project CAS, with account release only after verified receipt. Alternatively explicitly specify a **single project CAS** that atomically commits the reply/action receipt, W completion and L terminal proof. Distinguish this from **ancillary** message handler leases held by an execution with another primary activation; those must never complete or transfer that activation.

Define the multi-message batch semantics relative to one primary credit, including what happens when other live launches hold distinct message credits. Preserve exact idempotent response/action receipts and prohibit double completion.

## Negative-space tests to add

- One dedicated message work item: reply/handled receipt then terminal completion; verify W, L, and global token each transition once.
- Crash or lost response between reply CAS, message acknowledgement, work completion, and account release: reconcile same original operations; do not reopen handled message or free token early.
- Two messages and two launch credits, dedicated primary intake of a batch: no theft of the other credit; either leave second message pending or atomically rematch coverage.
- Racing `TerminalizeLaunch(NoWork/Finished)` and message-primary claim: only one legal project terminal path.
- Ancillary Role handler in a launch with an unrelated primary task: reply completion does not terminalize/release the task activation.

**Disposition:** **Blocking** design correction before implementation or acceptance.