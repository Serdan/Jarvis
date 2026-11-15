# P2 — First-observation explicit yield gives different dry-run and live plans

**Frozen commit:** `26b5054ba70665a9e0894474c0557f40b07a9bc5`  
**Locations:** `scripts/manager-heartbeat.mjs:388-411, 429-440, 249-265`.

In the first pass that positively observes a reviewer launch, if the reviewer has both (a) a still-claimed review and (b) independently attributed explicit yield after that launch, the state machine finds `ExplicitReviewerYield`. In live mode it mutates the original record to `review-finished` and records `observedAtUtc` and `explicitYieldAtUtc`; `reviewerPauseStillProven` then allows a `ResumeVerifiedPausedReview` reservation. In dry-run mode the original record remains `reserved` and lacks `observedAtUtc`. `reviewerPauseStillProven` tries to compare the yield against `Date.parse(rec.observedAtUtc)` (NaN), cannot establish pause, and counts the existing claim as active.

**Independent reproduction:** one reviewer budget, one outstanding claimed review, reviewer originally reserved at 02:00, first attributed activity 02:01:10, valid `ExplicitAttributedReviewerYield` at 02:01:20, snapshot capture 02:01:30, zero unclaimed reviews. With identical initial journal and snapshot:
- `decide(config, snapshot, journal, now, true)`: `reservations=[]`, `counts.reviewer=1`, `BudgetFull`.
- `decide(config, snapshot, journal, now, false)`: one `ResumeVerifiedPausedReview` reservation, old record becomes `review-finished`.

The dry-run is not a faithful read-only preview of the same pass. Build a common effective record/transition for both modes before eligibility/capacity accounting; only gate persistence and UUID allocation on `dryRun`. Add a first-observation claim-plus-yield parity regression.