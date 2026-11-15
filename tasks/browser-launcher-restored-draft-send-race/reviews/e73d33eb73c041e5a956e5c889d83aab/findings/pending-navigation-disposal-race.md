# F01 — P1: Pending same-origin conversation navigation is still destroyed by failed-launch cleanup

**Review:** e73d33eb73c041e5a956e5c889d83aab  
**Frozen SHA:** bc9e454eadd27ff0d8c75381784fa1ccdc586db8  
**Prior rejected SHA:** 7571988db320803adc73445a5c691a901e980b8e (ChangesRequested seq3112)  
**Disposition:** Blocking — do not integrate this exact SHA.

## Exact invariant and source

The new cross-layer guard is useful but incomplete. `Client/Modules/BrowserBridge.fs:751-760` now requests `requireFreshHomeNoTurns=true` and maps explicit safety denials to `cleanup=retained`. However:

- `browser-extension/background.js:149-181,802-806`: `assess(reg)` accepts a still-loading, agent-created tab on the allowed origin; `closeCreatedTab` admits `loading`.
- `background.js:816-840`: after `chrome.tabs.get`, the fresh-home branch checks `current.url === reg.boundUrl`, not `current.pendingUrl` or a pending navigation. It then awaits `chrome.userScripts.execute`. The injected probe sees the **currently committed document's** `location.href`, readyState and user-turn markers; it cannot attest that Chrome has not already begun another same-origin navigation.
- `background.js:843-855`: checks owner, registration generation, window and connection, but does **not** revalidate the tab's pending navigation after the asynchronous page probe; then calls `chrome.tabs.remove`.

Thus the prior page result's historical disposal proof is not made safe against a pending, not-yet-committed `/c/...` navigation. The extension may have a committed home document that looks empty while Chrome's tab has `status='loading'` and `pendingUrl='https://chatgpt.com/c/late-conversation'`. The current checks still return `tabClosed`.

## Deterministic independent reproduction (no tracked source edits)

Using the frozen `browser-extension/tests/bridge.test.cjs` harness, appended a temporary in-memory `node:test` case via `Module._compile` (nothing written to the worktree):

1. Create an exact pinned agent-created `https://chatgpt.com/` tab using `createdTabForCloseRegression`; the hypothetical page result already asserted `safeToDisposeCreatedTab: true`.
2. While `closeTab` is awaiting `chrome.userScripts.execute`, use the harness's `beforeNextUserScript` hook to set `tab.pendingUrl='https://chatgpt.com/c/late-conversation'` and `tab.status='loading'`, leaving committed `tab.url` as `https://chatgpt.com/` and prior document ready/empty.
3. Invoke `close('late-pending-nav', { requireFreshHomeNoTurns: true })`; assert no `tabs.remove` is issued.

**Observed failure:** node exits 1; 60 existing bridge cases pass and the extra negative case fails because `h.removedTabs.includes(tab.id)` is **true** and the response explicitly contains `kind:'tabClosed'`. A second independent negative case with `pendingUrl` present before starting cleanup fails identically. This is a concrete deterministic race, not merely a missing test.

## Required correction

Fail closed whenever a created tab already has a pending non-initial navigation, especially a same-origin `/c/...`, even if its committed `url` and DOM still look like empty home. After the awaited probe, re-check tab navigation state, including `pendingUrl` and any ownership/document transition evidence, before removing; do not treat an old home document as authority for an in-flight destination. Preserve only the strictly verified `about:blank` initial-navigation exception, and retain tabs on unavailable or contradictory inspection.

Add a deterministic composed regression for the **post-result** interval when a same-origin `/c/` pending navigation starts during the asynchronous cleanup probe; assert no `tabs.remove`, bridge `cleanup=retained`, no resend, and safe still-fresh disposal. A second case with pending navigation present on close admission prevents narrower fixes. The existing tests for already-committed `/c/` URL and rendered user turns are insufficient.

**Scope note:** This finding concerns cleanup of a failed ChatGPT bootstrap, not an operator-requested close after confirmed successful send. No live authenticated browser navigation was attempted.