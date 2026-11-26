# Git-call optimization harness

This is an isolated console harness; it does not modify `Client.Tests.fsproj`, `Server.Tests.fsproj`, or any production source path.

It invokes the real public MCP tool methods in `Server`, routes commands through a real `ClientService` backed by a disposable in-memory `UserService` and response tracker, and forwards the typed command JSON through the production bounded `CommandDispatchQueue` into `Client.SignalR.Client.processIncomingCommand`. Git operations execute against a newly created local project repository and local bare remote. No live account, remote, production ledger, deployment, or scheduler is involved.

The in-process hub is a test transport, not a real SignalR socket. Therefore:
- `DurationMilliseconds` is end-to-end for the MCP tool method + server/client dispatch + local operation, but excludes HTTP JSON-RPC transport and real network RTT.
- `QueueMilliseconds` and `ExecutionMilliseconds` come from the production dispatch queue observer.
- `GitProcessStarts` and `RemoteFetches` are counted independently from Git Trace2 `start` events; they are actual process starts, not the historical LedgerStore runner-attempt counter.
- `ResponseDigest` covers result class, payload, ordered instructions, error, state-change evidence, inbox activation and pending-job shape. Known timestamps and the random fixture root are normalized; stable IDs and canonical ref names are retained.
- Writes are compared by exact ref names and normalized blob contents in the disposable bare remote, not by timestamp-sensitive commit OIDs.

## Run on a frozen source

From the repository root:

```sh
dotnet run --project scripts/perf/git-call-optimization-20261126/GitCallOptimization.Harness.fsproj -c Release -- --source-root "$PWD" --out /tmp/jarvis-git-call-baseline.json
```

The project references can be redirected to another isolated candidate worktree without copying the harness:

```sh
dotnet run --project scripts/perf/git-call-optimization-20261126/GitCallOptimization.Harness.fsproj -c Release \
  -p:JarvisClientProject="/path/to/candidate/Client/Client.fsproj" \
  -p:JarvisServerProject="/path/to/candidate/Server/Server.fsproj" -- \
  --source-root "/path/to/candidate" --out /tmp/jarvis-git-call-candidate.json
```

The source check requires `Client/` and `Server/` to be clean. Harness files in this benchmark worktree are intentionally outside those source paths. Keep the raw JSON report and Trace2 files under the printed fixture root.

## Compare reports

```sh
python3 scripts/perf/git-call-optimization-20261126/compare.py \
  /tmp/jarvis-git-call-baseline.json /tmp/jarvis-git-call-candidate.json
```

The comparison fails on response/error/instruction/state-evidence digest changes, canonical-ref or durable-tree changes, missing scenarios, a write that did not persist, a retry that changed durable state, a changed-input operation-ID reuse that was not rejected, or an unauthorized write that reached the client / invoked Git. It prints process/fetch/latency deltas but does not impose cross-host wall-time thresholds.

## Current scenario coverage

- `ListWorktrees`: physical main + linked worktrees, real Git process and status fan-out.
- `GetWorktree`: registered selector resolution and selected worktree read.
- `GetTask`: canonical ledger read through the MCP tool method.
- `RegisterAgent`: disposable remote ledger write.
- `GetOperationStatus`: post-write recovery/status read.
- Same operation-ID retry: verifies durable state does not advance twice.
- Same operation-ID with changed input: verifies conflict semantics and no durable state change.
- Unauthorized register attempt: verifies the write is rejected before client/Git dispatch.

The accepted historical 0/220/2200 population goldens and controlled delayed-fetch/cancellation evidence are reconciled in `docs/perf/git-call-optimization-20261126.md`. This initial harness intentionally uses a small physical-worktree fixture; large ledger population and delayed-transport scenarios remain separate focused runs rather than inflating the default run.
