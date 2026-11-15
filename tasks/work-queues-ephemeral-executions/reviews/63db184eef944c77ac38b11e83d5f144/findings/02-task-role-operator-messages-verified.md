# Verification V02 — Task/Role operator communication (no blocking finding)

**Source:** frozen 35e7465a122fd9d767a9f75494b241ff2eb7fe72; §§1, 3–6, 11–13, especially lines 349–371 and 381–409, and negative-space fixtures 60–66.

## Requirements traced to the contract

- **Durable recipient, not agent routing.** Exactly one immutable recipient Task(projectId,taskId) OR Role(projectId,roleId) is bound at posting. A Role(Jarvis,Lead) thread exists without any running Lead, numbered label or permanent agent registry. Full ordered conversation and messages survive new UUIDs and role configuration changes. Agent UUID remains an execution/audit handle; it is not a new permission principal.
- **Visibility versus ownership.** PostOperatorMessage appends the immutable message and pending handler demand in one project Git transaction. Reading or viewing is passive and cannot consume. ClaimPendingSubjectMessages performs a project CAS against subject queue version, current launch/role/task eligibility and handler generation. The first successful eligible CAS owns exactly the message IDs, while competitors retain read visibility but not designated responsibility.
- **Task semantics.** An active eligible task-stage actor can accept a Task message even amid valid parallel task stages. A message does not bypass task-stage claim, authoritatively change scope, reopen a closed task, unblock conditions, or confer a review/merge decision. Without an active eligible actor, it persists in task attention for later activation/triage. This is the specified first-actor model rather than a guarantee of instant dispatch for every dormant task.
- **Role demand without a running Lead.** Unhandled Role(Lead) messages contribute verifiable role-specific queue demand. Manager may create a role-assigned Lead launch only under the account-global capacity CAS and project demand-credit reservation. A new Lead can claim its one primary role-message work item; an already active Lead can accept message responsibility as an ancillary lease without a second primary task/work claim or another global token.
- **Concurrent demand safety.** If Manager already reserved unclaimed Lead launch L against M1, active Lead A may handle M1 only when the very same project CAS can rematch all other admitted compatible unclaimed credits injectively to distinct remaining message/work generations. With M1 alone, A defers and cannot strand L; with a compatible M2, L can be rematched before A accepts M1. The matching is against *feasible* subscriptions and the latest verified index coverage. Opposite claim orders converge; Manager cannot count a covered M1 as fresh demand.
- **Reply, acknowledgment and crash.** CompleteSubjectMessageHandling atomically records reply/explicit handled result (or an already durable receipt) and the Handled transition for exact message IDs and handler generation. Passive read and an alert never acknowledge. A stale handler cannot complete after its generation is fenced. Terminal, yield and verified reclaim transitions release any unfinished handler leases in the same project CAS, preserving original thread and partial work for successor delivery. Stable message/response operation IDs prevent duplicate canonical replies after lost ACKs.
- **Channel and workflow separation.** STOP alerts remain on existing queue_response_alert / get_response_alerts / clear_response_alert addressed to immutable execution UUID; ordinary task/role conversations are not a second STOP bus. Replies satisfy a blocking challenge only via its explicit versioned domain transition. The TUI shows Role/Lead messages, pending/claimed/handled receipts and task/role history, not a phantom worker inbox.

## Independently examined races

1. A and B both view M1, then claim: one subject CAS winner, one loser; no read consumption.
2. No Lead exists, M1 posted: pending verified role demand triggers the normal Manager/global capacity/handoff path, not an uncounted session.
3. A already holds a primary planning task while L is reserved against M1: ancillary acceptance checks exact compatible injective rematching; refuses if M1 is L's only demand.
4. A's response versus lost acknowledgment versus A's terminal/reclaim: canonical reply/handled receipt or redelivery; never a second unverified response.
5. Task T has concurrent Worker and Reviewer stages: exactly one handler lease; task mutation authority remains with the canonical task/review transition.
6. The message index is missing or stale: fail as coverage-incomplete rather than healthy empty.

## Assessment and implementation validation

**The subject-addressed queue requirement is met at the proposed-contract level; no blocking defect identified.** Tests 60–66 specify the core concurrency and role-demand cases, but are not executed evidence.

During implementation, explicitly test the atomic boundary between primary ClaimNextWork(role-message) and ancillary ClaimPendingSubjectMessages for the *same generation*, handler completion racing terminal/reclaim, and a second operator message posted while an earlier handler lease is active. Preserve exact message-to-response provenance rather than relying on a passive cursor or unverified UI notification. These are verification gates consistent with the existing spec, not a request to add a second security/identity layer.