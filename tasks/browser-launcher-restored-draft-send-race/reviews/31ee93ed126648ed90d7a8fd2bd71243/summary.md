# Browser launcher restored-draft cleanup — frozen independent re-review

**Request:** `31ee93ed126648ed90d7a8fd2bd71243`  
**Reviewer:** Reviewer 2  
**Frozen HEAD:** `7571988db320803adc73445a5c691a901e980b8e` (worktree HEAD reconfirmed)  
**Base:** `90ed868d04524d0483ccd979cffb5029d8cf7027`  
**VERDICT: CHANGES REQUESTED. Do not integrate this SHA.**

## Reviewed scope
Inspected the six-file exact diff; `launchAgent.js` return paths and ProseMirror stability; `BrowserBridge.fs` readiness/execution/cleanup; `Client/SignalR/Client.fs` default close flag; extension `background.js` ownership, same-origin navigation and final `chrome.tabs.remove`; the previous rejected review (`dd6a80851f5a40b5a0fd68d681be4332`), regression suites, and updated docs.

The previously reported result-time **F01** cases (`ExistingConversation` and `UnexpectedStartUrl`) now retain tabs, including contradictory proof=true. The previous **F02** default-false pre-injection readiness leak is corrected: the bridge attempts registration- and generation-pinned closure after no-injection timeouts, surfacing `cleanupFailed`; uncertain post-injection paths remain retained. Restored-draft stabilization is bounded and does not resend after a possible click.

## One blocking finding
**F01 / P1: Cleanup can act on a stale disposal proof after a same-origin conversation appears.** See `findings/postresult-disposal-race.md`. `safeToDisposeCreatedTab:true` is evaluated by the page only at response time (`launchAgent.js:10-20,58-63`). Later, `BrowserBridge.fs:833-850,895-904` sends an asynchronous `closeTab`; `background.js:130-182,767-810` checks exact ownership/generation and origin, but not the route or user-turn state when removing the tab. Thus a new `/c/<uuid>` route or user turn arising after the page result and before the close can be destroyed. The new composed tests do not model that late transition. Require cleanup-time fresh-home/no-turn revalidation and a deterministic test of a true proof followed by late same-origin conversation/turn before removal.

## Independent validation
- `node --test browser-extension/tests/chatgpt-launcher.test.cjs`: **34/34 passed**.
- `node --test browser-extension/tests/bridge.test.cjs`: **53/53 passed**.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~BrowserBridgeTests --logger console;verbosity=normal`: **38/38 passed**, .NET 11 Release; includes 30-second readiness cleanup. FS0760/FS3369 warnings are unrelated.
- `git diff --check 90ed868d..7571988d`: **exit 0**; worktree exact SHA and clean.
- No live authenticated UI submission was performed. Worker4 also reported BrowserSchedulesTests 19/19, not independently repeated in this review.

**Next:** Fix the late-transition close race, extend composed extension/bridge tests, and submit a new exact frozen review. No merge or deploy of this rejected commit.
