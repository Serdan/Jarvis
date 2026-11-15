# Reviewer 4 — independent frozen WQ09 review

**Verdict: Accepted, strictly for isolated inert/read-only UI groundwork.** This is not acceptance of live TUI integration, message posting, Scheduler, admission, source handoff, or production rollout.

## Frozen source and preservation
- Review request: `29a750dcc5cb4e34afdc76fdb07e8026`, task `wq-09-task-first-client-ui`.
- Inspected exact `6ed915d4615c9cc795c3da786e461a7bd0ac1f3a`; `git rev-list --parents -n 1` confirms sole parent `5e9cc791b5093bba221bdbd18afab77cb8d7fe83`, identical to review base.
- Four-file diff: adds `Client/Modules/TaskFirstUiProjection.fs` (492 lines), `Client.Tests/TaskFirstUiProjectionTests.fs` (301 lines), and registration entries in two .fsproj files. No other source delta. Previously accepted review-count UI and ConsoleTui source unchanged. Worktree is clean.
- The new module is pure snapshot projection: no live command handler, subscription, event, Scheduler dispatch, task claim, posting, UUID creation, or integration code. `previewComposer.CanPost=false`, preview prints explicit READ ONLY/no-claim header. The added source is compiled but not invoked from ConsoleTui.

## Semantics inspected
1. **Next/FIFO and blockers.** `projectDashboard` obtains runnable stages from accepted `Contracts.orderAvailable`; sorting uses blocks-other-unfinished-first, then availability time, project, sequence and stage ID. Blocked prerequisite stages are not surfaced as runnable. Complete stages are excluded and wake/requeue fixture restores FIFO. Explicit `GateState.IntegrationGateHold`, external-effect pending and `SourceRecoveryBlocked` remain visible recovery holds rather than success.
2. **Hard graph/SCC.** Uses the accepted `WorkQueueDependencyDiagnostics.analyze` over exact stage-generation dependencies, independent of task/message hints and waivers. Reconstructs an ordered path along witness edges, including self-loop. Cyclic members are forcibly removed from Next even when a waived dependency would make `orderAvailable` return that stage. If a cycle path cannot be reconstructed, Next/blocked/queue health fail to UNKNOWN rather than silently dropping the warning.
3. **Coverage and identity.** `QueueCovered`, `GraphCovered`, `MessageCovered`, `SourceCovered` and dangling hard prerequisites are enforced by `Contracts.orderAvailable`; missing graph evidence also makes cycle diagnostics UNKNOWN. Project/work/launch key identity mismatches produce UNKNOWN for queue views. Unavailable account snapshot gives UNKNOWN capacity instead of invented free tokens.
4. **Task/Role, launch and capacity.** Role(Lead) exists with no session. Pending/claimed-primary/claimed-ancillary/handled messages are represented without pretending ID order is chronology; composer has no posting authority. Unregistered/uncertain dispatch retains launchId but never manufactures an agent UUID. Registered STOP, stale heartbeat and missing lease are explicit. Capacity counts all unreleased account tokens and flags overcommit; it never authorizes reservation.
5. **Review-count regression.** Existing accepted review-count logic is byte-identical relative to review base, and bounded AgentView/Dashboard/terminal tests pass.

## Independent bounded .NET 11 Release evidence
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~TaskFirstUiProjectionTests --verbosity minimal`: exit 0, **21 passed, 0 failed, 0 skipped**, net11.0; compiled client/test assemblies in Release.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --filter 'FullyQualifiedName~TaskFirstUiProjectionTests|FullyQualifiedName~AgentViewTests|FullyQualifiedName~DashboardCacheTests|FullyQualifiedName~TerminalFrameTests|FullyQualifiedName~TerminalLayoutTests' --verbosity minimal`: exit 0, **85 passed, 0 failed, 0 skipped**, net11.0.
- Existing unrelated F# warnings FS0760 and FS3369 during compilation. No full unfiltered suite or hosted CI is claimed.

## Follow-up risks and non-acceptance boundaries
- **Integration precondition, not an inert-branch blocker:** Both inputs are labelled caller-verified. Before live UI use, the ledger reader must prove consistent heads/high-water across the project/account snapshots; the presentation model alone is not an authority.
- **Future hardening:** Validate message-map keys against `SubjectMessage.Id` (work and launch maps are checked, messages are not) and add a negative fixture. With inconsistent supposedly-covered message data, conversation projection presently assumes the caller's verification; live integration should not rely on that assumption silently.
- **Liveness wording:** A registered, leased claimed stage with `now=None` is labelled Active instead of Unknown; before live operational display add explicit unknown-clock semantics and test it.
- Escaping/width limits for untrusted message bodies are required before wiring terminal output, and no live transport or UI switch is included here.
- WQ04 producers and WQ05 adapters remain ChangesRequested; WQ06 accepted source is not integrated; WQ08 and exact combined UI/dependency review are pending. This verdict is **not** an authorization to integrate or activate those paths.

**Conclusion:** Within the expressly frozen, read-only WQ09 groundwork scope, the implementation preserves review-count behavior, surfaces graph/source uncertainty safely for Next, and passes its bounded tests. Accept this exact commit only as an inert building block; hold all live integration and activation for separate dependency and combined reviews.