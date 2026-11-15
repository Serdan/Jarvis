# Independent Reviewer 3 audit — P1 server failure isolation

**Canonical decision: ChangesRequested**

**Frozen task-bound source:** bca24efaee4e0284731e8a47a2d7d9b4453f56e5 (parent e837d2d67277af8eb4b634ff58d5ba9e1b59d19e). Review 8e074c1ba8514c5aa77d5b32f4929c01. Read-only inspection of the registered clean worker12-stateless-server worktree; no code/host/config changes.

## Independent evidence

- Verified local SDK 11.0.100 and exact HEAD plus parent; git diff --check returned exit 0.
- Ran full local .NET 11 Release Server.Tests on frozen source: 237 passed, 0 failed, 0 skipped. Includes actual loopback server startup/auth challenge for missing/corrupt/directory database, real writer-lock, single-flight timeout, simulated failed writes, client service dispatch tests.
- Ran local .NET 11 Release Client.Tests filtered BrowserSchedulesTests: 20 passed, 0 failed, 0 skipped. Includes absent cross-device activity proof across two independent schedule files and restarts, replacement/late verification, owner-lease and stale outcomes.
- Read actual patch and production callsites in Server/Program.fs, Server/McpTools.fs, Server/Services/{OptionalFeedbackStore,FeedbackStore,ClientService,HubService}.fs, Client/{Program,Modules/BrowserSchedules}.fs; searched all Server and Client references to FeedbackStore, GetAgentActivity and AfterAgentIdle.

## Verified and acceptable safety improvements

1. Server/Program.fs no longer requires DatabasePath, resolves no SQLite instance before readiness, and injects OptionalFeedbackStore lazily. Authentication and routing no longer depend on FeedbackStore constructor/schema migrations.
2. OptionalFeedbackStore.fs isolates SQLite on Task.Run with a 200 ms external caller deadline, atomic single-flight guard, no queue growth, one-second SQLite busy timeout, and sharing of that guard with scheduled pruning. The single stuck worker may outlive the request; its result is never represented as a successful canonical mutation. No production data deletion/migration.
3. HubService connection telemetry and MCP operation logs are best-effort; normal ClientService.SendCommandToUser no longer blocks or denies a genuine client command on a server activity-watermark write. Auth checks precede optional diagnostic operations. Feedback-only API methods return explicit unavailable errors.
4. Server HubService.GetAgentActivity rejects even registered older clients with ActivityProofUnavailable; client production verifier always returns explicit error, while BrowserSchedules.Service treats Ok None as missing proof. Therefore a reset SQLite or absent/missed cross-device sample does not authorize AfterAgentIdle. Once and Every paths are separate.
5. Canonical Git-CAS, task/review ledgers and client-owned effect journals were not modified by this patch. The tests are local/in-process; they do not prove live production server replacement, and Scheduler remains disabled.

## Required corrections before acceptance

### B1 — Disposable SQLite has no hard disk bound (resource acceptance failure)

Server/Services/FeedbackStore.fs:249-259 deletes old operations and connection_events after 30 days, but never prunes or caps the feedback table (INSERT in the SubmitFeedback path around lines 529-556). Neither those time-based deletions nor SQLite's retained freelist establish a maximum database-file size. OptionalFeedbackStore.fs caps concurrency and caller latency, but does not cap disk consumption, diagnostic write volume, or the amount of work done by an unbounded prune transaction. docs/stateless-server-p1-worker12.md itself acknowledges that feedback retention remains unbounded. On a tiny droplet, uncontrolled diagnostic disk growth can exhaust the host filesystem; simply catching the eventual SQLite error does not meet the explicit small/bounded-resource contract.

Required: introduce a finite, documented and enforceable **non-destructive** budget for future optional diagnostic writes and bounded cleanup work (for example file/page high-water with write-drop behavior, bounded batches, and observable unavailability). Preserve existing data; do not delete/migrate current production feedback. Test quota exhaustion and pre-existing oversized/corrupt databases while authenticated routing and unrelated client Git-CAS operations remain available. A 30-day age policy without a size ceiling is insufficient.

### B2 — Newly fire-and-forget activity hints lack a backlog budget

Server/Services/ClientService.fs:188-208 starts an unawaited Task.WhenAll of SignalR RecordAgentActivity sends per attributed command; the main SendCommandToUser path around lines 297-310 never awaits it. That properly removes a critical-path dependency, but does not bound outstanding hint sends when a registered client is persistently backpressured or hung. A stream of otherwise valid commands can create indefinitely many outstanding tasks and retained closures, contrary to the same bounded-memory requirement. The two-device success test uses immediately completed sends and does not cover this failure mode.

Required: coalesce/drop hints or bound per-user pending hint concurrency/age, with slow-recipient stress tests showing that ordinary MCP routing and source operations stay responsive and diagnostics do not accumulate unchecked. Hints must remain nonauthoritative for AfterAgentIdle.

## Not an objection / follow-up gates

- Permanently disabling production AfterAgentIdle is an allowed fail-closed solution for this task; independently durable cross-device activity proof remains a separate prerequisite before enabling it.
- Keeping the potentially grandfathered feedback SQLite and existing rows is correct; no operator authorization for a destructive cleanup is implied by this verdict.
- These findings do not authorize GitHub Actions, main push, live Scheduler/YieldSource, deployment, migration, host data deletion or Codex.

**Disposition:** ChangesRequested for bounded resource acceptance only. The central correctness isolation and idle safety changes are sound in this frozen patch; retain those protections while addressing B1 and B2. Freeze a new exact task-bound review of the corrected source.