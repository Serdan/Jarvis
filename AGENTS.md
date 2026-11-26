# Jarvis coordination

Use canonical tasks, assignments, reviews and challenges for coordination. Do not send discretionary notifications for routine progress, polling, reviews or resource synchronization. Reviewers claim independently from the shared queue via `reviewer_inbox`; never route or fan out review requests to named Reviewers or Lead. Respect notification blocks and autoreplies rather than evading them. Structured review/task state remains authoritative.

## Jarvis executor details

The parent workspace `AGENTS.md` is the single shared execution/lifecycle contract for this workspace. This section adds Jarvis-specific tool, lifecycle-result and coordination semantics; it does not restate the general persistence rule.

### Instruction delivery and precedence

The launch template tells new sessions to read both their own inbox and the applicable instructions. The inbox supplies the focused assignment and resolved role/agent layers. The explicit `get_agent_instructions` read supplies the applicable workspace/project/directory `AGENTS.md` files and repeats those same role/agent layers; do not treat the repeated role content as a second policy. `resolve_agent_instructions` returns role/agent layers only, not `AGENTS.md` or the assignment. Read the pinned task specification, current assignment generation, latest checkpoint and review feedback before acting. Resolution precedence is global → workspace → project/directory → role (attachment order) → agent → assignment; higher entries retain priority over later entries. A launch-template change affects new launches only. Existing sessions must explicitly reread current instructions after an instruction change.

### Tool calls

Before an unfamiliar, changed or uncertain operation, inspect the actual currently exposed tool schema. Use its exact required parameter names and fields; do not infer arguments from memory, old prompt examples, generic catalog prose or similarly named tools. A schema-validation failure means the rejected call did not execute: inspect the current schema, correct the arguments and retry the same intended operation. Never report success without a successful tool result. If an outcome is uncertain, reconcile the original operation ID against canonical state before retrying; do not create a new operation for the same uncertain effect.

### Lifecycle semantics

For Jarvis, assignment completion is terminal only after `complete_agent_work` succeeds. A blocked or interrupted assignment releases its slot only after `yield_agent` succeeds and returns it to Ready with a checkpoint and actionable continuation. A checkpoint alone, tool-call attempt, schema-validation error or conversational claim is not a lifecycle transition. The successful completion/yield result is the verification: stop immediately without follow-up calls or rereading your inbox. If the result fails or is uncertain, reconcile the original operation and canonical state. Never use an administrative recovery or state correction in place of an ordinary worker yield.

### Pending jobs

Wait only when a coordination operation returned a real pending handle. Use `get_job_result` on that exact handle and never reissue the original operation. Limit current-turn waiting to at most three spaced checks. If the result is still pending or the turn budget is reached, checkpoint the operation ID, job ID/handle, last evidence and exact continuation, then yield; do not spin or poll indefinitely.

Engineering jobs such as builds and tests are different: checkpoint the job ID, command, purpose, source SHA and required terminal evidence; continue independent authorized work, otherwise yield. Do not poll or wait for an engineering job in an inference turn, shorten its timeout or rerun it to obtain a result. Retrieve and reconcile the terminal result once when it becomes available.

### Identity and notifications

Use `agent_inbox` only for yourself and `observe_agent` for other agents. `agentId` is the initiating actor; `targetAgentId` is the subject. Never impersonate a target or read another agent's inbox. Use canonical state for coordination. Handle only your own `PendingNotifications`: acknowledge an exact notification only after it has been handled or deliberately dismissed as obsolete/nonactionable; leave unresolved notices pending. Do not send routine acknowledgment notifications.

## Work implementation and cutover guard

Work architecture and detailed acceptance requirements live in `docs/specs/work-stateful-orchestration-2026-11-17.md` and `docs/specs/work-stateful-implementation-alignment-2026-11-17.md`. Read those when implementing that system, not for routine agent dispatch.

**No silent activation:** Until independent review and explicit operator approval of cutover, the existing Git-backed project/account ledgers remain authoritative. Do not deploy, restart, migrate, activate the unattended Work Scheduler, or introduce parallel old/new live writers without authorization.

Preserve versioned transitions, idempotency, exact source/ref and external-effect provenance, recovery fencing, and no replay of uncertain effects. Proposed SQLite storage and its backup/restore requirements are not live authority. Manual grants remain ephemeral; the separate legacy FeedbackStore and its `attributed_agent_activity` are still relevant when making liveness claims. Missing/reset activity data is not proof of inactivity.
