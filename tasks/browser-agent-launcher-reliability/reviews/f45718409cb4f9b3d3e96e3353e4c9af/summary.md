# P1 browser launcher authorization follow-up

**Exact frozen source commit:** `f45718409cb4f9b3d3e96e3353e4c9af78aff1d3` (`refs/heads/fix/browser-agent-launcher-reliability`). This is a fresh review after ChangesRequested on `ed4e2728194df850269fadc6d8b85ce1a465fb38`.

## Changes

- Capture OpenTab's receiving WebSocket, authenticated connection ID and `agentTabAuthorizationEpoch` **at message receipt before queuing**, not when the queue drains. A request received before a disable/re-enable cannot adopt the newer opt-in.
- After the final awaited window lookup, recheck the destination host permission. After that last awaited permission call, verify socket/connection, opt-in/epoch and the pinned live window registration identity and generation synchronously before `chrome.tabs.create`.
- Add deterministic negative regressions for (1) revoked host permission during final window lookup; (2) window re-registration during final permission recheck; (3) queued OpenTab across opt-in disable/re-enable. Preserve no-retry semantics for a possibly created tab.

## Evidence

- Node browser-extension suite: **43/43 passed**.
- Full Node browser-extension plus ChatGPT launcher suites: **exit 0** (all tests passed).
- `.NET 11 Release` focused `Client.Tests` filter `FullyQualifiedName~BrowserBridgeTests`: **26/26 passed**, zero failed or skipped, 33 s.
- Full `dotnet test Client.Tests/Client.Tests.fsproj -c Release` built `Common`, `Client`, and `Client.Tests` successfully but **timed out** at the 120-second command limit. Output included activity-persistence reads/writes failing due to `UnauthorizedAccessException` and `InvalidDataException` in the sandbox; this is **not a passing full-suite result**. The isolated bridge suite passed separately.
- `git diff --check`: clean. Source branch pushed and tracking remote exact commit.

No deployment, client/browser restart, or merge was performed. Independent review and Lead-managed integration remain pending.

## Independent Reviewer 2 findings — 2026-11-15

**Verdict: Changes requested.** The pinned OpenTab epoch/window/permission guards and adopted-tab generation checks are good improvements, but authorization still fails closed incompletely during asynchronous assessment.

**Verified tests at the exact frozen head:**
- `node --test browser-extension/tests/bridge.test.cjs`: **43/43 passing**.
- `node --test browser-extension/tests/chatgpt-launcher.test.cjs`: **29/29 passing**.
- `dotnet test Client.Tests/Client.Tests.fsproj --no-restore --filter FullyQualifiedName~BrowserBridge --verbosity quiet`: **26/26 passing**.
- Three **additional reviewer-only negative harness tests fail** without any submitted source change: (1) `executeJs` injects after user disables agent-created tabs during `assess`; (2) `captureDom` likewise injects after opt-out; (3) `openTab` revoked during its first permission check is silently dropped and misreported as a timeout/uncertain outcome.

**Required review findings:**
1. [P1: opt-out during assessment still permits script and DOM injection](findings/opt-out-assess-injection.md).
2. [P2: definite opt-out during first host-permission check returns no OpenTab response](findings/open-revocation-silent-timeout.md).

Keep the frozen snapshot unchanged; request fixes in a new source commit and resubmit for an independent review. No merge or deployment authorized by this verdict.
