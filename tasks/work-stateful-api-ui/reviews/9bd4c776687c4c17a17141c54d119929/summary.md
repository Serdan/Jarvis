# Reviewer 3 — H0 project-isolation P1 re-review

**Review:** `9bd4c776687c4c17a17141c54d119929`  
**Frozen source:** `27a96794bbef0fe95f8a5c73be553ce6e517948f`  
**Base:** `0e9a8d4ea958090c94af11952329565358fdd79b`  
**Target:** `refs/heads/main`  
**Disposition:** Accept the exact H0 correction; this does **not** authorize H1 live binding, integration, Scheduler, deployment, or migration cutover.

## Independent findings

- Read the previous Reviewer 2 P1 finding at `c8a43bd40fc247aaa00408fea1d2dd11` and independently inspected the exact correction patch and full frozen `WorkStatefulContracts.fs` / `WorkStatefulProjection.fs`.
- `WorkStage` now carries canonical `ProjectId`. Node ancestry, selected node maps and stage joins are keyed by `(ProjectId, localId)`; unauthorized stages whose local node ID collides with an authorized project are not selected or disclosed through stage rows or diagnostics.
- Duplicate-node and duplicate-stage checks are project-scoped, so distinct authorized projects can reuse local node/stage IDs without being rejected globally. Projection row IDs are project-qualified.
- `CommandRequest` now binds the project identity and `ExpectedStageGeneration` for future stage commands. The removed project argument from `ReleaseRecoveryFence` is supplied by the command envelope. These remain inactive H1 contracts with no server transport or action enabled in H0.
- New negative and positive fixtures explicitly exercise unauthorized colliding-stage privacy, authorized same-ID reuse across two projects, malformed stage identity, and command-envelope generation/project binding.
- Exact patch changes only `Client/Modules/WorkStatefulContracts.fs`, `Client/Modules/WorkStatefulProjection.fs`, and `Client.Tests/WorkStatefulProjectionTests.fs`. The frozen worktree was clean. `git diff --check 0e9a8d4..27a96794` passed.

## Independent test evidence

`dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter FullyQualifiedName~WorkStatefulProjectionTests` on the exact frozen worktree: **19 passed, 0 failed, 0 skipped** (.NET 11).

A broader `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore` was separately started as Jarvis job `6fa02e79161340e784a591abef40c775` and had not completed when recording this review. The output included activity-persistence `UnauthorizedAccessException` / read warnings; **do not claim full-suite pass** without a completed result.

## Boundary

This accepts only the H0 P1 project-scoping correction on the exact frozen source. H1 must remain gated on independently accepted and integrated E/F/G contracts, authenticated server enforcement and explicit operator cutover. Lead owns exact-commit integration.