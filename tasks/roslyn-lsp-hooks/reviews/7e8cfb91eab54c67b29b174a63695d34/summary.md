# Independent review — Roslyn code-quality hooks

**Task:** `roslyn-lsp-hooks`  
**Reviewer:** Reviewer 3  
**Frozen source:** `de596e2d0f0f70fbe35a72a2c8b822d2708941db` (two commits after base `944bca7fa38cb5e973b04ce7829966cca351202b`)  
**Disposition:** **ChangesRequested**

## Verified independently

- Claimed the unclaimed frozen review `7e8cfb91eab54c67b29b174a63695d34` at review version 1; inspected the exact clean registered Worker 6 worktree and 17-file diff.
- The Release .NET 11 focused test command `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter "FullyQualifiedName~CodeQualityHooksTests|FullyQualifiedName~GitCommandTests" --verbosity minimal` completed **23/23 passed**, exit 0.
- `git diff --check 944bca7fa38cb5e973b04ce7829966cca351202b de596e2d0f0f70fbe35a72a2c8b822d2708941db` completed exit 0, no whitespace errors.
- Source inspection covers LSP session lifecycle, opt-in approval, after-write hook dispatch, `git_commit` commit-selection guards, tests, and author limitations. No reviewer source changes, merge, deployment or Wayfold edits.

## Blocking findings

1. **P1, active-lifecycle integration:** `Client/SignalR/Client.fs:774-788` prewarms only for retired `TaskWrite/ResumeAgent`; the actual Ready-to-Working `agent_inbox` dispatch is `TaskRead` (line 766). See `findings/prewarm-retired-activation-path.md`.
2. **P1, incomplete acceptance:** full client-suite pass, Wayfold cold/warm small/large parity, RSS, project/analyzer isolation, live restart recovery and positive exact-commit auto-fix/advisory tests are not evidenced. The author correctly reports that a 180-second full-suite attempt timed out. Focused 23/23 does not satisfy the canonical criteria. See `findings/acceptance-evidence-and-positive-hook-tests.md`.

## Additional issues

- **P2:** pre-commit approval is required for nonmatching path-only commits whenever *any* pre-commit hook is configured (`ClientShell.fs:265-273`).
- **P2:** after-write blocking format failure returns an operation error *after* the original write persisted, without communicating that state (`ProjectBrowser.fs:1206-1221`).
- See `findings/hook-scope-and-partial-write-outcomes.md`.

**Review boundary:** Source-level findings and the independently executed 23 focused tests only. The disposable-root smoke results described in the authored documentation were read, not independently repeated; no full-suite, Wayfold, daemon-memory, or production acceptance is claimed.