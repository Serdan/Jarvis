# Independent frozen re-review — Changes requested

**Review:** `a4c9e5d32f8b4b20928726b5054ba706`  
**Frozen source:** `26b5054ba70665a9e0894474c0557f40b07a9bc5` (base `7a14651c1cd620bf707130c26e446b9f9921418f`)  
**Reviewer:** Reviewer 2

The patch addresses the original steady-state duplicate-launch reproducer: an observed taskless reviewer holding a canonical claimed review consumes capacity; explicit yield and claim resolution can release it. The new restart, post-resolution and operator-recovery regressions are substantial. However, the revised claim/activity boundary is not causally safe when review-head and attributed-activity reads interleave, so the P1 duplicate-launch acceptance gate remains open.

## Findings

1. **P1 — Cross-source claim/activity ordering race** (`findings/claim-activity-atomicity-race.md`). An activity sampled *after* a canonical review decision may be attached to a previously fetched head that still shows the claim as Claimed. A subsequent no-claim head then treats the post-resolution activity as pre-resolution and authorizes a second taskless session. Independently reproduced with schema-valid, positively attributed snapshots; requires a verified ordering barrier or fail-closed treatment.
2. **P2 — Dry-run/live parity defect on first observed explicit yield** (`findings/first-observation-dry-run-divergence.md`). The same snapshot and initial journal return BudgetFull in dry-run but ResumeVerifiedPausedReview in live planning; reproduced.

## Verification

- `node --test scripts/tests/manager-heartbeat.test.mjs`: **41 passed, 0 failed** on frozen worktree.
- Inspected the exact base-to-head diff in `scripts/manager-heartbeat.mjs`, its test suite, `docs/ManagerHeartbeat.md`, and `config/manager/Manager.role.md`.
- Executed two separate read-only Node reproductions against the frozen implementation; both exited 0 and produced the mismatches documented above.
- No source files were modified; no Manager schedule, deployment, or browser action was activated.

**Verdict: ChangesRequested.** Resolve the P1 attribution ordering boundary and add the counterexample before considering this P1 follow-up accepted. Fix the deterministic dry-run inconsistency in the same scope.