# Independent frozen follow-up review — RequestChanges

**Review:** `6ea138e944204761ab6bfa717fce7e64`  
**Frozen head:** `39cc5d65ca88867df7903626606f8e4ad1ad0421`  
**Base:** `6ab793928fa7f998b9d5796061cf66d3c0b02e40`  
**Task:** `browser-agent-launcher-reliability`

## Status of the two previous P1 findings

- **Created-tab identity and provenance:** Corrected in this patch. F# now pins the returned registration ID, generation and agent-created state across readiness, Execute and CloseTab. The extension checks stale generation/ownership after asynchronous preflight; negative tests cover manual tab adoption and prevent injection or cleanup after adoption.
- **Mutable window alias across OpenTab:** Corrected in this patch. The bridge sends the selected opaque window ID and generation; the extension revalidates both before creating the tab. Negative tests cover alias reassignment and permission-check generation changes.

## Independent verification

- Inspected focused diff from earlier frozen `ef1756a99c12392febacc6810fce2dc1b030343d` to current `39cc5d65...` and the relevant F#/extension implementations and tests. Clean local review worktree `worker5-browser-launcher` verified at the exact frozen HEAD.
- Ran `node --test browser-extension/tests/bridge.test.cjs browser-extension/tests/chatgpt-launcher.test.cjs`: **66/66 passed, exit 0**.
- Initiated independent .NET 11 Release `dotnet test Client.Tests/Client.Tests.fsproj --configuration Release --no-restore` (Jarvis job `697cca7c1f314daca0103fa68c17c1c7`). Build completed and test runner started; the job was still running at document write, and activity-persistence warnings occurred. **No independent full .NET pass is claimed.** The submitted summary reports 26/26 focused BrowserBridgeTests, but that claim was not independently rerun by this reviewer.

## New blocking P1: opt-in revocation race

See `findings/open-tab-opt-in-revocation-race.md`.

At `browser-extension/background.js:625–641`, the extension checks `allowAgentCreatedTabs` and connection epoch *before* awaiting `validWindow`, then calls `chrome.tabs.create` without repeating those authorization checks. Deterministic independent Node-harness reproduction triggered a successful `setAgentCreatedTabs({allowed:false})` during the final window lookup: after revocation `allowAgentCreatedTabs=false`, yet `createdTabs=1` and the bridge returned `tabOpened` (tab `needsReauthorization`). This creates a new tab after the operator withdraws explicit permission. The correction must revalidate opt-in and connection/epoch after the final await, before irreversible tab creation, with a negative-space regression.

## Deployment gate and verdict

Live Edge three-bootstrap verification and adverse extension/profile flips remain unverified pending operator-authorized update/restart. No reviewer source change, merge, restart or deployment was performed.

**Verdict: RequestChanges.** The two prior P1 findings are addressed, but this independently reproduced authorization bypass blocks approval.