# Independent review — browser scheduler/Challenge documentation drift

**Review:** `f3274c18c4d14fb6a9f70e426531928a` | **Reviewer:** Reviewer 4 | **Verdict:** ChangesRequested
**Frozen commit:** `609157037f016f1ee4c09fd6b2b0b4991224fab3` | **Base:** `d702352ef705f9991ac1cb14dc15a13cce78aa44` | **Target:** `refs/heads/main`.

## Independently checked
- Canonical task contract and immutable review metadata, clean source-exact docs worktree, full frozen patch and eight-file scope: **104 additions / 65 deletions; no runtime/config changes**. `git diff --check` exited **0**.
- `Client/Program.fs:365-371`: `AfterAgentIdle` independent proof returns `ActivityProofUnavailable`; docs correctly treat it fail-closed. `Client/Modules/BrowserSchedules.fs:713-721`: recurring `UiIncompatible:` maps to `failed`, one-shot to `suspended`, with uncertain-outcome path; correctly described as unresolved rearm safety risk.
- `browser-scripts/chatgpt/launchAgent.js:78-82`: frozen model preference is GPT-6 Extra High / Work GPT-6.2 Astra Medium / Chat Instant, separate from Operator's newer High/Sol request.
- Challenge integration commit `13d4124348f8a287673d8568a371adf918a33240` is an ancestor of frozen source (`git merge-base --is-ancestor` exit **0**). Frozen tree contains `Client/Modules/ChallengeWorkflow.fs`, `Client.Tests/ChallengeWorkflowTests.fs` and typed `ChallengeRead`/`ChallengeWrite` commands.
- Live `list_commands`: protocol **3.68**, general and legacy scheduling plus Challenge methods. Connected callable tools **now include** `list_challenges`, `get_challenge`, `set_challenge_subscription`.
- Independently ran `node --test scripts/tests/manager-heartbeat.test.mjs browser-extension/tests/bridge.test.cjs` in the **exact frozen, clean worktree**, exit **0**. No .NET or live browser tests run by this reviewer.

## Source author's prior test evidence (not independently re-executed here)
The pre-existing summary reports docs-specific `node -e` checks **8/8 PASS**; Manager heartbeat **26 passed**; browser bridge **73 passed**; filtered Release BrowserSchedules **28 passed, 1 skipped**. It also reports pinned baseline ChatGPT launcher tests **3 passed / 64 failed**, attributed to fixture drift around integrated model picking. Treat these as author-reported, not new independent reruns, and do not infer UI delivery acceptance or launcher correction from them.

## Decision
The core source-state and safety corrections are supported. However three newly edited present-tense statements incorrectly say the ChatGPT connector lacks direct Challenge list/get/subscription operations:
- `docs/DurableAgentChallenges.md:3`
- `docs/DurableAgentChallengesIntegration.md:5`
- `docs/ManagerHeartbeat.md:39-40`

See `findings/stale-challenge-connector-exposure.md` for reproduced evidence, operational impact, and bounded correction. These are task-in-scope current-connected-capability assertions, not a net-new feature requirement. Update the statements to reflect *current* exposed actions and preserve the no-cutover, protocol, subscription and deployment caveats.

**Limits:** No source edits, merges, live schedules, deployments, client restarts, remote rollout verification or full .NET test suite. No claim of end-to-end acceptance.