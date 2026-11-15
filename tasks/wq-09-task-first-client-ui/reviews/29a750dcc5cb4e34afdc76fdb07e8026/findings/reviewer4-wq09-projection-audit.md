# Reviewer 4 — WQ09 task-first inert TUI projection, independent frozen audit

**Verdict requested: ChangesRequested.** Review `29a750dcc5cb4e34afdc76fdb07e8026` for task `wq-09-task-first-client-ui`; exact frozen head `6ed915d4615c9cc795c3da786e461a7bd0ac1f3a`. Scope is isolated, read-only groundwork only; this is **not** an assessment or authorization of live WQ04/WQ05/WQ06/WQ08 integrations or Scheduler activation.

## Provenance and isolation

- Independently verified `git rev-list --parents -n 1 6ed915d...`: the **sole immediate parent** is `5e9cc791b5093bba221bdbd18afab77cb8d7fe83`, the required independently accepted review-count baseline.
- Frozen diff comprises exactly four files: new `Client/Modules/TaskFirstUiProjection.fs` (492 lines), new `Client.Tests/TaskFirstUiProjectionTests.fs` (301 lines), and additions to `Client/Client.fsproj` and `Client.Tests/Client.Tests.fsproj`. No modifications to `AgentView.fs`, `ConsoleTui.fs`, `AgentDashboard.fs`, existing review-count implementation, WQ04/05/06 producers/adapters, dispatch or scheduler handlers.
- Independent registered worktree `reviewer4-wq09-6ed915`, exact HEAD and initially clean. Read-only git grep found only new model/test plus project compile inclusions for `TaskFirstUiProjection`; no live TUI caller or operation dispatch. An ignored disposable FSI fixture used for independent negative reproductions was deleted; no source commits or pushes.

## Independently executed .NET 11 Release evidence

Command: `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter "FullyQualifiedName~TaskFirstUiProjectionTests|FullyQualifiedName~AgentViewTests|FullyQualifiedName~AgentDashboardCacheTests|FullyQualifiedName~TerminalFrameTests|FullyQualifiedName~TerminalLayoutTests" --nologo --verbosity minimal`

Result: **Passed 85, failed 0, skipped 0, total 85**, exit 0, test execution duration 2s (build/restore separately elapsed). Existing submitted WQ09 21 tests are included. Compilation produced unrelated existing FS0760 and FS3369 warnings. **Not a full, unfiltered Client.Tests suite pass**; the submitter reports its previous 180s unfiltered attempt timed out. No GitHub Actions, hosted checks, Node tests, deployment, production restart or main push used.

## Verified correct within inert scope

- Verified WQ01/02 `Contracts.orderAvailable` is used for preview availability, retaining blocking-first FIFO and not exposing gated/unfinished unsatisfied prerequisites as `Next`.
- `WorkQueueDependencyDiagnostics.analyze` supplies hard-edge SCC warnings; self loops and multi-stage cycles produce ordered real hard-edge paths, not a sorted pseudo-path. Cyclic stages are excluded from `Next` even when a waiver makes them appear available.
- Incomplete queue, graph, message, source coverage and missing dependency stage generation produce `Evidence.Unknown`, not empty green lists. Integration and source recovery holds are represented for unclaimed blocked rows and several claimed states.
- `Role(Lead)` conversation exists with no agent session; Task/Role messages preserve Pending, ClaimedPrimary, ClaimedAncillary and Handled; sorting is explicitly canonical-ID rather than chronology. Unregistered launches retain launchId and do not invent agent UUIDs. STOP and stale heartbeat are shown as unproven recoveries. Composer is `CanPost=false` and preview is explicitly read-only.

## Blocking finding 1 — Claimed dependency gate is falsely labeled ACTIVE

**Source:** `Client/Modules/TaskFirstUiProjection.fs:373-386`, `:251-265`, `:389-415`, `:429-436`. In the `WorkState.Claimed launch` branch, only SourceRecoveryBlocked, IntegrationGateHold and ExternalEffectObservedPendingGate receive recovery labels; all other gates, **including `GateState.DependencyGatePending`**, call `claimedState`. A fresh registered lease causes `RowState.Active`, rendered as `ACTIVE claimed by L`. The blocked/recovery section excludes Claimed rows and QueueHealth.Blocked counts zero.

This is a **reachable canonical state**, not a fabricated enum combination: accepted core `Server/WorkQueueContracts.fs:1147-1174` sets `DependencyGatePending` on already-claimed work when a new unsatisfied hard dependency arrives, without releasing the original claim.

**Independent .NET11 FSI negative reproduction against the exact Release assemblies:** project P has A `Claimed("L")`, `Gate=DependencyGatePending 2`, `Dependencies={"B"}`; B is unsatisfied and Available; L is registered with fresh lease. `projectDashboard` returns `ActiveAndStalled=[A: Active "L"]`, `QueueHealth.Blocked=0`, and preview prints `ACTIVE claimed by L | prerequisites: B`. A is not `Next`, but the status suppresses the crucial gate hold and overstates normal execution.

**Required correction:** make every nonclear gate an explicit nonhealthy held/stalled state, especially DependencyGatePending, while preserving claim/token and never treating it as runnable or a resolved recovery. Add a fixture exercising the real canonical hard-edge mutation on a claimed stage, including fresh/stale lease and all gate variants. Review blocked/held counts and section naming so the blocked claim is not silently green.

## Blocking finding 2 — Message canonical-ID/index mismatch is shown as verified thread

**Source:** `Client/Modules/TaskFirstUiProjection.fs:313-323` checks map-key identity for stages and launches but not messages; `:169-204` collects `Map.toList |> List.map snd` and sorts by `SubjectMessage.Id` without asserting that map key equals message ID. `MessageCovered=true` alone does not turn a demonstrably inconsistent map into a valid canonical history.

**Independent negative reproduction:** `Messages=Map.ofList [("wrong-index-key", { Id="canonical-message"; Subject=Role "Lead"; State=Pending; ... }) ]`, with `MessageCovered=true`. `Dashboard.Threads` returned `Evidence.Known` containing message `canonical-message`, not `Evidence.Unknown`. This can present duplicated, reordered, or wrongly indexed immutable history as verified.

**Required correction:** verify message index key/record ID alignment and nonblank IDs before declaring `Threads=Known`; reject conflicting canonical IDs and invalid Subject IDs rather than silently substituting map values. Add a negative message-map mismatch case.

## Blocking finding 3 — Contradictory account/project capacity snapshots display free capacity

**Source:** `Client/Modules/TaskFirstUiProjection.fs:281-293`, `:307-310`, `:405`, `:474-480`. Capacity counts unreleased account tokens but never verifies that current project's known registered/reserved launches have corresponding account-global tokens or states; two `Ok` snapshots are displayed side by side without reconciliation warning.

**Independent negative reproduction:** the same snapshot above contains `Launch L` in `Registered(uuid-1,session-1)` with `AccountTokenId="token-L"`, but the supplied account has `MaxConcurrentAgents=3`, `Tokens=Map.empty`. Preview reports `Account-global capacity: 0/3 occupied; 3 free` while showing the registered L. These records cannot safely be treated as a reconciled two-ledger capacity view; passing two independently readable snapshots is not evidence of a consistent cross-ledger join.

**Required correction:** represent missing/unreconciled account-token evidence as `Unknown` or an explicit inconsistent/unsafe capacity state; require adequate version/join evidence in the eventual adapter and add mismatch tests. Do not infer real launch admission authority or free reservation tokens from this read-only preview.

## Verdict and next gate

**ChangesRequested** for the exact frozen commit, despite the 85/85 bounded test pass. The above negative inputs are outside the submitted 21 WQ09 tests and demonstrate fail-open *presentation*, not an operational command dispatch. Keep WQ09 isolated/inert, unchanged on main, and require a **new task-bound frozen source review** after corrections and bounded Release negatives. The known WQ04 producer and WQ05 adapter ChangesRequested conditions, WQ06 unintegrated transport, WQ08 future planner, WQ03 source/host limits, and all operator-controlled production gates remain independent blockers. This review grants no permission for main integration, Scheduler, live handlers, migration, deployment or Codex.
