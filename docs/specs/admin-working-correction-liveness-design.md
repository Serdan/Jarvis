# Administrative Working correction: server-mediated liveness design

**Status:** isolated protocol-3.65 compatibility candidate with durable MCP admission and correction permits; not reviewed, integrated or deployed. Legacy-client drain and detached process-tree termination proof remain unimplemented rollout blockers.
**Task:** `task-c4460fbd65a244419650fc5ef5669a6f`, Worker 3.
**Context:** independent review `6fd76f4209ba4f75a86b05426e93803e` requested two P1 fixes for real worker admission and failed-heartbeat races. Jarvis Lead directed this design toward the rare correction path rather than new Git transactions per ordinary tool call.

## Existing topology and measured costs

All MCP tool requests in the current API pass through `Server/McpTools.fs:McpToolHelpers.send` and `ClientService.SendCommandToUser`; the latter chooses a registered SignalR client/device and tracks each pending response using `ClientResponseTracker`. The server is intentionally **single-instance**, with ephemeral `UserService` connection registrations; the orphan Git ledger remains authoritative for task/attempt/agent state. `FeedbackStore` already inserts a SQLite `operations` record before each MCP command and marks it completed when the *server-side* request returns.

The existing telemetry is **not evidence of source-operation termination**: `ClientService.SendCommandToUser` can time out after 10 minutes, cancel its pending correlation or receive an HTTP cancellation while the client is still executing. `McpToolHelpers.finishOperation` then marks the telemetry record completed, even though the operation may still be running. Also `beginOperation` intentionally swallows SQLite errors and returns `None`; this is fine for advisory diagnostics but **not** for an authoritative negative liveness check. The telemetry has no agent, device generation, correlation or terminal-client-ack identity yet.

The early canonical Git lease wrote on ordinary tool calls and was replaced by an in-flight reservation. One reservation **per** tool yielded a measured 100-call mixed load of **44.45 s, 233 Git writes and 13,350 Git process starts**. The current in-process coalesced scope avoids most of that: **6.84 s, 33 actual notification writes and 2,046 Git starts** (local fixture; same 34 searches / 33 file reads / 33 self-notifications). It still leaves an unbounded canonical blocker after process crash and can never safely self-expire solely by elapsed time.

## Principle

**The server can coordinate request admission, but cannot itself certify that a disconnected client or child process has stopped.** Its request table must be treated as a *conservative veto*, never as a second authority for task state. An operator correction still requires a single canonical Git transaction with exact task, attempt, agent version, assignment generation, nonempty reason and immutable audit event.

On a healthy one-server installation, moving the hot-path in-flight reference count into the existing server request pipeline avoids canonical begin/end Git transactions. Expensive liveness verification is done only when an operator attempts a correction. If liveness is unknown, **refuse correction**, rather than guess inactivity.

## Proposed admission/correction protocol

1. **Before dispatch:** For each genuine initiating agent call, register a uniquely identified in-flight entry in a per-user, per-agent coordination gate. The server records the chosen device, connection generation, correlation ID and request identity *before* calling `ReceiveCommand`. The caller's actor identity is `RequestDeviceRouting.agentId()` (or the checked command actor); it is **not** a target-only `ObserveAgent` identity. Do not register a cross-project destination notification as activity by the source agent in the destination.
2. **Alias safety:** Until the server knows a verified canonical project ID for every worktree selector, key the gate by `(authenticated user ID, normalized agent ID)` across projects. That may conservatively reject a rare correction while the same-named agent runs in another project; keying only by unverified project name would allow alias bypass. Project IDs may narrow keys later only after authoritative mapping.
3. **Normal completion:** Mark an entry terminal only when a response from the **same device connection and generation** is accepted by the server's response tracker, or when the server has proved dispatch never occurred. Timeout, browser cancellation, SignalR loss, server restart and failure to deliver a response are *indeterminate*, not proof of termination.
4. **Correction enters exclusive gate:** The operator's authenticated `CorrectAgentWorkingState` call targets `TaskWriteCommand.OwnerId`; acquire an exclusive gate for that user/target agent **before** checking liveness or dispatching the Git CAS. Reject when any work entry is active or indeterminate. While the exclusive gate is held, reject or queue new calls claiming that target agent. The operation must include pending-dispatch and not-yet-replied entries, not merely currently executing code.
5. **Expensive correction-path verification:** Inspect durable indeterminate entries across registered and previously known devices. Ask connected clients to attest their current executing operations and detached job ownership using a typed protocol; reject if any relevant device cannot prove a safe terminal state. Last-seen timestamps and mere SignalR disconnection are not proof. If verification succeeds, issue the existing Git correction with exact expected identities and audit; release the server gate only after the correction's outcome is known/reconciled.
6. **Trust boundary:** The server gate does **not** independently change `ReviewAgent.Execution`, agent versions, task generation or checkpoint. A user seeing `Ready` must be the result of the canonical ledger CAS, not a server presence projection. Any uncertainty only blocks the administrative mutation.
7. **Restart:** Server memory alone is insufficient: a restart can erase in-flight entries while an earlier client request is still executing. Reuse/extend the existing SQLite operation telemetry with a **separate execution-terminal state**, agent/device identity and correlation data, and a fail-closed health bit when journaling is unavailable. Unlike the current `completed_at`, a client terminal receipt (or proven non-dispatch) is the only clearing event. On restart, all previously admitted nonterminal entries remain correction vetoes.
8. **Client crash/disconnect:** A client process dying does not prove its spawned child or managed background job has ended. Recovery must be a separate operator-audited command backed by a device-specific terminal witness (e.g. local process/job-group/managed-job reconciliation from a reconnected, authenticated client), or the correction stays blocked. A server timer, idle period, generation change or client heartbeat alone cannot clear an outstanding execution.
9. **Uncertain correction result:** Reuse the operator's stable `operationId`; query the canonical Git operation receipt to resolve whether CAS succeeded before releasing the exclusive gate. Return the real receipt and affected-agent invalidation metadata when available. Never replace committed command outcomes with a generic liveness error.

## TOCTOU and negative-space acceptance cases

- **Admission-before-correction**: blocked owner call starts, operator correction with all correct ledger versions returns stale/busy, owner finishes with unchanged assignment token.
- **Correction-before-admission**: exclusive correction gate starts, concurrent self-agent command cannot be dispatched while correction CAS runs; once released it reloads canonical state and must not silently continue a stale execution.
- **Timeout-after-dispatch**: server timeout or HTTP cancellation before terminal client acknowledgment leaves an indeterminate entry and refuses correction, even if telemetry says request completed.
- **Disconnect/reconnect**: neither connection loss nor new device generation establishes a terminal receipt; review all unknown entries and possible surviving child jobs.
- **Server crash/restart**: in-memory gate is empty, but SQLite nonterminal entries remain vetoes; SQLite missing/corrupt/unavailable state refuses correction.
- **Aliased selectors**: an operation routed through a worktree alias prevents correction by logical project name for the same user/agent.
- **Ambiguous Git outcome**: an operation that committed in the client but lost its SignalR response cannot cause a false terminal server result; canonical receipt recovery uses the original operation ID.
- **Unassigned self-inbox**: a canonically registered Parked/NeedsWork agent can read its inbox without becoming Working; unknown/corrupt canonical agent state fails closed.
- **Remote job lifetime**: an ordinary `start_job` request is terminal when its job handle has been durably persisted and returned, but correcting its agent while that independently executing job is still doing work requires explicit job-ownership policy; the tool response alone is not sufficient evidence of the job's termination.

## Implementation scope and sequencing

1. Introduce a typed `AgentExecutionGate` in server with deterministic admission/correction races and ownership; do **not** immediately remove the existing Git safety guard.
2. Extend existing `FeedbackStore` operation records to preserve agent, device, generation and client-terminal versus indeterminate status without a second Git write path. Evaluate whether SQLite writes are affordable (they already occur) and fail closed when journaling is unavailable.
3. Place the exclusive gate specifically around the operator correction in `McpToolHelpers.send`, including cross-device dispatch. Add safe reconciliation for client restarts before claiming complete crash-recovery support.
4. After independent tests establish the new invariant, remove the client-side per-tool canonical reservation write path. Keep the canonical CAS and auditable operator reason untouched; decide whether bounded leases remain useful as advisory recent activity, not sole proof of quiescence.
5. Independently review frozen source and full Release suites before merging. No live deploy without authorization.

**Lead decision (2026-11-11): approved.** SQLite may serve as a durable conservative *execution witness*, not Git task-state authority. Uncertain dispatches must veto correction until authenticated device/process/job reconciliation. No blind expiry.

## SQLite implementation checkpoint (not yet active in MCP admission)

`FeedbackStore` now stores `agent_execution_witness` and
`agent_correction_permit` independently of prunable telemetry. Admission
inserts the existing telemetry row and a normalized user/agent witness
atomically with the correction-permit check, using a SQLite writer
transaction. Operator correction acquires a persistent exclusive permit
against unresolved witnesses using the same serialization; stable
operator operation IDs support restart replay. An unresolved Git outcome
cannot release that permit.

A request can be bound to an exact device, connection, generation and
correlation before dispatch. A valid client terminal receipt updates the
witness even after the ordinary response tracker times out. The SignalR
hub now records such receipts independently. Forged or stale-generation
receipts fail; only a request proved never sent can be marked NotDispatched.
The optional `ClientService.SendCommandToUser` binding hook fails
closed if durable route binding cannot be persisted.

**Important:** `McpToolHelpers.send` does **not yet** invoke this
admission/correction protocol. This increment is therefore tested storage
and optional routing support, **not** an operational replacement for the
client's Git reservation gate. Do not integrate it alone.

Server Release tests pass, including 16 concurrent admission/correction
races, restart retention, timeout and late-receipt negative tests. A
manual local SQLite microbenchmark of 100 full admission/route/telemetry/
terminal cycles took **43.0 ms (0.43 ms/cycle)**, excluding SignalR and
actual client or Git work. This is not an end-to-end latency measurement.

Next: wire initiating-agent MCP admissions before actual dispatch,
acquire the exclusive target permit for correction, handle all no-route
and ambiguous delivery outcomes, test crash/long-job reconciliation,
then remove the old Git reservation path only after independent review.

## Legacy-protocol migration barrier (next checkpoint)

Lead identified a missing negative case: **the first upgraded server may have
zero entries in the new witness table even while a pre-witness 3.63/3.64 request is still
executing on a client**. Empty tables are not proof of inactivity.

Every new or migrated SQLite database now seeds a singleton
`agent_execution_epoch` row in `AwaitingLegacyDrain`. The operator correction
permit transaction refuses every request while this row is unverified,
**including one with no unresolved witness records**. A typed
`AgentExecutionReadiness` projects the schema-checked states to F#. The
trusted (not publicly wired) `TryAuthorizeLegacyDrain` transition demands a
specific operator, stable operation ID, substantive evidence, and a successful
external legacy process/job quiescence verifier. It atomically records the
approval after checking the unresolved witness table, persists across server
restart, and only accepts idempotent retries with the same approval ID.

Tests model legacy 3.64 operations in old telemetry with no witness,
a server timeout, and server restart. Correction remains blocked before the
explicit verified drain. Tests also cover rejected verifier results,
idempotent approval replay, conflicting approval rejection and durable ready
state. Full Server Release passed **140/140** on this checkpoint.

**Not a complete migration protocol yet.** No authenticated real-device
process/child-job verifier is wired to approve the epoch, so a newly migrated
instance remains intentionally quarantined for correction. Before enabling
the new gate in production, reject old pre-witness client protocol sessions,
reconcile operations dispatched before migration, and require each connected
device/job to prove termination. An old client reconnecting must never
silently make `Ready` authoritative. Do not introduce a public "force ready"
shortcut. This branch is not integration-ready.

## Exclusive administrative correction gate (latest checkpoint)

`McpToolHelpers.send` acquires a persisted, target-agent-specific
`TryAcquireAgentCorrection` permit **before** forwarding
`CorrectAgentWorkingState`. This serializes with the same SQLite writer
transaction used by agent activity admission, across project selectors
and devices. The target is the command's `OwnerId`, not its initiating
operator.

A typed `AgentExecutionResolution` query distinguishes never-dispatched,
authenticated client-terminal and unresolved activity. The exclusive permit
is released only when the initiating request is proven never dispatched, or
when its exact client-terminal witness was persisted **and** its canonical
Git result was successful. Timeout, SQLite failure, dropped response and
uncertain Git outcomes conservatively retain the permit. Failure to release
does not mask the original command's result.

Tests cover authenticated MCP correction with no available device,
correction refused while a target owner has active work, persistent
exclusion through restart, eight concurrent second-device attempts,
and all three typed witness-resolution states. **Full Release Server:
143/143 passed.**

## Two-stage cross-project notification execution proof

A cross-project NotifyAgent request now admits its initiating source actor
and binds that original witness to the exact client/correlation for sender
AgentInbox validation. An authenticated terminal receipt is mandatory before
the destination stage. A new SQLite-writer-serialized
TryBeginAgentContinuation operation atomically creates a linked child witness
for the destination under the SAME source actor, with a unique parent ID;
it refuses an unproven terminal, repeated child creation, or a correction
permit acquired between stages. A persistent parent_request_id field is
migrated without deleting prior witnesses. Destination routes bind their
own independent client correlation and terminal receipt; both witnesses
survive server restart.

Infrastructure project-health probes no longer carry an initiating agent ID
or activate Ready assignments, and witness-bound source/destination routing
requires the current protocol. A proven pre-dispatch failure can terminate
only its own still-Admitted witness. Dispatched timeouts remain unknown;
untracked or forged receipts cannot advance the continuation.

Added deterministic tests covering source timeout/forged receipt and
correction in the interstage gap, repeated child admission, restart, and
authenticated end-to-end MCP notification through a simulated SignalR
client with two distinct exact receipts and no agent attribution on probes.
**Full Release Server 146/146 passed.** Previous full Client Release
557/557 also passed, before this server-only increment.

Remaining before independent acceptance: real authenticated legacy-client
and detached-child-job drain/recovery, full SignalR disconnect and SQLite
failure-path integration tests, ambiguous Git result and audited orphan
permit reconciliation. The old canonical Git reservation path remains
in place. No merge/deploy.

## Detached process-handle safety checkpoint

`StartJob` responds when its detached process starts, not when the process
tree terminates. An authenticated `StartJobResult` is therefore **not**
terminal execution evidence. `FeedbackStore.TryRecordAgentClientTerminal`
reads the original admitted command classification from the durable
execution witness, not time-pruned operation telemetry. For `StartJob`, it explicitly
refuses the `Dispatched -> Terminal` transition, even when SignalR delivers
the successful job handle. The client still receives its successful result,
but an administrative correction remains vetoed by the unresolved witness,
including after tracker eviction and later `GetJobResult` completion.

This is a deliberately conservative temporary gate, **not job lifecycle
reconciliation**. To release it safely, persist a child job identity with
authenticated device/process-generation provenance, reconcile all descendants
and external process-tree state, and atomically record certified terminal
evidence. A lone `JobStatus.Completed` only reports the monitored parent
process exit; descendants may remain. Canceled status may be observed before
the process tree finishes termination. Do not auto-clear unresolved jobs by
timeout, process restart, missing in-memory job handle, or a successful
`GetJobResult` poll.

Added a real authenticated `HubService.SendClientResponse` regression test
for a successful `StartJob` handle and repeated receipt, followed by a
separate terminal job-status poll: the original job witness remains unresolved
and vetoes correction. Also added prior tests for exact authenticated
late-terminal recovery, SQLite witness outage, replaced device generation,
legacy protocol, missing device and unsupported command dispatch refusals.
At this checkpoint, **Server Release 150/150 passes** (when validated).
The old canonical Git reservation gate remains in place; no merge/deploy.

## Tool classification must outlive disposable telemetry

The execution witness now owns a durable `tool_name` independently of the
30-day `operations` telemetry table. Both normal agent admission and the
cross-project notification continuation record the command classification
in the same SQLite writer transaction as admission. The authenticated
terminal-receipt path reads the retained witness classification, not a
LEFT JOIN against pruneable operation rows. `StartJob` can therefore never
become a generic terminal receipt merely because its telemetry expired.

Schema migration adds the column to earlier databases without resetting
the protocol epoch, backfills existing witness types from any surviving
operation rows, then labels child notifications only where the parent is
provably a `NotifyAgent`. Historical rows whose command classification
cannot be recovered remain NULL and **fail closed** on terminal receipt;
the target actor stays blocked from correction until independently
reconciled. Neither age nor a missing telemetry row constitutes evidence
of inactivity.

Regression tests cover retained `StartJob` and ordinary command labels
after telemetry is deleted, and a legacy schema migration where one
command's history survives while a second `StartJob` was pruned.
The safe ordinary response can terminate; the unknown historical
request remains unresolved.

## Exact detached launch identity and adversarial receipts

An authenticated successful `StartJob` response stores the immutable
`JobManager` GUID-N job ID, client-reported launch timestamp and first
receipt time alongside the original device/connection/generation/correlation.
Only the canonical 32-character lowercase nonempty GUID-N form emitted by
`JobManager` is accepted. A replayed matching receipt is idempotent; a
different job ID or start time, a reused job ID on the same device, a stale
device generation, wrong connection or wrong user must not overwrite the
original evidence. Malformed JSON, missing or failed responses and
noncanonical identities leave the witness unresolved with no job identity.
Neither recorded handles nor a later `GetJobResult` grant correction.

Negative-space regression tests cover all these cases and confirm the
original handle/first receipt persist across server restart and 30-day
telemetry pruning. A real authenticated `HubService.SendClientResponse`
fault-injection test also forces the SQLite `job_id` update to fail:
the successful client response still reaches the original tracker and
the witness stays `Dispatched` with no job identity. A later duplicate
from the *same* authenticated device route after database recovery
records the exact handle, even after the server tracker has evicted
its correlation. It remains nonterminal; correction is vetoed across
store restart. A missing receipt cannot be reconstructed from a
client timeout, disconnected transport, or missing in-memory job.

**Unresolved execution risk:** `Client/Modules/JobManager.fs` currently
marks `Completed` on the tracked parent process exit and marks `Canceled`
before proven descendant exit. Its job registry is process-local. Neither
status proves an OS-level detached process tree is quiescent, especially
across client restart. Separately `BoundedProcess.runWithCancellation`
uses OS process-group cleanup (Linux/macOS) or Job Objects (Windows), but
best-effort kill requests are not independently reaped/attested as a
persisted child-tree termination receipt. No generic process-command
terminal response should be promoted to proof of complete process-tree
quiescence without auditing that contract.

The server's terminal-receipt policy recognizes only types in the current
`AgentProtocol.commandDefinitions` catalog plus the synthetic `NotifyAgent`
continuation, explicitly excluding `StartJob`. Unknown future or persisted
tool classifications fail closed rather than inheriting generic terminal
semantics. A regression admits a hypothetical `FutureLongRunningCommand`
alongside an ordinary `ReadFiles`: only the known tool becomes Terminal,
while the future command still blocks correction after restart.

## Mainline promoted ToolOperation compatibility (integration blocker)

Mainline now automatically promotes slow MCP commands to client-owned
`ToolOperation` jobs. An `AgentCommandResponse.PendingJob` handle is a
**nonterminal response**: the original command can still be executing,
including Git mutations. P1's generic authenticated terminal-receipt path
must never accept that handle as `AuthenticatedClientFinished`. The
client-owned job can outlive the HTTP request, and a later ordinary
`GetJobResult` response is not inherently proof of the original actor's
operation ending. A premature Terminal witness here could authorize
administrative correction against genuine work.

The SQLite witness now has a separate immutable pending-job identity
(`tool_job_id`, `tool_job_started_at`, `tool_job_receipt_at`) linked
to the exact authenticated original user/connection/generation/correlation
and initiating agent/command, distinct from `StartJob` process handles.
`TryRecordAgentDeferredToolJob` only accepts a canonical GUID-N handle,
rejects mismatched actor/command/route and conflicting/reused job IDs,
and **keeps the original witness Dispatched**. The generic terminal UPDATE
also explicitly excludes rows with a durable pending-job ID, so an ordinary
late terminal receipt cannot bypass the marker. Neither age, poll timeout,
nor a failed/missing job lookup may release it.

**Not yet wired:** the current P1 branch predates the mainline
`PendingJob` protocol field. The P1 witness protocol is now **3.65**,
not 3.64: deployed mainline 3.64 clients already promote long-running
tools but lack the complete P1 witness and terminal contract. Both
3.63 and 3.64 belong to the legacy drain epoch and MUST be excluded
from witnessed dispatch until protocol 3.65 is deployed end-to-end.
During integration, HubService MUST
dispatch `PendingJob = Some handle` to the promotion method *instead of*
the generic terminal method, using the original authenticated route, and
must not mark a promoted operation finished until an independently verified
typed terminal job result is bound to that exact original witness.
Cross-project NotifyAgent's source/destination continuation requires the
same treatment. An isolated no-commit merge preview with main at
`4dc2b489` exposed six conflicts (Client/SignalR/Client.fs,
Common/Messages/AgentCommand.fs, two Server.Tests files,
Server/McpTools.fs, Server/Services/ClientService.fs); the preview was
aborted and its clean worktree deleted. No shared branch was merged,
rebased or deployed.

### Required rollout and proof boundaries

A safe deployment is a coordinated protocol transition, not simply a
server merge. Deliver 3.65-capable clients to every participating device,
deploy a server which authenticates and durably records pending handles
before it acknowledges them, and keep `AwaitingLegacyDrain` in force.
Only authenticated evidence that **all** 3.63/3.64 client executions,
outstanding tool jobs, detached process trees and obsolete server
instances are drained can justify the separate durable epoch transition.
Do not treat an empty witness table, a timed-out MCP request or a device
disconnect as that proof. The epoch verifier and real job terminal
attestation remain unimplemented, so the correction endpoint must stay
inaccessible in production. A successful read of a promoted job handle
must never release the correction permit, including in the
`CorrectAgentWorkingState` response path.

Mainline `JobManager` reports `ToolCompleted` or `ToolFailed` only after
its original operation task has finished and stores its `ToolResponse`.
`GetJobResult` supplies that response plus the stable `OperationId`,
but not the original client boot identity or job start time; the start
time must therefore come from the separately authenticated original
`ToolJobHandle`. Any future terminal transition must verify the
original project/actor/command, exact job ID, original operation ID,
authenticated device incarnation, non-pending terminal response and
typed job status. `Running`, missing/expired handles, transport
timeouts and unverified cancellations are never terminal evidence.
Even a `ToolCompleted` whose original command was `StartJob` is only
a **process handle** and cannot certify its detached child tree.


### Isolated P1/main compatibility checkpoint (2026-11-13)

The registered `worker3-p1-compat` worktree reconciles mainline's
client-owned pending `ToolOperation` protocol with the P1 durable
execution witness, without merging or deploying the shared branches.
A real authenticated `HubService.SendClientResponse` test confirms
that `PendingJob=Some` persists a nonterminal job handle rather than
marking the original command Terminal. The source and destination
of cross-project notifications each retain a separately routed witness.
Client canonical in-flight reservations and lease heartbeats remain
active around mainline's asynchronous execution.

A closed assignment with Execution=Stopped and State=Ready may still be
retained for owner inspection and completion. The self-`AgentInbox`
read bypasses a Working reservation **only** after an independent,
same-snapshot canonical check that the assigned task is Closed with
the exact retained attempt. It cannot restart, reserve, or report
Working. An unknown agent or nonclosed assignment remains fail-closed.

Legacy-drain approval is now stored with the exact `AgentProtocol.version`
that was independently verified. A previous/NULL version is read as
unverified, cannot acquire a correction permit, and cannot be silently
backfilled or reauthorized by replay of an old approval. Reauthorization
of a prior Ready epoch requires a separately designed audited transition;
the production drain verifier is still not implemented.

Validation on the isolated .NET 11 candidate after these changes:
162/162 server Release tests and 620/620 client Release tests pass.
A promoted tool handle now also preserves the first authenticated
`OperationId` alongside its job ID/start time. An exact replay is
idempotent, while conflicting operation identities or projects cannot
overwrite provenance. `NotifyAgent` accepts only its known logical
wire stages (source `TaskRead`, destination `TaskWrite`) at the
original actor/device/correlation, with distinct parent-child witnesses.
Neither stage's pending job authorizes correction or creates the
continuation before the parent has actual terminal evidence.

The candidate remains unreviewed; authenticated terminal ToolOperation
reconciliation, detached process-tree proof, cross-device legacy drain,
and review/performance gates remain open.

### Authenticated deferred ToolOperation termination reconciliation (2026-11-13)

GetJobResult can now certify an original pending *non-process* execution
only when the server recorded that precise poll before SignalR dispatch.
The durable agent_tool_job_poll table binds the original unresolved
witness to the authenticated user, device ID, connection/generation,
initiating agent, project, job ID and exact poll correlation. The original
pending handle separately retains immutable job ID, start time and
operation ID. The typed JobResult and original ToolResponse must agree.

Only ToolCompleted with a successful non-pending original response, or
ToolFailed with a non-pending original error, may advance the original
witness from Dispatched to Terminal. Running is an ordinary nonterminal
poll. A mismatched device/connection, actor, project, job/operation ID,
malformed result, cancellation, timeout or persistence error cannot
establish completion. An authenticated late retry can recover after
SQLite failure while the client receives its original result unchanged.
A completed source-side NotifyAgent TaskRead can now support its separate
destination continuation; the pending source handle cannot.

Commands involving process execution, scheduling or Git hooks remain
ineligible for this typed terminal promotion until OS-backed descendant
termination evidence exists. Client-only read-only polls still work
without the witness store, but produce no correction evidence.

Isolated .NET 11 verification: 162/162 server Release tests; unchanged
client source previously passed 620/620. Coverage includes actual
ClientService to HubService dispatch, wrong identities, Running and
process jobs, cross-project source continuation, restart/replay, and
injected SQLite terminal-update failure with late exact recovery.
The production legacy drain, process-tree proof, independent frozen
review and performance acceptance gates remain open. No shared
integration or deployment has occurred.

### Local 100-operation witness microbenchmark (2026-11-13)

The explicit .NET 11 Release SQLite test ran 100 admission,
authenticated dispatch-route and ordinary terminal receipt cycles
in 52.0 ms total (0.52 ms/call mean; 0.45 ms p95 per operation).
The fixture verifies zero unresolved witnesses at completion.
This is a local SQLite microbenchmark, not routed MCP/SignalR latency.
The separate 100-call simulated server-routing measurement follows;
deployed end-to-end latency remains a review/acceptance requirement.

### Routed 100-call MCP/SignalR witness benchmark (2026-11-13)

A separate explicit .NET 11 Release test, `BenchmarkHundredRoutedMcpWitnessCalls`,
drives 100 attributed `AgentInbox` calls through `JarvisMcpTools`,
`ClientService` routing, a simulated SignalR client, and authenticated
`HubService.SendClientResponse` into the durable SQLite witness store.
All 100 routes completed successfully with zero outstanding execution witnesses.
One run measured **314.5 ms total**, **3.15 ms/call mean** and
**1.30 ms p95/call**; startup outliers explain why the mean exceeds p95.

This measures the in-process server MCP/SignalR routing and SQLite path,
not a real network connection, client Git ledger operation or deployed
end-to-end latency. It does not waive the legacy drain and process-tree
terminal attestation requirements. An independent frozen review is still
required before any integration.

### Shared legacy-drain approval integrity gate (2026-11-14)

The server now uses one verified execution-epoch predicate for both read-only
readiness inspection and the SQLite-writer-serialized operator correction
permit transaction. Merely finding `state=Ready` with the current protocol
version is insufficient: the stored operator, original approval operation ID,
substantive drain evidence and parseable approval timestamp must all be present.
A missing, malformed or partial approval therefore vetoes correction, including
after a server restart. No readiness state is inferred or repaired.

An exact approval replay is idempotent only with the same operator, operation
ID and evidence. Reusing an approval operation ID with different evidence or
a different operator fails without mutation. Negative-space tests corrupt each
persisted field, verify that both inspection and exclusive correction refuse
the state, reject mismatched replays, restore the complete evidence and verify
that valid correction admission still succeeds.

This strengthens the fail-closed barrier but does **not** implement authenticated
legacy-client/process/job quiescence attestation. The production epoch remains
`AwaitingLegacyDrain` until independently witnessed and approved; no operator
shortcut, integration or deployment is authorized by this change.

### Why a typed stale error is not enough to release an orphaned permit (2026-11-14)

An attempted shortcut was investigated and deliberately **not retained**:
releasing a newly acquired correction permit whenever the authenticated
client reports `LedgerFailure(StaleEntityVersion)`. Although the correction
planner can reject stale task/attempt/agent versions before constructing any
Git change, `LedgerStore.transact` also retries after a push attempt when
the subsequent verified snapshot does not contain the operation. A later
planner rejection does not certify that *no push was attempted earlier in the
same transaction*, nor rule out a delayed remote outcome.

Consequently the existing release rule remains conservative: proven
non-dispatch or a verified terminal successful canonical result can release
the permit; an error code by itself cannot. A correct recovery design needs
a per-operation, immutable, authenticated **never-pushed** attestation
covering all internal retries, or exact canonical receipt reconciliation
plus independent termination proof. A bare `StaleEntityVersion`,
`GitFailure`, timeout or absence at one observed ledger head is not that
proof. The `TryAcquireAgentCorrection` API and existing fail-closed
recovery semantics remain unchanged.

### Read-only correction permit provenance for operator recovery (2026-11-14)

A bounded `FeedbackStore.InspectAgentCorrectionPermit(userId, targetAgentId)`
query now exposes the exact persisted permit ID, original operation ID,
normalized target agent and creation timestamp to a caller that already
supplies the authenticated user ID. It changes no execution or ledger state,
retains user isolation, works across server restarts, and rejects malformed
persisted identity/timestamp values instead of silently treating them as
absence. Negative tests also confirm that inspecting a held permit cannot
release its target-agent admission fence.

This is an internal **read-only recovery building block**, not an exposed
operator mutation, and does not by itself justify releasing or superseding
an orphaned permit. An operator must still reconcile the exact canonical Git
operation status and independently prove any outstanding execution has ended.
If the Git result or client process remains uncertain, the permit stays held.

### Exact correction-command provenance (2026-11-14)

A stable correction UUID alone is **not** sufficient to replay a held
administrative permit: the same UUID could otherwise be reused in a different
project or with another requesting agent, target task, CAS version or reason.
`McpToolHelpers.send` now hashes the complete server-constructed
`TaskWriteCommand` and supplies that fingerprint, the project selector,
initiating agent and its freshly admitted SQLite request witness to
`TryAcquireAgentCorrectionForRequest`. The permit persists this provenance
atomically with target-agent exclusion; it is retained across server restarts.

Replays are allowed only for exactly matching immutable command provenance,
and only through a fresh authenticated, same-user/source/project
`TaskWrite` admission. The original witness identity must itself still
exist and have matching user, source and project. A mismatch, missing/corrupt
original witness, reused operation ID with altered inputs, or a cross-user
witness fails closed without releasing or overwriting the existing permit.
SQLite migrations add nullable columns but **never backfill evidence** for a
pre-upgrade permit: unbound historical permits cannot be borrowed by newly
bound retries.

The service also retains a legacy internal test entrypoint, which cannot
borrow a bound production permit. It is not exposed as an MCP mutation.
Persisted permit inspection now includes the original command fingerprint,
initiator, project and request ID for future audited reconciliation.

**This does not prove two different devices' same-named project selectors
resolve to the same canonical Git ledger.** A follow-on device/generation
pinning or canonical ledger-identity proof is required before cross-device
replays may be considered safe; orphaned/uncertain permits remain blocked
rather than force-released.

### Device and connection-generation correction routing (2026-11-14)

Request-bound correction permits now also persist the selected authenticated
Jarvis device ID and its registration generation. A new correction (and each
exact replay) writes the same expected device/generation to its initiating
`agent_execution_witness` inside the SQLite writer transaction acquiring
or resuming the permit. The routing layer explicitly addresses the pinned
device; `TryMarkAgentDispatched` checks that the actual SignalR-selected
device and generation match the persisted expectation before dispatch.
Reusing the same request witness with an identical pin is idempotent; attempting
to change its device/generation fails.

The read-only permit inspection exposes this route provenance. Migrations add
nullable device fields without inventing missing prior device evidence.
Cross-device/cross-generation permit replay is rejected, including reconnects
and concurrent selected-device changes. New negative tests verify a mismatched
device/generation cannot be marked Dispatched even when the command UUID,
target, project name, source label and fingerprint are identical.

A device's canonical ledger trust binding may still change while its SignalR
registration remains active. This safety barrier therefore does not replace
exact canonical ledger identity verification, the legacy-client/process
quiescence barrier or audited orphaned-permit reconciliation. All uncertainty
continues to hold the correction exclusion; no deployment is authorized.

### Crash-stable SignalR registration generations (2026-11-14)

The 3.65 correction permit now pins an initiating request to its device ID
**and registration generation**. Audit found that `UserService` originally
allocated that generation with a process-local counter resetting to `1`
after a server restart. Consequently, a replacement registration after a
restart could collide with a persisted pre-restart permit even when the
device ID and full correction command fingerprint matched. Persisting the
permit generation alone did **not** close that hole.

Production `Program.configureServices` now explicitly injects a
`FeedbackStore.AllocateDeviceRegistrationGeneration` callback into
`UserService`. SQLite atomically increments and returns a **server-wide,
never-pruned** integer high-water mark in a dedicated
`device_registration_sequence` table. It is independent of the pruned
`connection_events` table and in-memory registration lifetime, and uses a
SQLite writer transaction so separate store instances serialize their
allocations. A missing/corrupt/exhausted allocator refuses registration;
it cannot silently fall back to process-local generation. The service
checks each result against the largest generation already registered
within its own lifetime, rejecting invalid or duplicate values.

For a first install on a pre-allocator DB, initialization seeds the
persistent high-water above all **persisted** witness, permit and
connection-event generation values. This migration does not declare
legacy activity quiescent or mint missing old execution evidence.
`UserService.Register` allocates before changing live routing ownership,
so an allocator failure cannot partially replace a registered session.
The parameterless and clock-test `UserService` constructors retain a
process-local counter for isolated tests only; production uses the
explicit durable dependency.

Negative tests recreate services against the same SQLite database,
remove connection telemetry, allocate concurrently from independent
`FeedbackStore` instances, migrate a pre-counter database with an
unresolved witness and permit, corrupt/remove the live allocator,
and provide a deliberately duplicate-generation callback. They verify
strictly distinct registrations, unchanged held permit and prior live
route, and fail-closed behavior when storage cannot certify a new
generation.

**Remaining trust boundary:** this counter prevents ordinary same-database
server restarts from recycling generations. It does not attest that a
historical pre-witness device stopped executing, prove a detached process
tree terminated, or protect against restoration of an arbitrary stale
SQLite backup while an older server/client process continues running.
Those cases still require authenticated global legacy drain evidence and
the separate correction exclusion; keep `AwaitingLegacyDrain` in effect.

### Authenticated read-only correction recovery snapshot (2026-11-14)

The server now exposes `GetAgentCorrectionEvidence(targetAgentId, limit)`
under `workspace:read` as a server-local diagnostic MCP method. It reads
the authenticated user's **durable** unresolved permit (including its
original operation ID, full command fingerprint, source agent and pinned
device/generation), a bounded page of still-unresolved agent execution
witnesses with `HasMore`, and the schema-verified global protocol epoch.

The tool does **not** invoke a client command, activate any agent, acquire
or release a correction permit, approve the legacy drain, attest that
children have exited, or issue any Git correction. It deliberately returns
`readOnlyNonAtomic=true` and `authorizesCorrection=false`. The queries
may race activity admitted immediately after inspection, so even an empty
page with a verified epoch is not a correction authorization. A corrupt
or unavailable SQLite/epoch read returns an error, never a guessed success.

Coverage includes user isolation (the same agent label under another
account cannot reveal another user's witness/permit), missing OAuth
scope/identity, malformed or excessive target/page inputs, a protocol
epoch still in `AwaitingLegacyDrain`, inspection with a durable held
permit, and unchanged execution gates after the report. This is an
operator recovery *observation* prerequisite; canonical Git receipt
reconciliation and audited orphan-permit release remain outstanding.

### Append-only device-registration roster (2026-11-14)

Registration history in `connection_events` is deliberately pruned as
telemetry and is therefore not a trustworthy long-lived legacy-device
inventory. A fresh authenticated 3.63/3.64 client can also register even
though the 3.65 server refuses to route new witnessed agent operations
through its older protocol.

The server now records each authenticated SignalR registration into
`device_registration_roster`, including the exact user ID, device ID,
connection ID, negotiated client-provided protocol/client versions,
strictly monotonic registration generation and timestamp. **The roster
insert and generation increment share a single SQLite transaction.**
Failure, validation rejection or injected SQLite insert error cannot
leave an allocated generation without a durable registration or expose
the unrecorded client route; the `UserService.Register` call still
performs this allocation before mutating any prior live session.
Roster records survive disconnected-session eviction, connection-event
telemetry pruning and server restart, and concurrent registrations
across separate store instances serialize correctly.

`ListDurableDeviceRegistrations(userId,limit)` provides a bounded,
user-scoped, newest-first diagnostic page and explicit `HasMore`;
`GetAgentCorrectionEvidence` now includes this evidence for the
authenticated user and **`rosterCanOmitPreUpgradeDevices=true`**.
Absence from the roster is *never* proof of device quiescence because
clients active before this roster was first introduced may not appear;
nor does registration imply the client process, agent or descendant
jobs have terminated. No automatic legacy epoch transition or
correction permit release is implemented. Independent, authenticated
fleet-wide pre-roster drain and OS process-tree evidence remain required.

### Registration-generation cutover protects legacy approval (2026-11-14)

A successful legacy-drain approval previously recorded an operator, timestamp,
evidence, operation UUID and protocol, but did not bind that approval to a
device-registration frontier. An old 3.63/3.64 (or otherwise incompatible)
client could register *after* an approved drain while the same Ready marker
would still authorize administrative corrections.

The epoch record now durably stores
`approved_registration_generation`, read from the monotonic SQLite
`device_registration_sequence` **inside the same writer transaction**
that verifies the operator's drain proof and checks unresolved executions.
`ReadVerifiedExecutionEpoch` uses that cutover as its compatibility fence:
if ANY user has an append-only roster entry with a later generation and
a non-current `AgentProtocol.version`, it refuses Ready. The same predicate
is used for operator inspection, correction admission and exact approval
replay; all are fail-closed across server restart. A later current-version
registration cannot erase that incompatible historical evidence.

The predicate also verifies the current durable allocator exists, is
integer-valued and has a high-water at least as large as the approved
generation. A missing, negative, malformed or future-valued cutover,
or a rolled-back/deleted allocator, invalidates the Ready marker. A
pre-upgrade Ready row gains a **NULL** cutover through migration and
cannot borrow new validation evidence; existing approvals are never
silently backfilled. Matching modern registrations after approval are
allowed, as they use the 3.65 witnessed command protocol.

This is a safety veto, **not** a fleet-wide drain verifier. Existing old
registrations *before* the approval are still the independent authenticated
verifier's responsibility, including any pre-roster devices, old server
instances and detached process descendants. The production callback is
still not wired/exposed, so the live epoch remains
`AwaitingLegacyDrain`. A postapproval legacy registration requires a new
independently audited drain epoch; there is intentionally no automatic
reset or timeout-based recovery.

### Device-local immutable correction ledger identity (2026-11-14)

The server's correction permit pins the full TaskWrite command fingerprint,
the exact authenticated user/source/project and device registration
generation, but that alone does not pin how a device resolves its
`ProjectName` string to a **physical checkout**. The existing crash-safe
`PendingIntentJournal` is keyed by each checkout root. If an unresolved
correction is retried after the same device remaps its project selector to
a *different* checkout root, the new root's local journal may contain no
prior operation UUID, and could execute the same command against another
ledger identity.

A separate root-independent, permanent client-side
`correction-operation-pins/<uuid>.json` record now anchors the first
execution of `CorrectAgentWorkingState` to the selected full checkout
root, canonical project ID, ledger ID and epoch, approved remote binding
hash (including both approved fetch/push URLs), project selector and
serialized **full command SHA-256**. It lives under the client's existing
OS-account Jarvis configuration, not the physical checkout. Remote URLs
may contain sensitive information; the pin retains their hash rather than
plaintext. Its creation is guarded by a cross-process per-operation
exclusive file lock, a flushed write-through temporary file, atomic
no-overwrite move and durable parent-directory sync on supported platforms.
All of this happens before the checkout-local pending-intent journal and
**before any Git mutation**. Exact retries against the original identity
are allowed; any changed root, ledger, remote, project or command is
rejected, without calling the original ledger writer. A corrupt or
oversized pin is an error, not an implicit invitation to create a new one.
The pin is not deleted on command success because successful commands
also must not be replayed against a new canonical identity with the same
UUID.

This closes the same-client, same-SignalR-generation **cross-root retry**
hole. It does **not** attest that the device's *first* project selector
was the canonical ledger the operator intended, cannot retroactively
attest a pre-pin operation, and does not protect against deletion of
the entire local configuration or restoring a stale backup. A server-held
permit and exact canonical Git receipt reconciliation remain necessary.
No reviewed production correction is enabled: legacy drain, OS-backed
child-tree termination proof, and independent frozen review are still open.

### Permanent correction UUID ownership after permit release (2026-11-14)

A correction permit is intentionally deleted after independently attested
successful canonical result or proven non-dispatch. That release must not
also release the **operation UUID's immutable identity**. Otherwise a
later request by the same authenticated user could reuse the successful
UUID on another target agent or project, or from a different device
generation, once the first target's exclusion was no longer present.

`agent_correction_operation_identity` is now an append-only,
user-scoped SQLite record keyed by `(user_id, operation_id)`, retaining
the original target, project, requesting actor, complete TaskWrite SHA-256,
pinned device ID/generation and first admitted request witness. Its first
insert is atomic with acquisition of the target-agent correction permit
and pinning of the original admitted request's expected route, in the
same SQLite writer transaction as all other agent admissions. A failure
to persist the immutable UUID ownership also rolls back the exclusive
permit and route pin; no client dispatch is authorized.

Every subsequent correction admission checks this record, **even after
the target's previous permit was released**. An exact retry on the
same target, project, actor, command and device generation may proceed
through normal exclusion/liveness gates with a fresh initiating witness.
Changed target, project, actor, command payload or device/generation is
rejected. An unbound legacy internal correction entrypoint cannot borrow
a bound production UUID. A pre-upgrade held bound permit with no matching
permanent UUID history remains unresolved and cannot be silently
backfilled. Other authenticated users have independent UUID namespaces.
The operation identity is not subject to 30-day telemetry pruning and
survives server restart.

This does not independently prove the Git operation outcome or original
execution quiescence. A durable identity record is a permanent
**non-reuse fence**, not permission to release a permit. Uncertain Git
outcomes remain blocked and must still be reconciled against exact
canonical operation receipts. Production correction remains disabled
behind the unverified legacy-drain epoch and OS-backed child-process
proof gates.

For **bound production corrections**, the server also validates a
nonempty GUID and normalizes its SQLite identity key to the canonical
lowercase `D` representation before looking up or inserting the
permanent claim. The Git ledger indexes operations by parsed GUID, so
different textual spellings such as braced uppercase and unbraced
lowercase must never create independent operation identities. The full
TaskWrite fingerprint remains tied to the original exact command
serialization. Internal pre-existing unbound migration fixtures retain
their historical textual IDs but cannot claim or borrow a bound
production UUID. Alternate-case/braced retargeting and non-GUID
production inputs are explicitly rejected in regression tests.

### Client completion is not native process-tree quiescence (protocol 3.66, 2026-11-14)

The 3.65 terminal classifier used a negative `ProcessExecution` permission
check. That is insufficient: many commands classified as read-only, including
`TaskRead`, `LedgerRead`, `ReviewRead`, and `GitStatus`, execute Git
through `BoundedProcess.run`. Even if the awaited Git parent exits, Git
helpers, credential programs, hooks, filters, or detached descendants can
continue executing. A single client handler reply therefore cannot prove
OS descendant termination and must not authorize a Working-to-Ready
administrative correction.

Protocol 3.66 introduces an explicit small allowlist of truly synchronous,
nonprocess commands. For **all other known** command kinds, an exact
authenticated SignalR response is stored as `client_response_at` with a
first-outcome `client_response_succeeded` bit, while the witness remains
`Dispatched`. A typed `ToolCompleted` / `ToolFailed` poll on the exact
original deferred job records the same distinction. An unknown command,
missing historical tool classification, uncorrelated response, timeout,
client reconnect, or absent response still cannot obtain terminal proof.
The response observation is *not* a process-tree certificate, regardless
of how long its handler has already been finished.

`StartJob` continues to retain its exact job ID, start timestamp and
client-return evidence without treating the handle or parent exit as full
process-tree termination. The client-side `CancelJob` now records a
cancellation *request* without marking the job `Canceled` or setting
`CompletedAt` until its parent process Exited event is observed. Neither
that event nor `Kill(entireProcessTree = true)` currently certifies the
descendant tree is completely reaped, so server correction witnesses remain
unresolved until independent OS-backed reaping proof is implemented.

An authenticated, **successful** `NotifyAgent` source RPC may still
continue to its distinct destination RPC without a quiescence certificate,
since this is a logical continuation of the same operator-requested
notification. The source witness is now classified by its actual
`NotifyAgent` semantic operation, not merely the outer `TaskWrite`
protocol envelope. The continuation validator requires the root
`NotifyAgent` identity and a durable first successful source reply
(`client_response_succeeded=1`); an error, absent result, duplicate
conflicting reply or pre-migration row cannot satisfy it. Both source and
destination Git-backed witnesses remain unresolved for administrative
correction even after both replies are delivered.

Existing 3.65 `Terminal` witnesses of Git-backed, process-capable or
unknown commands are **monotonically demoted** on migration to
`Dispatched`, retaining any original terminal timestamp only as
`client_response_at` evidence. Their success bit is not backfilled:
a historical response timestamp does not establish whether the original
validation succeeded. This migration is restart-idempotent and intentionally
fails closed for legacy execution. The protocol bump also invalidates any
earlier-version legacy drain approval; no transition to Ready is made.
The authenticated read-only correction evidence now distinguishes
`clientResponseAt`, `clientResponseSucceeded` and
`requiresIndependentProcessProof`, with no mutation or permit release.

The remaining production gates are unchanged: attest every 3.63–3.65
pre-witness client and obsolete server, prove OS-backed descendant reaping
across every participating device/process incarnation, reconcile the exact
canonical Git correction UUID outcome and review a frozen tested source.
No production operator approval tool or automatic cleanup is exposed.

Normal protocol-3.66 Git-backed client replies and successful `StartJob`
handle receipts deliberately return an unresolved process-liveness
classification after storing the authenticated reply. `HubService`
logs these **expected** nonterminal observations at Debug rather than
Warning, including exact deferred ToolOperation completion. Corrupt,
unrecognized, conflicting, unauthenticated, or missing receipts remain
warnings. Ordinary Git reads therefore do not flood the operator's
diagnostic logs merely because their process-tree proof is still pending.

### StartJob process-group ownership before OS receipt support (2026-11-14)

Before this revision the long-running client `StartJob` runner executed directly
inside the client's inherited OS process group and remembered only the leader's
`Process` handle. On Linux and macOS, `CancelJob` could lose the entire
descendant tree after the leader exited; `Process.Kill(entireProcessTree=true)`
enumerates only descendants reachable from the still-living leader. On
Windows, no kill-on-close Job Object enclosed the explicit StartJob tree.

`StartJob` now reuses `BoundedProcess.groupProcessInvocation` rather than
starting the original executable in the caller's process group. Linux uses
the existing `setsid` session wrapper; macOS the managed `setsid` host; and
Windows the managed group host gated on assignment to a kill-on-close Job
Object. The client retains the Windows Job handle until the leader exits.
Both `CancelJob` and the leader's `Exited` callback attempt group
termination, and group termination precedes `WaitForExit` to avoid hanging
when a descendant keeps inherited output streams open. This applies to
normal leader completion as well as cancellation; no child is intentionally
detached to continue after the job's leader completes.

The original executable is checked for existence relative to the configured
working directory and effective search `PATH` before launching the helper,
so a missing program still produces a synchronous launch error rather than
an apparently successful short-lived job handle. Command metadata retains
the original executable and arguments, not the hidden group wrapper.

Linux live-process regressions spawn a child that keeps running after its
parent exits and another child while the parent is alive and canceled. Both
verify cleanup and test the actual child PID. They do **not** establish that
the OS has reaped every group member, certify a specific historical request
after client restart, or prove cross-device quiescence. An interrupted
client may leave Linux/macOS descendants executing before the next verified
recovery cycle; a successful kill signal is itself not a reap receipt.
No server `Dispatched` execution witness is promoted to `Terminal` by
this change. Independent OS-backed per-incarnation termination certificates,
crash-resilient reconciliation, and the global pre-witness legacy drain
remain prerequisites for activating administrative correction.

### StartJob launch/exit ownership handshake (2026-11-14)

An additional concurrency audit found that OS group ownership was not yet
atomic with the job's startup. The `Process.Exited` handler could run as soon
as `Process.Start` succeeded, before a short-lived Windows helper had been
assigned to its kill-on-close Job Object and before its redirected stream
readers were initialized. Premature `releaseSupervisor()` could close the
Windows job handle during assignment or make the launcher return failure
after a real process had run. An independently racing `CancelJob` checked
`Running` before obtaining the launcher lock, allowing the Exited callback
to complete and release group ownership between the status check and a
later attempt to signal that historical process group.

All three paths now acquire **the same per-job launch/exit gate**. The
launcher holds the gate across native process start, Windows Job Object
assignment/readiness notification, stdout/stderr reader initialization, and
transfer of resource ownership to Exited. The Exited callback waits for
that transfer and ignores a failed launch whose cleanup retained ownership.
`CancelJob` checks `Running`, records the cancellation request and
initiates group termination under the same gate; `SyncRoot` continues
to own output/status. A canceled job still becomes terminal only when
its parent exit is observed, not when Kill returns.

Focused regressions exercise 24 concurrent very short-lived `StartJob`
processes and 12 concurrent short-lived parent exits racing two
cancellation requests per job, in addition to the explicit live descendant
and cancellation tests. This closes an in-process handoff race only.
Neither the lock nor the job's terminal status attests crash-stable
descendant reaping, detached process escape, an obsolete client incarnation,
or fleet-wide legacy quiescence. The corrective operator endpoint and
legacy execution epoch remain disabled pending independent proof.

A one-shot `Exited` notification raised before launch handoff cannot be lost: after transferring native ownership, the launcher checks `HasExited` and invokes the same idempotent completion routine for an already-dead leader. The `CompletedAt` timestamp continues to mean **observed parent exit only**, never verified process-tree quiescence.

If cancellation arrives after the leader has exited but before its callback recorded completion, the caller observes `HasExited` while holding `LaunchGate`, does not set `CancellationRequested`, and does not send a signal to the historical process-group ID. The normal exit finalizer remains responsible for group cleanup and preserving a completed exit code; this avoids a late cancel signaling an OS-reused group identity.

This `HasExited` check is only a best-effort stale-signal avoidance, **not** a PID-reuse-safe kill primitive: a leader can exit between observation and `killpg`. Proving correct group identity and complete termination across that race requires an OS-backed lifetime handle (for example a scoped cgroup/job supervisor) and durable birth/boot identity. The server's unresolved process witnesses continue to veto correction regardless of these client statuses.

Lock ordering is deliberately `LaunchGate` before `SyncRoot` across both cancellation and `Exited`. `Process.HasExited` can itself raise `Exited`, which may call `WaitForExit` and drain output-event callbacks requiring `SyncRoot`. A draft that queried `HasExited` while holding `SyncRoot` stalled the focused test host; the final version queries it under `LaunchGate` **before** taking `SyncRoot`. The 24-test focused .NET 11 Release suite then completed successfully. Windows Job Object lifetime behavior still requires execution on Windows for platform-specific assurance.

### Device-local append-only StartJob ownership evidence (2026-11-14)

The previous process-group work improved cleanup while the client was alive, but
`JobManager` still retained the OS process identity only in its in-memory
registry. A client restart could lose the exact PID and birth identity of a
native process whose execution had been admitted by the server.

`ProcessOwnershipJournal` now writes two **immutable, GUID-N keyed**
device-local records under the user's Jarvis configuration directory
(`process-ownership-v1`), independent of project checkout or worktree:

- `<jobId>.intent.json` is created and flushed **before** calling
  `Process.Start`. It pins the canonical logical project ID, existing
  persisted device ID, new client-process incarnation UUID, job UUID, and
  creation timestamp. A crash between this stage and an actual spawn leaves
  an explicit unresolved intent, not an implied absence of work.
- `<jobId>.launched.json` is created only after `Process.Start`, pinning the
  original intent SHA-256, OS parent PID, UTC start-time observation, and on
  Linux the kernel boot UUID and `/proc/<pid>/stat` start tick when
  obtainable. The launch stage is written before `StartJob` returns its
  handle; on Windows, the helper is already assigned to its kill-on-close
  Job Object but remains behind its readiness event until this stage is
  written. Failure to persist the launch stage causes the started native
  process to be terminated best-effort and the API call to fail. The original
  unresolved intent is **not** erased by that error.

Both stages use create-new files with write-through, flushed file data and
POSIX directory fsync; parent directory creation is synced before proceeding.
The journal directory is made private to the local OS user on Unix. Neither
stage is ever silently overwritten, deleted, or reclassified by cancellation,
process exit, job status, or telemetry pruning. The journal deliberately
excludes raw executable arguments, environment variables and process output.
The read-only `inspectAt` rejects missing, corrupt, oversized, symlinked or
SHA-mismatched evidence; an intent with no launch record remains explicitly
uncertain. The conservative records may require eventual operator-audited
retention management, not automatic TTL deletion.

**These records are forensic anchors, not reaping receipts.** PID and kernel
boot/birth identity help distinguish process incarnations, but they do not
prove that the PID still belongs to a given execution after races, that
descendants stayed in the process group, or that every descendant stopped
after the client crashed. A short-lived process may exit before all optional
birth metadata can be captured; empty metadata stays unknown rather than
being synthesized. Windows power-loss directory durability and native Job
Object lifetime require separate platform validation. Pre-upgrade clients
have no retrospective journal entries. Nothing in this change turns
server-side `Dispatched` into `Terminal`, releases a correction permit,
approves the protocol-3.66 legacy drain, or exposes an operator force-Ready
endpoint. Future authorization will still need authenticated provenance,
independent OS-backed process-tree quiescence, full legacy fleet drain, and
canonical Git outcome reconciliation.

The targeted .NET 11 Release JobManager suite passed **26/26**, including
real `StartJob` journal binding, durable launch reinspection, immutable UUID
stage reuse rejection, incomplete crash-intent inspection, corruption and
symlink rejection, and existing process lifecycle/concurrency cases. A
previous development build exposed an F# JSON record-constructor limitation
and a launch-error `Process.Dispose`/exit-callback lock inversion; both were
corrected before acceptance. The eventual independent frozen review must
include the error paths and both native-platform implementations.

**Final full Release verification (2026-11-14):** Server 182/182 and Client 630/630 passed using .NET 11. The resulting branch remains isolated, unreviewed and undeployed; none of the outstanding production correction gates were relaxed.

### Read-only device-local launch recovery command (protocol 3.67, 2026-11-14)

A verified `StartJob` response already pins the exact job GUID-N and launch
timestamp under the server's authenticated `agent_execution_witness`
connection/device-generation route. After a client restart, the in-memory
`GetJobResult` registry may no longer exist, even though
`process-ownership-v1/<jobId>.intent.json` and its optional immutable
`launched` stage remain on disk. The server **must not infer missing process
work from that in-memory lookup**.

New `GetProcessOwnershipEvidence` is a standalone, **read-only** protocol
command and public MCP tool, not a correction endpoint. It accepts only an
explicit logical `projectName` and exact native `jobId`; normal authenticated
device selection (including explicit MCP `deviceId` override) determines
which connected client answers. Client routing parses the canonical project
selector and binds the expected logical ID before reading its local journal.
The client compares that ID with the persisted intent, and independently
checks the intent's device ID against the current client's persisted device
identity. A different project, different device, bad GUID, corrupt journal or
missing intent fails closed instead of returning fabricated negative evidence.

A successful, bounded response reports the original job UUID, canonical
project ID, device and client-incarnation IDs, intent creation time, and one
of exactly **`IntentOnly`** or **`Launched`**. For `Launched`, it includes the
captured parent PID, available OS start/birth and boot observations, the
intent SHA-256 binding and launch-record timestamp. Missing optional OS
observations remain null, not manufactured. Every successful result has
`ProcessTreeStatus = "Unverified"`; the command never probes, kills, waits
for or reaps an OS process. It is non-activating and bypasses source instruction
loading as a cheap job-control observer.

This diagnostic tool uses OAuth `workspace:read`, the Client `ReadOnly`
permission, and the explicit process-free server terminal allowlist.
**Terminal for this read-only diagnostic is not Terminal for the historical
`StartJob` execution witness.** The server does not consume this response to
advance an old witness, authorize `Working -> Ready`, waive a correction
permit, or accept an operator legacy-drain attestation. The authenticated
device route makes the selected device auditable; a same-user local
process that can edit its own journal remains outside any independently
trusted OS quiescence proof. The server must compare request UUID, owner
device and recorded launch GUID against its durable `StartJob` witness
before using the answer as even diagnostic provenance.

Adding a typed wire command advances the protocol to **3.67**. Protocol
3.66, just like 3.65 and earlier, is now a legacy incarnation for this branch:
no approvals or empty registries from prior protocol epochs may authorize a
3.67 correction. The global pre-witness fleet drain, crash-stable
OS process-tree proof (including detached children and PID reuse), exact
canonical Git outcome reconciliation, independent frozen review and operator
approval remain mandatory. This branch remains isolated and undeployed.

The device comparison uses `DeviceIdentity.tryReadExisting`, **not**
`getOrCreate`: a read-only forensic lookup must neither create a missing
persistent device ID nor silently repair/replace a corrupt one. A missing,
invalid, symlinked or mismatched identity fails closed as unverified.
Tests confirm invalid identity bytes are left unchanged, no directory is
created for a missing ID, and symlinked IDs are rejected.

The server regression also submits a deliberately **forged** diagnostic
response with `ProcessTreeStatus = "Terminated"`. The diagnostic may finish
its own process-free read witness, but the server must keep the original
`StartJob` witness `Dispatched`, retain the correction exclusion, and
refuse Working-to-Ready until independent OS-backed verification. This
negative-space test protects the distinction even if a future client
regression or malicious source fabricates a positive-looking result.

**Protocol 3.67 Release verification (2026-11-14):** Server 182/182, focused Client JobManager + ClientConfig 35/35, and full Client 632/632 passed with .NET 11. The immutable launch read remains diagnostic only, and the administrative correction epoch and endpoint remain disabled. No merge, push or deployment.


### Isolated current-main reconciliation (2026-11-14)

The separate `worker3-p1-main-integration` worktree combines current main
`b9b9e7a4` with the isolated protocol-3.67 P1 candidate `f3aed0ba`;
neither source branch nor the main checkout was modified. Eight textual
merge conflicts were reconciled against the newer taskless execution,
Challenges, logging, cross-project notification and source-validation code.
The six-argument ClientService constructor preserves mainline MCP timing
telemetry and the P1 SQLite execution-witness dependency. The durable
before-dispatch device/generation/correlation binding precedes SignalR.
Cross-project notifications retain mainline canonical-source validation and
merged source effects while creating a separate durably admitted child
witness. Existing generic challenge lifecycle events initialize the P1
administrative-provenance fields to historical-compatible empty defaults.
Newer SignalR invalidation mock contracts and service constructor fixtures
were updated to match the combined code.

The merged .NET 11 Release Server suite passed **186/186**, and the staged
merge has no conflict markers or whitespace errors. The first complete
Client Release run passed 710/711 and exposed a taskless Working self-inbox
regression: explicit unassigned Working was incorrectly sent through an
assignment-only admission fence. The canonical taskless-state recognition
fix passed the exact focused regression **1/1**, and the subsequent complete
.NET 11 Release Client run passed **711/711** (zero failures or skips).
This is a compatibility
checkpoint, NOT an authorization to merge, deploy, approve the legacy
epoch, clear a Git-backed process witness, release a correction permit, or
execute an administrative Working-to-Ready correction. All independent
OS-backed descendant-reaping proof, pre-upgrade fleet drain and exact
canonical Git outcome-reconciliation requirements remain open.
