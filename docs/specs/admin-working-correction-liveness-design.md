# Administrative Working correction: server-mediated liveness design

**Status:** design investigation with an isolated server coordination prototype (`Server/Services/AgentExecutionGate.fs`); not wired into MCP dispatch or approved.
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
This is a local SQLite microbenchmark, not end-to-end MCP or
SignalR latency; a separate 100-call routed benchmark with p95
remains a review/acceptance requirement.
