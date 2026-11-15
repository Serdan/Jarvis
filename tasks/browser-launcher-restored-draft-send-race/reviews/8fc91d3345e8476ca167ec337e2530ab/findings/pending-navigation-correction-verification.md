# F01 correction verification — pending same-origin navigation

**Classification:** Resolved P1 from prior frozen review `e73d33eb73c041e5a956e5c889d83aab` (ChangesRequested seq3148). **No new blocking finding.**

**Exact source:** `53188653f56f28433b44d847c98199b2cfb6babd` (base `90ed868d04524d0483ccd979cffb5029d8cf7027`); corrected since prior frozen SHA `bc9e454eadd27ff0d8c75381784fa1ccdc586db8`.

## Defect and correction
Prior `closeCreatedTab` in `browser-extension/background.js` admitted committed `https://chatgpt.com/` even when Chrome reported a pending same-origin `/c/` navigation and the old document's user-turn probe still returned zero. This destroyed an in-flight conversation. The new code, on `requireFreshHomeNoTurns=true`, rejects any noninitial tab with `status !== "complete"` or a nonempty `pendingUrl`, besides wrong committed URL, discarded/frozen state or window mismatch. After awaiting `chrome.userScripts.execute`, it re-reads a fresh `chrome.tabs.get` snapshot and repeats the committed/pending/status/window checks. Missing or failed reinspection retains the tab. The only bypass is the narrowly matched initial `about:blank`, loading with `pendingUrl === boundUrl`. Existing synchronous pinned-registration, generation, connection and window checks still precede `chrome.tabs.remove`.

## Deterministic tests
- `browser-extension/tests/bridge.test.cjs` models `tabs.get` as a **snapshot** (not a mutable tab reference), and introduces before/after-user-script hooks. Tests assert no `tabs.remove` when `/c/` navigation is pending at admission, during the asynchronous DOM probe, or after the probe returns the old home document. A loading home with no visible pending URL and a contradictory complete/pending URL are also denied.
- The post-probe `tabs.get` rejection retains the tab with `CleanupSafetyUnverified`. Verified fresh zero-turn home still closes; exact initial `about:blank` case still closes; manual and unrelated tabs survive. The cleanup path does not replay `executeJs`.
- `Client/Modules/BrowserBridge.fs` sends `requireFreshHomeNoTurns=true` for definitive failed-launch disposal; maps `CleanupSafetyChanged` and `CleanupSafetyUnverified` to `cleanup=retained`; rejects other cleanup failures. `Client.Tests/BrowserBridgeTests.fs` composes guarded denial across `notSubmitted` and `uiIncompatible`, checks retained registration and no additional dispatch, and preserves uncertain-send retention.
- `browser-scripts/chatgpt/launchAgent.js` reports disposal proof only without a Send attempt, on the exact new-chat home with no existing user turns/bubbles; its restored-draft clearing waits for Send disable before one-click submission. Contradictory `ExistingConversation`/`UnexpectedStartUrl` reasons are denied in bridge even with a truthy page proof.

## Independent execution and provenance
- `node --test browser-extension/tests/chatgpt-launcher.test.cjs browser-extension/tests/bridge.test.cjs`: **100 tests passed, 0 failed**, exit 0.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~BrowserBridgeTests --logger console;verbosity=normal`: **43/43 passed**, exit 0, net11.0. Non-blocking FS0760 and FS3369 compiler warnings outside modified paths.
- `git diff --check 90ed868d..53188653`: exit 0; `git status --short --branch` showed no file changes; `git rev-parse HEAD` confirmed exact `53188653f56f28433b44d847c98199b2cfb6babd`.

**Boundary:** This is deterministic source/bridge validation, not live ChatGPT UI evidence. Chrome APIs and UI state are inherently asynchronous; the review accepts the specific scoped admission/probe navigation defenses, not a claim of transactional browser-wide state locking. No merge, deploy or live browser send was performed.