# Orchestration V2: commit review workflow

**Implementation status:** Isolated client-side V2 domain prototype. Not wired to MCP, the TUI, or the V1 task store. Do not migrate production tasks yet.

## Contract

A review request is a durable ledger record for **one exact source Git commit**. A reviewer does not approve "whatever the branch currently contains."

1. Register agent and reviewer identities. Create a task, assign a primary attempt with owner and independent reviewer, then BeginAttempt. Only a Working attempt holds an agent execution slot.
2. The agent commits source changes in a normal Git worktree. The source commit must be a valid Git commit reachable from the specified local `refs/heads/...` source branch.
3. SubmitForReview supplies a caller-stable operation UUID, task ID, exact current attempt version and assignment generation, an independent review UUID, commit OID, source ref and summary. One ledger commit atomically persists the review request, changes the attempt to ReadyForReview, and releases the agent's execution slot. The source branch, index and HEAD are **not** modified.
4. GetReviewerQueue reads the canonical remote ledger and projects **Pending** requests for the named reviewer, ordered by submission sequence and task ID. It returns the exact commit OID, source ref and summary for inspection. The reviewer must inspect the pinned commit rather than a moving HEAD.
5. The assigned reviewer decides with Accept or RequestChanges, matching the pending review UUID and expected review record version. The decision, feedback and attempt transition are committed atomically. An agent cannot review its own attempt.
   - **Accept** requires that the referenced commit object is available to the reviewer's client. It marks the review Accepted, the attempt Accepted, and the task Implemented. It **does not** merge, push, integrate, close or retire the worktree.
   - **RequestChanges** requires feedback, marks that review ChangesRequested, and returns the attempt to Queued with a new assignment generation. It does not seize the owner's slot or interrupt other work. A later submission must use a fresh review UUID; previous reviews and feedback remain in Git.
6. Source integration and remote push are a separate verified workflow. An accepted review remains bound to its submitted SHA. An implementation may need to push its source branch explicitly for a reviewer on another device; the ledger does not implicitly push source commits.

Ledger records are at `tasks/<taskId>/task.json`, `tasks/<taskId>/attempts/<attemptId>.json`, `tasks/<taskId>/reviews/<reviewId>.json` and `agents/<encodedAgentId>.json`. They are updated using a **single canonical orphan-branch fast-forward Git commit** with one operation-index record. An operation UUID survives lost responses; retries consult the indexed result before checking state preconditions. Competing writes re-evaluate the whole transition against the newly fetched remote tip. Reviewer requests are a projection of these durable records, not a parallel queue.

## Verification

- Two independent clones and a disposable bare Git remote.
- Create/assign/begin/submit; independent reviewer queue sees exact SHA.
- Operation UUID retry after a cross-clone submission yields the original result, not a second review.
- Owner cannot accept its own submission.
- RequestChanges preserves feedback and increments assignment generation; stale generation cannot begin.
- Submission releases the execution slot; a returned review cannot interrupt another Working attempt.
- Reviewer on another clone cannot accept an unavailable source commit; explicit source push/fetch makes the commit inspectable.
- Approval marks Implemented but does not implicitly integrate or push source code.
- Existing LedgerStore concurrency, rewrite detection and source checkout isolation tests continue passing.

## Remaining integration work

This prototype does not expose new MCP commands or replace current V1 task handling. Before rollout, add native command DTOs, permissions/capabilities, reviewer inbox and task views, approved canonical binding configuration, spec authoring, full integration evidence, migration, and the protocol 4.0 cutover. Registration and task creation here are minimal domain bootstrap operations, not the complete V2 task authoring/migration facility. Agent labels distinguish logical actors; the actual trust boundary remains authenticated Git/Jarvis permissions, not cryptographic per-agent identity.
