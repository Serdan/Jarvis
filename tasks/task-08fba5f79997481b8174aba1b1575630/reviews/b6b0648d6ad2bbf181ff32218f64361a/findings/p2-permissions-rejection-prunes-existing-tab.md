# P2 — Transient permission API failure is mistaken for tab disappearance

**Frozen commit:** `a09846f96c58b6ee82f88006d0c577be4790bab6`; owner worktree unchanged.

## Root cause
In `browser-extension/background.js`, `assess(reg)` now correctly distinguishes missing tabs by first running `chrome.tabs.get(reg.tabId)` (~120), catching a tab-lookup rejection as `"missing"`. But the *second broad try/catch* around URL, origin, permissions and loading checks (~129-165) **also returns `"missing"` for every exception**. In particular, if `chrome.permissions.contains({origins:[...]})` rejects for a transient Chrome API error, the tab still exists yet `assess` reports `"missing"`. The new `refreshRegistrations` (which prunes every `"missing"` result) then removes its registration from memory and durable `chrome.storage.local`; the paired client is advertised that the authorization has disappeared.

The distinction is material: a temporary inability to establish *authorization* should fail closed for execution, with an existing registration retained as `needsReauthorization`, not be interpreted as proven tab closure.

## Independent deterministic reproduction
Reviewer-only test `transient permissions API rejection does not erase an existing tab registration` in isolated `reviewer1-browser-tab-race-a098` registers tab 17, verifies `chrome.tabs.get(17)` continues to succeed, injects one thrown `chrome.permissions.contains`, and calls `getState`. Expected: existing tab registration still listed/persisted (unavailable for execution until authorization can be verified). Actual: **0 registrations**, and registry erased. Reviewer-only `node --test --test-name-pattern 'closed tab must stay removed|transient permissions API rejection' browser-extension/tests/bridge.test.cjs` fails exactly at `Only a verified missing tab may be permanently unregistered: 0 !== 1`. Owner's 48 Node tests pass because they only simulate permissions true/false, never API rejection.

## Required
Only a verified missing-tab outcome from `chrome.tabs.get` or the concrete tab removal event should permanently unregister a tab. Catch errors in unrelated authorization/navigation/permissions APIs separately, reporting a non-ready state without destroying the registration. Retry on next refresh; do not authorize page execution on uncertain outcomes. Add a permissions-rejection regression, including a real existing tab and cold-start persistence checks as appropriate.

**Impact:** Temporary Chrome extension API errors can permanently revoke explicit user registrations. This violates the specification's distinction between missing tabs and extant tabs needing reauthorization.
