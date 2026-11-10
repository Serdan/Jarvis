# Independent review — general browser automation and rendered DOM snapshots

**Verdict: RequestChanges. Two P1 blockers, one P2 runtime/cancellation defect, one P2 end-to-end acceptance gap.**

Frozen source: `66d4e06566c3df5e8b771875f421815e19bc6770` on `refs/heads/feature/browser-bridge`, base `f7ff9012005ceb387b27d7f211f4106711b93716`, target `main`.

## Reviewed implementation

- Authenticated local loopback WebSocket bridge with origin/HMAC pairing; extension popup opt-in, registered windows/tabs, host permission, per-registration epoch/generation and URL authorization.
- New-tab quota and controlled close only for `agentCreated` registrations. Side effects on uncertain open/script execution are not blindly retried. Project-relative UTF-8 scripts use isolated user-script execution and bounded results.
- DOM capture uses live `outerHTML` from the authorized document or subtree, immediate/delay/selector/event waits with MutationObserver, visibility polling fallback and listener cleanup. Inline size checks and explicit file mode transfer frozen UTF-8 bytes via 32-KiB chunks with SHA-256 integrity, no implicit Git commit or page-content log.
- Existing docs and tests broadly cover authorization, navigation, timeouts, mutation/event waits and snapshot bounds. The implementation is substantial but not yet integrable.

## Blocking findings

1. **P1 — Several browser commands throw before dispatch.** `findings/p1-activity-match-failure.md`. The `AgentCommandInfo.detail` pattern omits `BrowserListWindowsCommand`, `BrowserCloseTabCommand`, and `BrowserRunInNewTabCommand`. F# emits FS0025. `receiveCommand` calls `Tui.StartActivity`→`invocation`→`detail` *before* permission checks or browser dispatch, so these operations fail with a match exception even on a valid paired tab/window.
2. **P1 — Frozen branch is incompatible with integrated Jarvis.** `findings/p1-protocol-drift-merge.md`. Branch protocol **3.22** vs current main **3.63**. Independent `git merge-tree --write-tree main 66d4e06` exits 1 with seven source conflicts in shared command/API/permission/runtime files. Full tests run against obsolete 3.22 branch, not the required combined protocol 3.63 code. Must forward-port and re-test.
3. **P2 — DOM file export has no overall deadline.** `findings/p2-unbounded-export.md`. `CaptureDom` controls the initial capture deadline but performs up to 256 independent chunk reads, each with a fresh 10-second timeout. A slow-but-progressing transfer could continue beyond the server's 10-minute forwarded-call timeout, write/rename a file afterward and leave the caller with an unknown outcome. Propagate a global operation deadline/cancellation and make post-timeout persistence impossible, or use an explicitly durable export job.
4. **P2 acceptance gap — no live paired extension E2E.** `findings/p2-live-browser-acceptance.md`. Author reports an Edge headless hydration fixture, but no extension-paired real Edge/Chromium end-to-end run through window registration, new tab, project script, event/selector wait, export and close; the task requires this before acceptance.

## Independently verified evidence

- Browser branch Client Release: **290 passed / 0 failed** — job `46565b58243d4329b48c18d1992375db`.
- Browser branch Server Release: **60 passed / 0 failed** — job `b8341c25fe2844afac0c087388bcf699`.
- Node browser extension tests: **35 passed / 0 failed** — job `71b40ad9e5494c5797d66ecc9f3c4cbd`.
- Frozen exact HEAD and clean worktree verified. `git diff --check base..HEAD` exits 2 due to trailing whitespace in documentation (minor, not independently blocking).
- `git merge-tree --write-tree main frozen` exits **1** (seven conflicts). Branch compatibility is not established.

No source changes, integration or deployment performed by reviewer. **Request changes** and a new frozen review after an up-to-date protocol 3.63 integration, formatter exhaustiveness tests, end-to-end deadline handling and paired-browser acceptance evidence.
