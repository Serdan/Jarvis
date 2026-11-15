# Independent frozen re-review: Changes requested (P1)

**Task:** `manager-reviewer-duplicate-launch`  
**Frozen commit:** `096198e2dd60538ac35c85fa4387c267481b2b4d`  
**Review request:** `b49eaf0d928b4e5fa9b6c7d21e082473`

The previous blocking case (stale explicit reviewer yield with a *still-claimed* review) is corrected. The patch records the actual yield timestamp, invalidates stale pause proof when attributed activity is newer, covers operator recovery and journal restart, and preserves fail-closed legacy evidence handling.

**Remaining blocking P1:** `findings/post-yield-activity-without-claim.md`. The fix does not count an active/uncertain *terminal reviewer session* after a newer attributed call when no review remains claimed. A resumed original tab can therefore receive a duplicate `UnclaimedFrozenReview` launch. Independently reproduced twice against the exact frozen commit, including the never-claimed and previously-resolved-claim variants. This is outside all 35 current tests.

**Verification:** clean registered review worktree HEAD matched the frozen SHA; `node --test scripts/tests/manager-heartbeat.test.mjs` passed 35/35 (0 failures). Two read-only `node --input-type=module -e` reproductions exposed the duplicate. No repository changes, production deployment, or Manager activation.

**Verdict:** Changes requested. Add session-level post-end activity suppression independently of claimed review count, with negative restart tests; then submit a new frozen review.
