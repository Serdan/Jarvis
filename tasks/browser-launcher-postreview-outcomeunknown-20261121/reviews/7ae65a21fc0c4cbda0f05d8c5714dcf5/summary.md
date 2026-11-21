# Reviewer 3 — independent corrective security re-review

**Verdict proposed:** Accepted (bounded to the late-preflight revocation defect). **Review:** `7ae65a21fc0c4cbda0f05d8c5714dcf5`. **Frozen HEAD:** `22e2ea77c4f3f63df27d0efdac0e7a0543259f00`, parent `b7033b4b7bcdee0936cf7239a625a4ecaccf3257`, original base `d702352ef705f9991ac1cb14dc15a13cce78aa44`. Registered worktree `launcher-cdp-revocation-fence-operator-20261121`: exact HEAD and clean. Independent predecessor: Reviewer 1 `15cc33b08f8b4f3890403e963b7c7802` / `findings/late-tab-preflight-revocation.md` (ChangesRequested seq10284).

## Original P1 and source assessment

Reviewer 1 reproduced a forbidden arbitrary project-script execution after explicit operator opt-out during a newly introduced asynchronous fresh-home `chrome.tabs.get`, which followed the last `authorizePageExecution` in frozen `b7033b4b`. This violates the existing pre-injection authorization fence; an eventual `OutcomeUnknown` cannot undo a Send.

At `22e2ea77` the exact-home `chrome.tabs.get` inspection is now **before** the final awaited `authorizePageExecution(pagePin)` in `background.js:493-509`. That authorization obtains tab/window/host permission, then synchronously checks connection, tab authorization epoch, registration identity/generation, session, window generation and permission. There is **no additional await between return of this final authorization and invocation of `api.execute`** (`background.js:508-514`). This closes the demonstrated opt-out window. The exact renderer-side `location.href === "https://chatgpt.com/"` guard is retained (`:443-455`) for navigation between Chrome preflight and actual renderer execution. Debugger focus proof is still read-only before any project script, with cleanup in `finally` (`:473-519`).

## Reproductions and negative tests

- Independently ran `node --test browser-extension/tests/bridge.test.cjs` against the exact registered frozen source: **85/85 passed**, exit 0, timeout false. This includes the two newly added deterministic tests: late operator opt-out, and disable/re-enable authorization-epoch ABA, each asserting **one read-only focus probe, zero project-script executions and debugger detach**.
- The same 85-test run also passes active-to-background focus, denied debugger, visibility proof failure, route drift after attachment and between tab preflight/in-frame wrapper, stale-registration/host-permission/window checks, and no-debugger other-origin execution.
- `git diff --check b7033b4b7bcdee0936cf7239a625a4ecaccf3257 22e2ea77c4f3f63df27d0efdac0e7a0543259f00`: exit 0, no output. `git status` on exact worktree: clean.
- A broad `node --test browser-extension/tests` job was initiated separately; its result is not claimed until observed terminal. Narrow negative suite above is definitive for this bounded review.

## Limits

No live authenticated Chrome tab or actual Send was exercised; mocks cannot attest native debugger runtime behavior. The verdict concerns the security corrective source and the original P1 only, **not** browser production readiness, authenticated End-to-End Send, successful installation, integration/publication, scheduler enablement, or replay of any prior `OutcomeUnknown`. The privileged debugger permission remains a separate operator/install decision; no code or external state was changed by this review. No further source defect established within the frozen corrective diff.
