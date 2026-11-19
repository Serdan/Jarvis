# Jarvis coordination

Use canonical tasks, assignments, reviews and challenges for coordination. Do not send discretionary notifications for routine progress, polling, reviews or resource synchronization. Reviewers claim independently from the shared queue via `reviewer_inbox`; never route or fan out review requests to named Reviewers or Lead. Respect notification blocks and autoreplies rather than evading them. Structured review/task state remains authoritative.

## Work implementation and cutover guard

Work architecture and detailed acceptance requirements live in `docs/specs/work-stateful-orchestration-2026-11-17.md` and `docs/specs/work-stateful-implementation-alignment-2026-11-17.md`. Read those when implementing that system, not for routine agent dispatch.

**No silent activation:** Until independent review and explicit operator approval of cutover, the existing Git-backed project/account ledgers remain authoritative. Do not deploy, restart, migrate, activate the unattended Work Scheduler, or introduce parallel old/new live writers without authorization.

Preserve versioned transitions, idempotency, exact source/ref and external-effect provenance, recovery fencing, and no replay of uncertain effects. Proposed SQLite storage and its backup/restore requirements are not live authority. Manual grants remain ephemeral; the separate legacy FeedbackStore and its `attributed_agent_activity` are still relevant when making liveness claims. Missing/reset activity data is not proof of inactivity.
