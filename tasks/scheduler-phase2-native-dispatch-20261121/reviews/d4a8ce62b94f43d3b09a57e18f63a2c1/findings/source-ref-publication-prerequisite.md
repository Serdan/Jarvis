# Integration handoff note — remote source branch trails the independently reviewed commit

**Classification:** integration/publication prerequisite; non-blocking to the source-code verdict.  
**Task:** `scheduler-phase2-native-dispatch-20261121`  
**Review:** `d4a8ce62b94f43d3b09a57e18f63a2c1`  
**Frozen code:** `3ea482c5fedd95b3a88817f0b8a432e93955fd34`

Verified twice with independent `git ls-remote origin` (last job `cae152e5f6944860a96a3592ea1c5487`, exit 0):

- `refs/heads/jarvis-review/scheduler-phase2-native-dispatch-20261121/d4a8ce62b94f43d3b09a57e18f63a2c1/r1` = **`3ea482c5fedd95b3a88817f0b8a432e93955fd34`**.
- `refs/heads/feat/scheduler-phase2-native-dispatch-worker6-20261121` = **`ef854433852b95a76b31d7af1465c5e08262a3a7`** (the immediate parent of the frozen code).

The registered clean isolated local worktree is at `3ea482c5...`; its immutable managed review ref proves the exact source, so independent code review is possible. However, a future integration using the recorded **remote sourceRef** presently reaches the *previous* rejected revision, not the accepted candidate. Before an integration submission/publication, the responsible source owner/integrator must verify the exact remote source ref is safely advanced by a normal non-force update to the reviewed SHA and independently recheck its binding. Never infer integration eligibility from an Accepted review alone; never force-push, merge, activate, or replay an uncertain publication here.

This is a handoff/provenance prerequisite rather than an observed defect in the frozen read-only adapter, and does not change the narrow code-verdict on the historical-sequence repair. No source/ref publication was made by Reviewer 4.
