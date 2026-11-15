# P1: Post-yield reviewer activity without an active claim still permits duplicate browser launch

**Frozen source:** `096198e2dd60538ac35c85fa4387c267481b2b4d`  
**Location:** `scripts/manager-heartbeat.mjs:288-315, 368-425`

The patch correctly invalidates a persisted explicit yield/recovery when `lastAttributedActivityAtUtc` becomes newer. But the invalidation only changes `pausedClaimReviewers`; it never reclassifies the terminal `review-finished` / `cleared` reviewer session as unresolved. The capacity and eligibility checks in lines 383-388 and 412-425 only catch that reviewer when `claimedReviews` still contains a claim (or `unresolvedReviewers` was set for an `observed` record). For a terminal record with **no active claim**, a later positively attributed call provides evidence that the old browser tab resumed after yielding, yet the planner counts zero active reviewers and reserves another launch for the same agent.

## Independent read-only reproduction

Ran against the registered clean `worker4-manager-reviewer-duplicate` worktree, whose HEAD exactly equals the frozen commit. Used `node --input-type=module -e` to import the actual `decide` implementation; no repository files were changed.

- 02:00: one unclaimed review, Reviewer 1 NeedsWork/Stopped -> reservation `UnclaimedFrozenReview`.
- 02:01: first attributed tool call 02:00:30, **no review claimed** yet -> journal record becomes `observed`; no duplicate.
- 02:04: explicit attributed reviewer yield 02:03, manager disabled to avoid any new reservation -> journal record becomes `review-finished`; counts reviewer = 0.
- 02:06: original reviewer tab makes a new attributed tool call at 02:05, while the latest explicit yield remains 02:03. The queue still contains one unclaimed review, the reviewer still appears NeedsWork/Stopped, and no review claim exists.
- **Actual:** `decide` returns `[["Reviewer 1", "UnclaimedFrozenReview"]]` and counts reviewer = 1 **after reserving the duplicate**. **Expected:** suppress Reviewer 1 and count the post-yield session as unresolved until a newer explicit yield or independently conclusive session end is observed.

The same behavior occurs when a review was previously claimed and later resolved while Manager was disabled, followed by a fresh attributed call in the original tab: after claims become empty, the next enabled heartbeat reserves `UnclaimedFrozenReview` even though activity is newer than the yield.

This is distinct from the newly added regression `a terminal explicit yield cannot outlive newer reviewer activity across journal restart`: that regression deliberately keeps `claimedReviews = [REVIEW]`, which masks the missing session-level guard through existing claim capacity accounting.

## Required correction

On each fresh authoritative snapshot, compare newer attributed reviewer activity with the **latest conclusive end boundary** on terminal reviewer journal records, not just with current claims. If activity postdates that proof and no later conclusive yield/operator recovery or claim/session end has been established, treat the existing reviewer session as unresolved, consume reviewer capacity, and suppress a second launch **even with zero claimed reviews**. Preserve the existing positive relaunch behavior after genuinely conclusive end evidence. Add negative regressions for (a) observed-before-claim -> explicit yield -> resumed old-tab activity -> still-unclaimed queue, and (b) resolved claim -> post-resolution tool activity, including journal persistence. Do not change the live browser bridge or activate the Manager schedule.

The frozen commit's existing 35 Node planner tests all pass; this additional sequence fails the intended no-duplicate invariant.
