# P1: duplicate taskless reviewer launch while a claimed review is in progress

**Frozen commit:** `062dd4a5e3dbfc9e47a49c851e279ec256f1629f`  
**Location:** `scripts/manager-heartbeat.mjs:173-198, 199-208, 222-249`

The journal marks a reviewer launch `observed` after the first positively attributed tool call. On that pass `observedThisPass` suppresses another reservation. But on later passes, `observed` is terminal and the reviewer-specific eligibility check only requires `NeedsWork/Stopped`, `slotAvailable`, no Ready assignment, and `unclaimedReviewCount > 0`. A claimed review is independent of task assignment: a reviewer can hold a `Claimed` frozen review while still being `NeedsWork/Stopped` with an available canonical task slot. No claimed-review or live-reviewer-session evidence is represented in the planner's snapshot.

**Independent deterministic reproduction (Node, exit 0):**
1. Reviewer 1 is `NeedsWork/Stopped`; review queue has two unclaimed reviews. A first planner pass reserves Reviewer 1.
2. Reviewer 1 begins work and claims one review; queue now has one unclaimed. Attributed activity at 02:00:30 is verified by a 02:01 snapshot, but the canonical taskless state stays `NeedsWork/Stopped`. The next planner pass transitions the first journal record to `observed` and makes no immediate reservation.
3. At 02:16, without any reviewer completion or yield and with the same claimed review still active, another fully verified snapshot has `unclaimedReviewCount: 1` and the reviewer still `NeedsWork/Stopped`. `decide` returns a **new `UnclaimedFrozenReview` reservation for Reviewer 1**.

Expected: no duplicate browser launch for that reviewer while their earlier claimed review remains in progress, even if more unclaimed reviews exist. Actual: the second reservation is eligible and can lead to a second ChatGPT session. This violates the no-duplicate-launch requirement and bypasses effective reviewer capacity accounting.

**Required:** Carry authoritative claimed-review/active-reviewer-session information (or an equivalent durable reviewer lease/positive completion protocol) into eligibility and capacity accounting. Keep the session reserved or fail closed until an explicit, verifiable completion/release condition; do not treat first tool activity as proof the review session ended. Add a regression covering observed first activity, claimed review still pending, a later heartbeat with additional queued reviews, restart, and eventual safe relaunch after completion. Preserve the existing worker yield and blocked pre-dispatch fixes.