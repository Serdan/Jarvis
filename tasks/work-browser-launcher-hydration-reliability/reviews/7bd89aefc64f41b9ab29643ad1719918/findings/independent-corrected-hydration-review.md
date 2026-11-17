# Independent corrected hydration and pre-Send review — Reviewer 1

## Frozen provenance and ownership

- Reviewed `a531b94d382bca321cb8985b52eb31d1d09e994c` against required original baseline `a5cb1906ab5402d047d525e495cbc38fe750cd5c`; registered worktree HEAD independently returned the exact frozen OID and worktree was observed clean.
- Diff spans only `browser-scripts/chatgpt/launchAgent.js` and `browser-extension/tests/chatgpt-launcher.test.cjs` (211 insertions, 29 deletions); no Server/Client/Scheduler/Work-state edits.
- Comparing prior rejected `2c1d0084baf1d86f3f05f46f7ebf97a1953f5e95` with corrected head shows a **test-only** virtual-clock/harness change. Launcher production logic is unchanged in this correction.

## Independently executed checks

1. `node --test browser-extension/tests/chatgpt-launcher.test.cjs`: **43 passed / 0 failed / 0 skipped**, exit 0, independently run twice from frozen worktree.
2. `node --test browser-extension/tests/bridge.test.cjs browser-extension/tests/native-private-host-gate.test.cjs`: **68 passed / 0 failed / 0 skipped**, exit 0.
3. `node --check browser-scripts/chatgpt/launchAgent.js` and `node --check browser-extension/tests/chatgpt-launcher.test.cjs`: both exit 0.
4. `git diff --check a5cb1906ab5402d047d525e495cbc38fe750cd5c a531b94d382bca321cb8985b52eb31d1d09e994c`: exit 0.

## Root-cause resolution and safety analysis

The old loop's independent 1800-ms stabilization deadline could classify a fresh empty editor as `UnexpectedSendState` before ChatGPT's hydrated Send control became inactive. The corrected loop uses the *single* bounded overall launch deadline and three consecutive empty/inactive observations. An enabled Send is an unsafe observation, not authority to insert text. A >2050-ms enabled-Send fixture now succeeds once after stabilization, while persistent enabled Send exhausts the budget without editing or clicking.

The formerly flaky 100-ms boundary test previously relied on wall-clock scheduling. Now the test injects a VM-only `Date.now()` and timer; the precise observed sleeps are 75 ms plus 25 ms. The third inactive sample occurs exactly at the deadline: `LaunchDeadlineExceeded`/`notSubmitted`, original empty editor, zero clicks. This addresses the previous reviewer’s blocker without loosening production guards or changing production source.

Additional static/fixture checks confirm route and zero-user-turn rechecks after mode hydration, stable mode-control/editor identity and Chat selection throughout asynchronous waits, draft-reappearance and changed-composer fail-closed results, prompt-integrity check before Send, and immediate deadline rejection before clicking. Send is attempted at most once; click uncertainty is `outcomeUnknown`, never retried; durable submission requires user-turn evidence plus a server-issued conversation UUID. Page-facing argument whitelist still rejects credential/private bootstrap fields and diagnostics expose bounded structure, not text.

## Verdict and scope limit

**No blocking finding. Accept exact frozen P1 change.** Hermetic test and static source acceptance do **not** establish live ChatGPT UI behavior under all hydration races. This decision does not authorize merging unreviewed successor commits, live browser dispatch, Work Scheduler enabling, deployment, or source integration. Those remain separate control gates.
