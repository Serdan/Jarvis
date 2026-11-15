# Reviewer 3 independent frozen review — WQ-02 browser launcher refresh

**Review:** `a55e1d13fa5d14c5691d1f3a2d34871b`  
**Exact frozen source:** `a55e1d13fa5d14c5691d1f3a2d34871bb881767f`  
**Recommendation:** **ChangesRequested** — one P2 contract/correctness issue, no suggestion to loosen live safety checks.

## Source provenance and scope

- Independently read the task specification and review ref, and checked the exact source worktree. `git show --no-patch --format=%H%n%P%n%s HEAD` confirms a clean two-parent merge: first parent `e0ae297f42217a19cde29fb7729bf509a2448e53` (WQ-02 integrated main), second `9a5750ab93bb1ac26f957f319b929744981ede61` (previously accepted combined launcher).
- `9a5750ab` contains the original accepted launcher `53188653f56f28433b44d847c98199b2cfb6babd`. `git diff --name-status 9a5750ab a55e1d13 --` all eight launcher/extension/bridge/test/doc paths is empty: accepted launcher files remain byte-identical, without merge-conflict modifications.
- Relative to WQ-02 baseline, exactly eight browser-launcher paths differ (+667/-54). `git diff --check e0ae297 a55e1d13` exits 0 and the tested worktree is clean.

## Independently executed checks

- `node --test browser-extension/tests/bridge.test.cjs browser-extension/tests/chatgpt-launcher.test.cjs`: **100 passed, 0 failed**, exit 0, job `74720d18e6cf45fab301e55cd11c7d0e`. Includes restored-draft stabilization, one-click/unknown-outcome, post-probe navigation, and fresh-home cleanup.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~BrowserBridge --verbosity minimal`: **43 passed, 0 failed**, exit 0 on `net11.0`, job `8382be1f621043c680f7f8f255f88a6d`. One existing FS0760 at `Client/Program.fs:188`, unrelated to the reviewed diff.
- No full solution-suite claim and no live browser send test; no hosted Actions, Codex, main mutation, or deployment.

## Finding R3-1 — P2 blocking: unverified failed-tab cleanup is reported as ordinary retention/success

**Evidence:** `Client/Modules/BrowserBridge.fs`, function `runInNewTab` / local `closeDefinitive` (~lines 754–772), treats both `BrowserCloseTabError:CleanupSafetyChanged` **and** `BrowserCloseTabError:CleanupSafetyUnverified` as `Ok "retained"`. Later, the `status = "notSubmitted"` branch (~lines 904–915) wraps this in **`Ok`** with `cleanup = "retained"`, not `cleanupFailed` / a tool error. In `browser-extension/background.js:836-855`, `CleanupSafetyUnverified` specifically means the independent page probe failed, returned no conclusive top-frame result, or a post-probe tab inspection failed. This is distinct from a verified changed route or new turn (`CleanupSafetyChanged`).

**Why it matters:** The original task explicitly requires failure when failed-launch cleanup cannot be verified; the modified `browser-extension/README.md` likewise says a missing/incomplete probe retains the tab **but otherwise produces a cleanup error**. The implementation instead turns a failed independent safety inspection into an apparently normal `notSubmitted` result with no failure reason. An orchestrator can therefore treat a leaked launcher tab as a completed definitive failure; repeated incidents can exhaust the 20 agent-created-tab capacity. The exact F# regression `composedLateConversationGuardRetainsTabWithoutRetry("notSubmitted", "CleanupSafetyUnverified", "inspection-unavailable")` currently codifies this silent downgrade, and passes.

**Required narrow correction:** Never close or retry after an unverified inspection. Preserve `cleanup=retained` for a positively observed changed/active conversation (`CleanupSafetyChanged`); **propagate `CleanupSafetyUnverified` as an explicit `cleanupFailed`/error** with the pinned registration so the caller can reconcile or clean it up manually. Update the composed F# negative test to assert failure with no extra close/replay; leave `outcomeUnknown` and confirmed-send behavior untouched. Re-run Node and .NET11 Release targeted tests and request a fresh exact frozen review.

## Other inspected invariants / limits

The launcher clears restored ProseMirror text without clicking Send, waits for three empty/inactive observations, rejects new user turns/route changes, and marks `sendAttempted` before its single click. The extension's guarded close verifies agent-created registration/generation and a fresh zero-turn home page, including Chrome pending navigation before/during/after DOM probing. These fail-closed paths are well covered locally. The identified issue is **error classification**, not permission to close an uncertain conversation.

**Explicit verdict: ChangesRequested** on exactly `a55e1d13fa5d14c5691d1f3a2d34871bb881767f`. Do not integrate this SHA into main as accepted.