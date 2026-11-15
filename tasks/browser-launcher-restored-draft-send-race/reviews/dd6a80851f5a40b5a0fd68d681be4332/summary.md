# Browser launcher restored-draft stabilization and failed-tab cleanup — frozen independent review

**Request:** `dd6a80851f5a40b5a0fd68d681be4332`  
**Task:** `browser-launcher-restored-draft-send-race`  
**Reviewer:** Reviewer 2  
**Frozen submitted head:** `406568403971356a62dcedd3090ba31883c939aa`  
**Base:** `90ed868d04524d0483ccd979cffb5029d8cf7027`  
**VERDICT: CHANGES REQUESTED. Do not integrate this frozen commit.**

## Reviewed scope and evidence

The six-file diff changes `browser-scripts/chatgpt/launchAgent.js`, `Client/Modules/BrowserBridge.fs`, two deterministic test files, and two docs. The stabilization change verifies one unchanged composer, empty text, absent enabled Send for three consecutive samples under an 1.8-second cap, no route drift/new turn before insertion and click, then sends once with existing durable URL/bubble evidence. The bridge pins agent-created tab registration to its connection and generation; `outcomeUnknown` is retained and never replayed. These aspects are coherent.

**Independent validation on the claimed frozen worktree:** `node --test browser-extension/tests/chatgpt-launcher.test.cjs`: **34 passed / 0 failed**; `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~BrowserBridgeTests --logger console;verbosity=normal`: **28 passed / 0 failed** on .NET 11 Release; `git diff --check BASE HEAD`: **exit 0**. Warnings FS0760/FS3369 are outside these changes. The submitter also reports extension bridge 53/53 and BrowserSchedules 19/19, but these two counts were not independently re-run in this review. No live authenticated ChatGPT UI submission was attempted.

## Blocking findings

1. **F01 / P1 — Unsafe cleanup of active conversations.** `findings/unsafe-close-on-existing-conversation.md`. The page script returns `notSubmitted/ExistingConversation` when a new user turn appears, and `notSubmitted/UnexpectedStartUrl` for a route transition. The bridge closes for any `notSubmitted` solely because the *launcher* did not click Send. Exact ownership is checked, but no empty/no-conversation proof exists at cleanup. This can close an in-progress/confirmed conversation in an agent-created tab. Preserve such tabs or provide an explicit fresh-empty-tab safe-close proof and test the composed state transitions.

2. **F02 / P2 — Known pre-injection readiness failure retains failed tab with default close preference.** `findings/preinjection-readiness-leak.md`. `cleanupNotReady()` retains on `closeAfterExecution=false`, even when no script was injected and a safe pinned cleanup is available; the MCP default is false. Current readiness tests only exercise true. Clean up definitive no-injection failures, preserve ownership/uncertainty fences, and cover the default false path.

## Required re-review

Publish a **new exact frozen commit/review** after correcting both findings with deterministic tests, revalidate the Node suite and .NET 11 Release bridge tests, and retain the non-replay contract. This review's passing tests establish the submitted behavior but do not resolve the above cross-layer safety violations. Do not merge or deploy this rejected snapshot.
