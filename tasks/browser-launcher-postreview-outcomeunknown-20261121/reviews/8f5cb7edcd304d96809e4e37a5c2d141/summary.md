# Reviewer 1 — independent frozen-source review

**Proposed verdict:** Accepted — no blocking finding against the bounded post-Send confirmation-budget correction.

**Identity and scope.** Review `8f5cb7edcd304d96809e4e37a5c2d141`; immutable source `9521c44dc1560dd467605a95c586c1d20ce0cbc3` (parent `6d03aa02df8f5ce6d8acf10fd51a93f0d9aaab61`; lineage through `8da38dc7` and `d1af5ae1`; original main base `d702352e`). The direct patch changes only `browser-scripts/chatgpt/launchAgent.js` and `browser-extension/tests/chatgpt-launcher.test.cjs` (18 insertions, 2 deletions), not the bridge or scheduler.

**Source inspection.** The script computes `acknowledgementReserveMs = min(8000, max(3000, overallMs - 12000))`, bounds model/mode selection to `deadline - reserve - 1500`, and gates composer editing / the only `send.click()` behind `preSendDeadline = deadline - reserve`. After that one attempt the existing `until` checks use the original overall deadline to observe new user-turn/bubble evidence and a server-issued UUID conversation URL. A missing acknowledgment, incomplete route, or failed click remains `outcomeUnknown`; there is no post-click retry or success derived solely from optimistic local routes or tab cleanup. The shorter selection/composer window trades some pre-send availability for a stronger post-Send evidence window; that is consistent with this change's objective.

**Independent checks actually executed.**
- `git show 9521c44d`, immutable parent/source diff, and tracked-path status: worktree HEAD exactly `9521c44d`; two reviewed tracked files clean; four unrelated untracked `diagnose*.js` files present and excluded from evidence.
- `git diff --check 9521c44d^ 9521c44d`: exit 0.
- Offline `node --test browser-extension/tests/chatgpt-launcher.test.cjs` from the frozen worktree: **74 passed, 0 failed, exit 0**, 57.36s. An initial 45-second bounded attempt timed out after 55 passing cases; the complete rerun finished successfully.
- Additional independent in-memory virtual-clock fixture checks (no file changes): at 27s overall with Send available at 18s, exactly one click, `outcomeUnknown/SubmissionUnconfirmed` on deliberately absent acknowledgment, 8,995ms of post-Send budget; with Send first available at 19.5s, zero clicks, `uiIncompatible/SendUnavailable`, clock terminates at 19s. Both passed.

**Evidence limits.** These are deterministic offline fake-UI checks, not a live ChatGPT submission, provider/backend receipt, deployment, or proof that real-world delayed acknowledgment will always arrive within 8s. The added committed regression checks the pre-Send fence, while older fixtures cover delayed user bubble / UUID route; a dedicated deterministic successful late-Send + >3s acknowledgment case would add useful coverage but is not a demonstrated defect in this isolated 20-line correction. No live prompts, integration, deployment, or schedule changes were made.

**Findings:** No reproducible blocking defect identified. Recommend **Accepted** for the exact frozen commit only, with integration/deployment remaining separately gated.
