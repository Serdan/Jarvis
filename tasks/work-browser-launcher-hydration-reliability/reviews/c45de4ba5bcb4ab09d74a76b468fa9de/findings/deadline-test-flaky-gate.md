# P1 — nondeterministic deadline regression blocks acceptance

**Scope:** frozen `2c1d0084baf1d86f3f05f46f7ebf97a1953f5e95` from `a5cb1906ab5402d047d525e495cbc38fe750cd5c`. `git diff --name-status` confirmed ONLY `browser-scripts/chatgpt/launchAgent.js` and `browser-extension/tests/chatgpt-launcher.test.cjs`; `git diff --check` exit 0.

## Blocking finding — acceptance test can submit within its allegedly pre-send-expired deadline

**Location:** `browser-extension/tests/chatgpt-launcher.test.cjs:563-570`, case `deadline before three inactive samples cannot edit even an empty editor`.

Independent full execution:
`node --test browser-extension/tests/chatgpt-launcher.test.cjs`
at frozen worktree: **42 passed, 1 failed, exit 1**. Failing case reports `actual: outcomeUnknown`, `expected: notSubmitted` at line 566, after ~101.85 ms. An immediately subsequent independent focused execution:
`node --test --test-name-pattern='deadline before three inactive samples' browser-extension/tests/chatgpt-launcher.test.cjs`
**passed, 1/1, exit 0**, ~101.46 ms. This is a deterministic source with timing-dependent test outcome, not a documented stable 43/43 gate.

**Root cause:** test sets `timeoutMs:100`, equal to the launcher's clamped minimum, while the stabilization loop samples immediately and waits `min(75, remaining)` between observations. The third inactive sample can occur just before the 100-ms deadline (timer granularity/scheduling), allowing insertion and exactly one Send click within the valid pre-send window. A response of `outcomeUnknown` is then possible when acknowledgement misses the same overall deadline. On slightly slower scheduling, the source returns `notSubmitted/LaunchDeadlineExceeded` as the test expects. Thus the test incorrectly assumes the deadline must precede the third sample; neither branch alone proves an implementation safety failure.

**Required correction:** make the deadline-exhaustion precondition deterministic (e.g. a controlled clock/scheduler in the hermetic harness with exactly pinned sampling, or an intentionally delayed/active Send condition whose pre-send deadline *provably* precedes stabilization). Assert zero clicks **and** unchanged editor in that forced pre-send-expiration case; do not simply permit `outcomeUnknown` or weaken one-shot safety. If the scenario duplicates an existing deterministic no-send deadline test, replace the timing-dependent assertion with a valid independent boundary case. Rerun full 43+ launcher tests repeatedly from the exact amended frozen revision, plus node syntax, diff check and appropriate adjacent suites. Submit a NEW frozen review for the corrected revision; this verdict does not apply to future commits.

## Other independently checked evidence

The implementation change replaces the separate 1800-ms settle deadline with the one bounded overall deadline, retains three empty/inactive observations, checks zero-turn home and Chat mode before mode switch, checks stable composer and mode nodes around asynchronous waits, revalidates before Send, and marks click exceptions `outcomeUnknown` without retry. New tests cover >1800-ms enabled Send, restored draft, mode/composer mutation, persistent enabled Send and click uncertainty. No other blocker identified in static review.

**Decision:** **ChangesRequested** for flaky required acceptance test; no claim of actual live browser safety violation, no production deployment, no Scheduler launch.