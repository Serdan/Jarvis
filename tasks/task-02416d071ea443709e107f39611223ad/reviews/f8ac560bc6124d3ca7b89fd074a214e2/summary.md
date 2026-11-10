# Independent review — native Chromium/JarvisClient browser automation interoperability

**Verdict: RequestChanges — one new reproducible P2 tab-lifecycle defect.** The native-browser integration is independently verified and materially strengthens acceptance evidence.

Frozen source `00cfe57589436cacf573d2b512f295c432405674` on `refs/heads/feature/browser-bridge`; base/current main `ecb59e2272c7e6c4e127345f5034695c2f6e5465`, target `main`. Current main updater commits are included in feature history; no unintegrated drift. Corrected earlier protocol 3.63 and public DOM MCP OAuth scope defects remain fixed.

## What is verified

- **Real supported browser↔client path:** Independently built the `browser-extension/tests/live-client-host/LiveClientHost.csproj` in Release and ran `node browser-extension/tests/live-chromium-smoke.cjs /tmp/.../chrome-linux64/chrome --real-client` using official **Chrome for Testing 157.0.8103.50** in a disposable isolated profile. **Exit 0; all 14 stages passed.**
- The smoke test launches an actual Chromium MV3 extension service worker and enables per-extension **Allow User Scripts** in the disposable browser UI. It runs the real production `Client.BrowserBridge.Service` assembly via a small test-only .NET host, including HMAC loopback pairing, synthetic window registration/new-tab opt-in, unapproved-origin denial, created-tab lifecycle, native `USER_SCRIPT` execution, post-hydration selector capture, timer wait, future custom DOM event wait, and end-to-end file export through the actual client.
- The native export produced **150,524 UTF-8 bytes** with exact SHA-256 **`4a84d857780943bc7a0ec7bb2cffe7050a4ca01c6552127d6ace8f781a4a2a2e`**, exercising multiple 32-KiB chunks. The test verified actual rendered DOM includes post-hydration `Ω🙂` content, checksum, non-overwrite of an existing file, refusal to close a manually registered tab and successful safe close of an ordinary agent-owned tab.
- **Evidence boundary:** Registration and opt-in in this disposable profile are synthetic, using direct extension-worker functions; the test host bypasses full OAuth/TUI/MCP server and cannot assert a real human's popup consent or deployed server-to-client route. Earlier independently verified routed Server OAuth tests and Node consent tests provide supporting evidence, but an explicitly operator-authorized end-to-end manual setup is still needed for production deployment. This limitation is stated accurately by the submitter. The browser task's literal requirement for *at least one live supported Chromium/Edge test on a generic JS-rendered page* is now met; remaining manual setup should not be silently represented as completed.
- The first local rerun with system Google Chrome could not discover the MV3 service worker, and Edge's settings UI lacked the harness's Chrome-specific toggle selector. Those attempts did not reach bridge assertions. The correct official Chrome for Testing runtime passed independently. All temporary browser binaries, profile, archive, and ad hoc test copies were removed after testing.

## New blocking finding

**P2: Discarded/frozen agent-created tabs cannot be safely closed** — `findings/p2-close-discarded-agent-tabs.md`.

`background.js:118–154` returns availability `discarded`/`frozen` for valid owned tabs. `closeCreatedTab` at lines 602–632 rejects every availability except `ready` or `loading`, yielding `RegistrationStale` before `chrome.tabs.remove`. A tab that Chromium discards for resource pressure is still owned by Jarvis, with matching origin/window/generation, and closing it does not require page execution or waking the tab. The task requires handling discarded tabs and controlled tab lifecycle. `runInNewTab` at `Client/Modules/BrowserBridge.fs:693–712` can also ignore this cleanup error after a readiness timeout, leaving an owned tab despite a close-after-execution request.

I added one test **only to an isolated temporary copy** of the existing Node harness: create approved agent tab, set same-origin `status=complete` and `discarded=true`, close by valid registration/generation. Reproduction job `c2dccfdd68184ebe8f3be9b72be0cfc2` fails the new assertion (`actual kind="error"` vs expected `tabClosed`) while its 20 baseline tests pass. Source worktree untouched. Fix ownership-vs-readiness separation, allow safe close for valid discarded/frozen agent-created tabs, retain explicit manual-tab/cross-origin/window/generation/permission refusals, and test composite cleanup.

**Minor documentation follow-up:** `docs/BrowserDomSnapshots.md:3,70` currently says no live extension verification even though the new technical native smoke passes. Clarify *automated native verification passed; manually authorized popup/full MCP E2E not yet performed* rather than denying that any live extension was tested.

## Independent full-suite and integration checks

- Client Release **572/572 passed** — job `84958a754c524eac872d806746deaead`.
- Server Release **128/128 passed** — job `da499bc46b674090bd34922a04a5d46e`.
- Browser extension Node **35/35 passed** — job `ed900405c7d143fc981f9d97b400d833`.
- Updater Python black-box **21/21 passed** — job `5faf7bc7ec5349c29458c7c0d1b18023`.
- Native real-browser smoke **14/14 stages passed** — job `79450f4ba50e401a80c73ab4c0816e2c`.
- Temporary added negative-space regression **1 reproduced failure** (plus 20 baseline passes), job `c2dccfdd68184ebe8f3be9b72be0cfc2`.
- Frozen HEAD exact and clean, `git diff --check` clean, current main is an ancestor, `git merge-tree --write-tree main 00cfe575` succeeds without conflicts.
- No source changes, Git integration, deployment, user browser interaction, or production state modification by reviewer.

**Decision: RequestChanges.** Correct/negative-test the discarded/frozen owned-tab close path, update the native-vs-manual acceptance wording, submit a new frozen review. The native Chromium interoperability requirement is independently demonstrated. No integration/deployment approval is implied.
