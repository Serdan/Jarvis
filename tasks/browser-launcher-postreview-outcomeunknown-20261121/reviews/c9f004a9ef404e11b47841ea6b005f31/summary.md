# Independent review — ChangesRequested (frozen 64d94fc8)

**Review:** c9f004a9ef404e11b47841ea6b005f31. **Exact SHA:** 64d94fc8115245b1b522c464858c2ee3d9c44b1b, parent 265a3f4e, base d702352e; registered worktree `launcher-background-focus-cdp-operator-20261121` clean at exact HEAD. Source paths: `browser-extension/background.js`, `manifest.json`, `tests/bridge.test.cjs`, `docs/ChatGPTAgentLauncher.md`.

## Scope and positive evidence

- The implementation limits attempted DevTools focus emulation to agent-created, registered-window ChatGPT home tabs reported as complete/nonpending/nonfrozen, and does not actively select tabs. It requires user-visible approval for a new broad, non-optional Chrome `debugger` permission. It probes `document.visibilityState` and `document.hasFocus()` before executing with emulated focus and uses `finally` to disable and detach on normal/exceptional paths. Denied attach/enable generally fails before arbitrary page script execution.
- Independently ran frozen bridge fixtures with `node --test browser-extension/tests/bridge.test.cjs`: **81/81 pass**, exit 0; `git diff --check d702352e 64d94fc8` exit 0; worktree exact SHA and clean. Two extra adversarial fixture scenarios were run by appending tests only in memory to the existing test source via Node Module compilation (no file writes): they reproduce the conditions documented below. Existing green fixtures do not establish concurrent-navigation/foreground-change guarantees.
- No live newly permissioned Chrome extension was tested, no prompts sent, and no integration/deployment/schedules or source edits performed. `launchAgent.js` does itself check exact `https://chatgpt.com/` and existing conversation before acting; the browser bridge's generic `executeJs` boundary is wider.

## Findings in independent documents

1. **P1 — `findings/foreground-to-background-race.md`.** Initially active tab returns from `acquireBackgroundFocus` without emulation or page focus proof; tab can turn inactive before executing the user script, while bridge still returns normal result. This reintroduces the exact background input uncertainty the change exists to prevent, violating pre-send focused-transport gating.
2. **P2 — `findings/fresh-home-route-race.md`.** Same-origin `/c/...` navigation after the pre-attach fresh-home check is permitted by subsequent origin-only authorization and frame wrapper. Generic executeJs can run on a preexisting conversation route despite privileged fresh-home contract. The specific `launchAgent.js` has its own guard, limiting demonstrated impact; no claim that it sent a prompt into that route.

**Verdict rationale:** the original background input transport requirement remains unfulfilled under a normal active→inactive tab switch (P1). The fresh-home privilege boundary also requires a narrow time-of-use correction (P2). This is ChangesRequested on the immutable source, not an authorization for new features or broader redesign; independent adjudication decides disputed/new-scope remedies. Broad `debugger` permission must not be deployed without explicit user approval, irrespective of source verdict.

**Evidence limits:** offline fixtures model races rather than proving Chrome live behavior. No live send, no backend completion proof, and no claim of production duplication.