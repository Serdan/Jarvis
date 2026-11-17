# Reviewer 2 — frozen corrective Roslyn/LSP hooks review

**Request:** `a6d9f140e8cb4f7b8a9d0f24a2b8ce61`  
**Frozen head:** `768f23da13519beb0b54fa3c1f517bfd8dc23700`  
**Base:** `944bca7fa38cb5e973b04ce7829966cca351202b`  
**Prior review:** `f135fae7b35043a3ac75c49a5f1313cc`, ChangesRequested at sequence 7109  
**Verdict:** **ChangesRequested** for task acceptance (the original synchronous-inbox blocker has been materially corrected).

## Independent evidence

- Inspected the exact frozen 18-file diff and the five-file correction from prior head `e00abdd6432768065af9716758f6115cab4a0f1f`; reviewed earlier summary/findings and canonical task criteria.
- `Client/SignalR/Client.fs` now schedules `prewarmFromInbox` through `InboxPrewarmDispatcher.TrySchedule` using `Task.Run`, rather than synchronously doing Git discovery in `TaskRead`. Per-assignment keys deduplicate in-flight/successful work; unresolved source lookup returns false and permits retry. `CodeQualityHooks.hasApprovedPrewarmCandidate` gates the background lookup on approved configured hooks. This addresses the prior P1 response-blocking source path.
- The new dispatcher tests cover slow discovery, repeated keys, unknown binding retry and distinct keys. The new hook test checks no-hook/disabled/unapproved roots in isolation. These are useful unit regressions, not a real `TaskRead -> AgentInbox -> Git selector -> Roslyn` integration test.
- Independently ran Release .NET 11 `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter "FullyQualifiedName~CodeQualityHooksTests|FullyQualifiedName~GitCommandTests|FullyQualifiedName~WorktreeTests|FullyQualifiedName~DispatchProbeTests" --verbosity minimal` on the exact frozen registered worktree: **56 passed, 2 skipped, 0 failed; exit 0**. The two live Roslyn cases were skipped because this run did not set `JARVIS_ROSLYN_LSP_DLL`. Author documentation reports 58/58 with an official DLL; this reviewer did not independently reproduce the live two. Build emits F# warnings, not errors.
- `git diff --check 944bca7fa... 768f23da...` exited 0, and the registered frozen worktree remained clean after testing.

## Outstanding

1. **P1 acceptance evidence still open** (see `findings/acceptance-gates-open.md`): full unfiltered client-suite result is unresolved; no Wayfold edited-file small/large cold/warm format parity/final-newline/RSS; no demonstrated independent analyzer/compilation context for two worktrees; no actual inbox-to-prewarm integration timing/idempotence test. These were already requested at prior verdict and are explicit canonical criteria.
2. **P2 negative gate is global rather than per target** (see `findings/cross-project-prewarm-gate.md`): one approved hooked root can cause background Git enumeration for unrelated no-hook sourced inboxes. This does not reintroduce synchronous response blocking, but weakens the no-hook fast-path claim and should be covered by a mixed-project regression.

## Scope

This verdict does **not** assert that async prewarm is ineffective; the prior synchronous P1 source issue is corrected. It rejects declaring the complete Roslyn hooks task accepted without the remaining bounded acceptance evidence. No source edits, merge, deployment, Wayfold modification or native acceptance were performed.