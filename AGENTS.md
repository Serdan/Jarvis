# Jarvis coordination

Use the canonical task, review, assignment, and challenge mechanisms to coordinate work. Do not create discretionary notifications for routine state changes, progress, polling, review submissions, or review decisions.

Reviewers form an interchangeable pool. Submit a frozen review through `submit_review`; eligible reviewers claim from the shared queue. Never direct a review request to `Reviewer N`, Lead, or another named reviewer by `notify_agent`; never fan out to the pool. Reviewers read `reviewer_inbox` for queue work. Review decisions and feedback are already structured inbox events.

Agents own their notification preferences. Respect a `Blocked` notification outcome and its optional autoreply (for example, `Use the review queue.`); do not evade the block by changing operation IDs or recipients. Notification blocking does not suppress canonical task, review, or challenge state.

## Server architecture: minimal durable Work authority

The **target** architecture is documented in
`docs/specs/work-stateful-orchestration-2026-11-17.md` and its
`work-stateful-implementation-alignment-2026-11-17.md` migration plan.
The operator has explicitly relaxed the old statelessness requirement because
autonomous Work scheduling makes per-mutation remote-Git transactions too
expensive and complex. Keep the server small, but allow the appropriate
durable local state.

- After an independently reviewed and explicitly authorized cutover, **one
  authoritative server-side SQLite database** may own Work, task graph,
  queues, priorities, claims, reservations, account-global capacity,
  authorization receipts, and coordination audit events. Local SQLite
  transactions, not GitHub round-trips, must be the normal mutation path.
  Multiple clients contact the same authenticated service.
- Source repositories and exact frozen implementation/review/integration
  artifacts remain in Git. Export consistent, deterministic human-readable
  JSON snapshots of coordination state to a separate Git backup history
  asynchronously; unavailability of GitHub must not block ordinary claims.
  Backups must be verified by tested JSON-to-empty-SQLite restore.
- Ordinary server restart must preserve local durable state. Loss of the
  host's persistent storage requires restore and **Recovery** reconciliation.
  After a stale restore, the Scheduler must deterministically fence uncertain
  launches, claims and external effects before specialized Recovery work
  investigates newer Git artifacts. Never equate missing data with proof
  that an action did not happen.
- Preserve versioned state transitions, idempotency, source/ref provenance,
  STOP, generation, exact review/integration outcomes, one-shot external-effect
  journals, permission boundaries, and fail-closed unknown outcomes.
- Server-memory-only operator-approved **manual grants** remain intentionally
  ephemeral and do not enter SQLite or backups. Never export plaintext
  scheduled bearer credentials. The existing SQLite FeedbackStore remains a
  separate legacy/telemetry concern; do not replace or remove it on
  architectural-purity grounds.
- Important current transitional fact: `attributed_agent_activity` in the
  existing FeedbackStore can influence `AfterAgentIdle`. Loss or reset of
  that data is not proof of inactivity and must not authorize a launch.

**No silent activation:** until the new backend, importer, backups, recovery
and safety gates are reviewed and the operator explicitly approves cutover,
the existing Git-backed project/account ledgers remain authoritative for
existing live and default-off Work behavior. Do not introduce simultaneous
old/new live writers, deploy/restart, or launch the unattended Scheduler merely
because these target instructions have changed.
