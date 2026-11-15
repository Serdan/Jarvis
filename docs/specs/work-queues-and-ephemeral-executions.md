# Work queues and ephemeral agent executions

**Status:** Proposed architecture specification; design reference, not implemented or deployed.
**Date:** 2026-11-15
**Scope:** Jarvis per-project coordination, Git-backed work ledger, agent roles, queue dispatch, resumable execution, reviews, integration, challenges, Manager, and migration.
**Authority:** This document proposes a future replacement for the persistent agent-assignment portions of docs/specs/orthogonal-agent-state.md and the current Manager/reviewer launch convention. Existing APIs and live ledger semantics remain authoritative until a separately reviewed, operator-approved cutover.

## 1. Decision

Jarvis manages **durable work** and **ephemeral executions**, not durable assignments to named worker IDs.

- Tasks, frozen reviews, integrations, planning requests, and challenge follow-ups can publish actionable items to project-scoped work queues.
- Role subscriptions determine which queues a newly launched execution is expected to consume. A role does not imply a permanently active session or perpetual work item.
- Manager uses queue availability and role concurrency budgets to launch disposable agent sessions. Each launched session atomically claims **one** eligible work item; the Manager does not bind tasks to worker IDs in advance.
- Every claimed item has an immutable checkpoint history, latest continuation instructions, and an explicit terminal transition. Any compatible session can continue it on a later activation.
- Yielding unfinished work atomically persists a checkpoint and releases its claim. Completing a stage advances the work to its next explicit queue or terminal state. Blocking frees its execution slot while retaining a durable dependency.
- An agent ID is a coordination/attribution label, **not** a security principal. All agents running under the owner's account have the same account permissions. Do not implement agent-ID-based permissions, role ACLs, or anti-impersonation mechanisms. Future contributors receive permissions at the authenticated account level.

The role responsibilities are:

| Role | Responsibility |
| --- | --- |
| Manager | Allocate bounded execution slots and dispatch confirmed one-shot launches for queues with actionable work |
| Lead | Triage and prioritize work, set implementation readiness and workflow policy; no per-worker assignment maintenance |
| Worker | Atomically claim implementation work, modify its isolated source context, checkpoint, yield, block, or submit completed implementation |
| Reviewer | Claim frozen independent review work, record findings and decision |
| Integrator | Claim accepted integration work, validate and integrate **exact reviewed source**, record immutable evidence and completion |

A review, integration, or planning item needs no artificial implementation task merely to obtain a checkpoint.

## 2. Explicitly retained boundaries

1. The existing project's canonical Git orphan ledger remains the sole source of coordination truth. No new database, external message bus, or server-resident mutable workflow database.
2. Source branches, Git worktrees, immutable review refs and integration evidence remain distinct from orchestration state. An agent's conversation is never the durable work record.
3. The server continues exposing operations over the owner's authenticated account and the existing client project binding. Queue subscriptions and claim generations are coordination tools, not new permission boundaries.
4. The workflow does **not** infer acceptance from task completion, merge from review acceptance, or task closure from integration. Domain transitions remain explicit and independently auditable.
5. No persistent worker assignment, exclusive reviewer identity, or dedicated Integrator task is required. A named persistent agent ID may still be used for logging, role attachment, and convenience.
6. Browser tabs can be closed immediately after a confirmed one-shot submission; tab lifetime is not evidence that a model session ended. An uncertain submission must not be blindly repeated.
7. New work-queue plumbing is not permission to deploy, restart the live server/client, enable the Manager schedule, or launch unattended agent sessions. Those are separate rollout operations.

## 3. Vocabulary and durable objects

**Task** — Durable unit of requested work with canonical specification and lifecycle. It may produce multiple implementation stages and reviews; it is not owned perpetually by a worker ID.

**Work item** — One actionable stage or event, referencing its authoritative subject (task, review, integration candidate, challenge, or planning event). It is placed in exactly one queue and has one current claim at most.

**Queue** — Project-scoped index of work items eligible for a type of role. Standard queues initially: planning, implementation, review, integration, and challenge-triage. Queues are not authorization boundaries.

**Subscription** — Persistent role configuration listing queues and, optionally, selection policy. An agent can inspect or claim subscribed queues as a matter of cooperative protocol; the account's actual tool permissions are unchanged.

**Launch** — A bounded, journaled attempt to start one session for a role/queue. It consumes Manager capacity even before a work claim exists. A launch may end without finding work.

**Activation** — One execution of a role that has claimed an item, identified by an opaque activation ID and claim generation. It can checkpoint while running and must reach an explicit yield, stage-complete, or block outcome. An activation is not a task or a persistent worker assignment.

**Claim** — Exclusive, current reservation of one work item by one activation, protected by the ledger's atomic compare-and-swap and a monotonically increasing claim generation. The generation is a stale-write coordination guard, not an identity or security check.

**Checkpoint** — Immutable progress evidence linked to the work item and activation; readable independently of any earlier ChatGPT session.

**Dependency** — A canonical condition/challenge that keeps a work item blocked until its specified release rule is satisfied.

Agent display should primarily report available work, pending launch, active executions, and blocked work. The old Waiting/NeedsWork/Parked roster distinction is removed. If agent-level labels are shown, Idle is no activation, Ready is a confirmed pending dispatch, and Working is an active execution; Blocked belongs to the work item, not to an agent. No persistent agent Ready state is required to select a queue.

## 4. Canonical records and identity

The schema below is illustrative; exact on-disk names and fields must be finalized with tests. Reuse the verified Git ledger's commits, operation receipts, review records, challenge indexes, and concurrency rules.

~~~text
work-items/<work-id>/item.json
work-items/<work-id>/events/<sequence>.json
work-items/<work-id>/checkpoints/<sequence>.json
work-activations/<activation-id>.json
work-index/<queue-id>/<shard>.json
queue-subscriptions/<role-id>.json
~~~

~~~json
{
  "workId": "work-immutable-id",
  "queueId": "implementation",
  "subject": { "kind": "Task", "id": "task-123", "stage": "implementation" },
  "origin": { "kind": "PlanningDecision", "id": "decision-456", "ledgerSequence": 1234 },
  "priority": 20,
  "status": "Available",
  "blockedBy": [],
  "claimGeneration": 3,
  "claim": null,
  "latestCheckpointSequence": 1208,
  "version": 7
}
~~~

~~~json
{
  "activationId": "activation-opaque-id",
  "workId": "work-immutable-id",
  "claimGeneration": 4,
  "roleId": "worker",
  "agentId": "Worker 2",
  "launchId": "launch-opaque-id",
  "state": "Working",
  "sessionIds": ["opaque-provider-session"],
  "startedAtUtc": "2026-11-15T00:00:00Z"
}
~~~

The agentId in an activation is descriptive. Changing it gives no additional permissions and is not a requirement for resuming the same work on a later activation. The claim generation and work version coordinate stale writes; they do not prove who the human or model is.

Work items are durably keyed by a deterministic identity derived from project, queue, authoritative subject, and the exact event/stage generation that made work available. Replaying the same event creates no second item. A new revision or ChangesRequested decision creates a new stage generation where necessary, without rewriting or erasing old events.

A task's implementation attempt can remain as the stable **work-stage** record spanning several activations, but it has no permanent ownerAgentId. Historical owner and assignment records are retained as audit provenance, not as current scheduling authority.

## 5. Source-of-truth and queue publication

- A work item points to canonical data; it does not duplicate task specifications, review findings/decisions, accepted source hashes, or integration receipts as alternative authority.
- A domain change producing work (task intake, Lead readiness decision, frozen review submission, review verdict, challenge satisfaction) publishes or updates the corresponding item **in the same Git-ledger transaction**, or uses a deterministic reconciler that rechecks the source and commits the missing projection before exposing availability.
- If a projection is missing, stale, or unverified, the system must not advertise a healthy empty queue or silently invent eligibility. Query the source, repair the derived index with CAS, or report incomplete coverage.
- In-memory queue counts and sharded indexes accelerate Manager reads but never authorize a claim. ClaimNextWork revalidates item state, subject state, dependencies, version and claim in the authoritative ledger.
- Queue creation is domain-specific and explicit. This design is not a general event bus, scriptable rule engine, or automatic approval system.

The initial producers are:

| Canonical event | Queue item |
| --- | --- |
| New task requiring triage or priority change | planning |
| Lead marks implementation stage actionable | implementation |
| Worker submits a new exact frozen review | review |
| Review returns ChangesRequested | implementation correction, with prior findings and source context |
| Review is explicitly Accepted and integration is pending | integration |
| Pending challenge condition requires triage/action | challenge-triage or a queue specifically configured for that condition |
| Blocking dependency becomes satisfied | Existing blocked work item becomes Available; no duplicate item |

Task completion without review, acceptance without integration, and non-code work are valid policy variants; the Lead selects workflow requirements in the task specification/queue metadata. The queue mechanism must not force all tasks through review or merge.

## 6. Subscriptions, prioritization and fairness

A role subscribes to one or more queue IDs in versioned project configuration. Changing the configuration affects future claims, not existing activations. Default role subscriptions: Lead to planning, Worker to implementation, Reviewer to review, Integrator to integration. Manager is a scheduled control-plane actor, not a permanent queue claim.

Queue selection is deterministic at one verified ledger snapshot: highest priority first (lower numeric priorityRank if retaining current convention), then older enqueue sequence, then stable item ID. If multiple queue subscriptions exist, configured queue weights/priority are explicit. Starvation prevention can be added after measuring actual contention; do not hide ordering behind agent-ID names.

Lead prioritizes **work**, not people. It may defer an item, change priority or readiness, or choose a required skill/role, without assigning a named Worker N. A worker that finishes one activation does not acquire a preferred claim on the next one.

A role subscription is expected behavior, not access control. The current owner account remains able to invoke the same Jarvis operations under any agent label.

## 7. Claim, checkpoint and terminal state machine

Work item states:

~~~text
Available --atomic claim--> Claimed
Claimed   --checkpoint-->   Claimed
Claimed   --yield-------->  Available
Claimed   --block-------->  Blocked
Blocked   --wake--------->  Available
Claimed   --finish-stage->  Completed (+ optional next queue item)
Available/Blocked --cancel-> Cancelled
~~~

A finished stage is not necessarily a finished task. For example, implementation completion can produce an available frozen review item; the implementation item is Completed while the task remains InProgress.

**ClaimNextWork** selects one eligible item, allocates a fresh activation ID, increments claimGeneration and atomically updates work item + activation within one Git transaction. Competing clients must produce at most one winner. A loser rereads the queue and may claim a different item; it never assumes the original claim succeeded. The return includes canonical subject, frozen source/spec context where relevant, latest checkpoint, claim generation, exact ledger snapshot, and the workflow continuation requirements.

**CheckpointWork** appends immutable summary, nextAction and optional evidence/source SHA while preserving Claimed. Replays with the same operation ID and arguments return one receipt; different arguments for the same operation ID are invalid.

**YieldWork** is a *terminal operation for that activation*: atomically writes a final checkpoint, marks the activation Yielded, releases the item claim, and makes unfinished work Available. The next worker can have any label or session ID. No extra task reassignment or ownership transfer is needed. Old activations are not silently resumed.

**CompleteWork** writes a final checkpoint, terminates the activation, transitions this stage to Completed, and records any explicit next-stage item(s). It must not implicitly close a task, approve a review, or merge code.

**BlockWork** writes a checkpoint and dependencies, terminates the activation, and leaves the item Blocked with no active claim. When dependencies are satisfied, the original item is requeued once with its existing checkpoint history.

A session may make several checkpoints before it yields. A claim is not released merely because a browser tab closes, a session ID changes, an elapsed-time threshold passes, an agent returns a prose message, or a review verdict is written. Work completion and session termination are separate facts.

Every work-scoped mutation compares the latest work version and claim generation. An obsolete activation cannot publish new checkpoints, yield, or complete an item after its claim was explicitly released. This is coordination against stale work, **not** an agent-ID permission check; account-level Git/file tools retain their existing permissions.

## 8. Manager execution and capacity

Manager's only required scheduling decision for normal queue work is:

~~~text
launchCount = min(eligibleWorkCount, freeRoleCapacity, freeGlobalCapacity,
                  remainingDispatchBudget)
~~~

Counts use verified canonical queue eligibility and include in-flight, submitted, observed and uncertain launches. Role budgets are configuration, not a fixed count of persistent worker IDs; retain current hard ceilings (5 worker sessions, 3 reviewer sessions) until explicitly revised. Other roles can have separate small limits.

For each launch:

1. Read a verified consistent work/claim/launch snapshot. If coverage or activity evidence is incomplete, suppress launch and report why.
2. Durably reserve one role-capacity launch ID, *not* a particular task or worker assignment.
3. Dispatch one ChatGPT bootstrap for project + role + eligible queue(s) + launchId, using the existing one-shot submission semantics. Close its temporary browser tab once submission is positively confirmed.
4. The newly started agent reads its role instructions and invokes ClaimNextWork. If the queue has emptied, it records a no-work terminal outcome and ends; the Manager must not spin or keep launching because of the stale count.
5. When the claim succeeds, its activation becomes Working. Session checkpoints and final outcomes are canonical work mutations. On a terminal outcome the Manager releases role capacity and may dispatch another session.
6. A submitted-but-unobserved or uncertain browser launch still consumes reserved capacity until positively reconciled. No retry of an ambiguous submit.

The Manager must not launch merely because a Worker/Reviewer/Integrator/Lead ID is registered or subscribed. It must not infer taskless reviewer availability from a parked/unparked agent field. No browser tab is a proxy for claim/session state.

The Manager heartbeat can remain the existing AfterAgentIdle schedule initially. Its planner/journal can be adapted to queue counts and activation terminal events; do not add a second recursive Manager launch path. Scheduling the Manager itself is a separate operational control from the work-queue engine.

## 9. Session interruption, recovery and background jobs

Normal turn end must be an explicit atomic YieldWork, CompleteWork or BlockWork. The next session reconstructs its work solely from the authoritative subject, checkpoint(s), source refs and durable events.

A crashed or silent session may leave a Claimed item. Preserve that claim and role capacity until a verified terminal event or an explicit recovery transition. Time alone signals investigation, not proof of termination. Existing working-duration 25/30-minute guidance remains advisory.

A recovery action records a reason, evidence, old activation, new claim generation and replacement eligibility. Positive evidence can include a terminal Jarvis execution event, acknowledged yield, or an explicit audited operator correction. If evidence is ambiguous, surface **RecoveryNeeded** and keep the claim unavailable rather than silently double-launching. Designing a fully automatic, *verifiably safe* recovery path for truly lost browser sessions is its own implementation gate.

After recovery, old activation work-scoped mutations fail stale-version/claim-generation validation; do not attempt agent-ID security enforcement. Ordinary account-authorized source Git/file actions remain possible. Prevent concurrent source edits operationally using isolated worktrees, source snapshots and clear agent instructions.

Long-running mutating subprocesses are part of the active work context. A worker cannot safely yield and requeue a source-writing item while an untracked mutating job may still change that source. It must finish/cancel the job or park the item on a durable job dependency until termination is verified. Read-only jobs and independent artifacts may be treated separately with explicit evidence. A completed background job does not on its own end the agent activation.

## 10. Task → review → integration workflow

1. A task is created. Lead claims a planning item, records priority and implementation readiness, and publishes implementation work without selecting a worker ID.
2. Manager sees eligible implementation work and starts role sessions up to capacity. Each worker atomically claims one task-stage item and reads its last checkpoint; different turns may be served by different workers.
3. Yield releases unfinished implementation work. A frozen review request completes that implementation stage and publishes a review item, while the task itself remains open or in progress.
4. A reviewer claims the review item, inspects the exact immutable frozen ref/SHA and documents findings. Review claims are still exclusive and review decisions remain canonical. The review stage's checkpoint survives reviewer session changes.
5. ChangesRequested publishes a new implementation-correction stage linked to the exact review findings and previous source context. Accepted publishes an integration item referencing the canonical review ID and exact frozen source SHA. Neither event silently alters task ownership or closes the task.
6. Integrator claims the integration item, submits the existing canonical integration record, checks review acceptance and source/target identities, merges the **exact** accepted commit when conflict-free, runs required validation, pushes and verifies remote ancestry, and records CompleteIntegration idempotently.
7. A merge conflict or required source edit cannot be silently incorporated as unreviewed code. Publish an explicit correction item and require a fresh frozen review for modified source. An uncertain push/integration outcome must first be reconciled against Git and the canonical integration receipt before any retry.
8. Task closure is explicit after the required final workflow evidence; it is not inferred from a work-item completion or review decision.

Non-source tasks can define a shorter workflow; an accepted review need not imply an integration item if the task explicitly has no integration target.

## 11. Challenges, blocks and resumability

Existing durable challenge conditions become dependencies of work items rather than dependencies requiring one named worker to keep an attempt in its attention slot. The challenge's issuer, conditions, evidence and satisfied/blocked/cancelled states remain canonical.

A blocked work item keeps its latest checkpoint and source context. Challenge completion reconciles dependency indexes and requeues the same item once when all required conditions are satisfied. If an item has several dependencies, satisfying one does not wake it prematurely. No notification, browser session or synthetic worker assignment is required for wakeup.

An operator question can create a role-routed work item or an operator-visible condition, as defined by the existing challenge contract; the queue does not invent a new threaded messaging system.

## 12. Proposed API surface (names provisional)

Reads:
- ListQueues / ListAvailableWork(queueIds, limit, cursor, expectedSnapshot)
- GetWorkItem(workId) / GetWorkHistory(workId, cursor)
- ListRoleSubscriptions / GetRoleSubscription(roleId)
- GetActivation(activationId) / ListActiveExecutions(project, role)
- ListQueueHealth and coverage diagnostics for Manager and the TUI

Writes (all with caller-stable operationId, atomic Git CAS and appropriate version assertions):
- SetRoleSubscriptions(roleId, queueIds, scheduling policy)
- PublishWork or StageWork with authoritative subject/event reference (domain producers typically do this atomically)
- ClaimNextWork(queueIds, activationContext, optional itemHint)
- CheckpointWork(workId, activationId, claimGeneration, summary, nextAction, evidence?, sourceCommit?)
- YieldWork(workId, activationId, claimGeneration, summary, nextAction)
- CompleteWork(workId, activationId, claimGeneration, outcomeRef?, summary)
- BlockWork(workId, activationId, claimGeneration, dependencyIds, summary, nextAction)
- WakeWork / CancelWork with canonical source and dependency validation
- RecoverWorkActivation with verified evidence and an auditable reason

Subject-specific review, integration, task and challenge APIs remain canonical. Queue operations either call those existing domain mutations or coordinate with them transactionally; they do not invent alternative verdicts, approvals or merge authority.

Bootstrap should request one role/queue execution and include the launch ID, never an assumed task owner ID. The claim response supplies the complete durable continuation context.

## 13. UI, audit and diagnostics

The primary TUI view should show per-project queue depth, available/claimed/blocked counts, role dispatch capacity, in-flight/uncertain launches, and work progress. Agent identities are useful as attribution, not roster slots requiring Parked/Waiting transitions.

Each work history includes item creation, exact subject/event provenance, claim and generation, activation start, checkpoints, yield, block/wake, stage completion, recovery, and source snapshot/integration evidence. Durations are derived from timestamped events and reported as wall-clock observations, not productive CPU time. A stalled execution stays visibly unresolved instead of appearing idle.

Do not migrate old Waiting/Parked state totals into new agent counts. Report pending work and active executions separately. No claim that an agent is Waiting is meaningful merely because it subscribes to a queue.

## 14. One-time migration and compatibility

This replaces the current orthogonal assignment model and must have a separately reviewed cutover. Do not dual-write old assignments and new work claims indefinitely.

1. Inventory a verified orphan-ledger head, all active tasks/attempts, review requests and verdicts, integration submissions, challenge waits, agent checkpoints, working sessions, and Manager launch journal. Snapshot and verify rollback sources.
2. Drain or explicitly resolve live Working assignments and uncertain Manager launches before cutover. If any are still active or ambiguous, refuse activation rather than inventing completion. Park the Manager schedule during the conversion.
3. Convert each actionable queued/started task stage into one deterministic implementation item. Preserve original spec, attempt generation, frozen source and checkpoint history. Old ownerAgentId/agent assignment records remain readable as historical attribution only.
4. Convert unclaimed/claimed reviews into review work items without rewriting frozen review refs or recorded verdicts. Convert accepted-but-not-integrated reviews into integration items only after verifying canonical acceptance and absence of a completed integration record.
5. Preserve deferred/blocked work as blocked items with equivalent durable dependencies and reverse indexes. Do not drop pending operator/challenge evidence. A formerly Parked or NeedsWork agent with no underlying work must not manufacture a queue item.
6. Materialize complete queue indexes and a coverage marker in one checked Git transaction. Reconciliation must distinguish truly empty queues from incomplete index coverage.
7. Perform disposable two-client conversion tests, restart tests, and one controlled local shadow planning run. Deploy matching client/server schema versions with a validated protocol handshake; old clients must not write incompatible current records.
8. Verify counts, checkpoints, frozen refs, integration receipts, challenge wakeups and idempotent replay before activating queue-based Manager dispatch. Keep rollback steps explicit; a rollback must not overwrite work committed under the new schema.

Existing live rollout of the current Manager/scheduler is a separate undertaking. Do not combine this migration with an unreviewed production upgrade or enable unattended scheduling as a side effect of creating this spec.

## 15. Incremental implementation plan

**Phase A — Frozen contracts and simulations.** Review this specification; define exact records/transitions, source-to-queue adapters, operation names and the cutover ledger schema. Build a pure deterministic queue/claim/activation planner against disposable Git fixtures. No production mutation.

**Phase B — Canonical work ledger.** Implement queue items, idempotent publication, claim CAS, checkpoint, yield, completion, block/wake and recovery history. Add bounded indexes and coverage markers with multi-client consistency tests.

**Phase C — Producers and role subscriptions.** Adapt task intake/Lead prioritization, review submissions/verdicts, challenge dependencies and integration readiness to publish deterministic work items. Store subscriptions by role without new agent-level permissions.

**Phase D — Manager and ephemeral bootstrap.** Replace persistent Ready-worker/NeedsWork-reviewer selection with queue availability and role capacity. Reuse one-shot browser launch journal, uncertain-outcome handling, confirmed tab close, and exact activation terminal evidence. Implement no-work exits.

**Phase E — Domain cutover.** Decouple implementation attempt ownership from worker labels; keep immutable review/ref/integration behavior and migrate historical checkpoints and work records. Adapt agent inbox/TUI to work-centric projections. Run two-client/restart/performance validation.

**Phase F — Release and controlled activation.** Independent frozen reviews and exact integrations at each boundary. Stage client/server binaries and data conversion with rollback. Obtain operator release authorization; run an isolated end-to-end workflow with 5 workers/3 reviewers, Lead and Integrator. Only then retire legacy state-writing APIs.

Do not start Phase F until independent reviews of the current Manager duplicate-launch correction and pending rollout constraints have been resolved.

## 16. Acceptance and negative-space tests

1. Five available implementation items with five worker slots: five simultaneous sessions may claim at most five distinct items. Ten concurrent ClaimNextWork calls across two Git clones produce exactly five unique claims, no duplicates.
2. Six sessions competing for five items: the extra session gets a canonical NoWork response and ends; no new task or permanent agent assignment is created.
3. Worker A checkpoints and yields a partially implemented task; Worker B with a different label and provider session claims it, reads exactly the checkpoint/source, and continues. No reassignment command.
4. Yield writes its final checkpoint and releases the claim in one transaction. A late checkpoint/complete from the prior activation fails stale-generation validation and cannot revert the new claim.
5. A stalled session without verified terminal evidence retains its work claim and capacity. Timeout, changed agent ID, closed bootstrap tab, or review decision alone cannot make it available.
6. Reviewer decides an accepted review immediately after an activity sample but before its own terminal yield: Manager cannot launch a duplicate reviewer session on the basis of review resolution alone. Positive yield frees capacity.
7. A browser submit that times out after possible dispatch is marked uncertain and not retried blindly; a known pre-submit failure can release its capacity without a duplicate tab.
8. Replaying task-readiness, review acceptance, changes-requested or challenge-satisfaction events creates no duplicate work items. Concurrent publishers and two-client CAS retries converge.
9. A task with pending review is not selected as implementation work. ChangesRequested produces one correction item carrying exact findings; Accepted produces one integration item pinned to exact frozen SHA.
10. Integrator cannot record a mismatched or unreviewed source as the accepted integration. Push success with lost acknowledgement is reconciled from remote Git and canonical integration receipts before another mutation.
11. Blocked items with two challenge dependencies wake only when both are satisfied. Busy workers or absent historical worker IDs do not strand the work. Operator responses remain durable.
12. A running source-mutating background job prevents unsafe release/reclaim of its worktree context until termination or explicit durable dependency handoff.
13. Role subscriptions do not alter OAuth account permissions. Different labels under the owner account can cooperatively claim the same role's available work; versions and claim generations prevent duplicate work mutations, not impersonation.
14. Lead and Integrator execute multi-turn work without synthetic permanent tasks, checkpoint and yield correctly, and can be resumed by a differently labeled session.
15. An absent, partial or stale queue-index coverage marker does not present zero work as healthy. Repair or report an actionable coverage failure.
16. Migration retains old source specs, audit events, review refs, findings, accepted SHA, integration records, task checkpoints and challenge dependencies. Refuse conversion with unresolved active sessions; no forced ledger ref rewrite.
17. Manager never launches merely because a subscribed agent ID is idle; with no eligible work it launches zero sessions. Manager budget caps and no-recursion rule survive restart.
18. Release validation includes .NET 11 Release Client/Server tests, Node Manager/browser suites, disposable Git two-client conflict tests, updater rollback, protocol/API compatibility and a bounded end-to-end queue-to-integration scenario.

## 17. Explicit non-goals

- No agent-ID-based permissions, role ACL, identity binding or contribution account security system. Contributor permissions are a separate future **account-level** feature.
- No generic workflow scripting engine, arbitrary event bus, automated source merges, automatic review acceptance, or forced identical pipeline for every task.
- No assumption that ChatGPT tab closure, elapsed time, or a new opaque session ID proves prior execution termination.
- No replacement for protected Git refs, immutable frozen reviews, ordinary test evidence, or operator-controlled deployment.
- No immediate migration or automatic Manager activation as part of writing this document.

## 18. Design choices intentionally settled

- Work claims are **per activation**, not per persistent worker ID.
- Work checkpoints survive arbitrary session/agent changes.
- Manager dispatches sessions from **queue demand**, not an idle-agent roster.
- Review and integration are regular queue subjects with existing canonical evidence, not special lifetime tasks.
- Subscriptions route work; authenticated accounts supply permissions.
- Yield releases unfinished work only with an atomic final checkpoint and activation termination.
- Blocked is a work/dependency state; no Parked or Waiting agent disposition.
- Uncertain browser launch and unsafe stalled execution recovery remain fail-closed and explicit; a future verified recovery mechanism may improve liveness without weakening exclusivity.
- The project's Git orphan branch remains authoritative, with derived indexed availability and no parallel mutable authority.
