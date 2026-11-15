# P1 — Browser can create a new tab after the operator disables agent-created tabs

**Frozen source:** `39cc5d65ca88867df7903626606f8e4ad1ad0421` (review `6ea138e944204761ab6bfa717fce7e64`). **New finding**; the prior two P1 findings are materially addressed in this follow-up.

## Evidence

`browser-extension/background.js:625–641` awaits an origin-permission query, then checks `socket`, `connected`, `connectionId`, and `allowAgentCreatedTabs` at lines 627–628. It subsequently awaits `validWindow(message.windowRegistration)` at line 632. The identity/generation checks at lines 633–637 do **not** repeat the opt-in/connection checks, and line 641 calls the irreversible `chrome.tabs.create`.

The extension popup can call `setAgentCreatedTabs({allowed:false})` during that final asynchronous `validWindow` call. That explicitly revokes the opt-in, but the already-running `openNewTab` continues and creates a tab.

## Deterministic independent reproduction

Against the **clean frozen worktree** `worker5-browser-launcher` at `39cc5d65...`, I reused the Node bridge test harness in memory (no source changes), hooked its second `chrome.windows.get` call during a dispatched `openTab`, and in that hook invoked `h.message("setAgentCreatedTabs", {allowed:false})`. The hook represents the window query awaited by the final `validWindow`.

Observed, with Node exit code 0:

```json
{"revocation":{"ok":true},"windowCalls":2,"allowAgentCreatedTabs":false,"createdTabs":1,
 "openResponse":{"kind":"tabOpened","tab":{"agentCreated":true,"availability":"needsReauthorization"}}}
```

The created tab exists despite successful opt-in revocation *before* `tabs.create`. The returned `needsReauthorization` merely prevents later use; it does not undo the unauthorized creation.

## Required fix and regression

After the final awaited `validWindow` lookup, and immediately before `chrome.tabs.create`, revalidate `socket === requestSocket`, `connected`, `connectionId === requestConnection`, and `allowAgentCreatedTabs` along with immutable window ID/generation. Fail closed with a definitive pre-creation error on revocation; never report `tabOpened` or automatically retry. Add a deterministic extension regression where the opt-in is switched off during that last window lookup and assert `createdTabs.length === 0`; also cover disconnect/unpair at the same boundary if permitted by the intended epoch semantics.

**Impact:** bypasses a user-controlled authorization switch in the exact browser-launch path under review. **Severity: P1 / RequestChanges.**