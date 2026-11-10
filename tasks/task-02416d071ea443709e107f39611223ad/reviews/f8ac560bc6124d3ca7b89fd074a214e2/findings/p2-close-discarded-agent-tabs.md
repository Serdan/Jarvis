# P2 — Discarded/frozen Jarvis-created tabs cannot be closed, defeating lifecycle cleanup

**Frozen source:** `00cfe57589436cacf573d2b512f295c432405674`, `feature/browser-bridge`.

## Root cause

`browser-extension/background.js:118–154` implements `assess(reg)`, which validates session epoch, registered window, current URL/origin and window membership; if the tab is **discarded** it returns `"discarded"` (line 148), and if **frozen** `"frozen"` (line 149), before reporting `"ready"`. These states are ordinary browser lifecycle states and do not invalidate `agentCreated`, tab ID, origin or parent window.

`closeCreatedTab` at `browser-extension/background.js:602–632` correctly rejects manually registered tabs, but then unconditionally requires `availability === "ready" || availability === "loading"` (lines 617–619); for an otherwise valid created tab in the above states it returns **`RegistrationStale`** without ever invoking `chrome.tabs.remove`. Closing an owned tab **does not require its page to be awake**. The task explicitly requires handling discarded tabs and safe controlled close of created tabs.

`Client/Modules/BrowserBridge.fs:693–712` compounds this in the `runInNewTab` readiness failure path: after bounded readiness polling, it invokes `bridge.CloseTab(registrationId)` when `closeAfterExecution` is true but silently discards any error. A created tab that becomes discarded while waiting therefore remains orphaned despite requested cleanup.

## Independent repro

I used an isolated temporary copy of the extension's existing Node mock harness (not a source-tree change) to add a negative-space test: pair, register window, opt in to created tabs, create an approved `https://example.test/chat/new` tab, set its reported URL/status to same-origin complete but `tab.discarded=true`, and request `closeTab` for the exact created registration/generation. The expected safe result is `tabClosed` and removal; the actual response is **`kind: "error"`**. Independent job `c2dccfdd68184ebe8f3be9b72be0cfc2` failed this one added regression, while the baseline 20 tests passed.

## Required change

Separate **tab ownership/identity safety** from **page execution readiness**. For `closeTab`, revalidate the registration as created by Jarvis, generation/session, stable tab ID, permitted URL/origin and unchanged parent window, but allow safe closing when the tab is `discarded` or `frozen`, without waking/activating or executing code. Continue to fail closed for stale generation, changed origin/window, registration revoked, manual/user-owned tabs or missing tab. Add regressions for both discarded and frozen created tabs, plus manual-tab refusal and cross-origin navigation denial. Composite cleanup must report or preserve errors rather than quietly claiming success.

**Severity P2** — legitimate browser resource cannot be cleaned up through the advertised lifecycle API, with potential orphaned tabs on bounded composite execution. No user data exfiltration or window closure claimed.
