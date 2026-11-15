# P1 stateless Jarvis server: SQLite dependency audit and failure contract

Source branch: `fix/p1-stateless-server-worker12`. Task: `p1-restore-stateless-jarvis-server`, revised canonical spec at seq3759. These are proposed isolated code changes **for review only**; nothing here authorizes production rollout, Scheduler/YieldSource activation, data deletion, or migration.

## Owner and durability boundary

- **Client and approved Git-backed orphan ledgers:** canonical project/task/agent/assignment/review/integration records, operations with idempotency and Git CAS, execution/source effects, schedule claim recovery. Server restart must not change any canonical source head or claim decision. Client-side source-effect SQLite (WQ03) is a *different client-owned component*, not this server diagnostics database, and is not touched.
- **Server:** authenticated OAuth/MCP/SignalR forwarding, bounded transient response correlation, device connection registry and short-lived exports. The explicitly permitted durable server exception remains Jarvis-level agent instruction files. There are no new server-owned durable records.
- **Existing SQLite FeedbackStore:** `operations`, `feedback`, `connection_events`, and historical `attributed_agent_activity`. All are now **noncanonical diagnostics only**. `operations` and `connection_events` have 30-day retention, and feedback records are user-scoped optional reports. `attributed_agent_activity` schema and prior records are kept intact to avoid destructive migration; production does not read or write them for scheduling. Prior authorization is unknown: **do not delete/migrate the existing database**.

## Correctness hole and source changes

Previously `Server/Program.fs` required `DatabasePath` and eagerly initialized SQLite before accepting OAuth traffic; startup failed if the DB was inaccessible. `HubService` synchronous lifecycle event logging and `McpTools` operation logging could block during SQL lock/disk stalls. Critically, `ClientService.SendCommandToUser` persisted agent activity *before* dispatch and refused ordinary agent commands when this write failed. `GetAgentActivity` returned a missing row as `None`, and a due `AfterAgentIdle` schedule could interpret `Ok None` as sufficient to launch a Manager.

New behavior:

1. `OptionalFeedbackStore` has **lazy** SQLite startup; missing configuration is accepted; failures return `TelemetryUnavailable`. All diagnostic SQLite requests execute off request/SignalR threads in one single-flight background worker with a 200 ms caller budget. A hung worker never permits more workers to accumulate; later diagnostic requests are dropped immediately. A 1-second SQLite command default timeout complements the outer deadline. Periodic 30-day pruning shares the same gate.
2. OAuth initialization, MCP routing, client command dispatch, Git-CAS operations and SignalR connect/disconnect never synchronously initialize or wait for SQLite. Operation/connection diagnostics are best-effort, possibly lost or completed after timeout; diagnostic metadata is not a canonical idempotency receipt. Explicit Feedback and connection-diagnostics methods can return `TelemetryUnavailable`, without misleading success or impacting unrelated tools.
3. Cross-device `RecordAgentActivity` notifications are optional hints; loss or delay is permitted. Their failure cannot reject an authenticated client operation. No SQLite activity is consulted for launch safety.
4. **`AfterAgentIdle` is intentionally disabled pending independent, durable, authenticated cross-device activity proof.** Production client verification returns `ActivityProofUnavailable`; the server rejects `GetAgentActivity` even for older clients, rather than returning an old/reset SQLite value. The scheduler additionally treats `Ok None` as `ActivityProofUnavailable`, never as evidence that the agent has been idle. Verified non-`None` provider tests retain normal scheduling semantics, but no such provider is wired into production. `Once` and `Every` use their existing client-owned schedule store and are unchanged.
5. No server DB backup, recovery, migration, deletion, or persisted infrastructure requirement is introduced. Existing telemetry may disappear on replacement without changing client/Git-ledger correctness.

## Adversarial acceptance matrix

| Failure / scenario | Required outcome | Local evidence |
| --- | --- | --- |
| `DatabasePath` missing, database path is directory, corrupt SQLite file | OAuth metadata 200; unauthenticated MCP and SignalR challenge 401; server does not abort | `ServerMcpAuthenticationTests.server_authentication_and_routing_start_with_missing_corrupt_or_unavailable_sqlite` |
| SQLite initialization/file access error | Optional diagnostic error, no reinitialization on request thread, preexisting bytes unchanged | `OptionalFeedbackStoreTests.missingConfigurationAndUnusableDatabaseDoNotBlockInitializationOrRequests` |
| In-flight SQLite stall; two callers; service replacement | Caller deadline enforced, future diagnostics dropped while worker stuck, second independent client remains responsive, new instance works | `OptionalFeedbackStoreTests.lockedOrHungTelemetryIsSingleFlightBoundedAndDoesNotBlockTwoClientsOrRestart` |
| Real concurrent SQLite writer lock | A diagnostic write returns unavailable within the request deadline; the connection recovers after lock release | `OptionalFeedbackStoreTests.realSQLiteWriterLockIsOptionalAndCannotGateRequestThread` |
| Disk-full-style SQLite write error | Error not fabricated as success; noncanonical diagnostics recover independently | `OptionalFeedbackStoreTests.diagnosticWriteFailureIsNotReportedAsSuccessAndFutureOperationsCanRecover` (injected IO failure, not a real full filesystem) |
| Attributed client command with no persistent server activity watermark; two registered devices | Client operation dispatched; same-user hints only, no other-user leakage; reconnect/replacement can dispatch | `ServerServiceTests.attributed_tool_dispatch_survives_missing_sqlite_and_notifies_only_user_devices` |
| Two independent schedulers, local hinted activity, missing server activity, restart | Zero `AfterAgentIdle` launches; explicit proof error, including after stale deadline | `BrowserSchedulesTests.missingCrossDeviceActivityProofBlocksBothClientsAndRestartedIdleSchedules` |
| Missing active provider or stale/unknown outcome | Zero idle launch; original outcome reconciliation/owner-lease tests remain | Existing `BrowserSchedulesTests` negatives and corrected positive fixtures |

These are bounded **local .NET 11 Release** tests in disposable directories; no privileged host fault injection and no real production Git-CAS/remote effects. Loopback authentication tests prove auth boundaries, while in-process dispatch tests prove the server no longer depends on SQLite; they do not constitute end-to-end multi-machine production proof.

## Restrictions and follow-on proof

- Production `AfterAgentIdle` stays disabled until an **independently retained client/Git-owned activity stream** can prove both completeness and freshness across every registered device and server restart; verification must bind authenticated user, canonical project and agent identity, event high-water, observed wall clock and attempt/ownership. A stale local hint or merely elapsed timer must never suffice. Cross-ledger atomicity and late effects need review; a timestamp alone is insufficient proof of global idleness.
- Review must independently inspect the frozen SHA; no live Scheduler or source handoff activation, GitHub Actions, main push, deployed server, migration, or deletion under this task.
- The existing FeedbackStore's API retains legacy diagnostic activity methods solely for compatibility with old tests and database schema; no production callsite consumes them. Altering schema/destructive cleanup is a separate explicitly authorized task.
- Diagnostics are bounded in concurrency and time. Stored feedback retention is historically unbounded and might still need an operator-approved policy. Single-flight protects request paths, but an OS-level kernel I/O hang could occupy one background worker until process exit. This is an explicit operational limitation, not an idle scheduling permission.
