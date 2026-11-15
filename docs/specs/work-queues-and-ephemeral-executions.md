# Work queues and ephemeral agent executions

**Status:** Proposed architecture specification; design reference, not implemented or deployed. This branch is a Lead correction draft, not the frozen original under independent review.
**Revision basis:** `e737c2da901ae4073f7ecc066781f428020ffdd1` (frozen review `d4fbb56e2d4c451eab1ef23c281dc647`).
**Date:** 2026-11-15
**Scope:** Jarvis per-project coordination, Git-backed work ledger, agent roles, queue dispatch, resumable execution, reviews, integration, challenges, Manager, and migration.
**Authority:** This document proposes a future replacement for the persistent agent-assignment portions of docs/specs/orthogonal-agent-state.md and the current Manager/reviewer launch convention. Existing APIs and live ledger semantics remain authoritative until a separately reviewed, operator-approved cutover.

## 1. Decision

Jarvis manages **durable, prioritized work with task-addressed communication** and **ephemeral executions**, not durable assignments or conversations to named worker IDs.

The desired autonomous loop is intentionally simple: **Lead prioritizes the actionable work queue; Manager launches as many role sessions as verified demand and capacity allow; each Worker atomically claims the highest-priority eligible task; the result and any question to the operator are recorded against the task.** Review and integration reuse the same pattern with their own queues. Manager dispatches capacity, not task ownership. The operator primarily sees *what work needs attention*, not which numbered worker was allocated.

- Tasks, frozen reviews, integrations, planning requests, and challenge follow-ups can publish actionable items to project-scoped work queues.
- Role subscriptions determine which queues a newly launched execution is expected to consume. A role does not imply a permanently active session or perpetual work item.
- Manager uses queue availability and role concurrency budgets to launch disposable agent sessions. Each launched session atomically claims **one** eligible work item; the Manager does not bind tasks to worker IDs in advance.
- Every claimed item has an immutable checkpoint history, latest continuation instructions, and an explicit terminal transition. Any compatible session can continue it on a later activation.
- Yielding unfinished work atomically persists a checkpoint and releases its claim. Completing a stage advances the work to its next explicit queue or terminal state. Blocking frees its execution slot while retaining a durable dependency.
- Existing agent IDs currently overload three incidental functions: a serialized execution slot (semaphore), a task focus, and an agent-addressed inbox. The new design **separates role capacity reservations from work claims**, and moves work questions/replies into durable task/subject conversations. A stable numbered Worker N identity is not required to schedule, claim, checkpoint, resume, review, or address work.
- Agent ID remains an optional compatibility/attribution label only, **not** a security principal. All agents running under the owner's account have the same account permissions. Do not implement agent-ID-based permissions, role ACLs, or anti-impersonation mechanisms. Future contributors receive permissions at the authenticated account level.

The role responsibilities are:

| Role | Responsibility |
| --- | --- |
| Manager | Reserve bounded role capacity against eligible unreserved demand, then dispatch role sessions; never prioritize or preassign a worker |
| Lead | Triage incoming work and operator decisions; prioritize and mark work ready; no worker selection or per-worker assignment maintenance |
| Worker | Claim highest-priority eligible implementation work atomically; read its durable task conversation/source, implement, checkpoint, ask operator, yield, block or submit completion |
| Reviewer | Claim frozen independent review work, record findings and decision |
| Integrator | Claim accepted integration work, validate and integrate **exact reviewed source**, record immutable evidence and completion |

A review, integration, or planning item needs no artificial implementation task merely to obtain a checkpoint.

## 2. Explicitly retained boundaries

1. The existing project's canonical Git orphan ledger remains the sole source of coordination truth. No new database, external message bus, or server-resident mutable workflow database.
2. Source branches, Git worktrees, immutable review refs and integration evidence remain distinct from orchestration state. An agent's conversation is never the durable work record.
3. The server continues exposing operations over the owner's authenticated account and the existing client project binding. Queue subscriptions and claim generations are coordination tools, not new permission boundaries.
4. The workflow does **not** infer acceptance from task completion, merge from review acceptance, or task closure from integration. Domain transitions remain explicit and independently auditable.
5. No persistent worker assignment, exclusive reviewer identity, or dedicated Integrator task is required. A numbered persistent agent ID is not a coordination address; optional execution labels may appear in historical logs. Role subscription is a versioned configuration for launches, not a membership list of named workers.
6. Browser tabs can be closed immediately after a confirmed one-shot submission; tab lifetime is not evidence that a model session ended. An uncertain submission must not be blindly repeated.
7. New work-queue plumbing is not permission to deploy, restart the live server/client, enable the Manager schedule, or launch unattended agent sessions. Those are separate rollout operations.

## 3. Vocabulary and durable objects

**Task** — Durable unit of requested work with canonical specification and lifecycle. It may produce multiple implementation stages and reviews; it is not owned perpetually by a worker ID.

**Work item** — One actionable stage or event, referencing its authoritative subject (task, review, integration candidate, challenge, or planning event). It is placed in exactly one queue and has one current claim at most.

**Queue** — Project-scoped index of work items eligible for a type of role. Standard queues initially: planning, implementation, review, integration, and challenge-triage. Queues are not authorization boundaries.

**Subscription** — Persistent role configuration listing queues and, optionally, selection policy. An agent can inspect or claim subscribed queues as a matter of cooperative protocol; the account's actual tool permissions are unchanged.

**Launch** — A bounded, journaled attempt to start one session for a role/queue, with an exclusive demand reservation while no claim exists. It consumes Manager capacity even before a work claim exists. A launch ID may acquire at most one work item over its entire lifetime, including after yield or completion; an unsuccessful launch ends with a canonical NoWork/PreDispatchBlocked outcome.

**Activation** — One execution of a role that has claimed an item, identified by an opaque activation ID and claim generation, bound to its one originating launch. It can checkpoint while running and must reach an explicit yield, stage-complete, or block outcome. An activation is not a task or a persistent worker assignment; terminal activation and terminal launch events are committed together where applicable.

**Claim** — Exclusive, current reservation of one work item by one activation, protected by the ledger's atomic compare-and-swap and a monotonically increasing claim generation. The generation is a stale-write coordination guard, not an identity or security check.

**Checkpoint** — Immutable progress evidence linked to the work item and activation; readable independently of any earlier ChatGPT session.

**Dependency** — A canonical condition/challenge that keeps a work item blocked until its specified release rule is satisfied.

**Role capacity slot** — One project/role-scoped concurrent execution budget reservation, allocated to a launch, not a named agent ID. The slot is a semaphore token recorded across devices in canonical coordination state and released only by the evidence policy defined in section 8.

**Work conversation** — An append-only, subject-addressed exchange linked to the durable task (or another canonical review/integration/planning subject) with operator/agent messages, questions, answers, decisions, and references to challenge conditions. Messages are independent of whichever session currently claims the work; the conversation is not a second task specification or an alternate permission channel.

Agent display should primarily report available work, pending launch, active executions, and blocked work. The old Waiting/NeedsWork/Parked roster distinction is removed. If agent-level labels are shown, Idle is no activation, Ready is a confirmed pending dispatch, and Working is an active execution; Blocked belongs to the work item, not to an agent. No persistent agent Ready state is required to select a queue.

## 4. Canonical records and identity

The schema below is illustrative; exact on-disk names and fields must be finalized with tests. Reuse the verified Git ledger's commits, operation receipts, review records, challenge indexes, and concurrency rules.

~~~text
work-items/<work-id>/item.json
work-items/<work-id>/events/<sequence>.json
work-items/<work-id>/checkpoints/<sequence>.json
work-activations/<activation-id>.json
work-launches/<launch-id>.json
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
  "displayLabel": "Worker",
  "launchId": "launch-opaque-id",
  "state": "Working",
  "sessionIds": ["opaque-provider-session"],
  "startedAtUtc": "2026-11-15T00:00:00Z"
}
~~~

The optional display label or historical agentId in an activation is descriptive. Changing it gives no additional permissions and is not a requirement for resuming the same work on a later activation. New launches do not need pre-registered numbered workers. The claim generation and work version coordinate stale writes; they do not prove who the human or model is.

Launch records hold stable launch ID, role, eligible queues, versioned demand reservation, journaled browser dispatch state, optional one activation, and terminal/uncertain evidence. One ledger-CAS reservation is counted against BOTH role/global execution capacity and unclaimed demand. A process-local Manager journal is a side-effect journal, not the cross-device reservation authority. The exact record representation is fixed in Phase A.

Work items are durably keyed by a deterministic identity derived from project, queue, authoritative subject, and the exact event/stage generation that made work available. Replaying the same event creates no second item. A new revision or ChangesRequested decision creates a new stage generation where necessary, without rewriting or erasing old events.

A task's implementation attempt can remain as the stable **work-stage** record spanning several activations, but it has no permanent ownerAgentId. Historical owner and assignment records are retained as audit provenance, not as current scheduling authority.

**Source-writer lineage:** Canonical key project + source repository + task + target ref + explicitly selected lineage ID. Every mutable implementation stage carries lineage, stage generation, pinned base, and current continuation SHA. Within a lineage, exactly one source-writing stage may be actionable or claimed; independent review items may coexist. ClaimNextWork checks both item version and lineage generation/claim in one CAS. Different lineages require an explicit branching/merge policy.

## 5. Source-of-truth and queue publication

- A work item points to canonical data; it does not duplicate task specifications, review findings/decisions, accepted source hashes, or integration receipts as alternative authority.
- A domain change producing work (task intake, Lead readiness decision, frozen review submission, review verdict, challenge satisfaction) publishes or updates the corresponding item **in the same Git-ledger transaction**, or uses a deterministic reconciler that rechecks the source and commits the missing projection before exposing availability. Queue/attention indexes have per-domain producer high-water marks and per-shard coverage marks checked against the verified Git ledger snapshot; a one-time migration coverage marker does not prove ongoing coverage.
- If a projection is missing, stale, partial, behind its latest producer high-water mark, or unverified, the system must not advertise a healthy empty queue or silently invent eligibility. Query the canonical source, repair the derived index with CAS, or report incomplete coverage. Manager must not reserve launches and ClaimNextWork must not return normal NoWork on unverified index coverage.
- In-memory queue counts and sharded indexes accelerate Manager reads but never authorize a claim. ClaimNextWork revalidates item state, subject state, dependencies, version and claim in the authoritative ledger.
- Queue creation is domain-specific and explicit. This design is not a general event bus, scriptable rule engine, or automatic approval system.

The initial producers are:

| Canonical event | Queue item |
| --- | --- |
| New task requiring triage or priority change | planning |
| Lead marks implementation stage actionable | implementation |
| Worker submits a new exact frozen review | review |
| Review returns ChangesRequested | join or create the single current correction stage for the matching source-writer lineage and generation; keep all review findings; stale verdicts go to Lead triage |
| Review is explicitly Accepted and integration is required by task policy and exact target/source are valid | one integration item pinned to review ID, frozen SHA and target |
| Pending challenge condition requires triage/action | challenge-triage or a queue specifically configured for that condition |
| Blocking dependency becomes satisfied | Existing blocked work item becomes Available; no duplicate item |

Task completion without review, acceptance without integration, and non-code work are valid policy variants; the Lead selects workflow requirements in the task specification/queue metadata. The queue mechanism must not force all tasks through review or merge.

## 6. Subscriptions, prioritization and fairness

A role subscribes to one or more queue IDs in versioned project configuration, independently of any numbered agent registry. Changing the configuration affects future claims, not existing activations. Default role subscriptions: Lead to planning, Worker to implementation, Reviewer to review, Integrator to integration. Manager is a scheduled control-plane actor, not a permanent queue claim.

Queue selection is deterministic at one verified ledger snapshot: highest priority first (lower numeric priorityRank if retaining current convention), then older enqueue sequence, then stable item ID. If multiple queue subscriptions exist, configured queue weights/priority are explicit. Starvation prevention can be added after measuring actual contention; do not hide ordering behind agent-ID names.

Lead prioritizes **work**, not people. New tasks and operator requests enter the planning queue until Lead records priority, dependencies, readiness, and the appropriate work type. Manager only observes verified eligible demand and capacity; it cannot implicitly decide which tasks are more important. Each Worker selects the highest-priority eligible implementation work using the canonical ordering under CAS, independent of its label. Lead may defer or reprioritize an item without assigning a named worker. A worker that finishes one activation does not acquire a preferred claim on the next one.

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

**ClaimNextWork** selects one eligible item for a specified current launch ID and caller-stable operation ID, allocates a fresh activation ID, increments claimGeneration and atomically updates the work item, activation, launch's one-claim marker and its demand reservation within one Git transaction. Competing clients must produce at most one winner. A loser rereads the queue only after its own original operation outcome is definitively known; it never assumes an ambiguous claim failed. A launch with any previous successful claim can never claim again, even after that activation yields or completes. The return includes canonical subject, frozen source/spec context where relevant, latest checkpoint, claim generation, exact ledger snapshot, and the workflow continuation requirements. If the result is lost after possible commit, the caller MUST query its exact operation receipt and/or immutable launch-claim mapping and replay the same operation ID/payload before ever issuing a different claim. An unknown outcome is not a NoWork response.

**CheckpointWork** appends immutable summary, nextAction and optional evidence/source SHA while preserving Claimed. Replays with the same operation ID and arguments return one receipt; different arguments for the same operation ID are invalid.

**YieldWork** is a *terminal operation for that activation and its originating launch*: atomically writes a final checkpoint, marks the activation/launch Yielded, releases the item claim, terminates the launch's logical capacity reservation, and makes unfinished work Available. A separately configured strict provider-session capacity budget can remain reserved until its own host-terminal evidence. The next worker can have any label or session ID. No extra task reassignment or ownership transfer is needed. Old activations and launches are not silently resumed. For source-writing work, normal yield requires an independently retrievable, immutable continuation commit (plus repository and ref provenance) verified reachable from the configured remote BEFORE this transaction. A dirty/unpublished worktree cannot be silently yielded into the available queue. Non-source work may have no source commit. A failed source snapshot or unresolved mutating job leaves the work claimed or explicitly blocked with a durable dependency; it never advertises recoverability it cannot prove.

**CompleteWork** writes a final checkpoint, terminates both activation and originating launch, transitions this stage to Completed, and records any explicit next-stage item(s). Required source/frozen-review evidence must already be durably reachable and verified. It must not implicitly close a task, approve a review, or merge code.

**BlockWork** writes a checkpoint and dependencies, terminates the activation and originating launch, and leaves the item Blocked with no active claim. When dependencies are satisfied, the original item is requeued once with its existing checkpoint history. If a mutating job or dirty source prevents safe release of an execution/worktree, the blocking transition must represent that dependency and retain the necessary execution/workspace fence until positive handoff or verified termination.

A session may make several checkpoints before it yields. A claim is not released merely because a browser tab closes, a session ID changes, an elapsed-time threshold passes, an agent returns a prose message, or a review verdict is written. Work completion and provider-session termination are separate facts. A terminal Jarvis launch receipt fences future claims and work-scoped mutations for that launch; it is positive *logical* execution termination evidence, not proof that the browser/provider process has exited. When strict physical-session concurrency is required, retain capacity until independent host-terminal evidence or explicit recovery; do not relabel a Jarvis yield as a provider-process exit.

Every work-scoped mutation compares the latest work version and claim generation. An obsolete activation cannot publish new checkpoints, yield, or complete an item after its claim was explicitly released. This is coordination against stale work, **not** an agent-ID permission check; account-level Git/file tools retain their existing permissions.

## 8. Manager execution and capacity

Manager's only required scheduling decision for normal queue work is:

~~~text
unreservedEligibleDemand = count(unique eligible work generations not covered by live compatible launch-demand credits)
launchCount = min(unreservedEligibleDemand, freeRoleCapacity,
                  freeGlobalCapacity, remainingDispatchBudget)
~~~

Demand accounting and role capacity are separate invariants. In-flight, submitted, observed and uncertain launches count against role/global capacity; **unclaimed launch reservations also cover one unique eligible work generation each for demand accounting, without selecting the task a worker must claim**, so a second Manager pass cannot use that same still-available item as justification for another launch. Manager reservation, idempotence and demand-coverage accounting are canonical Git-ledger CAS, not just an in-process journal or one-device lock. Reservations may conservatively suppress launches on transient stale demand, but must never create duplicate dispatch for the same work demand. By default, role budgets bound active or uncertain Jarvis launch/activation executions, **not dormant provider conversation tabs**; one-claim-per-launch fencing makes a logically terminated session ineligible for more work. If a deployment instead requires a bound on physically alive provider sessions, its additional capacity counter must wait for verified host-terminal evidence. Role budgets are configuration, not a fixed count of persistent worker IDs; retain current hard ceilings (5 worker executions, 3 reviewer executions) until explicitly revised. Other roles can have separate small limits.

For each launch:

1. Read a verified consistent work/claim/launch snapshot. If coverage or activity evidence is incomplete, suppress launch and report why.
2. Durably reserve one role-capacity launch ID AND one demand credit against the unique eligible work-generation set in the same canonical ledger transaction. The credit may use a work-generation ID internally to deduplicate overlapping role subscriptions, but **it is not a task assignment and does not prescribe what the worker claims**. Manager cannot choose or bind a specific task to a Worker N. If overlap, priority or eligibility changes, CAS-revalidate/rebalance credit coverage without launching excess sessions; reservations are idempotent by launch ID and no work generation may support two still-unclaimed launch credits.
3. Dispatch one ChatGPT bootstrap for project + role + eligible queue(s) + launchId, using the existing one-shot submission semantics. Close its temporary browser tab once submission is positively confirmed.
4. The newly started Worker reads its role instructions and invokes ClaimNextWork with its launch ID, preserving/recovering the caller-stable operation ID across an uncertain result. **The Worker claims the highest-priority eligible queue item**, irrespective of which work generation Manager used to account for launch demand. The claim CAS consumes/reconciles its launch demand credit with the actual chosen item. If the queue has emptied, it records a canonical NoWork terminal launch outcome, releases its demand/capacity reservations, and ends; Manager must not spin or keep launching because of the stale count.
5. When the claim succeeds, its activation becomes Working and the launch records that its lifetime one-claim allowance is exhausted. Session checkpoints and final outcomes are canonical work mutations. Yield, CompleteWork and BlockWork terminate this activation and its launch atomically; the same launch can never acquire a second item. A positively recorded logical terminal outcome frees logical role capacity. If a configured ceiling means **actual provider sessions**, independent terminal-host evidence is required before freeing that stricter capacity; the Jarvis terminal event must not be mistaken for provider exit. An ambiguous launch or terminal outcome retains its slot until recovery.
6. A submitted-but-unobserved or uncertain browser launch still consumes reserved capacity until positively reconciled. No retry of an ambiguous submit.

The Manager must not launch merely because a Worker/Reviewer/Integrator/Lead ID is registered or subscribed. It must not infer taskless reviewer availability from a parked/unparked agent field. No browser tab is a proxy for claim/session state.

The Manager heartbeat can remain the existing AfterAgentIdle schedule initially. Its planner/journal can be adapted to queue counts and activation terminal events; do not add a second recursive Manager launch path. Scheduling the Manager itself is a separate operational control from the work-queue engine.

## 9. Session interruption, recovery and background jobs

Normal turn end must be an explicit atomic YieldWork, CompleteWork or BlockWork. The next session reconstructs its work solely from the authoritative subject, checkpoint(s), exact reachable source snapshot refs and durable events. An agent-facing success message, a closed browser tab, and a local worktree path are not source-continuation evidence.

A crashed or silent session may leave a Claimed item. Preserve that claim and role capacity until a verified terminal event or an explicit recovery transition. Time alone signals investigation, not proof of termination. Existing working-duration 25/30-minute guidance remains advisory.

A recovery action records a reason, evidence, old activation, new claim generation and replacement eligibility. Positive evidence can include a terminal Jarvis execution event, acknowledged yield, or an explicit audited operator correction. If evidence is ambiguous, surface **RecoveryNeeded** and keep the claim unavailable rather than silently double-launching. Designing a fully automatic, *verifiably safe* recovery path for truly lost browser sessions is its own implementation gate.

After recovery, old activation work-scoped mutations fail stale-version/claim-generation validation; do not attempt agent-ID security enforcement. Ordinary account-authorized source Git/file actions remain possible. Prevent concurrent source edits operationally using isolated worktrees, source snapshots and clear agent instructions.

Long-running mutating subprocesses are part of the active work context. A worker cannot safely yield and requeue a source-writing item while an untracked mutating job may still change that source. It must finish/cancel the job or park the item on a durable job dependency until termination is verified. Read-only jobs and independent artifacts may be treated separately with explicit evidence. A completed background job does not on its own end the agent activation.

**Source-writing continuation contract.** Every source-writing activation uses an isolated workspace derived from a pinned base/source commit and records the expected source repository identity. Before normal yield, it commits all needed tracked and untracked changes (or records deliberately excluded/reproducible artifacts), publishes the WIP state to a generation-scoped, non-review remote ref such as `refs/heads/jarvis-wip/<work-id>/<activation-id>`, then independently verifies the exact Git commit SHA is reachable at that ref on the configured remote. WIP snapshots are marked unfinished, not reviewed or integration-eligible. The final checkpoint records the repository identity, base, exact commit, generation-scoped ref, and unresolved tests/issues. An item with dirty uncommitted state, missing remote proof, or a still-mutating job cannot be made Available on normal yield; it stays claimed pending resolution or is blocked with a durable dependency and a safe handoff fence. Read-only/document-only work can instead checkpoint without a source snapshot.

Subsequent activations create/fetch their own isolated workspace from the checkpoint's immutable source commit, not the predecessor's path or a mutable branch tip. Past WIP refs and immutable commits remain available for recovery until retention policy permits cleanup. A late previous activation may still edit local files or call account-authorized Git tools; the claim generation only fences Jarvis work-state mutations, not filesystem permissions. Per-activation refs prevent a late push from changing the *pinned continuation commit*, and operational instructions/worktree isolation prevent routine concurrent edits. Source pushes precede ledger yield: if the push succeeds and ledger CAS fails, the old claim remains current and the extra WIP ref is recoverable; if the ledger yield succeeds, the exact recorded SHA is already verifiably reachable.

## 10. Task → review → integration workflow

1. A task is created. Lead claims a planning item, records priority and implementation readiness, and publishes implementation work without selecting a worker ID.
2. Manager sees eligible implementation work and starts role sessions up to capacity. Each worker atomically claims one task-stage item and reads its last checkpoint; different turns may be served by different workers.
3. Yield releases unfinished implementation work. A frozen review request completes that implementation stage and publishes a review item, while the task itself remains open or in progress.
4. A reviewer claims the review item, inspects the exact immutable frozen ref/SHA and documents findings. Review claims are still exclusive and review decisions remain canonical. The review stage's checkpoint survives reviewer session changes. Writing a verdict does **not** release the review item claim or complete the reviewer's activation/launch. After the verdict the reviewer must terminally CompleteWork; verdict and terminal lifecycle are independently idempotent, and the integration producer must not be run twice.
5. ChangesRequested joins or creates the one current source-lineage correction stage linked to the exact review findings and previous source context. Multiple independent reviews of the same current revision add findings to that one stage, not multiple concurrently claimable source writers. Feedback for an older superseded source remains auditable and requires Lead triage before modifying current source. Accepted produces an integration item only when task policy and reviewed frozen SHA/target provenance require it. Neither event silently alters task ownership or closes the task.
6. Integrator claims the integration item, submits the existing canonical integration record, checks review acceptance and source/target identities, merges the **exact** accepted commit when conflict-free, runs required validation, pushes and verifies remote ancestry, and records CompleteIntegration idempotently.
7. A merge conflict or required source edit cannot be silently incorporated as unreviewed code. Publish an explicit correction item and require a fresh frozen review for modified source. An uncertain push/integration outcome must first be reconciled against Git and the canonical integration receipt before any retry.
8. Task closure is explicit after the required final workflow evidence; it is not inferred from a work-item completion or review decision.

Non-source tasks can define a shorter workflow; an accepted review need not imply an integration item if the task explicitly has no integration target.

### Canonical review claims and queue projections

The existing independent review record is the sole authority for frozen source SHA/ref, review claim, decision, and version. A review work item is a projection for dispatch/checkpoint; it must never keep a second independently writable reviewer owner. The queue claim and canonical review claim are one Git-ledger CAS. Legacy direct claim/release/withdraw/decide APIs must either participate in that same atomic transition or be disabled by protocol handshake at cutover.

| Canonical event | Work-stage effect |
| --- | --- |
| ClaimNextWork(review) | Atomically set canonical review Unclaimed to Claimed and work item Available to Claimed; bind one activation and immutable frozen SHA/ref |
| Review checkpoint | Keep both claims, do not infer verdict or source integration |
| Release before verdict | Atomically release canonical review and terminalize activation/launch, requeue the same work item once |
| Withdraw before verdict | Canonical review Withdrawn, work item Cancelled and activation fenced with explicit terminal/recovery evidence |
| Decide Accepted or ChangesRequested | Persist canonical verdict and idempotent downstream policy producer, mark still-claimed work verdict-recorded/terminal-pending; do not free reviewer capacity |
| CompleteWork after verdict | Write final checkpoint and terminate stage/launch; cannot decide again |
| YieldWork or BlockWork after verdict | Reject and never reopen/requeue decided review; require CompleteWork or audited recovery |
| Crash after verdict | Preserve verdict and unresolved claim until verified terminal evidence or audited recovery; no second decision or duplicate integration item |

Direct-claim/queue-claim and release/decide races use the canonical review version and one CAS ordering; losers reread. Ad-hoc or non-source reviews do not create integration work without explicit policy and provenance. Integration always pins the exact accepted review ID, reviewed source SHA and target, never mutable branch HEAD.

### Source-writing lineage serialization

Frozen reviews may proceed concurrently, but one mutable implementation lineage has only one actionable source-writing stage and one current writer. Publication and ClaimNextWork revalidate task stage generation, pinned base, lineage writer key and current source continuation. Concurrent Lead readiness and two ChangesRequested verdicts converge on one correction work item with references to both reviews; they do not create two simultaneous correction writers. Older feedback remains as evidence but cannot overwrite a newer stage tip. Lead chooses whether to incorporate stale findings or explicitly establish a new stage.

Parallel implementation branches require distinct explicit lineage IDs, pinned source baselines and a reconciliation/integration plan. Cancellation and supersession preserve all historic review findings. Any stale claim/checkpoint/completion for an obsolete stage generation fails CAS rather than replacing the current source.

## 11. Challenges, blocks and resumability

Existing durable challenge conditions become dependencies of work items rather than dependencies requiring one named worker to keep an attempt in its attention slot. Operator communication is normally addressed to the **task**, not a Worker N inbox. The challenge's issuer, conditions, evidence and satisfied/blocked/cancelled states remain canonical.

A blocked work item keeps its latest checkpoint and source context. Challenge completion reconciles dependency indexes and requeues the same item once when all required conditions are satisfied. If an item has several dependencies, satisfying one does not wake it prematurely. No notification, browser session or synthetic worker assignment is required for wakeup.

**Block/satisfaction race:** BlockWork checks authoritative condition versions and results in the same Git CAS that writes its dependency indexes and transitions the work stage. Already fully satisfied dependencies cannot produce a permanent blocked wait; use an explicit manual block, stay claimed, or complete/yield when safe. If BlockWork commits first, the later challenge-satisfaction transaction wakes the item; if satisfaction commits first, BlockWork's expected versions fail and it retries against current satisfied state. Per-domain source/index high-water marks allow a reconciler to repair missed derived wakeups after crash/restart, without requeuing twice. Multiple dependencies still require all-satisfied.

### Task-addressed two-way operator communication

A task (or canonical non-task work subject such as a standalone frozen review) owns its complete durable conversation. A worker asking a question writes an append-only operator-visible **Question** event referencing the task and, when relevant, its current work item, activation and source/checkpoint revision. The message is not sent to a persistent worker ID. The operator replies **to that task/question**, even if the original session is gone and a different worker later claims it. The next activation receives a bounded conversation history plus an exact cursor over newer messages; no previous ChatGPT conversation or private agent inbox is required.

Messages distinguish **Note**, **Question**, **Response**, and **Decision**; each has an immutable event ID, source subject, originating operator/account or execution label, timestamp, optional reply-to ID, and body. Routine checkpoints and tool telemetry remain in their structured audit streams, not in the human conversation; only meaningful human-directed exchanges appear as messages. An operator may post a message while no worker is active. Informational messages do not alter task specs or claim state. A Decision that changes scope/priority/readiness requires the normal explicit task/Lead mutation, not an implicit chat-to-spec rewrite. Store message bodies once on the Git-backed work-subject event stream; operator-attention lists and inbox unread counts are derived indexed projections with coverage proofs. Optional push/UI notifications are only hints and cannot become workflow authority.

A Question is **blocking only when the worker explicitly links it to a durable operator-response challenge condition** and BlockWork commits the checkpoint/dependency. Nonblocking questions can coexist with ongoing work. A response addressed to the question is committed with the matching challenge condition satisfaction, checked against its version/issuer/subject, so all required dependencies can wake the same blocked item exactly once. A response is not an authorization for unrelated actions, a review acceptance, or a silent task closure. If no execution is active, the response remains visible and the next eligible worker retrieves it. Replies are not routed to a historical agent label.

Project-wide questions without a natural task can use a durable planning/decision work subject and appear in the same operator worklist; do not create a permanent 'Lead inbox' as an alternate channel. The existing durable challenge API remains the source of truth for condition state and operator consent; the subject conversation provides the readable question/answer context without duplicating decision semantics.

The operator's primary view is **Needs my response**, grouping outstanding questions by project/task title, context, urgency and dependency effect. After replying, the task either remains actionable or is requeued by its canonical dependency transition. There is no need to find the numbered worker that asked.

## 12. Proposed API surface (names provisional)

Reads:
- ListQueues / ListAvailableWork(queueIds, limit, cursor, expectedSnapshot)
- GetWorkItem(workId) / GetWorkHistory(workId, cursor)
- ListRoleSubscriptions / GetRoleSubscription(roleId)
- GetActivation(activationId) / ListActiveExecutions(project, role)
- ListQueueHealth and coverage diagnostics for Manager and the TUI
- GetWorkConversation(subject, cursor, limit) / ListOperatorAttention(project, cursor, limit), with verified index coverage and exact challenge links

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
- PostWorkMessage(subject, kind, body, replyTo?, operationId) / RequestOperatorAnswer(subject, question, blockingDependency?, operationId) / RespondToWorkQuestion(subject, questionId, response, operationId) with challenge-backed atomic response/condition state when blocking

Subject-specific review, integration, task and challenge APIs remain canonical. Queue operations either call those existing domain mutations or coordinate with them transactionally; they do not invent alternative verdicts, approvals or merge authority.

Bootstrap should request one role/queue execution and include the launch ID, never an assumed task owner ID. The claim response supplies the complete durable continuation context, including conversation cursor and outstanding operator answers/questions. Numbered worker names are never required for normal operation.

## 13. UI, audit and diagnostics

The primary TUI view should be **work-first**: per-project prioritized tasks (title, stage, priority, blocked reason, current claim/last checkpoint), an operator-attention worklist grouped by task, queue depth, and reserved/active/uncertain role capacity. Selecting a task opens its full conversation and immutable history; replying to the task works without locating a worker. Active executions may show role, launch ID and optional friendly attribution label, but numbered Worker N identities are not primary navigation or roster slots requiring Parked/Waiting transitions.

Each work history includes item creation, exact subject/event provenance, claim and generation, activation start, checkpoints, yield, block/wake, stage completion, recovery, and source snapshot/integration evidence. Durations are derived from timestamped events and reported as wall-clock observations, not productive CPU time. A stalled execution stays visibly unresolved instead of appearing idle.

Do not migrate old Waiting/Parked state totals into new agent counts. Report pending work and active executions separately. No claim that an agent is Waiting is meaningful merely because it subscribes to a queue. Existing agent-addressed notifications/messages are retained as historical evidence, not used for new work routing. During migration, link resolvable task-specific outstanding questions to their canonical subject without rewriting their original event; ambiguous/project-wide messages become explicit planning/triage attention, never silently dropped or guessed at.

## 14. One-time migration and compatibility

This replaces the current orthogonal assignment model and must have a separately reviewed cutover. Do not dual-write old assignments and new work claims indefinitely.

1. Inventory a verified orphan-ledger head, all active tasks/attempts, review requests and verdicts, integration submissions, challenge waits, agent checkpoints, working sessions, **all connected devices' pending-operation journals**, outstanding browser schedules/launches and Manager launch journal. Snapshot and verify rollback sources. Preserve existing attempt source bindings (repository, baseOid, targetRef, sourceRef) and verify every old logical stage maps injectively to one new work item.
2. Drain or explicitly resolve live Working assignments, **claimed independent reviews**, in-flight integrations, source-mutating background jobs, outstanding launch demand reservations, pending write intents/unknown outcomes across devices, and uncertain Manager/browser launches before cutover. Claims, jobs and reviews need verified terminal/release evidence; a reviewer verdict alone is not execution termination. If any execution, local worktree mutation, or dispatch remains active/ambiguous, refuse activation rather than inventing completion. Park the Manager schedule and fence old-protocol writes during conversion.
3. Convert each actionable queued/started task stage into one deterministic implementation item. Preserve original spec, attempt generation, frozen source and checkpoint history. Verify that every unfinished source-writing stage has a reachable, immutable continuation SHA or an explicit blocked manual recovery condition; a dirty local worktree is not a recoverable checkpoint. Old ownerAgentId/agent assignment records remain readable as historical attribution only.
4. Convert **unclaimed** reviews into review work items without rewriting frozen review refs or recorded verdicts. A claimed review must first have a verified verdict/release/explicit audited recovery before conversion; never manufacture an unowned review claim while an old reviewer may still be running. Convert accepted-but-not-integrated reviews into integration items only after verifying canonical acceptance, exact frozen source SHA and absence of a completed integration record.
5. Preserve deferred/blocked work as blocked items with equivalent durable dependencies and reverse indexes. Do not drop pending operator/challenge evidence. A formerly Parked or NeedsWork agent with no underlying work must not manufacture a queue item.
6. Materialize complete queue/attention indexes with per-producer and per-shard high-water coverage marks in one checked Git transaction at a verified head. Reconciliation must distinguish truly empty queues from incomplete index coverage after subsequent domain events; missing/partial/lagging coverage must never present as zero work.
7. Perform disposable two-client conversion tests, restart tests, and one controlled local shadow planning run; include claimed reviewers, pending integration/ledger-write intents, dirty worktrees, running mutating jobs, partial conversion crashes, incompatible old writers, and uncertain launches as explicit refuse-to-cutover fixtures. Deploy matching client/server schema versions with a validated protocol handshake that fences old writers before the first new-format commit; old clients must not write incompatible current records. A failed conversion keeps the old schema authoritative rather than partially exposing new queues.
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
9. A task with pending review is not selected as conflicting implementation work on its source lineage. Multiple ChangesRequested reviews produce one current correction stage carrying all exact findings; Accepted produces at most one integration item pinned to exact frozen SHA, target and explicit task workflow policy.
10. Integrator cannot record a mismatched or unreviewed source as the accepted integration. Push success with lost acknowledgement is reconciled from remote Git and canonical integration receipts before another mutation.
11. Blocked items with two challenge dependencies wake only when both are satisfied. Busy workers or absent historical worker IDs do not strand the work. Operator responses remain durable.
12. A running source-mutating background job prevents unsafe release/reclaim of its worktree context until termination or explicit durable dependency handoff.
13. Role subscriptions do not alter OAuth account permissions. Different labels under the owner account can cooperatively claim the same role's available work; versions and claim generations prevent duplicate work mutations, not impersonation.
14. Lead and Integrator execute multi-turn work without synthetic permanent tasks, checkpoint and yield correctly, and can be resumed by a differently labeled session.
15. An absent, partial or stale queue-index coverage marker does not present zero work as healthy. Repair or report an actionable coverage failure.
16. Migration retains old source specs, audit events, review refs, findings, accepted SHA, integration records, task checkpoints and challenge dependencies. Refuse conversion with unresolved active sessions; no forced ledger ref rewrite.
17. Manager never launches merely because a subscribed agent ID is idle; with no eligible work it launches zero sessions. Manager budget caps and no-recursion rule survive restart. Five arbitrarily labeled workers can claim five distinct highest-priority tasks without a pre-created Worker 1–5 roster.
18. Release validation includes .NET 11 Release Client/Server tests, Node Manager/browser suites, disposable Git two-client conflict tests, updater rollback, protocol/API compatibility and a bounded end-to-end queue-to-integration scenario.
19. Worker A has dirty tracked and untracked changes: normal yield refuses without a verified durable source snapshot; after a pushed WIP snapshot Worker B on a different machine reconstructs identical source from the exact SHA and can continue without Worker A's worktree. Unacknowledged remote push before ledger CAS leaves old claim intact and the WIP ref recoverable.
20. Two concurrent Manager passes with one still-available work item and five free worker slots yield only one live launch-demand reservation. Before any worker claims, a repeated tick launches zero additional workers; after a valid terminal outcome/requeue, it may launch again. Also test overlapping role subscriptions and two device journals.
21. A launch that claims, yields and then calls ClaimNextWork again is rejected. Claim operation succeeds but its response is lost: exact operation replay/launch lookup returns the original activation and must not claim a second item. Unknown claim outcome fails closed.
22. A reviewer records Accepted while its activation is still active: integration can be enqueued from the canonical verdict, but the review work item stays Claimed (terminal-pending) until explicit terminal CompleteWork; the reviewer launch capacity is not released by verdict alone. Positive logical terminal event is required; enforce an independently verified terminal-host event if the configured limit refers to provider-process concurrency. The verdict and review-stage completion are separately idempotent.
23. Migration refuses unresolved claimed reviews, in-flight integrations, pending client write intents, old writers, mutating jobs, unverified WIP source and ambiguous launch reservations. Recovery records verified evidence before allowing conversion; replay and two-client CAS remain idempotent.
24. Worker A posts an operator question on Task T, checkpoints and yields. Worker B with a different label claims T and reads the exact question and any reply. The operator can answer while no worker exists. The response is task-addressed; no Worker A inbox or persistent agent ID is required.
25. Two clients concurrently respond to a blocking question. Its linked challenge is satisfied once, the task wakes once only after all dependencies resolve, and all conversation events remain visible. An informational operator message does not implicitly mutate the task spec or decide a review; response to a stale/superseded question cannot silently revive cancelled work.
26. The operator's 'Needs my response' worklist distinguishes genuinely outstanding questions from answered, superseded, nonblocking notes, and incomplete index coverage. Migration preserves legacy agent-addressed messages for audit and routes only unambiguously task-linked questions into task attention; no message silently disappears.
27. Queue review claim races a legacy direct claim: exactly one canonical claim wins or the direct writer is rejected by the new protocol gate. Decided review followed by YieldWork never reopens review; a crash after verdict before CompleteWork preserves the verdict and unresolved launch until verified terminal outcome.
28. Two independent reviews of one task both return ChangesRequested, yet only one source-writing stage for a given lineage is claimable; older review feedback cannot regress the current source tip. Explicit separate lineages permit parallel writing only with their own merge/integration plan.
29. BlockWork races satisfaction of the last challenge dependency before/during/after CAS without stranding the item. A stale or partial index coverage watermark reports incomplete coverage instead of zero. Cutover refuses pending intents, partial conversion or incompatible old-protocol writers.

## 17. Explicit non-goals

- No agent-ID-based permissions, role ACL, identity binding or contribution account security system. Contributor permissions are a separate future **account-level** feature.
- No generic workflow scripting engine, arbitrary event bus, automated source merges, automatic review acceptance, or forced identical pipeline for every task.
- No assumption that ChatGPT tab closure, elapsed time, or a new opaque session ID proves prior execution termination.
- No replacement for protected Git refs, immutable frozen reviews, ordinary test evidence, or operator-controlled deployment.
- No immediate migration or automatic Manager activation as part of writing this document.

## 18. Design choices intentionally settled

- Work claims are **per activation**, not per persistent worker ID.
- Work checkpoints survive arbitrary session/agent changes, including verified remotely retrievable source snapshots for source-writing work.
- Manager dispatches sessions from **unreserved queue demand**, not an idle-agent roster; canonical CAS demand reservations prevent simultaneous Manager passes from launching duplicates.
- Review and integration are regular queue subjects with existing canonical evidence, not special lifetime tasks.
- Operator communication is task/subject-addressed with immutable messages and challenge-backed blocking questions, not agent-inbox-addressed; numbered Worker IDs are historical labels, not required allocation semaphores.
- Lead determines task priority, Manager only dispatches role capacity against verified demand, and Workers claim highest-priority eligible tasks atomically.
- Subscriptions route work; authenticated accounts supply permissions.
- Yield releases unfinished work only with an atomic final checkpoint, durable reachable continuation source when needed, and activation/launch termination. Each launch claims at most once; lost claim results are replayed by stable operation ID.
- Blocked is a work/dependency state; no Parked or Waiting agent disposition.
- Canonical review claim/verdict and queue dispatch/terminal state are CAS-linked, never allowing post-verdict yield to reopen a review.
- Source writers are exclusive within an explicit task lineage while independent frozen reviews may run in parallel; superseded feedback cannot regress source state.
- Challenge block/wake races and continuously versioned queue/attention index coverage are fail-closed, including during migration.
- Uncertain browser launch and unsafe stalled execution recovery remain fail-closed and explicit; a future verified recovery mechanism may improve liveness without weakening exclusivity.
- The project's Git orphan branch remains authoritative, with derived indexed availability and no parallel mutable authority.
