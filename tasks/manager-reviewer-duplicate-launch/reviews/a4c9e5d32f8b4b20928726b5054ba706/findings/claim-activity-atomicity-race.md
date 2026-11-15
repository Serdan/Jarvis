# P1 — Post-resolution reviewer activity can be laundered into claim-present evidence

**Frozen commit:** `26b5054ba70665a9e0894474c0557f40b07a9bc5`  
**Locations:** `scripts/manager-heartbeat.mjs:339-350, 239-241, 272-298`; `docs/ManagerHeartbeat.md` claimed-review/agent-activity snapshot protocol.

## Failure

The planner combines a claim enumeration pinned to a verified Git ledger head with a separately attributed client tool-call timestamp. Those reads are not atomic or causally ordered. In an `observed` reviewer record, lines 344-350 update `claimObservedActivityAtUtc` whenever that head still lists any claim and the independently sampled activity time is later. The code never verifies that the tool call happened *before* the claim was decided. Lines 284-298 later treat this timestamp as an end boundary, allowing relaunch when the next ledger head lacks the claim.

## Deterministic independent reproduction (Node; exit 0)

I ran `decide` against the frozen worktree with a one-reviewer budget, no worker budget, and entirely schema-valid verified snapshots:

1. At 02:00, the Manager reserves/launches Reviewer 1. At 02:01 it sees claim A at ledger head H1 and attributed activity 02:00:30; the journal becomes `observed`.
2. The Manager reads review state at H1 at 02:02:05, while A is still Claimed. At 02:02:10 the reviewer decides/releases A, then makes another attributed tool call at 02:02:20 (the old tab has resumed/continued). The Manager samples that later activity and stamps a complete snapshot at 02:02:30, still using its earlier verified H1 review enumeration. The planner advances `claimObservedActivityAtUtc` to **02:02:20**, mislabeling a *post-resolution* call as claim-present.
3. At 02:03 the Manager sees authoritative head H2 with zero claims; attributed activity is still 02:02:20. Because the stored boundary equals the latest activity, `reviewerSessionResumedAfterEnd` treats the session as quiescent, changes the old record to `review-finished`, and emits a **new `UnclaimedFrozenReview` reservation for Reviewer 1**.

This is the exact duplicate-launch exposure the follow-up must prevent. Both review reads are internally consistent with their respective Git heads and all five snapshot verification flags are true; the race is between when the canonical head was read and when the independent activity event was sampled. A `capturedAtUtc` timestamp and a common head for *review queue versus claimed reviews* do not establish ordering between that head and the later client activity. Test `claim-present evidence for later activity preserves safe canonical relaunch` (lines 837-867) assumes the missing causal ordering.

## Required correction

Do not promote a separately read attributed activity event to “observed while claim was present” solely because a previously fetched ledger head still contains a claim. Bind claim-present activity to a verifiably ordered observation barrier (for example re-read a verified canonical review head **after** the activity observation and ensure the claim remains present, with an appropriate version/timestamp contract), or conservatively retain the reviewer-session guard when provenance/order is indeterminate. Add a regression with a claim-head read before resolution and an activity read after resolution, followed by a no-claim head; assert **no second reservation** until an independently verified yield/stop or operator recovery.