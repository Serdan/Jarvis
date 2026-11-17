# P1 — Required end-to-end activation and acceptance evidence remains incomplete

**Frozen source:** `e00abdd6432768065af9716758f6115cab4a0f1f`.

## Checks independently completed by Reviewer 2

- `git diff --check 944bca7fa38cb5e973b04ce7829966cca351202b e00abdd6432768065af9716758f6115cab4a0f1f`: exit 0, no output.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter "FullyQualifiedName~CodeQualityHooksTests|FullyQualifiedName~GitCommandTests|FullyQualifiedName~WorktreeTests" --verbosity minimal`: 40 passed, 2 skipped (live Roslyn DLL path not set).
- Repeated exactly that filter in the clean source worktree with `JARVIS_ROSLYN_LSP_DLL` set to the installed official `roslyn-language-server.dll` v5.13.0-1.26514.10: **42 passed, 0 failed, 0 skipped**, exit 0. This independently confirms the new live session recovery and selected-only pre-commit auto-fix cases pass on the installed local Roslyn.

## Outstanding explicit task criteria

- `docs/CodeQualityRoslynHooks.md` reports a 240-second unfiltered Release client-suite timeout with activity-persistence errors, not a passing full-suite result. Those errors need isolation and reproducible resolution/evidence; don't imply full-suite pass.
- No Wayfold small/large **edited-file** cold/warm timing, `dotnet format` parity (including final newline), RSS, or project-load diagnostics evidence was supplied.
- The canonical criterion of two *distinct worktree compilation/analyzer contexts* remains unverified. Pure branch-selector `WorktreeTests` checks are not a substitute for live independent project/settings state.
- Newly added tests do not exercise the actual `TaskReadCommand -> AgentInbox -> prewarmFromInbox` dispatch. They do not demonstrate nonblocking activation, no-hook latency, repeated inbox idempotence, or delayed/unknown source-binding behavior end to end.

The correction materially improves positive commit-hook coverage and Roslyn recovery, and these tests passed independently. They do **not** close the task's remaining explicit acceptance gates. Provide bounded acceptance tests and measurements with isolated worktrees, keep Wayfold unchanged except controlled tests, and avoid deployment without approval.

**Evidence boundary:** No Wayfold benchmark, full unfiltered suite, daemon RSS, or two-worktree analyzer-state run was executed by Reviewer 2.
