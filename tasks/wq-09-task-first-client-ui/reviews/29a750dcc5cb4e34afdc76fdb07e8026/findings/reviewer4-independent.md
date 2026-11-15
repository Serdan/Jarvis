# Reviewer 4 — supplemental audit and decision reconciliation (WQ09)

**Canonical verdict: ChangesRequested (decision sequence 3686).** This replaces the initial proposed acceptance in this document. The frozen review has already been decided, and an additional concurrent decision attempt correctly failed the version guard. The independently reproduced blocking failures are detailed in `findings/reviewer4-wq09-projection-audit.md`.

## Provenance and bounded Release evidence
- Frozen exact SHA `6ed915d4615c9cc795c3da786e461a7bd0ac1f3a`, sole parent `5e9cc791b5093bba221bdbd18afab77cb8d7fe83` (verified with `git rev-list --parents -n 1`); registered Worker 7 worktree clean.
- Four source-diff files: new pure projection and tests, plus Client and Client.Tests fsproj compile registrations. No existing review-count UI/ConsoleTui/operational handler edits, and no live calls to projection from the current TUI.
- Independently ran `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~TaskFirstUiProjectionTests --verbosity minimal`: **21/21 passed**, no failures/skips; net11.0 Release build passed.
- Independently ran a Release no-build combined WQ09 + AgentView + DashboardCache + TerminalFrame + TerminalLayout filter: **85/85 passed**, no failures/skips. This is not evidence of a full unfiltered suite.

## Correct inert behavior
- `Contracts.orderAvailable` drives blocking-first FIFO; `WorkQueueDependencyDiagnostics.analyze` gives exact hard-stage SCCs, with explicit closed paths and no cyclic stages in Next, even under waivers.
- Unknown `QueueCovered`, `GraphCovered`, `MessageCovered`, `SourceCovered` or dangling hard prerequisites makes queue views UNKNOWN, not empty. Dedicated source/integration recovery gates are marked held, never eligible Next.
- `Role(Lead)` conversation exists with zero sessions, message states are visible, unregistered launches never invent UUIDs, STOP/stale status is explicit, and composer is `CanPost=false`. The entire module is inert/read-only.
- Existing review-count UI source remains unchanged and regression tests pass.

## Three blocking display-integrity findings (confirmed in code)
1. **Claimed DependencyGatePending shown ACTIVE.** `TaskFirstUiProjection.fs:373-386` matches only three nonclear gate variants and falls through to `claimedState`. With a registered fresh lease, `DependencyGatePending` displays `ACTIVE claimed by L`, not HELD. This is reachable by accepted `Server/WorkQueueContracts.fs:1147-1174` which creates `DependencyGatePending` on already claimed non-integration work following a new unsatisfied hard edge. It can also produce a zero blocked-count in the dashboard. Require a complete gate-to-row-state mapping and negative test.
2. **Bad message index displayed as verified.** `TaskFirstUiProjection.fs:313-323` validates work/launch map keys but not message map key versus `SubjectMessage.Id`; `threadRows` treats mismatched messages as `Known` when `MessageCovered=true`. That breaks the stated immutable canonical-history verification guarantee. Require fail-closed message identity validation and negative fixtures.
3. **Contradictory account/project views can show free capacity.** `capacityView` counts only the provided account's token map; `projectDashboard` does not check that known registered/submitted launches from the project have matching account-token evidence. Thus a registered launch alongside empty account tokens can display `0/3 occupied, 3 free`, falsely suggesting reconciled free account capacity. Require Unknown/inconsistent state or a verified two-ledger join before treating this as operational capacity.

Also harden no-clock liveness (a leased registered claim with `now=None` may be presented Active), and sanitize message bodies before any future terminal wiring.

## Scope and disposition
These are *presentation fail-open* cases, not evidence of live commands or source mutations. Nevertheless, they violate WQ09's required high-water/unknown fail-closed UI semantics and warrant **ChangesRequested** despite green bounded tests. Repair on isolated source, add three targeted negative cases, and submit a new task-bound frozen review. No main push, combined integration, Scheduler activation, deployment, migration, browser dispatch or Codex authorized. WQ04/WQ05 and later dependency gates remain separate.