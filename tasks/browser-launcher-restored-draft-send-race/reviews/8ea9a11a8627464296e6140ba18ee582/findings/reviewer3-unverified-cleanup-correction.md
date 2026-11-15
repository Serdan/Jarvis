# Reviewer 3 — independent frozen re-review of browser-launcher cleanup correction

**Review:** `8ea9a11a8627464296e6140ba18ee582`  
**Original task:** `browser-launcher-restored-draft-send-race`  
**Frozen source:** `f1c076f2cecc26a1a922aeec48250bc17a765f09`  
**Verdict: Accepted.** Prior R3-1 P2 error-classification finding is resolved on this exact commit.

## Immutable provenance and scope

- Independently claimed the previously unclaimed frozen request at ledger sequence 3400. `review_details` pinned the exact source head `f1c076f2cecc26a1a922aeec48250bc17a765f09`.
- Verified `git show -s --format=%H %P`: direct sole parent `a55e1d13fa5d14c5691d1f3a2d34871bb881767f`, previously reviewed ChangesRequested at decision seq3374.
- `git merge-base --is-ancestor 9a5750ab93bb1ac26f957f319b929744981ede61 f1c076f2` exited 0. The prior accepted combined launcher stays an unchanged ancestor. The lineage also passes through WQ-02 integrated baseline `e0ae297f42217a19cde29fb7729bf509a2448e53`.
- `git diff --name-only a55e1d13 f1c076f2` returned only `Client/Modules/BrowserBridge.fs` and `Client.Tests/BrowserBridgeTests.fs`. `git diff --check` exited 0; tested registered worktree `worker7-browser-launcher-wq02-refresh` is clean. No changes to extension, UI launcher, queue architecture or accepted ancestor.

## Previous blocking P2 contract finding — resolved

- In `Client/Modules/BrowserBridge.fs:751-765`, `closeDefinitive` invokes one `CloseTab` with exact registration ID, owner connection, registration generation and `requireFreshHomeNoTurns=true`. Only the positively observed `BrowserCloseTabError:CleanupSafetyChanged` maps to `Ok "retained"`. `BrowserCloseTabError:CleanupSafetyUnverified`, along with all other close failures, maps to `Error "cleanupFailed:<bounded error>"`, without another close request.
- The `notSubmitted` branch (`BrowserBridge.fs:907-917`) returns `Error(registrationId, "NotSubmitted;cleanupFailed:BrowserCloseTabError:CleanupSafetyUnverified")` when the guard cannot establish safety. The `uiIncompatible` branch (`~862-906`) likewise preserves sanitized incompatibility diagnostics, pinned ID and explicit `cleanupFailed` rather than a silent retained/success result. `CleanupSafetyChanged` continues to retain the tab, never claiming it was closed.
- The `outcomeUnknown` branch returns immediately without disposal, and the original one-click/no-blind-retry launch logic is unchanged. Review of `browser-extension/background.js:817-855` confirmed it uses `CleanupSafetyUnverified` for unavailable/inconclusive current-tab or page probes and `CleanupSafetyChanged` for positive route/turn/navigation changes.
- In `Client.Tests/BrowserBridgeTests.fs:547-616`, new composed negatives cover **both** `notSubmitted` and `uiIncompatible` under `CleanupSafetyUnverified`. They assert the pinned `late-conversation` ID, explicit failure string, absence of `cleanup=retained`, presence of the registered agent-created and unrelated manual tabs, and no subsequent close or resend frame within the bounded observation. Existing `CleanupSafetyChanged` cases continue to assert safe retention. This directly reverses the incorrect success expectation in the previous frozen review.

## Independent local validation on the exact frozen worktree

- `node --test browser-extension/tests/bridge.test.cjs browser-extension/tests/chatgpt-launcher.test.cjs`: **100 passed / 0 failed**, exit 0, Jarvis job `4b7a2019311146a582397d03bf8ec4e6`. Covers extension cleanup-probe failures, route changes, restored-draft stabilization, exact prompt, one-click and uncertain outcomes.
- SDK `dotnet --version`: **11.0.100**. `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~BrowserBridgeTests --logger console;verbosity=normal`: **44 passed / 0 failed**, net11.0, exit 0, Jarvis job `393f31c30751430abb5d13b73ff1a2e4`. Both new `CleanupSafetyUnverified` cases and all four positive changed-route retention cases passed.
- `dotnet build Jarvis.slnx -c Release --no-restore --nologo`: **exit 0, zero errors, one existing unrelated FS0760 warning** at `Client/Program.fs:188`, job `129ca830e2aa4dcf922259d759d3a9da`.
- Worktree status after tests/build: clean. No GitHub Actions, main push, deployment, source mutation or Codex.

## Decision boundary

**Accepted**, solely for exact SHA `f1c076f2cecc26a1a922aeec48250bc17a765f09`. The previously blocking cleanup error classification is repaired while refusing unsafe close and post-injection replay. These are deterministic local/source checks, not a claim of live browser end-to-end testing or integration into `main`. Lead retains integration ownership.