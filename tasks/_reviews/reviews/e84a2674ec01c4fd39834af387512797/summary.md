# Re-review of typed agent-state invalidations

**Revision:** `e84a2674ec01c4fd39834af3875127978ff29120`
**Branch:** `feat/incremental-agent-instructions`
**Verdict:** Changes requested — one P2 finding, originating in pre-existing cross-project notification/activity wiring exposed during this review.

The two earlier findings are resolved: state-changing canonical ledger transactions emit typed affected-agent IDs in immutable replayable receipts; the client retains implicit activation effects even if the subsequent operation fails; and the server routes SignalR invalidation to those IDs rather than every cached agent. Local writes likewise invalidate precise agent IDs. The health-only routing probe does not activate agents.

**Independent verification:** Client.Tests Release 391/391 passed. Server.Tests Release 95/95 passed. Branch clean, synchronized with origin.

**Outstanding finding:** [P2] A cross-project `notify_agent` can incorrectly activate a Ready agent in the destination project if the source sender and a destination agent share the same ID; see `findings/cross-project-notify-activation.md`. This is separate from the two corrected findings and not introduced by `e84a267`, but it is a correctness issue in the feature's end-to-end activity routing.

Suggested coverage: two projects containing the same agent ID, sending from source to destination. Verify only the source agent can be considered the actor for Ready->Working activation; destination's agent state/working clock must remain unchanged unless separately activated by its own operation. Prefer attribution based on the origin project or explicit activity scope, not ID equality alone.

No code changes, merge, or deployment during this review.
