# Independent deep review — P1 client-owned slow-tool auto-promotion

**Verdict: RequestChanges — two confirmed P1 regressions on cross-project notification paths.**

Frozen source `2349cb9c1e5b9ccdd15817224146882534039988`, branch `refs/heads/feature/auto-promote-tool-jobs`, base `28fd0c3d2fa887a8cd7289e07cb65b476de351f9`, current target `main`. Related task `task-706448339adf4c5190251a8b465fa312`.

## Scope and positive evidence

- Source patch reviewed across `Client/Modules/JobManager.fs`, `Client/SignalR/Client.fs`, `Client/SignalR/HubConnection.fs`, `Common/Messages/AgentCommand.fs`, `Server/McpTools.fs`, `Server/Services/ClientService.fs`, plus Client/Server regressions.
- Client owns the Task/Task.WhenAny lifecycle and keeps OS process-job model separate from tool-job model. Registry admits at most **32** queued/running tool jobs with **8** execution slots, terminal retention **100** / **30 minutes**, operation-id-keyed dedup/tombstone, explicit cancellation before execution and refusal after side effects may have started. SignalR response ingress limits concurrent callback work to **96**, so commands can poll without blocking behind long calls.
- Typed protocol **3.64** has explicit `PendingJob` and nullable/unknown `AgentStateChanges` semantics. Fast calls retain ordinary responses; slow calls return handle while original command continues. `GetJobResult` carries the exact terminal `AgentCommandResponse`, and polling is non-activating; Server attaches original instructions and mutation evidence on polling without replaying operations.
- Full independent Release Client suite: **557 passed / 0 failed**, job `5891c069aa444a898123499407b4704c`.
- Full independent Release Server suite: **129 passed / 0 failed**, job `a51d3e707bbb432584a3f775c2a416df`.
- Exact frozen HEAD verified and worktree clean. `git diff --check base frozen` exit 0; `git merge-tree --write-tree main frozen` exit 0 with tree `a713076e5f0369c2b50ae6fb9892398cc495fe22`. No source edits/integration/deployment by reviewer.
- These test suites test many core job lifecycle cases, but do **not** exercise a `Running` health probe or `Running` destination write in the server's cross-project notification workflow.

## Blocking finding 1 — P1: Pending health probe misclassified as verified ledger binding

See `findings/p1-pending-ledger-health-incorrect-routing.md`.

`ProjectNotificationRouting.healthProbe` sends `LedgerReadCommand { Action = "Health" }`. Client `shouldAutoPromote` **includes** LedgerRead and after default 3 seconds returns `Result=Some(serialized handle), Error=None, PendingJob=Some handle` while actual ledger-health result is still unknown. `Server/Services/ClientService.fs:239–249` uses `| Some _, None ->` as its **only** success condition when choosing the device for a destination project. This treats a pending handle as proof of verified ledger ownership, and can route a cross-project mutation to a device before its source ledger binding was validated. No end-to-end Server regression covers this transition.

**Required:** only trust a **terminal successful** ledger-health result; resolve a pending job on its original candidate device/actor with a bounded deadline before dispatch, otherwise fail closed without treating a handle as health. Regression with multiple eligible devices and slow healthy/unhealthy probes.

## Blocking finding 2 — P1: Pending destination causes `mergeStateEffects` null dereference after write admission

See `findings/p1-pending-cross-project-mutation-merge-crash.md`.

For `NotifyAgent` with distinct `SourceRef` and destination, `Server/McpTools.fs:753–785` does `mergeStateEffects sender destination` after sending the destination write. Slow destination writes are now promoted, and their `AgentStateChanges` is deliberately **null**, not `[]`, until completion (`Client/SignalR/Client.fs:830–839`). But `McpToolHelpers.mergeStateEffects` (`Server/McpTools.fs:395–404`) does `(prior.AgentStateChanges @ final.AgentStateChanges) |> List.distinct` unconditionally, which throws **NullReferenceException**.

**Independent compiled-code reproduction:** An isolated F# script `/tmp/jarvis-review-pending-merge-repro.fsx` loaded the frozen `Common.dll` and `JarvisServer.dll` directly and invoked the **actual** `McpToolHelpers.mergeStateEffects` with a successful source response and a pending destination response; it printed `MERGE ERROR: NullReferenceException: Object reference not set to an instance of an object.`. An earlier independent F# experiment verified failure with both empty and nonempty preliminary state-change lists. Neither test changed tracked source. Existing `ServiceTests.fs` `mergeStateEffects` fixture only uses terminal/non-null changes.

A real destination notification may still commit after its server MCP invocation crashes and loses the job handle. It cannot safely be retried blindly, and even a naive null-coalescing fix would risk dropping preliminary sender activation receipts from eventual polling.

**Required:** explicit typed pending-state merge and correlation/recovery, preservation of both preliminary sender mutation evidence and later terminal destination evidence, no false `[]`, and routed regressions for successful source + pending destination, with/without source state changes, completion/error/duplicate polling.

## Integration/deployment boundary

Protocol 3.64 is a coordinated client/server change. Current `main` merges cleanly; a future browser-bridge integration may introduce an independent Server.Tests conflict that must be resolved and retested. **Do not integrate or deploy** this branch while P1 findings remain, and not without operator approval afterward.

**Decision: RequestChanges.** Two independently grounded P1 blockers outweigh otherwise strong local job and transport design. Re-review a new frozen commit after both correctness paths are fixed and regression-tested.
