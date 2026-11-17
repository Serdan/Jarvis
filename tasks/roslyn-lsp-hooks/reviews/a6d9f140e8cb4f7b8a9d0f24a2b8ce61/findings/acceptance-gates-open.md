# P1 — Explicit end-to-end task acceptance evidence remains open

**Review:** `a6d9f140e8cb4f7b8a9d0f24a2b8ce61`  
**Frozen head:** `768f23da13519beb0b54fa3c1f517bfd8dc23700`  
**Prior finding:** `f135fae7b35043a3ac75c49a5f1313cc/findings/acceptance-gates-remain-open.md`

The async TaskRead correction and focused tests are meaningful, but the canonical `roslyn-lsp-hooks` acceptance criteria require more:

1. **Unfiltered client suite:** `docs/CodeQualityRoslynHooks.md` still reports a 240-second timeout and activity-persistence `UnauthorizedAccessException`/`InvalidDataException` evidence, rather than a resolved full-suite result. Reproduce/triage the failure with a bounded unfiltered test and distinguish environmental pre-existing failures from regressions. Do not convert timeout into passing evidence.
2. **Actual TaskRead activation:** `Client.Tests/SignalRDispatchTests.fs` directly tests `InboxPrewarmDispatcher.TrySchedule`, not the production `TaskReadCommand -> TaskApi.read -> AgentInbox -> prewarmFromInbox` path. Add a bounded deterministic source-bound activation test that proves response delivery is independent of a stalled Git lookup, repeated reads are idempotent, and delayed/unknown source bindings can eventually prewarm without defaulting to main.
3. **Wayfold formatting/performance:** No small/large edited-file cold/warm latency, output parity with `dotnet format` (including final newline), daemon/client RSS or project-load diagnostics are provided on representative Wayfold source.
4. **Distinct worktree compilation state:** Two disposable local C# roots formatting does not verify two distinct active worktrees with independent project/reference/analyzer configuration sharing the official Roslyn daemon. The task explicitly requires this evidence.

**Independent bounded run here:** focused .NET 11 Release tests passed **56, skipped 2, failed 0** on frozen branch, with live DLL-dependent cases skipped; `git diff --check` exit 0. Author reported 58/58 with official Roslyn DLL. Neither focused result closes the four points above.

**Disposition:** Preserve the corrected nonblocking implementation and positive tests. Supply or explicitly scope out each remaining acceptance criterion via a new canonical task/approval decision; do not treat acceptance of this corrective slice as full task integration or deployment authorization.