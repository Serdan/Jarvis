# WQ-08: practical non-agent dispatch prototype (2026-11-16)

**Scope:** Task `wq-08-manager-scheduler-bootstrap`. This is an isolated,
default-off implementation boundary on current main, not a production Scheduler.
No new model Manager, process, timer, hosted action, migration, deployment or
restart is installed. The existing `InertScheduler.plan` remains guarded by
WQ-04's `Contracts.canDispatch = false`; no fabricated high-water witness
permits runtime scheduling.

## Small, explicit lifecycle

1. **Select demand:** Existing deterministic WQ-08 planning orders eligible
   blocking-first/FIFO work; WQ-04 defines `RankedRoleDemand` and historical
   `RoleConfiguration`. The source adapter must pin the actual canonical Git
   state and role evidence. When source authority is missing, report an
   actionable unknown rather than inventing an empty queue.
2. **Reserve/admit:** Existing WQ-07 `reserveGlobal` and `admitProject`
   are account-first, project-second Git CAS boundaries. A failed C01 can
   only release after `abortUnboundAdmission` records a project tombstone;
   existing project admission is not overridden. Cap=1 is supported without
   a model Manager. `drivePreparedLaunch` composes these steps with a typed
   `SchedulerAdmissionPorts` contract: reserve, admit, then the original
   browser one-shot. An unknown admission response attempts only C01 abort;
   if project admission already exists, it surfaces reconciliation and holds
   the token, rather than releasing it speculatively.
3. **Journal original browser attempt:** `SchedulerDispatchPrototype.runOriginal`
   accepts the original pinned plan, selected WQ-04 demand, historical role,
   observed Git heads and account/project admission receipts. Its injected
   `TryMarkOriginal` port **must durably CAS a one-shot marker before**
   `SubmitOriginal` is invoked. An existing marker never resends, even when
   the process crashed before recording a browser result.
4. **Record and reconcile:** Accepted browser submission is persisted in the
   original journal and then in the WQ-07 project dispatch CAS. Unknown,
   exception, transport timeout, missing journal and failed outcome/project
   CAS return `NeedsReconciliation` with original evidence. Explicit
   definitive no-effect is visible, not silently retried.
5. **InitializeAgent:** Existing WQ-08 `initializeFirstConnect` accepts the
   first provider-session key for the original launch, commits exactly one
   UUID under project CAS, and replays the original receipt for the same
   execution. A different session is rejected. WQ-06 conversation/handler
   transport remains independently owned and must be bound later.
6. **Finish/release:** Existing WQ-07 terminal receipt and global release
   remain separate. This prototype does not auto-release unresolved browser
   attempts or reset an unknown operation.

These are injected **client-owned port contracts**, not server-local durable
state. The server remains a stateless authenticated relay. There is no
production implementation of the browser, account/project Git, WQ-04
canonical source or WQ-06 conversation ports in this change.

## Operator/agent recovery for exceptional ledger state

`SchedulerDispatchPrototype.manualRepairPacket` contains launch ID,
project/work, role/version, fingerprint, original browser operation ID,
account/project Git heads, source ref/commit (when known), last observed
browser outcome and the reason a decision is pending.

An authorized agent can investigate or repair the ledger directly instead of
requesting application-code changes:

- Before any edit, record **the current verified remote SHA**, original
  operation IDs and relevant `git diff`. Keep an immutable recovery branch or
  commit reference to the original ledger head. Do not overwrite another
  writer's work or force-push an unverified replacement.
- Inspect the **original** browser journal and project dispatch receipt, then
  compare against the account token and project launch terminal state. A
  missing reply is not proof that submission did not happen. Check the
  existing session/execution, not a fresh provider session.
- If the state is demonstrably incorrect and correction is authorized, edit
  the affected canonical record, commit a reason and before/after evidence
  with the original IDs, and push using ordinary Git CAS. A conflicting push
  means re-read and reconsider, not force. Preserve original evidence so
  another agent can reconstruct the intervention.
- If the browser effect is still unknowable, **leave it explicitly unknown**
  and do not issue another one-shot launch or release the account slot.
  A new launch is a separate authorized decision after reconciliation.

The packet is guidance, not an automatic ledger mutation tool. Agents are
trusted to perform documented, auditable exceptional repair.

## Evidence and remaining boundaries

The new NUnit tests exercise cap=1 Worker dispatch with no Manager,
first-connect same-execution retry/different-session rejection, accepted
original replay without another browser send, unknown submit, marker
write timeout, crash between marker and browser result, rejected no-effect,
lost original browser result persistence, project CAS conflict with browser
receipt retained, and actionable WQ-04/role/source mismatch. Existing WQ-08
tests additionally cover actual disposable two-clone Git last-slot and C01
races; none of these tests are a production browser or authoritative account
ledger invocation.

**Still open:** WQ-04 canonical producer/source-index and historical role
authority must be independently accepted as actual same-head evidence;
WQ-06 role conversation/handler binding; WQ-03 original host/browser effect
journal adapter; production scheduler wake, migration and explicit operator
activation. This patch does not change their files or assert those interfaces
already exist. Review must freeze the exact branch SHA, and accepted review
does not itself authorize production cutover.
