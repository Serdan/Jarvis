# Independent security re-review — ChangesRequested (frozen b7033b4b)

**Reviewer:** Reviewer 1. **Canonical request:** `15cc33b08f8b4f3890403e963b7c7802`. **Exact source:** `b7033b4b7bcdee0936cf7239a625a4ecaccf3257`, parent `a9e758fa1274b6e036708beb6da055721644fdab`, base `d702352ef705f9991ac1cb14dc15a13cce78aa44`. Registered `launcher-cdp-review-races-operator-20261121` worktree observed exact HEAD and clean.

## Positive evidence

- Independently reviewed the exact `background.js` patch, adjacent authorization/revocation mechanisms, tests, and Reviewer3 predecessor review `c9f004a9ef404e11b47841ea6b005f31` (ChangesRequested seq10274 on `64d94fc8`).
- Reviewer3 P1 is addressed in source: `acquireBackgroundFocus` no longer skips debugger/focus proof when the initially inspected tab is active. It maintains emulation through page execution and releases it in `finally`; test simulates active→background and checks no native tab activation.
- Reviewer3 P2 is addressed for project-script injection: the special fresh-home route now has strict `location.href === "https://chatgpt.com/"` inside the renderer wrapper and a last browser tab URL preflight; tests cover drift after debugger attachment and after the last tab preflight. The initial read-only focus probe may still run after navigation, but it does not execute the arbitrary project script.
- `node --test browser-extension/tests/bridge.test.cjs`: **83/83 pass**, OS exit 0.
- Combined offline `node --test` with `bridge.test.cjs`, `chatgpt-launcher.test.cjs`, `popup.test.cjs`, and `native-private-host-gate.test.cjs`: OS exit **0**, timeout false (output capped, so no unverified total claimed).
- `git diff --check b7033b4b^ b7033b4b`: OS exit **0**.

## Blocking independent finding

**P1: `findings/late-tab-preflight-revocation.md`.** The correction introduces a new asynchronous `chrome.tabs.get` after the final `authorizePageExecution` permission/epoch check. Explicit operator opt-out during that awaited tab query is not rechecked before `api.execute`, permitting a project script to run after revocation. An independently constructed, in-memory-only adversarial test reproduced this: `queriesAfterProbe=2`, `calls=2` despite opt-out; reported error `OutcomeUnknown:RegistrationChangedDuringExecution` only after user code ran. This assertion intentionally failed (OS exit 1), proving the race; the unmodified frozen suites pass. A bounded correction is to move the extra exact-home tab inspection before final `authorizePageExecution` and retain the renderer's exact-URL guard, then add negative tests. This is an existing source-authorization requirement, not new task scope.

## Verdict and evidence boundary

**ChangesRequested**, limited to the reproduced late authorization/revocation race. No author source edited, no live Chrome prompt sent or existing uncertain send replayed, no integration, deployment, scheduler effects, or browser permissions changed. Offline mocks and source review do not establish behavior in real Chrome; broad debugger permission and actual production acceptance still require their separate authority gates. This verdict does not adjudicate new work or authorize release.