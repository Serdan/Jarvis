# Independent frozen review: Changes requested (P1)

**Task:** manager-reviewer-duplicate-launch  
**Frozen commit:** `7564354e40ad8b18f53f6b71dcfc4c9e81dbbbdc`  
**Review request:** `d97f639cf1224efdb45087cd182c6a71`

The patch correctly introduces exhaustive claimed-review snapshots and guards unresolved observed reviewer sessions, but the stale-yield case still permits a duplicate launch. A terminal `review-finished` reviewer record retains `ExplicitReviewerYield` permanently and makes `pausedClaimReviewers` eligible even after newer attributed reviewer activity. With a claim still active, `decide` then reserves `ResumeVerifiedPausedReview`.

- **Blocking finding:** `findings/stale-reviewer-yield.md` (P1, independently reproduced via an in-memory `decide` sequence on the exact frozen commit).
- **Verification:** `node --test scripts/tests/manager-heartbeat.test.mjs` passed **33/33**. Regression is outside existing tests; reproduction returned `ResumeVerifiedPausedReview` after a later attributed call than the last explicit yield.
- **Required:** Invalidate prior reviewer pause/recovery proof on newer attributed activity; suppress launch while the claim remains active unless a subsequent conclusive yield/completion is observed. Add a negative regression, including journal restart.

This verdict is limited to the submitted frozen commit. No production Manager schedule was activated; no repository source was changed by this review.