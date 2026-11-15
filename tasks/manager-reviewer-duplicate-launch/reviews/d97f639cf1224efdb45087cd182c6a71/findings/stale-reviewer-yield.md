# P1: A stale explicit-yield marker permits duplicate reviewer launches

**Location:** `scripts/manager-heartbeat.mjs:328-339` (and the terminal-record handling at `253-285`).

The planner treats the latest reviewer journal record as `pausedClaimReviewers` whenever that terminal record has `completionEvidence === "ExplicitReviewerYield"` (or `reviewerSessionCleared === true`) and its previously observed claim is still present. Once the record becomes `review-finished`, the journal loop no longer reevaluates that completion against the reviewer's **current** `lastAttributedActivityAtUtc`.

This makes the pause proof permanent even if the reviewer resumes tool activity in an existing tab after yielding. A newer, positive reviewer-attributed tool call is evidence of activity *after* the explicit yield, so it must invalidate the pause; instead the scheduler can reserve a second browser session for the same reviewer while the frozen claim is still active.

## Independently reproduced on frozen commit `7564354e40ad8b18f53f6b71dcfc4c9e81dbbbdc`

1. At 02:00, `decide` reserves Reviewer 1 for one unclaimed review.
2. At 02:01, Reviewer 1 has claimed review R; the attributed tool activity at 02:00:30 moves the journal record to `observed`.
3. At 02:04 with the Manager switch disabled, an explicit Reviewer 1 yield at 02:03 is independently attributed. The old journal record transitions to `review-finished` with `ExplicitReviewerYield`, and no new launch is reserved.
4. At 02:06, with the switch enabled, the same review R is still Claimed by Reviewer 1, but a later positively attributed Reviewer 1 tool call at 02:05 is now visible. No new yield occurred.
5. **Actual:** `decide` returns a fresh `ResumeVerifiedPausedReview` reservation. **Expected:** no launch; this reviewer may already be active.

The reproduction uses the exported `decide` function without modifying repository files and produced:
```
initial                [ 'UnclaimedFrozenReview' ]
claimed                [] observed
yielded-disabled       [] review-finished
activity-after-yield   [ 'ResumeVerifiedPausedReview' ] review-finished
```

This violates the P1 goal of suppressing duplicate taskless reviewer launches after observed activity.

## Required correction

Bind each yield/recovery pause to explicit session evidence and invalidate that pause whenever positively attributed activity occurs later than the yield/recovery boundary, unless a **new** explicit yield (or conclusive canonical claim resolution) is observed. Apply this check even to terminal `review-finished` / `cleared` records. If no conclusive end evidence remains, fail closed and consume the reviewer capacity. Add a negative regression covering yield → newer tool call → claimed review still active, ideally including journal persistence/restart. Existing 33 planner tests pass but miss this sequence.
