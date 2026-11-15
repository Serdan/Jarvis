# Jarvis coordination

Use the canonical task, review, assignment, and challenge mechanisms to coordinate work. Do not create discretionary notifications for routine state changes, progress, polling, review submissions, or review decisions.

Reviewers form an interchangeable pool. Submit a frozen review through `submit_review`; eligible reviewers claim from the shared queue. Never direct a review request to `Reviewer N`, Lead, or another named reviewer by `notify_agent`; never fan out to the pool. Reviewers read `reviewer_inbox` for queue work. Review decisions and feedback are already structured inbox events.

Agents own their notification preferences. Respect a `Blocked` notification outcome and its optional autoreply (for example, `Use the review queue.`); do not evade the block by changing operation IDs or recipients. Notification blocking does not suppress canonical task, review, or challenge state.

## Server architecture: stateless by contract

Keep the Jarvis server a simple authenticated relay suitable for the cheapest
droplet that can run it. Minimize its CPU, RAM, disk, and service footprint.
Spreading authoritative persistent state across machines creates unnecessary
backup, migration, recovery, and operational work.

- Clients and the approved Git-backed ledgers own durable project, workflow,
  scheduling, execution, and recovery state. Never make server-local storage
  authoritative for claims, launches, source effects, or other project actions.
- Jarvis-level agent instruction files are the one intentionally durable
  server-filesystem exception. Live connection registries, request correlation,
  bounded transient exports, and other replaceable in-memory state are fine.
- The existing server-side SQLite FeedbackStore may have been previously
  authorized for disposable telemetry. Do not assume it was unauthorized or
  remove it merely for architectural purity. Small, bounded, best-effort
  diagnostics may remain only if losing the database is harmless to operational
  correctness and requires no backup, restore, or migration. Do not introduce
  new durable server-owned databases or files without explicit operator approval.
- IMPORTANT: `attributed_agent_activity` in that SQLite database currently
  influences `AfterAgentIdle` scheduling. An absent, reset, or recreated
  database must never count as proof of inactivity or authorize a launch.
  Fail closed until trustworthy client/ledger-owned activity evidence is
  available; do not call this path disposable until that is demonstrated.

Acceptance test: replace or restart the server with an empty data directory
(apart from Jarvis-level instruction files and normal deployment configuration).
Project correctness, idempotency, scheduling safety, and recovery must survive;
only nonessential diagnostics may disappear. Test missing state, reconnection,
multiple clients, and stale/unknown outcomes before depending on a server cache.
