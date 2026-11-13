# P2 — Client-owned tool-job exception discards uncertainty after possible mutation

**Frozen integrated source:** `5cda4a3e46acf17ef2ceb5ff4a0b993992f79f4e` on `integration/mcp-dispatch-net11`, base `4dc2b489878bcfae6eb7c42f0a65a57893e1c6e0`. Review `5def8199086d49c9a8b7fc64117d06b2`.

## Confirmed execution path

1. `Client/SignalR/Client.fs:1045–1103` uses `JobManager.startToolOperationWithCancelEvidence` for **every auto-promoted command**, including mutating task/review/ledger writes; it passes the actual execution callback to the generic job registry. Pending replies deliberately leave `AgentStateChanges=null` until completion.
2. `Client/Modules/JobManager.fs:342–350` constructs `failedResponse` with `Error=Some(ExecutionFailed ...)`, `AgentStateChanges=[]`, `InboxActivation=None`, and no pending job. At lines **381–385**, `ToolOperations.schedule` catches **any exception thrown by the already-started `execute()` callback**, and returns `entry.PreserveEvidence(failedResponse(...))`. The catch cannot establish that the callback had no side effects before throwing, and loses the original `entry.Handle.OperationId` in the error. The separate outer scheduler catch around line 398 has the same generic fallback even though execution may already have begun.
3. `Server/McpTools.fs:389–413` emits public `agentStateChangesStatus=unknown` **only** for `UncertainOutcome`; for `ExecutionFailed` it publishes **`confirmed` and `agentStateChanges=[]`** (except any independently preserved source evidence). Thus a post-effect fault in an auto-promoted operation asserts a complete no-change outcome and omits recovery operation ID, despite the possibility of a committed ledger write or Ready→Working activation.
4. This bypasses the correct handling in `Client/SignalR/CommandDispatch.fs` and `Client/SignalR/Client.fs:985–1000`, which use `UncertainOutcome` on unexpected execution failures. The newly corrected single SignalR dispatcher does not repair the deeper `ToolOperations.schedule` exception boundary. The existing `Client.Tests/JobManagerTests.fs:590–610` test explicitly expects `ExecutionFailed` for a faulting generic tool callback, but does not model a mutation before that fault.

## Independent negative reproduction

Created **only in reviewer-owned isolated worktree** `reviewer1-dispatch-job-uncertainty`, at frozen exact HEAD `5cda4a3e...`. Appended `Client.Tests/JobManagerTests.fs` test `toolJobUnexpectedFailureAfterSideEffectMustBeUncertain`: it registers a `LedgerWrite`-classified tool operation with a stable original operation ID, records an observable side effect inside the callback, then returns a faulted Task. The test asserts the side effect was executed and the resulting typed failure is `UncertainOutcome`, not a confirmed execution failure.

`dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~toolJobUnexpectedFailureAfterSideEffectMustBeUncertain` **failed 1/1, exit 1**, job `66430efefaef4848949235065924f871`. Actual:
`Some (ExecutionFailed "Tool operation failed: failure after effect")`.
Expected: `Some(UncertainOutcome ...)`. The test uses an in-memory effect marker rather than a live Git commit; it directly exercises the **production generic tool-job scheduler and exception classification**, which applies equally to command callbacks that have made Git ledger mutations. The worker's frozen branch and integration branch were never modified.

## Required fix

For exceptions after an auto-promoted callback has begun, return `UncertainOutcome` carrying the **existing** `entry.Handle.OperationId` and marking any unknown state effects as incomplete (never authoritative `[]`). Keep any known source/activation evidence through `PreserveEvidence`. A provable not-started admission rejection or canceled queued operation can continue to use ordinary `ExecutionFailed` with confirmed zero effects. The same distinction must hold for exceptions escaping scheduling after work may have started. Preserve durable original operation-ID deduplication and job polling; do not auto-replay mutating operations on unknown outcome. Add post-effect exception/recovery metadata tests covering initial and pending/polled responses and partial cross-project source evidence.

**Severity: P2 — blocks corrected MCP dispatch integration acceptance.** This is an exception-boundary correctness defect, not proof that a real ledger commit was lost in production. It violates the explicit acceptance gate against declaring post-execution mutation outcomes definitive when they are not.

No reviewer modifications to frozen branches, merges, deployments or production state.
