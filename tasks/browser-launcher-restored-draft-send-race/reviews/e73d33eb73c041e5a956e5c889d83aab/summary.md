# Independent frozen review — restored-draft cleanup correction

**Review:** `e73d33eb73c041e5a956e5c889d83aab`  
**Reviewer:** Reviewer 1  
**Frozen source SHA:** `bc9e454eadd27ff0d8c75381784fa1ccdc586db8`  
**Base:** `90ed868d04524d0483ccd979cffb5029d8cf7027`  
**Previous correction request:** `31ee93ed126648ed90d7a8fd2bd71243` (seq3112, SHA `7571988db320803adc73445a5c691a901e980b8e`)

## Verdict: CHANGES REQUESTED — P1; do not integrate frozen SHA

### Source inspection and improvements

Inspected the eight-file full branch diff, the exact source delta since seq3112, the F# bridge's definitively failed launch and error handling, extension registration/generation/window/origin pinning, its new live fresh-home/zero-turn probe and final `chrome.tabs.remove`, the launcher disposal proof contract and deterministic tests.

The correction genuinely adds `requireFreshHomeNoTurns=true` for definite failure; the extension probes live committed route and user-turn markers; F# maps `CleanupSafetyChanged` and `CleanupSafetyUnverified` to `cleanup=retained`. Tests cover late **committed** `/c/` navigation, rendered user turns and bubbles, unchanged-home closure, initial `about:blank` handling, no retry after uncertain submission, and no manual-tab closure.

### Blocking finding

**F01 / P1: pending same-origin conversation navigation still destroyed.** See `findings/pending-navigation-disposal-race.md` for exact source, reproduction and required correction.

At `browser-extension/background.js:816-840`, the guard checks `current.url===reg.boundUrl` and executes a probe of the *committed* home document; it never excludes `current.pendingUrl='https://chatgpt.com/c/…'` while `status='loading'`. The `await chrome.userScripts.execute` itself allows a pending conversation navigation to arise after the earlier tab query. `assess` admits loading same-origin tabs, the old home DOM still reports ready/zero turns, and the synchronous checks at 843-855 only verify authority, not pending destination. The extension removes the tab and reports `tabClosed` despite the in-flight conversation.

Independently reproduced twice **without editing tracked source** by in-memory `Module._compile` additions to the frozen `bridge.test.cjs` harness: (a) pending `/c/` URL already present before close; (b) `beforeNextUserScript` starts pending `/c/` navigation **during** asynchronous cleanup probe. Both negative tests deliberately fail preservation: `removedTabs` includes the tab and response has `kind=tabClosed`. Each run: 60 existing tests pass, one added safety assertion fails (exit 1). This is not merely a theoretical race.

**Required:** fail closed on pending noninitial navigation, re-check navigation after the awaited probe, retain on uncertainty, preserve verified `about:blank` exception; add deterministic regression covering the pending URL before close and arising during the probe, plus bridge retention/no-resend assertion. Submit a new exact frozen SHA.

### Independent validation

- Node launcher + extension: **94/94 pass** (`node --test browser-extension/tests/chatgpt-launcher.test.cjs browser-extension/tests/bridge.test.cjs`).
- .NET 11 Release BrowserBridgeTests: **41/41 pass** (`dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~BrowserBridgeTests --logger console;verbosity=normal`). Unrelated FS0760/FS3369 warnings.
- `git diff --check 90ed868d..bc9e454e`: exit 0; reviewed checkout clean at exact SHA.
- No live authenticated ChatGPT UI execution, source edits, merge, or deployment.