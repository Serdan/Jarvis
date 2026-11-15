# [P1] Queued OpenTab can cross a disable/re-enable authorization boundary

**Frozen commit:** `ed4e2728194df850269fadc6d8b85ce1a465fb38`

**Locations:** `browser-extension/background.js:247-250` and `:588-592`, `:637-640`.

The WebSocket handler queues `openNewTab(message)` on `openQueue`, but the new `requestAuthorizationEpoch` is captured only **when the queued operation begins executing**. A request received under the old opt-in can sit behind an earlier slow operation. The operator can disable then re-enable agent-created tabs while the request is waiting. When the queue releases, that stale request captures the **new** epoch and passes the final generation check; it creates a tab despite having originated before revocation.

**Independent reproduction:** In the existing Node harness, hold the first `openTab` at the second `chrome.windows.get` using `h.onWindowQuery` and an unresolved promise; enqueue a second `openTab` on the same socket; call `setAgentCreatedTabs(false)` and `setAgentCreatedTabs(true)`; release the first lookup. First request is rejected, but the **second creates one tab**. Independent negative test expected `h.createdTabs.length === 0`, observed `1`, `AssertionError: 1 !== 0`. The test was loaded in memory and did not edit the worktree.

**Required:** Capture the immutable socket/connection identity and authorization generation at WebSocket **receipt/enqueue time**, passing them to `openNewTab`, and reject stale queued requests before any side effect. Retain the immediate pre-`tabs.create` checks. Add a regression that holds the queue across disable/re-enable.