# Independent frozen re-review — Changes requested

**Frozen head:** `b0c4c453b3e2230149421e14251a8354bcaf32ca` (base `7a14651c1cd620bf707130c26e446b9f9921418f`).

Reviewed the manager heartbeat planner, its reviewer-claim/session state machine, the Manager instructions, documentation, and regression tests. `node --test scripts/tests/manager-heartbeat.test.mjs` passes **37/37** on the frozen worktree. Existing regressions cover active claims, post-yield activity with and without claims, post-resolution activity on later passes, journal restart, capacity, and audited operator recovery.

**Blocking P1:** `findings/post-resolution-activity-before-manager-observation.md`. When a canonical review resolves and the old tab makes a *later* attributed tool call **before** the Manager first observes the resolved claim, `decide` incorrectly treats the Manager's snapshot capture time as review-completion time. It then reserves a fresh reviewer browser session despite the old session having resumed. Reproduced on the pinned commit via a direct in-memory Node scenario; an unclaimed review launches Reviewer 1 again with no suppression.

**Disposition:** Changes requested. Do not integrate until claim-resolution ordering versus activity is positively established or the ambiguous interval fails closed, with a regression covering the first post-resolution observation and a restart. No source changes, deployment, or browser actions performed by this reviewer.