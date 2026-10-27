# Jarvis Orchestration V2 — architecture review and redesign

**Status:** Proposed architecture, not implemented
**Date:** 2026-10-27
**Basis:** Jarvis main at d7cdcf4; task-owner-scope patch 897dcce, protocol 3.19, server SQLite persistence, multi-client routing
**Supersedes design direction:** docs/specs/task-owner-scope-remediation.md on fix/task-owner-scope
**Decision:** Make the Jarvis server the authoritative transactional orchestration control plane. Git is authoritative for source code and provenance of imported task specifications; the server retains immutable pinned spec revisions and owns live task/assignment state.

## 1. Executive decision

The defect is architectural, not primarily a missing owner-scope comparison.

Today, the same task JSON is copied into every branch created after its commit. Each checkout can independently mutate status, owner, acknowledgement, checkpoint, or nextActor. Jarvis then reconstructs supposedly global truth by scanning all registered worktrees and guessing which copy is authoritative. Owner-worktree selectors merely patch one symptom. They cannot make source branches into a reliable transactional coordination database.

**Replace branch-local task-state JSON with one durable per-project control plane.** All task/assignment/review transitions are transactions in the server database. Git worktrees remain temporary execution environments, connected to an assignment through separately validated work bindings. Task specifications and code are immutable/traceable Git artifacts, not competing state stores.

A single-user system can still have many agents, devices, checkouts, network retries, and concurrent commands. Its correctness requirements are not meaningfully different from those of a multiuser dispatcher.

### Invariants

1. Exactly one current authoritative row for a task, independent of the number of Git checkouts or branches.
2. Agent responsibility (assigned task) is independent of the checkout used to implement it.
3. Agent availability is independent of owning parked tasks or tasks awaiting review.
4. Source code integration, reviewer acceptance, task completion, and agent handoff are four distinct facts.
5. Every state transition is atomic, revision checked, idempotent under retry, and auditable.
6. A missing or relocated checkout is a source-binding problem; it never erases or duplicates an assignment.
7. Task state remains queryable when clients are disconnected.
8. Git branches may be cloned, moved, rebased, merged, or deleted without mutating orchestration truth.
9. No second mutable task-state authority, including a writable Git mirror or per-client cache.
10. Failure must not silently turn Blocked, Parked, Queued, or Unavailable into Free.

## 2. Deeper review of the current implementation

| Concern | Current implementation | Failure and root cause |
|---|---|---|
| State authority | Task JSON alongside HTML in a selected source checkout | Branch creation copies live assignment records; reconciliation needs filename/owner heuristics |
| Task identity | Project-name string plus task ID; current checkout inferred from owner.worktree | A directory rename can make an active claim disappear; separate devices may use the same project name for unrelated repositories |
| Agent ownership | Task owner agent and task checkout bundled together | Cannot distinguish an agent's responsibilities from the machine/branch implementing them |
| Scheduling | One actionable assignment per agent, including reviewer handoffs | Agent cannot readily take a new job while an old task awaits review |
| Status | status + nextActor + acknowledgement + checkpoint interpreted together | Possible contradictory states, stale receipts, and ambiguous readiness |
| Global discovery | Scan every registered Git worktree for every snapshot/claim | Partial failures, inherited duplicates, potentially large/slow snapshots and page-specific false Free states |
| Mutation | Per-checkout Git commit plus in-process mutex | Atomic only in one process/checkout; no global transactional guarantee for competing mutations |
| History | Git history for task JSON intertwined with source branch history | Integration can merge stale coordination state; task-only commits complicate retirement and acceptance |
| Multi-device | Server routes to one selected client per call | Identical logical projects are not inherently linked across devices; orchestration disappears when the selected client disconnects |
| Task provenance | Task HTML and JSON in selected branch; descriptionCommit optional | Source specifications can change while an agent works without an explicit, pinned assignment baseline |
| Review/integration | Completion and Git source parity inferred together in snapshot | Acceptance, merging, and cleanup have distinct semantics but too much responsibility in the branch scan |
| Availability | UI derives Free/Parked/Blocked from the task roster | Task state is not actual session presence or agent capacity |

Recent fixes to actionable owner scoping, paginated global roster, and explicit Parked status are useful *temporary corrections* but do not change these boundaries.

### Alternatives evaluated

| Design | Benefits | Why it is not the target |
|---|---|---|
| Continue per-worktree JSON plus a stronger owner resolver | Least disruptive, retains branch-local files | Fundamental duplication and merging problems persist |
| Authoritative Git ref/branch distinct from source branches | Retains Git as a transactional/audit store; optimistic compare-and-swap is feasible | Introduces a custom Git state engine, cross-clone ref syncing, query indexing, server/client primary selection, and backup/failover logic |
| **Server-side transactional database + immutable event history** | Single authority across clients; strong transactions; efficient inbox and pagination; accessible with clients offline; fits existing server | Requires an intentional breaking migration and reliable server backups — acceptable here |

A disposable Git proof-of-concept verified that compare-and-swap update of a dedicated reference can work without checking out source files. This makes the Git-ref option viable, but the shared server database is better aligned with Jarvis's existing authenticated multi-client architecture. The server already uses Microsoft.Data.Sqlite and SQLite WAL for feedback.

## 3. Ownership boundaries and identities

### 3.1 Server control plane

The server stores task records, immutable task-spec revisions, execution attempts, assignment generations, inbox/agent capacity, reviewer decisions, checkpoints, dependencies, integration evidence, audit events and workspace registrations.

Each authenticated operation is scoped by **(authenticated user ID, projectId)**. The server does not rely on the currently selected device to decide which task belongs to which agent. Read-only task queries require no client connection.

### 3.2 Client source plane

Clients own local Git and filesystem operations: worktree discovery, source reads and patches, running tests, commits, branch merges, push operations and validating Git integration evidence.

Clients report typed evidence back to the server. An offline/removed worktree affects only source operations and binding health; it does not affect task ownership or checkpoint history. The server never pretends that a client-supplied string is verified Git evidence.

### 3.3 Persisted identity model

- **projectId:** UUID, independent of a display name or directory. Add it to version-controlled .jarvis.json; existing projects register/mint it once, with explicit approval when linking another device or clone. Clones inherit the same projectId by design.
- **workspaceId:** Stable UUID for a registered physical repository on a particular device; stored in private client configuration. The server records (projectId, deviceId, workspaceId), physical Git common-directory identity and recent observations.
- **worktreeBindingId:** Stable UUID for a specific registered checkout association, not the agent's identity. Its path, branch and head are mutable observations, never task ownership keys.
- **taskId:** Stable project-scoped logical ID; existing numeric and date-prefixed IDs remain valid. Uniqueness is enforced at (userId, projectId, taskId).
- **specRevisionId:** Immutable task description revision, including original source Git path, blob ID and commit ID, plus server-retained content for resilience.
- **attemptId:** Stable UUID for one implementation attempt of a task. Attempts survive relocation, retirement or recreation of a checkout.
- **assignmentId + generation:** Identity of an assignment and monotonically increasing revision for new reviewer instructions. Acknowledgements must name the exact generation.
- **agentId:** Stable project-scoped configured name such as Agent 3 or Agent 10, not a Git branch, client device, process ID or ChatGPT conversation ID.
- **operationId:** Client-generated idempotency key for every write, persisted with its response.

All IDs use explicit server-side validation; user-friendly names remain labels. Multiple projects with identical filenames or display names cannot collide.

## 4. Data model

SQLite with WAL, foreign keys, transactions, indexed queries and schema migrations. Use dedicated OrchestrationStore rather than mixing operational schemas with FeedbackStore access logic. One durable server database is sufficient; a separate deployment of PostgreSQL is not required merely because resources are available.

Logical tables:

- Projects(userId, projectId, displayName, settings, version)
- Workspaces(userId, projectId, workspaceId, deviceId, repo identity, registration status, lastObserved)
- WorktreeBindings(projectId, bindingId, workspaceId, branchRef?, pathObservation?, headOid?, lastVerified, state)
- Tasks(projectId, taskId, title, taskLifecycle, currentSpecRevision, currentAttemptId?, version, createdAt, updatedAt)
- TaskSpecs(projectId, taskId, specRevisionId, immutableContent, contentHash, originGitBlob?, originGitCommit?, originPath?, createdAt)
- Attempts(projectId, attemptId, taskId, attemptPhase, agentId?, assignmentId?, branchRef?, baseCommit?, bindingId?, version, updatedAt)
- AssignmentGenerations(projectId, assignmentId, generation, instructions, baseCommit?, issuedBy, issuedAt, receiptAt?, beginAt?)
- AgentSlots(projectId, agentId, currentAttemptId?, version)
- Checkpoints(projectId, attemptId, checkpointId, assignmentGeneration?, summary, verifiedEvidence, blocker?, nextAction?, sourceHead?, createdAt)
- Reviews(projectId, attemptId, reviewId, decision, reviewerId, notes, evidence, createdAt)
- Integrations(projectId, attemptId, targetRef, sourceCommit, integratedCommit, verificationMethod, verifiedAt, verifiedBy)
- TaskDependencies(projectId, taskId, dependsOnTaskId)
- Events(projectId, sequence, operationId, actor, eventType, entityId, version, payload, timestamp)
- OperationResults(userId, projectId, operationId, requestHash, result, createdAt) for idempotent replay

Foreign keys enforce valid references; unique and partial indexes enforce one active execution slot per agent and at most one designated current attempt per task. Event writes and affected rows commit in **one database transaction**. Do not attempt to derive the canonical current state by replaying every event on ordinary reads; relational rows are the materialized state and Events is the append-only audit history.

Task definitions are canonical **immutable revisions**; agents are assigned a pinned spec revision. Task description HTML remains available as ordinary Git documentation and source provenance. A source-branch edit does not silently rewrite the instructions of an in-progress assignment. Explicit ReviseTaskSpec imports the new content, creates a new revision, and optionally reissues instructions.

### Distinct state axes

**Task lifecycle:** Open, InProgress, Implemented, Integrated, Closed, Cancelled.
- Implemented means the reviewer accepted an implementation, not that it is already in main.
- Integrated requires recorded Git integration evidence; Closed is an explicit final decision.
- A documentation-only task may have an explicit evidence-free closure policy, but never pretend it had a source merge.

**Attempt phase:** Prepared, Queued, Assigned, Acknowledged, Working, ReadyForReview, ChangesRequested, Parked, Blocked, Accepted, Superseded, Abandoned.
- Parked means intentionally not progressing; Blocked includes a reason/dependency.
- ReviewReady does not consume agent execution capacity.
- A task may preserve multiple historical attempts, but only one primary active attempt unless multi-attempt execution is explicitly requested.

**Turn/slot state:** an agent may have multiple task responsibilities and multiple queued assignments, but at most one **executing** attempt by default. BeginAttempt atomically acquires the slot. Completing an agent turn, requesting review, parking or blocking releases it. Reviewer feedback creates a new queued generation; it never secretly commandeers a busy agent.

**Presence:** optional explicit session/device heartbeat and its TTL. No heartbeat is inferred from an assignment, status, network process, or ChatGPT chat.

This separation permits: Agent 3 owns a Parked Task 308, Task 305 awaits reviewer integration, and Agent 3 simultaneously executes another task. The roster can report **Available for work** or **Working** independently of the parked/reviewer task counts. Absence of an active assignment is not proof the chat session is online.

## 5. Transaction protocol and concurrency

Every mutating MCP command carries projectId (or a resolved project name), operationId, expectedVersion for each modified logical entity, and actor/audit context. The server authenticates the caller and authorizes the specific task action.

1. Resolve project identity and permission from authenticated user and registered project.
2. Begin SQLite write transaction (BEGIN IMMEDIATE or equivalent) with bounded busy handling.
3. Check operationId and request hash: return the previous result if identical; reject reuse with different input.
4. Validate version, transition, agent slot, task dependencies, ownership and expected assignment generation.
5. Apply row mutations and enforce unique database constraints.
6. Insert immutable event(s) with monotonic project sequence and store the operation result.
7. Commit atomically; send response. Network timeout after commit is safe to retry with the same operationId.

Reads use a consistent snapshot and indexed queries. GetOrchestrationSnapshot paginates tasks with a stable cursor and snapshot revision; avoid OFFSET pages that silently skip/duplicate rows during concurrent writes. Roster and reviewer counts come directly from authoritative rows, never from source scans. Bound instructions, diagnostics, and event-history payloads separately.

There is no per-worktree global-assignment mutex, owner.worktree lookup, source-branch state write, or Git merge conflict as part of an assignment transaction. Multiple clients can route concurrent operations through the same database. SQLite is the single writer *per transaction* and automatically arbitrates these mutations.

If the server commits state but the response is lost, clients retry the idempotent operation. If the server records assignment success but a later source checkout operation fails, the attempt is retained as Unbound/NeedsCheckout with an actionable error; it does not silently disappear.

## 6. Source worktrees, provenance and integration

### Task creation and descriptions

CreateTask accepts substantive HTML/spec content or a reference to an existing repository file. Importing a Git file records its content and exact blob/commit/path as an immutable spec revision; this import must be checked against a connected, authorized workspace and recorded with the client's evidence. Existing docs/tasks HTML files can remain in source history as human-readable references. New canonical task state is **not** written as docs/tasks/*.json.

GetTask reads the server store, including the pinned spec revision and audit history. Agents can retrieve assignments while no client is connected. An explicit ExportTaskSpec can materialize a read-only human file in a checkout, but editing that export does not automatically mutate authoritative state. There must be no automatic bidirectional synchronization.

### Attempt binding

An attempt references an optional source branch and workspace binding. On assignment, Jarvis can create a new branch/worktree via the client and register its branch ref, base SHA, binding UUID and current HEAD. Or it can use an existing registered checkout explicitly.

A moved or renamed checkout changes path observation, not task ownership. Resolve a checkout against the registered workspace and current branch ref; when unique, refresh binding metadata. When absent or ambiguous, report NeedsCheckout and offer explicit BindAttemptCheckout. Do not use private Git administrative directory names as persistent identities. A detached worktree must be bound explicitly. Worktree removal is safe only after the source checks and active running-slot conditions pass; task state and history survive removal.

### Completion and source acceptance

The reviewer may Accept an attempt without implying any merge. Integration requires an explicit separate operation that verifies source commit ancestry or documented patch-equivalence against a specified target, including the target revision and the observing client/workspace. The evidence is saved, then the attempt and task are marked Integrated transactionally. A failed or unavailable Git verification leaves the attempt Accepted/PendingIntegration, never falsely Integrated.

Push is an explicit source-control action after integration, following the user's current orchestration policy. A Git push failure does not roll back reviewer acceptance or erase evidence; the task shows PendingPush/FollowUp if configured. Do not perform Git writes as hidden side effects of read-only MCP calls.

The worktree cleanup command checks source integration, dirty/staged/untracked/ignored files, live jobs and executing attempt bindings; it no longer has to preserve task-only state commits on the source branch.

## 7. New tool surface (breaking, intentionally)

Keep normal local Git, file, command, job and worktree operations. Move all orchestration commands to **server-owned** MCP methods, using typed responses and unchanged authentication discipline.

- RegisterProject / ListProjectRegistrations: establish projectId and device/workspace association.
- CreateTask / ImportTaskSpec / ReviseTaskSpec / GetTask / ListTasks / GetTaskHistory.
- CreateAttempt / AssignTask / GetAgentInbox / GetAgentRoster.
- AcknowledgeAssignment / BeginAttempt / CheckpointAttempt / SubmitForReview.
- ReviewAttempt (Accept, RequestChanges, Reject) / ParkAttempt / BlockAttempt / ResumeAttempt.
- BindAttemptCheckout / GetAttemptSource / VerifyIntegration / RecordIntegration / CloseTask.
- GetOrchestrationSnapshot (cursor-based, consistent, no Git scan).
- ExportOrchestrationArchive / InspectMigration (read-only portability and repair).

Replace GetAgentAssignment (singular) with GetAgentInbox(project, agent); agents still need to know only their project and stable name. Inbox distinguishes ActiveTurn, Queued, NeedsAcknowledgement, ReadyForReview, Parked and Blocked. For single-turn convenience, BeginNextAssigned picks an eligible queued attempt atomically and returns exact assignment generation. Review work can be queued to the same agent without violating active-slot uniqueness.

Do **not** preserve legacy WriteTask/ClaimTask/UpdateTask/CheckpointTask clients behind silent compatibility aliases in the new protocol. Server/client version gate should reject incompatible task writes rather than partially apply them. Use a major protocol version (proposed 4.0), coordinate server and active clients, refresh MCP tool descriptions, and remove dead legacy WorkTask handlers.

Permissions: all server task mutations require verified user identity plus the appropriate write/assignment role; source operations still use explicit local client approval. Never let a request choose a device by changing another conversation's selection. A source-affecting action targets the attempt's **registered** workspace/device explicitly; missing device produces an availability error, not a silent fallback.

## 8. User interface and agent workflow

The Agents screen and global orchestration view show per-agent:
- Agent name, selected current executing attempt, explicit capacity (Available/Busy), optionally last heartbeat/session status.
- Queued assignments and next requested actor, including assignment generation and unseen/new markers.
- Owned tasks awaiting review, Parked, Blocked or pending integration (each with count, task ID and next action).
- Checkout binding health and source branch/head, independent of task ownership.
- Reviewer queue and integration/push queue, with distinct counters.

The Activity screen can display server task events alongside device-local job activity, with visible provenance and timestamps. It must not imply network liveness from persisted Working phase. Use same filter syntax and keyboard conventions as the existing Activity TUI.

### Example desired sequence

1. Reviewer creates/imports Task 315 spec and assigns Agent 1, optionally binding an existing checkout.
2. Agent 1 calls GetAgentInbox("Wayfold", "Agent 1"), acknowledges generation 12, begins the turn, and obtains the execution slot.
3. Agent implements in a feature worktree; CheckpointAttempt records tested commit SHA and source evidence; SubmitForReview releases the slot.
4. Agent 1 may begin Task 316 while Task 315 remains owned and awaits review.
5. Reviewer accepts Task 315. Orchestrator merges/pushes the implementation, records verified integration, then closes Task 315; worktree cleanup is independent.
6. Another feature worktree is cloned from main. It inherits *no* task ownership because assignments live only in the server ledger.
7. The checkout path changes. Task 316 and its agent assignment remain unchanged; only its worktree binding is refreshed or explicitly repaired.

## 9. Persistence, backup, outages and security

- Use SQLite WAL and explicit migration scripts; restore and integrity-check in tests. Do not reuse FeedbackStore's schema for orchestration tables.
- Perform regular verified **online SQLite backups**, capturing a transactionally consistent snapshot including the event log and task-spec blobs; encrypt backups and keep them outside the source Git repository. Define retention and a restore drill before production cutover.
- Provide authenticated ExportOrchestrationArchive with a portable versioned manifest, task specs, current state, event sequence/checksums, and redaction controls. It is a **backup/export**, not another live authority.
- During a server outage, source Git work remains editable locally; authoritative task transitions wait or are explicitly queued with idempotency keys. Do not invent successful checkpoints that have not committed.
- Server restarts must restore ongoing assignments, reviews, queue order, active turn state and operation deduplication. A persisted Working turn may be marked StaleTurn after missing a separately configured heartbeat; it is not automatically reassigned.
- Keep paths and device-scoped credentials out of project-level portable task IDs. Validate imported Git blobs; bound task spec sizes, snapshot pages and log payloads. Apply standard OAuth scopes and per-user DB isolation.
- If the project is registered from multiple devices, the server remains the common authority. Each device supplies independently verified source observations. No locally cached task JSON may become a competing writer.
- Project deletion and restore are explicit, audited, reversible operations; never rely on worktree pruning to reclaim orchestration data.

## 10. Breaking migration from protocol 3.19

A deliberate, **one-way cutover**, not a long-running dual-write migration.

1. Build the new schema, MCP API and tests on a dedicated design/implementation branch. Prepare a complete encrypted backup of the current server database and each participating Git repository, including branch tips.
2. Freeze old task mutations for each project; other source Git work may continue. Read all registered worktrees' HTML and JSON (including malformed diagnostics) into a migration report, preserving (checkout, commit, task ID, full JSON hash, owner.worktree, status, checkpoint and assignment revision).
3. Group by logical projectId and taskId. Import descriptions as immutable spec revisions. Do not select a current assignment by newest timestamp alone: use explicit authoritative owner scope where unambiguous and flag conflicts/legacy missing scope for operator decisions. Preserve **every** source record as migration evidence; never delete historical branch files.
4. Convert current tasks/attempts, parked/blocked ownership, reviewer queues, assignment generations and completed evidence into server rows and event records. Create explicit NeedsCheckout entries when a source binding is absent.
5. Run migration in dry-run mode and emit counts, duplicate IDs, conflicting branches, unresolved owners, retained specs, per-agent capacity and source evidence. Reconcile all actionable conflicts before committing the cutover.
6. Apply via one recoverable, versioned migration transaction per project, with migration idempotency key and checksum. Verify server inbox, roster, review queue, parked task state and history against the accepted report.
7. Deploy the new server and clients together. Disable old task writes completely and switch the TUI/MCP tools to server queries. Do not write task-state JSON into source worktrees after cutover; leave old JSON as read-only historical artifacts.
8. Confirm task handoffs, Git evidence, rollback/restore procedure, restart persistence and multiple devices. Only then close the migration checkpoint. Old branch files can be archived/cleaned separately if desired.

Rollback before accepting v2 writes: restore old server/versioned DB backup and old client binaries, retaining the Git source branches. After accepting v2 writes: restore **v2** database backup and events, not silently return to stale branch-local task state. No dual-authority rollback.

## 11. Acceptance and failure-injection tests

Tests must include:
1. Clone main into 20 linked worktrees; imported Task 315 has exactly one authoritative assignment, one reviewer entry, no false owner conflict.
2. Agent 3 owns parked Task 308 and works on another task; roster shows both responsibility and explicit execution capacity correctly.
3. Agent 1 submits Task 315 for review, immediately begins Task 316; reviewer sends Task 315 changes back while Agent 1 is busy; the new generation is queued, not auto-started.
4. Simultaneous AssignTask/BeginAttempt/reviewer-return requests from different MCP sessions cannot create duplicate active slots or stale state transitions.
5. Timeout after database commit followed by retry of identical operationId returns the same result and only one event; changed payload with reused key is rejected.
6. Process crash between every simulated state write, event insertion, and transaction commit never exposes a half-applied assignment; restart reconstructs consistent state.
7. Worktree renamed, removed, recreated, or detached: task ownership survives; source binding is refreshed if unambiguous or marked NeedsCheckout.
8. Server disconnected from all clients: GetTask, GetAgentInbox and snapshot work; source Git commands are unavailable with explicit errors.
9. Task specs edited on another branch after assignment: pinned spec and instructions do not change until explicit ReviseTaskSpec/reissue.
10. Concurrent pagination observes consistent unique task rows and counts; large task histories are bounded and independently paged.
11. Reviewer acceptance is distinct from verified merge integration and from push; failed evidence verification never marks Integrated.
12. Multi-device clones with the same registered projectId share server assignments but have distinct workspace bindings and permissions.
13. An inherited or malformed legacy task-state JSON produces a migration diagnostic, not a second live assignment; historical state remains inspectable.
14. Restore encrypted backup and check event/current-state consistency, attachment/spec hash integrity, and monotonic generations.
15. Unauthorized project registration, impersonated agent/actor identity, unregistered checkout binding and traversal/URL abuse fail safely.
16. Legacy protocol commands cannot mutate new task state after cutover.

Gate: full server/client test suites on supported platforms, a real disposable multi-worktree integration fixture, concurrency fault injection, database recovery/backup restore, and no regressions in existing Git source/edit/job APIs.

## 12. Implementation sequence and decision boundaries

**Phase A — Prototype the control plane:** schema and one end-to-end CreateTask → AssignTask → Begin → Checkpoint → Review cycle in disposable projects; no production data conversion.

**Phase B — Inboxes and work bindings:** multi-agent scheduling, registration/project identity, binding observation, review/reissue/queue semantics, large snapshot and TUI.

**Phase C — Completion and provenance:** immutable spec versions, verified merge evidence, Git history references, branch push/cleanup boundaries, complete audit/export/backup.

**Phase D — Migration and hardening:** dry-run migration, duplicate reconciliation, client/server major protocol rollout, legacy write removal, negative/race/crash/restore tests.

**Phase E — Production cutover:** approved migration report, coordinated deployment, live validation, verified backup and restore capability. Never automatically convert the current live Wayfold task state during the design phase.

### Decisions deliberately *not* made by this document

- No promise of autonomous agent session presence without an explicit heartbeat/connection identity.
- No automatic self-assignment across agent identities; orchestrator policy remains authoritative for assignment.
- No hidden worktree creation/push while reading an inbox or roster.
- No claim that a local Git push and SQLite transaction can be atomically committed together. Persist and reconcile durable outbox/evidence states instead.
- No need for a distributed SQL database until Jarvis itself is deployed as multiple independent writable server nodes.

## Conclusion

**Choose the best model rather than perfecting owner.worktree.** Task ownership belongs to the orchestration control plane; Git branches are implementation artifacts. The former must be serializable and durable, the latter reproducible and freely mergeable. Migrating to a server-authoritative ledger with per-agent queues, attempts, pinned specifications and explicit Git provenance eliminates the inherited-task bug at its source and supports Jarvis's actual multi-agent, multi-device purpose.

## Appendix A — normative state transitions

The following transitions are first-class verbs, not arbitrary writable status strings. Each requires expected row version and operationId, and each appends the resulting event inside the same transaction.

| Operation | Preconditions | Atomic effects | What it must never do |
|---|---|---|---|
| CreateTask | Unique project/task ID; valid pinned spec | Insert Open task, initial spec revision and event | Touch a source checkout implicitly |
| CreateAttempt | Task open/active; no unintended competing primary attempt | Create Prepared attempt and optional explicit source binding | Assign an agent automatically |
| AssignTask | Valid task/attempt/agent and reviewer authority | Insert new assignment generation, queue entry; set task InProgress | Occupy an execution slot or infer a worktree from task ID |
| AcknowledgeAssignment | Same agent, current assignment generation | Persist receipt of exactly that generation | Treat receipt as evidence that work started |
| BeginAttempt | Current queued/acknowledged generation, agent slot available | Atomically occupy slot and set Working | Begin a second attempt for the same agent |
| CheckpointAttempt | Current generation and authorized actor | Append immutable checkpoint/evidence | Replace assignment instructions or retroactively change older checkpoints |
| SubmitForReview | Working or acknowledged submitted attempt, evidence supplied | Set ReadyForReview; release execution slot; create reviewer inbox item | Treat work as merged or task as Closed |
| RequestChanges | Reviewer has the ReadyForReview item | Append decision, issue *new* agent generation, queue; keep checkpoint | Preempt another executing attempt |
| AcceptAttempt | Reviewer has ReadyForReview item | Append acceptance, mark Accepted; task Implemented | Mark code Integrated without Git evidence |
| ParkAttempt | Nonterminal attempt; actor authorized | Park with reason/condition, release slot, remove active turn | Clear the assigned agent, source binding, or history |
| BlockAttempt | Nonterminal attempt; concrete blocking reason | Block with reason/dependency, release slot | Silently become Available *and* lose blocked ownership |
| ResumeAttempt | Parked/Blocked/ChangesRequested; current authorization | Reissue generation and queue; retain prior evidence | Reuse a stale acknowledgement or immediately take a busy agent's slot |
| VerifyIntegration | Accepted attempt with recorded code evidence | Persist independent source-verification record | Synthesize code integration from a completed task status |
| RecordIntegration | Verified target/source and expected versions | Mark Integrated and append event | Perform an implicit push or worktree deletion |
| CloseTask | Accepted completed outcome and configured closure condition | Mark Closed, preserve attempts and audit | Infer that old source branches are disposable |
| CancelTask/AbandonAttempt | Explicit reviewer/orchestrator action | Terminal state, release slot and queue | Delete historical evidence |
| BindAttemptCheckout | Selected registered workspace and verified Git identity | Update attempt's *execution binding* and observations | Reassign the agent or change task status |
| RetireWorktree | Verified source/working-tree safety, no executing binding | Client removes verified checkout; server updates observation | Delete branch history or any task record |

A reviewer may queue return work while the previous executing slot is occupied by a different attempt. The queue is durable and the handoff becomes actionable for that agent only when the agent explicitly begins it. A terminal attempt has no open execution slot. Task and attempt phase can never be updated independently to a contradictory combination.

## Appendix B — consistent snapshot paging

A cursor containing an event sequence alone is insufficient to reconstruct historical query results from mutable current-state rows. Use either a bounded server-side materialized result snapshot with an expiring opaque token or temporal row versions derived from the append-only event log. **V2 initially chooses an expiring materialized snapshot** to make stable pages straightforward.

- On first query, transact a consistent read and materialize the bounded sorted ID/version set (not full unbounded descriptions) for that query, storing it with a short TTL, user/project identity and query hash.
- Every continuation token includes a random opaque snapshot ID, page position and expiry; verify identity, query hash and bounds. Return explicit SnapshotExpired rather than pretending new state belongs to the same page.
- When materialized item records change after snapshot creation, return the original captured summary version or an explicit ChangedSinceSnapshot marker; do not silently mix revisions.
- The first response provides the exact total for that snapshot. Roster summaries reflect the same point-in-time state.
- Provide a separate live refresh action to discard the token and query current authoritative state.
- Cap entries, bytes, session TTL, and concurrent snapshots per user to prevent resource exhaustion.

## Appendix C — project registration, failure recovery and portability

RegisterProject must verify that the selected local Git root contains the tracked projectId and that it is unique/valid for the authenticated user. A different repository claiming an existing ID must be explicitly linked rather than silently overriding a registered workspace. The device identity and local Git common-dir path are observations, not tenant authorization credentials.

Git actions are naturally two-stage relative to database writes. Treat them as durable commands with explicit PendingSourceAction / SourceActionSucceeded / SourceActionFailed observations and idempotent operation IDs. If the server loses contact after the client completes a Git operation, retry by checking repository state rather than blindly repeating a potentially non-idempotent branch creation/merge. Database transactions never hold open across network calls to a client.

Persist the exact authenticated user ID, asserted agent label, device/workspace identity, request hash and resulting event for an operation. An agent name by itself is an orchestration key, not an authentication credential.

Backups are part of the product, not a deferred operational footnote. Before migration, implement and test an online backup, encrypted off-server retention, schema-version metadata, checksums, restoration of event sequences and immutable spec revisions, and a recorded restore drill. Do not activate V2 without a verified recovery route.
