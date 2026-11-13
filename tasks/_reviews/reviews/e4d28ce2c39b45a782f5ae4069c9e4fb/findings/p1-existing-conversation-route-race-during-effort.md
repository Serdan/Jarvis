# P1 — ChatGPT effort-selection route race can send an agent bootstrap into an existing conversation

**Frozen implementation:** `04c27588f6df891f333032056a49864965f15657`, `fix/chatgpt-thinking-effort`, base `b24d43581821b9489539ae750caf21735a412cb9`, independent review `e4d28ce2c39b45a782f5ae4069c9e4fb`.

## Source path

- `browser-scripts/chatgpt/launchAgent.js:16–18` asserts `location.href === "https://chatgpt.com/"` **only at script start**. The script then performs asynchronous mode hydration, composer and model picker waits, and up to three Power slider keypresses with per-step UI confirmation (`lines 72–183`). Each wait yields to the browser, during which ChatGPT could navigate to an existing conversation or another same-origin route.
- The additional draft guard, `preflightOnly` guard, prompt insertion and immediate pre-send thinking effort check (`lines 184–205`) **do not revalidate the current URL**. The launcher then clicks Send unconditionally (`lines 206–211`).
- A same-origin route change to `https://chatgpt.com/c/<existing-uuid>` therefore allows the previously authorized bootstrap prompt to be sent into an unrelated existing conversation. After Send, `newUserTurn` is observed and `conversationUrl` accepts any canonical `/c/<uuid>` URL (`lines 214–253`), so the **existing** conversation can be returned as `status:"submitted"` as though it were a newly created agent chat.
- This is not an unknown post-send replay issue: the execution should fail closed **before** Send if the homepage/start conversation identity changes. The new effort selector adds multiple asynchronous waits, expanding the practical race window. Retaining a single preflight start URL check is insufficient to enforce the launcher's promised new-conversation isolation.

## Independent negative reproduction

Created reviewer-only worktree `reviewer1-effort-route-race` from exact frozen SHA `04c27588...`, leaving Lead's branch untouched. Extended the existing Node test harness's Power keyboard event so a simulated same-origin SPA route transition to an existing canonical conversation UUID occurs during the first effort-selection arrow key. Test `navigation to an existing conversation during effort change prevents Send` uses `thinkingEffort="High"` from initial Extra High, keeps the existing route after click (`noRoute:true`), and asserts both no Send and not-submitted outcome.

Actual **red regression, exit 1** via `node --test --test-name-pattern "navigation to an existing conversation during effort change prevents Send" browser-extension/tests/chatgpt-launcher.test.cjs`: `h.clicks === 1` (expected 0). A second run asserting non-submission first independently demonstrated `result.status === "submitted"` (expected not submitted), both at `browser-extension/tests/chatgpt-launcher.test.cjs:524`. The harness models a URL change with the same DOM shell, consistent with a possible ChatGPT SPA transition; it is a controlled negative proof of missing pre-send route validation, **not evidence that this occurred in a real user's ChatGPT conversation**.

## Positive checks

- On unmodified frozen Lead worktree `lead-thinking-effort`, `node --test browser-extension/tests/bridge.test.cjs browser-extension/tests/chatgpt-launcher.test.cjs`: **61/61 passed**, exit 0, job `2b1c1b3fa0274bffa236de723b86b99e`. Existing tests cover explicit/omitted efforts, keyboard slider mapping and hydration, menu Escape, unresponsive UI, restored draft, `preflightOnly`, and no replay after uncertain Send; they do not simulate route changes between initial check and Send.
- Verified the owner worktree `HEAD=04c27588...`, clean and tracking origin; `git diff --check b24d4358..04c27588` exit 0.
- Lead reports live Edge `preflightOnly` choosing High then restoring Extra High, with no prompt insertion/Send; no post-final live full send smoke. No full .NET Client/Server re-run independently since frozen diff only touches JS, its Node tests, and docs.

## Required correction

Revalidate the exact authorized start route `https://chatgpt.com/` **immediately before any prompt insertion and again immediately before the non-idempotent Send attempt**. Treat a pre-send location change as a definitive `notSubmitted`/provider incompatibility, leave the draft intact when possible, and do not create or return a new agent conversation ID. If the page becomes an existing conversation at or after Send, the outcome is unknown and must not be replayed; never label an unrelated route as a newly created conversation. Include regression with route changing (1) during Power slider step, (2) between composer insertion and Send, and (3) temporary same-origin route versus canonical unrelated UUID, while confirming the valid new-conversation route after Send remains supported. Preserve explicit effort checks and the no-replay boundary.

**Severity P1 for launcher conversation isolation, blocking this review.** Unintended bootstrap submission into an existing chat is a materially wrong external action and can corrupt another conversation. No frozen branch edits, merge, production deployment, or real Send by Reviewer 1.
