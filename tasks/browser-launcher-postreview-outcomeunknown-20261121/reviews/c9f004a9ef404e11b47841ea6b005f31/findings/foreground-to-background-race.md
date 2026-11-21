# P1 — Foreground-to-background transition bypasses focus proof

**Frozen source:** 64d94fc8115245b1b522c464858c2ee3d9c44b1b (parent 265a3f4e); `browser-extension/background.js` lines 361–365, 460–489; `browser-extension/tests/bridge.test.cjs` harness lines 159–187.

**Existing acceptance obligation:** background ChatGPT launch must not execute the one-shot prompt/Send until background-compatible input transport is confirmed, and must not activate another tab. Pre-send failures must fail closed rather than falling back to synthetic Send in an unfocused renderer.

**Reproduction (offline, source-bound, no file mutation):** construct a registered fresh-home ChatGPT tab. Set `tab.active=true` when `acquireBackgroundFocus` reads it. Move it to `active=false` immediately before the fixture's `userScripts.execute` (using `beforeNextUserScript`), simulating another tab gaining foreground during the intervening awaited permission/page-authority checks. The request returns a normal `result`, executes the user script, and `debuggerCalls.length===0`. This is possible because `acquireBackgroundFocus` returns null for the initially active tab; no visibility/focus probe runs when `releaseFocus===null`, and `authorizePageExecution` does not check focus.

**Impact:** normal Chrome tab changes can reintroduce the exact original background synthetic-input failure and lead to an uncertain post-Send state. The newly introduced focus-verification invariant is not preserved at the execution boundary.

**Bounded correction candidate:** use a focus mechanism/verification that covers execution irrespective of the initial `tab.active` snapshot; do not silently inject when the tab is no longer focused. A post-check should fail before script execution, not attempt a retry after side effects. Add a deterministic active→background race fixture; ensure any competing renderer focus state is checked at or inside the executed page boundary.

**Scope:** original requirement, not an added feature. No evidence of actual live duplicate sends; the fixture proves the gap in gating, not a production incident.