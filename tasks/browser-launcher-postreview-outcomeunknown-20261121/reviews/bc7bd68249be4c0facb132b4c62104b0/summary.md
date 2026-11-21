# Independent review — launcher background/model preference

**Review ID:** `bc7bd68249be4c0facb132b4c62104b0`  
**Frozen commit:** `265a3f4eb66a16d3b8ae1aab027d17e7110ad24a`  
**Base:** `d702352ef705f9991ac1cb14dc15a13cce78aa44`  
**Verdict:** **ChangesRequested**

## Scope inspected

Examined exact committed diff and corresponding source across `browser-scripts/chatgpt/launchAgent.js`, `Client/Modules/BrowserBridge.fs`, `browser-extension/tests/chatgpt-launcher.test.cjs`, `Client.Tests/BrowserBridgeTests.fs`, and `docs/ChatGPTAgentLauncher.md`. The registered source worktree was clean and its HEAD matched the frozen SHA. No source/main edits, deployment, schedule activation, live browser send, or uncertain-effect replay.

## Test evidence and limits

- Executed `node --test browser-extension/tests/chatgpt-launcher.test.cjs` on the frozen source: **76 passed, 0 failed**, terminal exit 0, ~57.6 s.
- Executed one independent in-memory adversarial fixture on the same unmodified launcher with a post-insert Chat model change. Output: `{"reportedStatus":"submitted","reportedModel":"GPT-6","liveModelAtEnd":"GPT-5.6 Sol","sendClicks":1}`. This **reproduces a violation**, not a pass of the model-integrity contract.
- SDK verified `11.0.100`. Initial `dotnet test ... --no-restore` exited 0 without test execution; separate Release build failed NETSDK1004 because this worktree lacked `project.assets.json`. `dotnet restore Client.Tests/Client.Tests.fsproj --ignore-failed-sources` succeeded. After restore/build, an explicit concise `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter FullyQualifiedName~BrowserBridge --logger console;verbosity=minimal` executed **50 passed, 0 failed, 0 skipped**, terminal exit 0, .NET11 Release (`Client.Tests.dll`, 37 s). The earlier no-op was not counted.
- Background Chromium behavior and remote backend delivery were not live-tested. Fixture outcomes cannot establish either.

## Decision basis

**Blocking P1:** The final pre-Send `surface()` guard checks mode/effort but not the **Chat model**; if the shared browser profile's Chat model changes between initial selection and Send, the launcher submits using a different model while claiming GPT-6. This directly violates the frozen source's explicit model-preference contract. Reproduction and a bounded remedy are in `findings/chat-model-drift-after-insertion.md`.

The background-tab contract remains mandatory, and this review does not require foreground activation, CDP installation, live prompt tests, or any net-new scope. Retain the one-shot Send and fail-closed uncertainty behavior. Additional corrective requirements beyond this finding require separate independent adjudication.
