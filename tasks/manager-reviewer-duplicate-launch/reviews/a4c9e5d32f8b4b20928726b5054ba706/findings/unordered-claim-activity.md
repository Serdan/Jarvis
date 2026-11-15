# P1 — Unordered claim snapshot can launder post-resolution reviewer activity

**Frozen commit:** `26b5054ba70665a9e0894474c0557f40b07a9bc5`  
**Location:** `scripts/manager-heartbeat.mjs`, `decide()` observed-reviewer processing (`claimObservedActivityAtUtc` update) and `reviewerSessionResumedAfterEnd()`.

The planner records a newer `agent.lastAttributedActivityAtUtc` as `claimObservedActivityAtUtc` whenever `snapshot.claimedReviews` still lists a review. The flags `reviewClaimsVerified` and `agentActivityVerified` independently certify their sources, but nothing proves the claim-state read was made **after** that attributed activity, or that the claimed-review list and activity are chronologically ordered. `capturedAtUtc` permits a fresh assembled snapshot containing an older verified ledger head and a newer attributed event.

**Repro (run independently against pinned source, no edits):**
1. At 02:01:30, Manager observes review R claimed at ledger head H1 and attributed reviewer activity at 02:01:00. Journal enters `observed`.
2. Review R resolves at 02:02:10, committing H2, but the original browser session remains active.
3. Manager constructs another snapshot using the already-fetched, individually verified H1 claimed-review enumeration and a newly fetched attributed tool call from **02:02:20**, with snapshot captured at 02:03:00. Each declared validation predicate passes. `decide()` advances `claimObservedActivityAtUtc` to 02:02:20 although the call happened **after** R resolved.
4. At 02:04:00 the next verified snapshot shows H2 and no claimed reviews, with the same last attributed tool call. `reviewerSessionEnd()` records `CanonicalReviewClaimResolved` and `reviewerSessionResumedAfterEnd()` sees activity equal to the contaminated claim-present lower bound, so it treats the old session as quiescent.
5. **Observed result:** `reservations: [{agentId:"Reviewer 1", reason:"UnclaimedFrozenReview"}]` — the same reviewer can be launched while its earlier tab is still active.

This defeats the P1 duplicate-session safeguard on mixed-time but individually authoritative reads; no forged fields, timestamp regression or invalid ledger head is needed. The 41/41 existing Node tests and `git diff --check` pass, but the tests assume activity and claims are ordered and therefore miss the case.

**Required fix:** Retain claim-present attribution only when positive evidence establishes that the claim was *still present after* the specific attributed call (e.g. read activity first, then re-read and verify the claimed-review set at a canonical ledger head and explicitly carry that ordering proof into the planner). If that ordering cannot be verified, preserve unresolved session capacity rather than marking completion on claim disappearance. Add a deterministic regression where an older claimed head is read before resolution and a newer tool event is read after resolution, followed by H2 with no claim. Assert no new reservation across restart and dry-run as well as normal planning.