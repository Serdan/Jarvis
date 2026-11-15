# P1 — A tool call after review resolution but before Manager observation permits a duplicate reviewer launch

**Location:** `scripts/manager-heartbeat.mjs`, `reviewerSessionEnd` (approximately lines 216–234), the observed-review transition (322–341), and `reviewerSessionResumedAfterEnd` (263–282).

**Problem:** Claim disappearance at a different verified ledger head correctly proves the claim is resolved, but it does **not** prove the old browser session has stopped. On the first snapshot that notices the resolution, `decide` stamps `completedAtUtc = snapshot.capturedAtUtc`. The resumed-session check then uses that *observation time* as if it were the moment the review completed. An attributed tool call made by the old tab after the actual review decision but before that Manager snapshot appears to precede `completedAtUtc`. The planner immediately frees the reviewer and emits a fresh browser launch.

**Reproduction against frozen commit `b0c4c453b3e2230149421e14251a8354bcaf32ca`:**

1. At 02:00:00, an unclaimed review launches Reviewer 1; 02:00:30 is the first attributed call. Manager at 02:01 sees review R claimed at ledger head A and records an `observed` session.
2. Review R resolves at 02:02:10 (ledger head B). The *same old browser session* makes another attributed call at 02:02:20, before Manager's next pass.
3. Manager at 02:03 obtains a complete, verified snapshot: head B, `claimedReviews: []`, one unclaimed review, Reviewer 1 `NeedsWork/Stopped`, and `lastAttributedActivityAtUtc: 02:02:20`.
4. `decide` transitions the old record to `review-finished`, stamps `completedAtUtc: 02:03:00`, then reserves **Reviewer 1 again** with reason `UnclaimedFrozenReview`. `suppressed` is empty.

This was reproduced with a direct `node --input-type=module -e` call importing `decide` in the frozen worktree; exit code 0 and the result contains the duplicate reservation. All 37 existing focused tests pass, but their post-resolution-activity test only covers activity *after a Manager pass has already observed the resolution*, so it misses this interval.

**Why it matters:** The change's explicit P1 objective is to suppress duplicate launches after review resolution while the old taskless session remains active. The resolution ledger-head transition and later activity are unordered in the available snapshot evidence. Treating capture time as resolution time is unsound and can start two live reviewer tabs, consuming capacity incorrectly.

**Required fix:** Do not use `snapshot.capturedAtUtc` as an ordering proof for the canonical review decision. Obtain positively ordered resolution/session-end evidence, or fail closed if attributed activity could have followed the resolution (including activity newly observed in the same snapshot as a claim disappearance). Add a regression with 02:02:10 resolution / 02:02:20 tool call / 02:03 Manager snapshot, including a journal-restart variant. Retain a positive test for proven quiescence or explicit yield.