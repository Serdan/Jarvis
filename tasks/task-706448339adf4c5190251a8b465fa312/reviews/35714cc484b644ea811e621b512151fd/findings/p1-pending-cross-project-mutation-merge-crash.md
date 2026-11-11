# P1 — Cross-project notification crashes when destination write is promoted

**Frozen:** `2349cb9c1e5b9ccdd15817224146882534039988`.

## Root cause

- A normal `NotifyAgent` with `SourceRef != ProjectName` first validates the sender in its source project, then dispatches the destination write. `Server/McpTools.fs:753–785` always calls `mergeStateEffects sender destination` after the destination dispatch, including when the destination returns a **pending** tool-job handle.
- `Client/SignalR/Client.fs:823–839` explicitly constructs pending envelopes with `AgentStateChanges = Unchecked.defaultof<_>` (**null**, meaning unknown, not an empty list), `Result = Some(handle)`, and `PendingJob = Some handle`.
- `Server/McpTools.fs:395–404` unconditionally merges `(prior.AgentStateChanges @ final.AgentStateChanges) |> List.distinct`. This code assumes both lists are real F# lists. When `final.AgentStateChanges = null`, **`List.distinct` throws `NullReferenceException`**, whether `prior.AgentStateChanges` is empty or nonempty.

### Independent reproduction

Executed a scratch F# script via the same .NET 10 SDK (no repository files modified), evaluating `(prior @ pending) |> List.distinct` for `let pending: string list = Unchecked.defaultof<_>`. Both `prior=[]` and `prior=["Worker 1"]` produced `NullReferenceException` (scratch `/tmp/jarvis-review-null-merge.fsx`). Existing `Server.Tests/ServiceTests.fs` coverage of `mergeStateEffects` only passes ordinary non-null terminal responses; it misses the new promoted destination case.

### Impact

Any cross-project notification whose destination `TaskWriteCommand` takes more than the 3-second promotion threshold reaches this path. The server tool invocation can fail with an unhandled exception **after the client already admitted the destination write**, which may subsequently commit successfully. The caller loses its pending job handle and its trustworthy source/destination effects, promoting exactly the uncertain-outcome/retry problem the feature is intended to prevent.

### Required correction

Represent pending mutation evidence distinctly from terminal empty evidence in the merge contract. At minimum, handle `PendingJob = Some` before list concatenation; never interpret a null/unknown destination change list as authoritative `[]`. Preserve known source activation evidence **and ensure it remains recoverable with the pending destination's result** when polled; terminal results must not omit preliminary source-project state changes. Ensure `GetJobResult` can provide the merged original outcome, or provide a typed correlation/recovery route. Add a real server-routed regression for source successfully validated + destination `PendingJob` and then completion, testing both source changes empty/nonempty, exact job handle and eventual effects, and no duplicate notification.

**Severity P1:** deterministic uncaught exception + uncertain potentially committed cross-project mutation on the core new slow-tool path. This is not a production observation.
