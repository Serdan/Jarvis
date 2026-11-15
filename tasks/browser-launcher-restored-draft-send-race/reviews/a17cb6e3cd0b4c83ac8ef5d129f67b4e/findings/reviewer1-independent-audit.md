# Reviewer 1 — independent audit of frozen combined browser-launcher source

**Request:** a17cb6e3cd0b4c83ac8ef5d129f67b4e  
**Frozen review/head:** `9a5750ab93bb1ac26f957f319b929744981ede61`  
**Scope:** combined integration candidate for `browser-launcher-restored-draft-send-race`; submitted by Worker 7.  
**Recommendation:** **Accepted**. No blocking defect found in the inspected frozen source or relevant invariants.

## 1. Immutable identity and integration provenance

- `git show --no-patch --format=%H%n%P%n%s 9a5750ab...` independently returned a clean two-parent merge: first parent `7b589173135178a634495146675dad5db647e5f8` (the pinned `origin/main`), second parent `53188653f56f28433b44d847c98199b2cfb6babd` (previously Accepted launcher source).
- Both `git merge-base --is-ancestor` checks exited 0. Local `refs/remotes/origin/main` resolved to `7b589173...`; review ref and candidate worktree resolved to `9a5750ab...` at review.
- `git diff --exit-code 53188653 9a5750ab --` all eight launcher/bridge/test/doc paths exited 0: **byte-for-byte preserved accepted implementation**, not an unreviewed conflict resolution.
- `git diff 7b589173 9a5750ab --name-status` identified exactly these eight files: `Client.Tests/BrowserBridgeTests.fs`, `Client/Modules/BrowserBridge.fs`, `browser-extension/README.md`, `browser-extension/background.js`, `browser-extension/tests/bridge.test.cjs`, `browser-extension/tests/chatgpt-launcher.test.cjs`, `browser-scripts/chatgpt/launchAgent.js`, `docs/ChatGPTAgentLauncher.md`. No unrelated paths. `git diff --check` returned 0; `git status --porcelain=v1` empty before and after tests.

## 2. Independent source/invariant inspection

- **Restored-draft stabilization, no old prompt submission:** `browser-scripts/chatgpt/launchAgent.js:90-170` selects one composer, clears restored draft through the editor, waits for three inactive-Send observations within a bounded deadline, and rechecks empty text, editor identity, zero user turns/bubbles, and exact home route before insertion/click. Persistent enabled Send, draft reappearance, route change, or new turn exits before Send. `launchAgent.js:171-229` records `sendAttempted` before exactly one click, requires new-turn/matching-bubble or constrained route evidence, waits for server-issued conversation UUID, and classifies missing post-click evidence as uncertain without retry.
- **Definitive versus uncertain cleanup:** `Client/Modules/BrowserBridge.fs:748-764,828-925` acts on an explicit `safeToDisposeCreatedTab=true` proof only for definitive `notSubmitted` or `uiIncompatible`; contradictory `ExistingConversation`/`UnexpectedStartUrl` and absent/malformed proofs retain the tab. `outcomeUnknown` never enters failed-launch cleanup. Guarded close outcomes report `closed`, `retained`, or `cleanupFailed`; `uiIncompatible` retains sanitized stage/selector diagnostics. Not-ready handling also uses the guarded close independently of `closeAfterExecution`.
- **Extension-side live proof/ownership fence:** `browser-extension/background.js:787-875` requires exact agent-created registration and generation, unchanged socket/session and registered parent, and checks Chrome's committed URL, `status`, `pendingUrl`, discarded/frozen flags, plus a fresh top-frame zero-user-turn DOM probe. It re-reads Chrome tab state *after* the asynchronous probe, thereby rejecting pending same-origin `/c/` navigation even when the previous committed document still shows home. The narrowly verified initial `about:blank` loading-with-original-`pendingUrl` case is a pre-injection disposal exception. Manual/unrelated tabs are not targeted.
- **Relevant negative-space tests:** `browser-extension/tests/bridge.test.cjs` exercises late conversation route, legacy user turns and new bubble, pending `/c/` before/during/after DOM probe, failed post-probe tab query, not-ready generic tabs, fresh home success, initial blank, and unrelated tabs. `Client.Tests/BrowserBridgeTests.fs` composes bridge/status/guard errors, malformed or contradictory disposal proof, no replay, cleanup failure reporting, and retained unrelated registrations. `browser-extension/tests/chatgpt-launcher.test.cjs` covers delayed Send disable, permanent enabled Send, draft replacement, route/turn changes, one-click evidence, and no retry after attempted Send.

## 3. Independently executed validation at the exact commit

- `node --test browser-extension/tests/bridge.test.cjs browser-extension/tests/chatgpt-launcher.test.cjs` returned **100 passed, 0 failed**, exit 0 (Jarvis local command job `485d304190fa4e0d97c2c70c1df014de`).
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --nologo --filter FullyQualifiedName~BrowserBridgeTests` rebuilt `Common`, `Client`, `Client.Tests` for **.NET 11.0**, then returned **43 passed, 0 failed**, exit 0 (job `58a1e2b03647463b9cd69c7852157708`). One unrelated FS0760 constructor-style warning at `Client/Program.fs:188`; no failing diagnostics.
- No GitHub Actions, deployment, merge, or source modification was performed by Reviewer 1.

## 4. Findings and evidence limits

**Blocking findings:** None in this frozen integration scope. Both accepted launcher behavior and the pinned main baseline coexist without changed accepted bytes; the expected local regression coverage passes.

**Non-blocking evidence boundaries:** These are deterministic local component/composed tests, not a live ChatGPT UI launch. The full `Jarvis.slnx` .NET Release suite was **not** independently rerun by this reviewer; Worker 7's submission cited an in-progress Client.Tests suite, so this review does **not** claim a full-suite pass. Chrome's final tab-state read and `tabs.remove` are separate APIs, so there remains an inherent sub-operation TOCTOU window, not evidence of a changed source regression or grounds to claim an atomic close. Any future integration/deployment or main advancement is Lead's responsibility, not this verdict.

**Decision recommendation:** Accept **only frozen SHA** `9a5750ab93bb1ac26f957f319b929744981ede61`. Do not substitute an advancing source branch or infer deployment authorization from review acceptance.
