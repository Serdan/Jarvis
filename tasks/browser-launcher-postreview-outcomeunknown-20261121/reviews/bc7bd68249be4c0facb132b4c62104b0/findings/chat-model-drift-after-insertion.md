# P1 — Chat model can drift after selection yet Send is reported as GPT-6

**Frozen source:** `265a3f4eb66a16d3b8ae1aab027d17e7110ad24a` (base `d702352ef705f9991ac1cb14dc15a13cce78aa44`). **Affected file:** `browser-scripts/chatgpt/launchAgent.js`.

## Original contract and impact

The review's explicit configuration contract is **Chat GPT-6 High** first, Work GPT-6.2 Astra Medium second, Chat Instant last, and selection failure may not authorize Send. Because model/effort preferences persist across browser tabs, the selection must remain true at Send, not only at initial picker selection.

`choose()` verifies the Chat model radio at initial selection (lines ~176–214). Later, the `surface(stage)` pre-Send guard (lines ~327–356) checks Chat/Work mode and the chosen effort but checks a model label only for **Work**, not for Chat. The sole Send gate (lines ~415–437) invokes `surface()`, so a Chat model switch with the same effort between selection and Send is invisible; the function subsequently returns `selectedModel: "GPT-6"` from the cached preference even if it sent on another Chat model.

## Reproduction — independent, executed, no source edits

Ran an in-memory extension fixture derived from the exact frozen `browser-extension/tests/chatgpt-launcher.test.cjs` and injected a post-insert callback that changes its live `selectedModel` to `GPT-5.6 Sol` while leaving reasoning effort High unchanged. Then executed the unmodified pinned `launchAgent.js`. Observed:

```json
{"reportedStatus":"submitted","reportedModel":"GPT-6","liveModelAtEnd":"GPT-5.6 Sol","sendClicks":1}
```

One adversarial fixture passed (reproducing the defect); this is **not** a passing selection-integrity test. No live browser, prompt transmission, replay, or source change occurred. The existing source-bound `node --test browser-extension/tests/chatgpt-launcher.test.cjs` suite passed 76/76, but it lacks this model-drift case (it tests effort resets).

## Requested bounded correction

At the final pre-Send verification, independently re-prove the actual Chat model (not merely mode and effort) using observable current picker/UI state; if unprovable, fail closed before Send. Bounded pre-Send re-selection is permissible only if the exact unsent draft, empty-home and one-shot gates remain intact. Add deterministic changed-Chat-model-after-insert, picker-removed and unrelated-tab selection races. Retain the no-retry rule after Send; do not infer successful backend execution from a click or tab cleanup.

This is an unfulfilled requirement of the submitted model-preference change, not a new feature request. It does not adjudicate a preferred architectural remedy, change the review scope, or authorize deployment.
