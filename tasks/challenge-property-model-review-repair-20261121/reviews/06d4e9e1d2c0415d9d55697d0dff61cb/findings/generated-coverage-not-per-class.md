# P1 — Mixed generator does not assert positive and negative outcome coverage per operation class

**Exact frozen source:** `332020cd50f3a2a3986933763edb8f81251e0b1c`.

The new model and generator at `Client.Tests/ChallengeStateMachinePropertyTests.fs:843-1022` are a substantive improvement: they include task/attempt state, dependency waits, owner and generation transitions, auto/manual requeue, challenge operations, retries and alternating two-clone sampling.

However, `GeneratedTaskAndChallengeCommandsExercisePositiveAndNegativePaths` (1228-1258) checks only **aggregate** accepted/rejected/replayed counts across 192 × 44 generated actions, plus a `seen` set populated *before observing the outcome* for each Kind. It does not assert that every essential Kind produced both an accepted and a rejected transition, or that the relevant pre-/post-generation and dependency contexts were reached. For example, all `ReopenGate` actions could reject while `seen` still contains `ReopenGate`, and the large aggregate accepted count could be supplied by unrelated `WaitOn` and `PassGate` actions. The test would remain green.

This fails the prior review's explicit requirement to **assert essential action-class coverage in positive and negative contexts**, not just generate the names. Four sampled real-Git seeds at 1261-1271 do not add per-class coverage accounting.

**Required:** Maintain per-command-class outcome counts and phase/context coverage, then assert each required accepted/rejected/replay/auto-/manual-requeue case actually occurs in the reference tier and a bounded appropriate subset is exercised in real Git. Avoid imposing impossible outcomes on operations that are intentionally observation-only. This is a test oracle/coverage defect, not a production finding.