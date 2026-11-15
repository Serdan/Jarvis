# Independent WQ03 source UUID transport review — ChangesRequested

**Task-bound frozen review:** `b4b9f0e236f44d7c9a416928f93d882a` for `wq03-source-effect-journal-bridge`. **Exact candidate:** `f85c288d45981aef5502ce953f7e135e2fd9e965`, whose sole parent is independently Accepted *inert* `e7a8a3edacbbcdfc3f74e72361b21a6a912f85da`. Source tree clean; `git diff --check` passes. The separately reviewed WQ03/UI candidate `e14173e` is not part of this frozen source.

## Independently verified positive behavior

- `Server/McpTools.fs` accepts an optional original caller UUID **only** for WriteFile/PatchFile; validates nonempty GUID and never substitutes SignalR correlation/ledger/job IDs. `Server/Services/ClientService.fs` forwards it on distinct `ReceiveSourceCommand`. `Client/Program.fs` preserves it through bounded queue; `Client/SignalR/Client.fs` preserves it through tool-job promotion and callback, including reconnect reply flow.
- `Client/Modules/SourceEffectHostBinding.fs` reloads checkout-local host-approved trust binding from the external config store, the verified canonical work ledger, and registered source repository identity. The testable binder chooses a unique Claimed Implementation/Registered launch and exact generation checkout; caller cannot inject a `SourceRouteInvocation.Scope` via RPC. File route is fingerprinted and checked. These are useful narrow diagnostic boundaries, not complete authenticated actor/admission proof.
- `SourceEffectJournal.beginBeforeDispatch` commits Prepared/fence before the awaited file handler, and same-DB retries of the original UUID, changed request/scope and concurrent SQLite clients are denied. The report and `YieldSource` remain RecoveryBlocked; process/job/Git/push/legacy/out-of-band outcomes are never upgraded to terminal proof.
- `JARVIS_WQ03_NONPRODUCTION_HOST_BOUND_FILE=host-bound-file-v1` remains opt-in/default off. In the opted-in generation checkout unbound legacy source routes deny; no Scheduler, migration, source succession, main push, hosted Actions or deploy is authorized.

## Blocking finding

**P1 missing-journal replay reset** in `findings/missing-journal-replay.md`. Operational `openAtHostRoot` recreates a missing SQLite DB with empty UUID records, and the resumed request becomes `FirstDispatch` even after the original UUID already caused an edit before journal loss. Therefore the caller-stable UUID is **not** durably fail-closed across journal loss/root replacement. Require explicit trusted provisioning separate from dispatch, persistent continuity pin, and two-client loss/restart/replay negatives before accepting this routed candidate. Canonical review verdict: **ChangesRequested**.

## Local independent Release evidence

- `dotnet test Client.Tests/Client.Tests.fsproj --configuration Release --no-restore --filter FullyQualifiedName~SourceEffect`: **24/24 passed** (.NET 11).
- `dotnet test Client.Tests/Client.Tests.fsproj --configuration Release --no-build --no-restore --filter FullyQualifiedName~WorkQueueGitLedgerTests|FullyQualifiedName~CommandDispatchQueueTests`: **25/25 passed** (.NET 11).
- `dotnet test Server.Tests/Server.Tests.fsproj --configuration Release --no-restore --filter FullyQualifiedName~source`: **10/10 passed** (.NET 11).
- These do **not** test journal deletion/root replacement. A disposable standalone shell fixture attempt was denied by local Jarvis command policy (`bash` denied); no independent destructive fixture was executed or claimed.

## Explicit remaining nonproduction limits

Authoritative upstream original-UUID admission/retry ownership, caller-to-registered-launch attribution, host WIP protection and retention, true original push/process-tree reconciliation, all mutation routes, full source succession, Scheduler and YieldSource remain unproven/disabled. Do not treat this narrow review as approval for any of those gates.
