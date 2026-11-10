# Independent re-review — browser bridge protocol 3.63 integration and DOM export deadline

**Verdict: RequestChanges.** Previous code findings largely fixed, but **one new P1 server authorization/dispatch failure** and **one outstanding P2 live-browser acceptance requirement** prevent acceptance.

**Frozen commit:** `b9dc25bc5340aa8cb043a75d4e47e69d0c3e9679` on `refs/heads/feature/browser-bridge`; reviewed base `28fd0c3d2fa887a8cd7289e07cb65b476de351f9`, target `main`.
**Prior rejected review:** `893bbba742f14b43939d7d531429facc` of `66d4e06`.

## Previously blocking issues: fixed in source

- **Client activity matcher:** `AgentCommandInfo.detail` now handles `BrowserListWindowsCommand`, `BrowserCloseTabCommand`, and `BrowserRunInNewTabCommand` exhaustively. Dedicated `everyBrowserCommandHasSafeActivityDetail` regression traverses all 12 browser command cases.
- **Protocol drift/merge:** merged current protocol **3.63** main into branch (merge `a519681`), updated typed command dispatch, server MCP routes and `agentId`/optional `worktreeName` signatures. Current `git merge-tree --write-tree main b9dc25b` succeeds with no conflicts.
- **Snapshot deadline:** `BrowserBridge.Service.CaptureDom` uses a single `Stopwatch.GetTimestamp` monotonic remaining-time function across initial response, chunk transfers, checksum, flush and final rename; per-chunk wait capped by the *same* deadline. New slow-chunk regression confirms `SnapshotDeadlineExceeded` with no temp/final file, and prior checksum test confirms fail-closed cleanup. This fixes the earlier unbounded 256×10s wait concern.
- JS extension permission/registration, same-tab identity and frozen chunk/hashed export paths remain in place. No source or tab side-effects were performed as reviewer.

## Remaining findings

**P1: Missing MCP authorization match for both DOM tools** — `findings/p1-dom-mcp-scope-match.md`.

`McpToolHelpers.requiredScope` in `Server/McpTools.fs:455–513` omits `BrowserCaptureDomCommand _` and `BrowserExportDomCommand _`; `send` invokes it before client dispatch. Both public MCP capture and export tools therefore throw `MatchFailureException` before reaching the browser. Independent build emits **FS0025 at Server/McpTools.fs(456,15)** with `BrowserCaptureDomCommand` example. Author's new activity-detail test only covers the *client* match; no routed public Server tests cover these two paths. Fix exhaustive authorization, ensure OAuth scope enforcement matches declared process/workspace permissions, and test both tools through public MCP send, including insufficient-scope rejection.

**P2: Live paired Chromium/Edge extension acceptance not performed** — `findings/p2-live-paired-browser-e2e.md`.

Author explicitly acknowledges no live paired extension + registered window/tab + project script + DOM waits/export/close fixture. Headless Edge hydration fixture and in-memory/mock browser bridge tests do not prove the extension/browser integration. This is an explicit task acceptance gate and requires operator-authorized real test with redacted evidence.

## Independent tests and source verification

- Merged Client Release tests **572 passed, 0 failed** — job `7160945be1c24221b4d802c9f5f6c283`.
- Merged Server Release tests **127 passed, 0 failed** — job `1367a741e017446193abf7966bb1953e` (includes new **FS0025** warning on security dispatcher).
- Browser extension Node tests **35 passed, 0 failed** — job `b8a9bc8fc7904c9e82fe99b720d51c0b`.
- **734 tests passed in total**, but none exercises public server capture/export authorization match.
- Frozen HEAD verified exact; worktree clean; `git diff --check 28fd0c3..b9dc25b` clean.
- Current main `git merge-tree --write-tree main b9dc25b` exits 0, no conflicts.
- Review saved without source edits, integration or deployment.

**Decision:** RequestChanges. Implement server authorization matcher and integration tests, fulfill live extension acceptance with operator consent, submit a new frozen review. Do not integrate or deploy this branch.
