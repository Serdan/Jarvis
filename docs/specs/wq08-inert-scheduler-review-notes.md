# WQ08 inert deterministic scheduling and first-connect review notes

**Task:** `wq-08-manager-scheduler-bootstrap`, revised spec sequence 3637, assignment generation 1.
**Base:** `e14173e116dc03b7dbdf261895fde3c893a643ee` (exact source baseline).
**Change scope:** `Server/WorkQueueSchedulerInert.fs`, its explicit fsproj compile entry, `Server.Tests/WorkQueueSchedulerInertTests.fs` and its test-project compile entry. Existing WQ01/WQ02/WQ07 sources are unchanged.

## What the code proves

- `InertScheduler.plan` uses already-defined WQ07 `Contracts.orderAvailable` and `Contracts.admitProject` as project readiness/credit oracles, and deterministically ranks blocking hard-edge predecessors first, then FIFO across the supplied account project set. The only newly supplied role facts are explicitly typed (project/role/config version/queues/instruction digest). An incomplete set or mismatched subscription/coverage fails closed, not as healthy empty demand.
- The planner is pure: no network, service registration, daemon, model, global reservation, Git write, browser send, agent UUID or real-time clock. A stable externally journaled launch ID and versioned input produce a stable length-prefixed SHA-256 request fingerprint. No Manager token is consumed at cap=1.
- Separate `reserveAccount` and `admitAfterReserve` fixture calls preserve WQ07 account-first/project-second ordering. The latter demands the exact original reserve-operation receipt and live token, checks the project head, then revalidates the credited work generation. There is no falsely atomic cross-ledger commit. `abortUnboundC01` makes a project tombstone if and only if the launch ID is unbound; `releaseAfterTerminal` requires matching project/role/token context and the terminal receipt.
- `dispatchAdvice` is an *advisory* one-shot state projection. An original `OutcomeUnknown` gives only `ReconcileOriginal`, never another browser send. `recordDispatchOutcome` is the existing pure WQ07 project CAS; it is not an implementation of an external browser journal.
- `initializeFirstConnect` delegates registration to WQ07 `Contracts.initializeAgent` after the eligibility, original-operation, pinned-role, session-key and project CAS checks. UUID candidate entropy is requested only for an eligible new registration; only a committed project-CAS winner is a registered agent. Same-session original-receipt replay returns the stored UUID/lease with no new allocation. A distinct session, copied key across registered launches, malformed/duplicate UUID, stale head or terminal fence rejects. The bootstrap returns an instruction *digest/reference*, not a speculative instruction body.
- `expireUnregistered` requires a deadline, asserted external original-journal settlement and the exact original dispatch CAS receipt before invoking WQ07 terminal fencing. A late InitializeAgent is refused. `coalesceWake` is a pure bounded-fallback calculation with no timer or model slot.

## Independent-review negatives

Local .NET 11 Release NUnit cases cover capacity=1 Worker without Manager; cap full; two-tier FIFO; graph/index/role high-water fail-closed; token receipt mismatch; stale project CAS; C01 abort vs project admission; unauthorized release; first-connect lost-reply replay, different-session and original-op mismatch; UUID/key collisions across launches; uncertain one-shot browser outcome; terminal/deadline ordering and forged original-dispatch evidence; and coalesced wakeups.

Four real **disposable two-clone Git** fixture variants independently commit and race their local orphan-like ledger records: different launches for the final account slot; C01 tombstone against a late project admission; two session keys racing one launch UUID; and two clients retrying the same launch/operation. The losing non-fast-forward push is detected, the winning remote marker re-read, and the matching *pure* CAS rejection or original-receipt recovery asserted. These tests **do not** claim that production account/project ledgers, browser effect journal, or host protection have been exercised.

## Explicitly unproven / blocked

- **No runtime binding:** `InertSchedulingSnapshot` and journal/deadline inputs are fixture assertions. A real adapter must independently verify authenticated account/project repository identity, complete project set, account/project Git CAS heads, WQ04 producer high-water, immutable role version/instructions, trusted WQ05 gate provenance and host-owned original browser intent / uncertainty proofs. No boolean flag in this fixture is an authorization.
- **WQ04** producer/versioned role demand and **WQ05** hard-edge review/integration adapter remain under correction. **WQ06** conversation/handler transport is inert and not integrated. The project must not integrate or activate WQ08 against those missing dependencies. WQ07 accepted global/project admission is reused but not edited.
- The accepted session key is a continuity token, not an authenticated physical-execution identity. An adversarial copied key within one launch is not distinguishable with current provider evidence; no claim of provider identity security is made.
- The UUID candidate generator runs before an attempted CAS, so a losing writer can discard its candidate; it is not registered as an agent. Exactly one UUID is *committed* for the winning launch/session.
- No live Scheduler/reconciler loop, model Manager, host execution, token admission, browser dispatch, migration, deployment, restart, hosted CI or main push is present or authorized.

Review the exact frozen source and these boundaries. Integration and activation remain Lead/operator-gated even if this inert source receives an independent Accepted verdict.
