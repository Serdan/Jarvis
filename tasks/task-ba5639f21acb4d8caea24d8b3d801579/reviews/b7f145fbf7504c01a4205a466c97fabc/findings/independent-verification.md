# Independent frozen review — per-project review-count UI

**Reviewer:** Reviewer 2
**Proposed verdict:** Accepted
**Exact source:** `5e9cc791b5093bba221bdbd18afab77cb8d7fe83`
**Original task:** `task-ba5639f21acb4d8caea24d8b3d801579`
**Frozen review:** `b7f145fbf7504c01a4205a466c97fabc`
**Target:** `refs/heads/main` with required baseline `8ed9671543f05073f7d453728b50772138dd87f2`

## Frozen source and accepted ancestry

- Independently ran `git show -s --format=%H%n%P%n%B` at exact submitted commit. It is a two-parent merge: first parent `8ed9671543f05073f7d453728b50772138dd87f2`, second parent `1463ccbe69b8989a5dd5b9503e054622feebdaa1`. Both independent `git merge-base --is-ancestor` checks exited zero.
- Verified canonical prior task-bound review `501eda16b8bf46538bf3efd9ea8cd70c` is **Accepted** at decision sequence 3391 for exact `1463ccbe69b8989a5dd5b9503e054622feebdaa1`.
- `git diff 8ed96715..5e9cc791 --stat` changes only `Client/Modules/AgentDashboard.fs`, `AgentView.fs`, `ConsoleTui.fs`, and their three Client.Tests counterparts: 166 insertions, 34 deletions. `git diff --check` exited zero. Comparisons `1463ccbe..5e9cc791` on all six relevant files show only 756 additive lines in upstream `Client.Tests/ReviewWorkflowTests.fs`; the other five previously accepted files are byte-identical. Frozen source worktree reported clean.

## Semantics inspected in source

1. `AgentDashboard.projectSnapshot` reads canonical `tasks/.../review.json` current records from `LedgerStore.readDashboardRecordsWithOidsAt`, rather than review-document, historical-event, or snapshot counts. The path scan includes task-bound reviews and independent `tasks/_reviews`. The exact fold counts `Unclaimed` in both numerator and denominator, `Claimed` in denominator only, and excludes terminal states.
2. Stable group identity consists of ProjectId, LedgerId and Epoch; aliases are grouped before the existing refresh fetch. Same identity is counted once, while identically named distinct ledgers remain distinct. The cached tuple is keyed by identity and verified head, and conflicting same-head U/T values or differing heads fail closed to unavailable rather than silently picking an alias.
3. `AgentView` and `ConsoleTui` display **unclaimed/total** on per-project lines and optional aggregate, in full or compact notation. Narrow terminals wrap counters into bounded lines with project label; long same-named projects retain disambiguating ledger suffix when possible. Stale, unavailable and temporarily unavailable statuses remain visually distinguishable from verified `0/0`. Project filtering shows matching project summaries while aggregate remains explicitly total across available projects.
4. Existing refresh paths are reused; no new network polling or source-repository writes are introduced. The review-state transition fixture exercises claim, release, decide, withdraw and independent-review counts on refreshed canonical ledger snapshots.

## Independently executed local .NET 11 evidence

- `dotnet --version`: **11.0.100**.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter 'FullyQualifiedName~ProjectReviewQueue|FullyQualifiedName~reviewCountsOfAliases|FullyQualifiedName~projectDashboardDeduplicatesSharedLedgers|FullyQualifiedName~AgentViewTests' --logger 'console;verbosity=normal'`: **35/35 passed**, exit zero. Compiled the pinned Client and Client.Tests Release projects.
- `dotnet build Jarvis.slnx -c Release --no-restore --nologo -v minimal`: **passed**, 0 errors, 1 warning (`FS0760` at `Client/Program.fs:188`, outside the six-file diff).
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~TerminalLayoutTests|FullyQualifiedName~TerminalFrameTests|FullyQualifiedName~AgentDashboardCacheTests' --logger 'console;verbosity=normal'`: **32/32 passed**, exit zero. Two named tests overlap first filter; 67 successful executions in total.
- Test scope was deliberately bounded, not a claim that the full test suite completed; no GitHub Actions, Codex, main push, Scheduler activation, deployment, or migration was performed.

## Decision and limits

**Accepted.** No blocking errors found in the exact frozen source under the requested ancestry, semantics, and locally bounded Release evidence. This decision authorizes neither integration nor production activation and does not assert unseen full-suite or live-terminal results.
