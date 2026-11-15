# [P1] Revoked host permission can still create an agent tab

**Frozen commit:** `ed4e2728194df850269fadc6d8b85ce1a465fb38`

**Location:** `browser-extension/background.js:627-649` (`openNewTab`).

The extension checks `chrome.permissions.contains({ origins: [...] })` **before** awaiting the final `validWindow` call. That call awaits `chrome.windows.get`. If the operator revokes the destination origin during that final lookup, the code checks connection, opt-in epoch, and window generation, but never checks the host permission again before `chrome.tabs.create`. A tab can therefore be created after its required site permission has been revoked.

**Independent reproduction:** Using the existing `bridge.test.cjs` harness without modifying any checkout file, hook the second `chrome.windows.get` via `h.onWindowQuery` to call `h.setHostPermissionGranted(false)`. With a valid pinned window and agent-tab opt-in, dispatch `openTab`. `windowQueries === 2`, but `h.createdTabs.length === 1` (expected **0**). The injected negative test fails: `AssertionError: 1 !== 0`.

**Required:** Recheck site permission after the final awaited window lookup, then revalidate the pinned socket, opt-in epoch and live window/registration state after the last authorization-related await, immediately before creating the tab. Add a deterministic negative-space test for this ordering. Maintain no blind retry after `tabs.create`.