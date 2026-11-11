# Independent re-review — browser owned-tab cleanup and native Chromium evidence

**Verdict: Accept. No remaining blocking source defects found in this correction.**

Frozen review: `d6526e2ebf4e3a8590e2775cca14e9d18ff81088` on `refs/heads/feature/browser-bridge`, current main/base `ecb59e2272c7e6c4e127345f5034695c2f6e5465`, target `main`. Previous rejected review `f8ac560bc6124d3ca7b89fd074a214e2`, frozen `00cfe575`, identified P2 inability to close discarded/frozen agent-created tabs.

## Corrected previous P2

- `browser-extension/background.js:118–155` now evaluates host permission before reporting the `discarded`/`frozen` states, including for initially pending about:blank navigation with the bound origin. The existing registration and session epoch, created-tab opt-in, origin/window checks remain.
- `closeCreatedTab` accepts `ready`, `loading`, `discarded`, `frozen` for **verified agent-created** registrations only. It still checks exact generation/session, valid current window, bound origin, host permission and unchanged parent window. It does not wake/activate/execute scripts to close a discarded or frozen tab; `chrome.tabs.remove` is used only after ownership checks.
- New JS tests cover discarded/frozen owned-tab closure without waking or touching manual/unregistered tabs, stale generations, revoked host permissions, moved window, cross-origin frozen tab and revoked pending-navigation permission.
- `Client/Modules/BrowserBridge.fs:693–718` now returns explicit `TabNotReady; cleanup=closed`, `cleanupFailed:<reason>` or `cleanup=retained` instead of silently swallowing errors. The added Client regression runs concurrent real 30-second bounded not-ready workflows, one successful cleanup and one explicit failed cleanup.
- `docs/BrowserDomSnapshots.md` now accurately states that automated real-Chromium+native-bridge integration passed but manually authorized extension-popup and full authenticated server/MCP flow remain unverified, and documents ownership-based closing.

## Independent verification on exact frozen source

- Full Client Release: **573 passed / 0 failed**, job `3fa300635ce54481b1d3aec62238fa36`.
- Full Server Release: **128 passed / 0 failed**, job `59fa22d66ae64805931cbf19a1c369cc`.
- Browser Node extension: **40 passed / 0 failed**, job `b566248a19d7416c84cae6ac36361683`.
- Native real Chromium: independently downloaded official disposable **Chrome for Testing 157.0.8103.50**, rebuilt `live-client-host` against this frozen Client assembly (job `ed2b2f08a9d24e14879a58d944e638ac`), and ran **15-stage** actual MV3 extension ↔ `Client.BrowserBridge.Service` smoke test with job `8832035ad15741d0adf48d9555cc04c9`: exit 0. Actual `chrome.tabs.discard` moved the agent-owned tab to `discarded`; actual client then closed it without reactivation, preserving a registered user tab. Other stages covered HMAC pairing, synthetic window opt-in, unauthorized origin denial, USER_SCRIPT execution, selector/delay/custom event DOM capture, existing-file refusal, and **150,524 byte** multi-chunk SHA-256 export (`4a84d857780943bc7a0ec7bb2cffe7050a4ca01c6552127d6ace8f781a4a2a2e`). Native harness uses synthetic extension consent in disposable browser only; real popup and authenticated full MCP path were **not** tested by this native run.
- `git rev-parse HEAD` verified exact `d6526e2...`; worktree clean; `git diff --check ecb59e2..d6526e2` exits 0; main is ancestor; `git merge-tree --write-tree main d6526e2` exits 0, conflict-free.
- Temporary reviewer-owned browser artifacts removed after test. No source edits, user browser actions, integration, or deployment.

**Decision: Accept** technical branch review. This is **not** operator authorization to integrate or deploy. Operator may choose a separately consented manual popup/full OAuth-MCP end-to-end acceptance before release.
