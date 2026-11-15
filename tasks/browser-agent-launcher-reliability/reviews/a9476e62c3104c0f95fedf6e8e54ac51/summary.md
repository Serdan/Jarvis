# Frozen browser-launcher follow-up — independent review

**Verdict: ChangesRequested** on `ed4e2728194df850269fadc6d8b85ce1a465fb38` (frozen review ref `refs/heads/jarvis-review/browser-agent-launcher-reliability/a9476e62c3104c0f95fedf6e8e54ac51/r1`).

The original final-window opt-in race is addressed for an operation already running: live socket, connection ID, current opt-in, and authorization epoch are rechecked just before `chrome.tabs.create`. However, two independent negative-space regressions remain:

1. **P1 — Site permission revoked during the final window lookup still permits tab creation**. See `findings/revoked-host-permission-after-window-lookup.md`. The original site-permission check precedes the final awaited window lookup, and there is no final site-permission validation.
2. **P1 — An OpenTab waiting in the serialized queue can cross disable/re-enable**. See `findings/queued-opentab-survives-optin-revocation.md`. The auth epoch is captured at queue execution, not when the request is received, allowing pre-revocation work to run with fresh authorization.

**Evidence:** On the exact frozen worker5 worktree, the checked-in Node extension+ChatGPT launcher suite passed **69/69**. Two independent harness tests injected in memory (no worktree changes) produced **40 passed / 2 failed** when run alongside existing bridge tests; each failure produced one unexpected created tab (expected zero). These demonstrate actual code paths, not hypothetical races. The .NET Release test job was initiated by a different agent and its result was not independently accessible to this reviewer. No deployment or integration was performed.

**Next step:** Fix both authorization boundaries and add the corresponding committed tests; submit a new frozen review snapshot. Do not silently update the existing frozen ref.

## Submitter's original summary

Addresses Reviewer 1 finding open-tab-opt-in-revocation-race on frozen 39cc5d65. After the final awaited window lookup, openNewTab rechecks live socket/connected/connectionId and agent tab opt-in before chrome.tabs.create; opt-in authorization generation additionally prevents disable/re-enable from reviving pending operations. New deterministic negative-space tests cover revocation, disable/re-enable and unpair at final window lookup. Node extension+launcher tests 69/69 passed; git diff --check clean. .NET 11 Client.Tests Release test job 1d22dc49f32f48cbab8720adc677422d still running when review submitted. No deployment, merge or browser restart.
