# P2 acceptance gap — live paired Chromium/Edge extension flow remains unverified

**Frozen source:** `b9dc25bc5340aa8cb043a75d4e47e69d0c3e9679`.

The task acceptance explicitly requires at least one real supported Chromium/Edge run through the **paired browser extension**, not only mock WebSocket frames and a headless page fixture. The minimum end-to-end flow should demonstrate user-authorized window and origin, new tab registration, project JavaScript execution, post-hydration DOM capture after selector wait, delay wait, custom event wait, bounded file export and safe created-tab close; include failure for non-Jarvis tab close and authorization revocation where practical.

The author's submitted summary confirms this remains missing: *"Still missing live paired Edge/Chromium extension end-to-end authorization acceptance; headless Edge fixture alone does not satisfy it."* Independent tests are positive on mocks/in-memory bridge (572 Client Release, 127 Server Release, 35 Node), but those do not verify supported live `chrome.userScripts.execute` behavior, MV3 background lifetime, real permissions, or the full authorization/dispatch path.

**Required before acceptance:** Run the live extension-paired fixture under explicit operator consent/setup, record steps, anonymized status outcomes and bounded output/hash evidence without page/credential leaks. Do not deploy or bypass user browser authorization to complete this test. Re-submit a frozen review with this evidence.

**Severity:** P2 acceptance gap. This is not independent proof of a runtime failure; it remains an explicit acceptance requirement.
