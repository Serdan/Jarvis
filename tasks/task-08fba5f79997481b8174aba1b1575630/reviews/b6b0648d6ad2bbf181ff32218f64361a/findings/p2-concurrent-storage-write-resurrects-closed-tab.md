# P2 — Out-of-order storage writes re-persist a removed tab registration

**Frozen commit:** `a09846f96c58b6ee82f88006d0c577be4790bab6`; owner worktree untouched.

## Root cause
`browser-extension/background.js` `persist()` (~92) calls `chrome.storage.local.set({registrations})` with no mutation sequencing. `register()` (~710-736) and the new `chrome.tabs.onRemoved -> unregister()` (~719-728, 813) can run concurrently and each independently await a storage write. `refreshRegistrations()` filters the live in-memory array, which prevents a stale refresh from overwriting **memory** but does not prevent delayed, previously issued storage writes from committing a stale registry **after** a newer removal's write.

## Independent deterministic reproduction
Reviewer-only isolated worktree `reviewer1-browser-tab-race-a098` adds test `closed tab must stay removed from storage despite an older pending registration write` in `browser-extension/tests/bridge.test.cjs`. Harness models Chrome's structured-clone/snapshot semantics at storage.set invocation and exposes a one-shot pending-write gate.

1. Register tab 17 (persisted `[17]`).
2. Start registering still-open tab 18; allow it to push its registration, then hold its `chrome.storage.local.set` snapshot `[17,18]` unresolved.
3. User closes tab 17; `onRemoved` calls `unregister(17)`, which persists `[18]` and advertises the current list.
4. Release the older registration write. It commits the stale captured `[17,18]` **after** the removal's `[18]` write.
5. Verify `getState` memory is only `[18]` but durable `chrome.storage.local` contains **`[17,18]`**, resurrecting the closed registration record.

Reviewer test command: `node --test --test-name-pattern 'closed tab must stay removed|transient permissions API rejection' browser-extension/tests/bridge.test.cjs`, result **FAIL as expected**: `Delayed earlier write must not resurrect ...`, actual `[17,18]` vs expected `[18]`. The owner branch's own 48 Node tests all pass and do not inject delayed storage commits.

## Required
Serialize writes to the registration storage key, preserving a monotonic latest-state order. Capture a fresh current registry snapshot when each serialized write executes, or otherwise enforce stale-writer rejection and a final rewrite after concurrent mutations. Ensure failed writes do not permanently poison the write queue, and preserve removal/advertise idempotency. Add a controlled out-of-order completion test using cloned data; a single-threaded mock that stores the live array by reference cannot detect this bug.

**Impact:** The feature's promised durable removal is not atomic with concurrent registration; an orphan record can persist across service-worker suspension/restart until startup cleanup catches it. In-memory filtering alone is insufficient.
