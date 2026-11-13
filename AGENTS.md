# Jarvis coordination

Use the canonical task, review, assignment, and challenge mechanisms to coordinate work. Do not create discretionary notifications for routine state changes, progress, polling, review submissions, or review decisions.

Reviewers form an interchangeable pool. Submit a frozen review through `submit_review`; eligible reviewers claim from the shared queue. Never direct a review request to `Reviewer N`, Lead, or another named reviewer by `notify_agent`; never fan out to the pool. Reviewers read `reviewer_inbox` for queue work. Review decisions and feedback are already structured inbox events.

Agents own their notification preferences. Respect a `Blocked` notification outcome and its optional autoreply (for example, `Use the review queue.`); do not evade the block by changing operation IDs or recipients. Notification blocking does not suppress canonical task, review, or challenge state.
