# Jarvis orchestration — orphan-branch cutover

**Status:** Revised 2026-10-29. This document replaces the over-scoped 2026-10-28 V2 architecture contract. The earlier design is available in Git history; it is **not** a backlog to finish.
**Authority:** `refs/heads/jarvis-orchestration`, an orphan branch of each project's existing Git remote.
**Current implementation:** The isolated `feature/orchestration-v2-ledger-store` branch implements much of this already. Commit `2aff9bb` removed the 15 legacy task MCP endpoints from that branch. This is **not yet deployed or cut over**.

## 1. Goal and boundary

The problem is simple: when tasks and assignment metadata live in normal source branches, every worktree can have a different copy. An orchestrator cannot reliably tell which one is current.

**Fix that by storing all live orchestration state in one orphan Git branch.** Ordinary source branches and worktrees contain source code, not authoritative task state. Multiple Jarvis clients fetch/update the same branch. The Jarvis server stays stateless and routes commands to the connected client.

This is a **replacement**, not an additive V1/V2 compatibility system.

- Remove the old repository-local task and cross-worktree orchestration MCP tools from the public surface. Do not make them forward to the new system. Do not preserve fallback readers, dual writers or a per-project compatibility toggle.
- Keep normal Git, worktree, file, job and client tools. They are not legacy orchestration tools.
- Leave existing HTML/JSON task files on disk and in Git history. They are no longer authoritative once the orphan branch is in use. An agent may inspect and manually recreate whichever tasks are still relevant.
- **Do not implement an automated migration tool, scanner, manifest, conversion pipeline or migration protocol.** No automatic copying or two-way synchronization.
- Do not introduce a database, stateful server, scheduler, event bus, separate repository, or a generalized distributed workflow framework.

## 2. Git authority and layout

For each project, use exactly one configured remote and one canonical orphan ref:

```text
refs/heads/main                       ordinary source code
refs/heads/feature/...                agent implementation branches
refs/heads/jarvis-review/...          review snapshots in source history
refs/heads/jarvis-orchestration       orphan branch, task authority
```

The orphan branch is created **explicitly**, has no shared ancestry with source history and is never merged into a normal source branch. No source worktree needs to check it out. The client reads and writes Git objects for this ref directly.

Use the already implemented ledger tree rather than designing a new schema:

```text
ledger.json
agents/<agent-id>.json
tasks/<task-id>/spec.html
tasks/<task-id>/task.json
tasks/<task-id>/attempts/...
tasks/<task-id>/reviews/...
tasks/<task-id>/integrations/...
ops/...
```

Exactly one current record for each task; Git history retains prior versions. Task IDs are stable within the project. Task records reference their implementation attempts and review requests. A source branch or worktree path is an implementation detail, **not the task owner's identity**.

The Git remote is the sole source of truth. Local binding/configuration and caches exist only to choose and verify the remote; they are not alternative task stores. Existing explicit initialize/bind/health operations are sufficient.

### Updating the orphan branch

Every logical mutation:

1. Fetches the current canonical ref and reads the state at that commit.
2. Validates the affected record(s) and current owner/review state.
3. Writes one new Git commit with the fetched head as parent.
4. Pushes without force. On a non-fast-forward rejection, refetches, rechecks the intended change and retries or returns a conflict.

Never blindly merge conflicting task JSON. Never rewrite, force-push or delete the authoritative branch in normal operation. A remote outage must not be treated as a successful update. For ambiguous push results, query the remote before retrying.

Keep the existing stable operation ID/deduplication mechanism so a retried tool call cannot accidentally apply twice. This is an implementation detail, not a mandate to add general-purpose operation-history, reconciliation or pagination APIs. A small amount of Git validation and remote-identity protection is warranted; elaborate trust ceremonies, new recovery protocols and independent backup services are not prerequisites for cutover.

## 3. Tasks and agents

An agent is identified for task ownership by its project-local label, not by an external session ID, device, or worktree. Session IDs are opaque provider-supplied execution identifiers. **Only one implementation agent owns a task at a time.** There is no concurrent implementation of the same task to coordinate.

Retain the agreed operator-facing agent states:

| State | Meaning | Who acts next |
|---|---|---|
| **Ready** | Agent checkpointed and yielded existing work | Operator resumes agent |
| **Assigned** | Orchestrator assigned work; agent has not begun | Operator starts agent |
| **Working** | Agent claimed its assignment and is working | Agent continues |
| **Completed** | Agent finished its responsibility and is available | Orchestrator assigns work |
| **Parked** | Orchestrator has no work for this agent | Nobody until reassignment |

A registered idle agent starts Parked. Each agent has at most one Working assignment. Assignment, start, checkpoint, yield, resume, completion and reassignment update the canonical orphan branch. The assignment includes the task, instructions and enough source context to continue in the correct worktree. A checkpoint carries a summary and next action so a restarted agent session can recover work.

Changing task ownership or an assignment generation invalidates stale task-scoped worker operations. If an old session tries to resume, checkpoint, submit a review, or otherwise act on a reassigned attempt, reject the operation **without changing the task**, explicitly tell it that ownership or generation changed, and instruct it to stop working on that task and reread its assignment. A changed **session ID alone** does not change task ownership or assignment generation; a superseded session nevertheless cannot continue or reclaim the current Working execution. An orchestrator explicitly decides to recover/reassign; elapsed time alone never steals ownership. Generic file/command tools have no intrinsic task identity; do not invent one to block unrelated commands.

**Task completion and agent availability are different.** An agent can complete its work, receive another assignment, and still have an earlier task awaiting review or integration. No review decision interrupts the agent's newer work.

Task descriptions are stored directly in the orphan branch as HTML. The assigned agent can read its pinned instructions/spec. Editing source-side HTML does not silently edit the canonical task. Use existing spec operations where useful; do not require special import/export workflows to make tasks usable.

### Working-turn timestamps and 25/30-minute guidance

An agent enters **Working** through BeginAttempt or a Ready-to-Working ResumeAgent. A normal turn **must end by leaving Working** (typically yield to Ready or complete to Completed) before writing its final response; ending a turn while still Working is invalid behavior. Checkpointing or submitting a review does **not** finish a turn. A stalled harness is the exception: the agent may remain Working until explicitly resumed.

The canonical agent record stores `stateEnteredAtUtc` (the start of its current state) and, while Working, `workingTurnStartedAtUtc` (the start of its current **timed execution segment**). Both are set on entering Working. Normally they remain equal. A reported stall recovery may begin a new timed segment **without changing state**, so the two timestamps deliberately differ. Prior state and segment boundaries remain reconstructible from the agent record's Git history and the recovery event; no saved durations, counters, server timer, or database are needed.

On each subsequent MCP tool response from the associated agent session, check elapsed **wall-clock time** since the current timed Working segment began (`workingTurnStartedAtUtc`):

- **25 minutes (soft stop):** Tell the agent to finish its current small unit, avoid starting more work, persist/commit changes, then yield to Ready or complete to Completed as appropriate, recording a concrete next action, and write its response.
- **30 minutes (hard stop):** Tell the agent to start no further work, use only operations necessary to persist/commit and yield or complete, and write its response promptly. A checkpoint by itself does not end the turn.
- **Before 25 minutes, or outside Working:** Do not emit cutoff instructions. Repeated responses after a cutoff may repeat the applicable instruction. The 30-minute instruction supersedes the 25-minute instruction.

These are **instructions, not forced cancellation**: tool responses are the delivery point, so a silent or stalled session cannot receive one until another tool response arrives. Neither cutoff changes agent state automatically or steals an assignment. The current session ID, all IDs associated with the current assignment, and both timestamps belong in the orphan-branch agent record, not in a client-memory-only mapping.

### Working session identity and restart

- **Opaque, provider-supplied IDs.** The current OpenAI adapter supplies `openai/session` MCP metadata; another provider may supply another opaque session identifier. Do not assume UUIDs, creation timestamps, lexical ordering, or ChatGPT-specific semantics. The agent must not invent its own ID. An explicit start or resume requires an incoming session ID.
- **Retain all associated IDs.** Store `activeSessionId` plus `associatedSessionIds` (an ordered, append-only list) in the canonical agent record for its **current task/attempt and assignment generation**. Beginning Working records the incoming ID as active and associated. An explicit resume from Ready may reuse the **most recently associated** ID, because the previous turn ended normally; an older superseded ID is not eligible. A new assignment/generation starts a fresh association list; the previous list remains recoverable through Git history. These are execution hints, not new ownership identifiers.
- **Explicit stall recovery:** a new session calls `ResumeAgent` while its assigned attempt is still Working. Validate task, attempt, owner, generation, and versions. The caller ID must be **new to that assignment**; atomically append it to the list, make it active, set `workingTurnStartedAtUtc` to the recovery time, and record one immutable `AgentStallRecovered` event with `occurredAtUtc`, agent/task/attempt/generation, new ID and recovery reason. The previous active ID stays in the list as superseded. Do **not** change `stateEnteredAtUtc`, agent state, task owner, assignment generation, or slot ownership. A new timed segment starts inside the same Working state; neither ID format nor elapsed time automatically detects a stall.
- **Fence superseded sessions.** A previously associated, non-active session ID cannot reclaim the Working assignment by calling `ResumeAgent`, even if task ownership and generation are unchanged. While Working, task-scoped worker mutations must match both the active session ID and the current task/attempt/generation; rejected old sessions receive a clear **stop-work** instruction. Outside Working, normal state-specific ownership and version checks apply, and only the most recently associated ID may resume from Ready (or a previously unseen ID may begin the next turn). The session fence does not make IDs credentials or replace assignment ownership checks.
- **Normal yield and resume:** leaving Working clears `activeSessionId` and `workingTurnStartedAtUtc`, but preserves the association list for that assignment. A Ready-to-Working `ResumeAgent` starts a new state/turn timestamp and does **not** record a stall. It may reuse the latest associated ID or append a new one, but it cannot reactivate an ID superseded by a later association. A resume against a reassigned task receives a stale-ownership stop instruction.
- **Routing for other tools:** generic calls (`run_command`, `read_file`, job polling, etc.) never append or activate IDs. An orchestrator reading an agent inbox must not associate itself with the agent. For every tool response, use the incoming ID and project to locate a unique Working agent: active ID receives its 25/30-minute guidance, superseded ID receives a stop-work instruction, and unrelated/ambiguous IDs receive no agent-specific instructions. Do not block unrelated generic commands as though they were task-scoped worker mutations.
- **Order from Git, not IDs.** The association list update and recovery event form one Git transaction. The order of successful associations is determined by the canonical ledger, not by inspecting any ID. No UUID parsing, additional execution-generation counter, server-side session registry, automatically detected stalls, or separate duration aggregates are needed.

The assumption is that a manually restarted stalled execution receives a different session ID. The retained association history prevents an older execution from reclaiming its slot, without depending on how the provider generates the ID.

### Timestamped workflow events and derived agent statistics

Every new immutable assignment issue/acknowledgment, checkpoint (including recovery and completion), explicit `AgentStallRecovered` event, review revision, review claim/release/decision, and task lifecycle event stores an `occurredAtUtc` timestamp in its canonical Git record. Integration plans store `plannedAtUtc` and verified integration retains `verifiedAtUtc`. These are UTC event times recorded when the Git mutation is prepared, not proof of productive computation. Existing events remain identifiable by their ledger sequence; timestamps add elapsed-time measurements.

Agent-level state changes update `stateEnteredAtUtc` only when the state actually changes. Entering Working also sets `workingTurnStartedAtUtc`; only an explicit recorded stall recovery may reset that clock without a state transition. Ordinary checkpoints, reviews and unrelated revisions must reset neither. Prior boundaries remain recoverable through Git history.

Derive agent statistics on demand from timestamps and Git history: time spent in each state (including operator wait in Assigned/Ready and idle time in Parked), timed Working-segment durations and P50/P95, checkpoints per segment and time between checkpoints, tasks completed per day, review wait/decision times and acceptance rates, time from acceptance to integration, and explicit stall-recovery frequency. Group these by agent and over time as needed; no dashboard or statistics API is required for the initial cutover. **Do not persist durations, totals, averages, percentile summaries, or turn counters.** State occupancy and timed execution segments are distinct: a stalled Working interval may contain downtime whose start is unknown. Wall-clock metrics must not be presented as CPU or productive time. A changed opaque session ID is not itself evidence of a stall; count only explicit recovery events.

## 4. Review workflow

Review is independent of the implementation assignment:

1. An implementation agent publishes a **review branch/ref** pointing to the exact source commit and submits its task/review metadata to the orphan branch.
2. The agent **continues working**. Submitting for review does not pause its execution slot.
3. Until the review is **claimed**, the owner may **replace/update** its submitted review with a newer snapshot. Preserve previous revisions in Git history; do not change the commit a reviewer already claimed.
4. A reviewer claims an exact revision. Accept or request changes against that frozen source snapshot; a claimed revision cannot be replaced underneath the reviewer.
5. Changes requested become durable feedback for the task's owner. They do not seize the agent's current assignment.
6. Acceptance is evidence about the reviewed snapshot, **not** an implicit merge, source push, agent completion or task closure.

One actionable review request per task at a time is sufficient. Claim and replacement compete via the same Git branch update. A reviewer may release a claim, and an orchestrator may explicitly reassign it without losing the reviewed snapshot.

Review refs live in ordinary source Git history, while their metadata lives in the orphan branch. Publishing a source ref and updating the ledger are two separate Git operations; if one succeeds and the other fails, inspect both remotes and reconcile explicitly. Do not add another transaction coordinator.

## 5. Integration and lifecycle

The orchestrator decides whether an accepted change should be integrated. The agent or operator uses normal Git commands to merge/cherry-pick/push the reviewed source as appropriate; Jarvis must not silently push code. Record source integration only when verified on the intended remote. Worktree cleanup is separate.

Keep explicit task states sufficient to distinguish **Open, InProgress, Implemented, Integrated, Closed, Cancelled**. A task can be reopened by an explicit action. A non-code task can close without pretending that source integration occurred.

A task's lifecycle must not depend on what branch happened to contain an old copy of its JSON. Git history and task/review records should explain what happened without requiring an additional audit service.

## 6. Tool surface and cutover

Expose the orphan-branch operations through the **existing V2 MCP tools**, which can retain their `V2` names. We do not need to design a new public command family or force a major-protocol rollout merely to rename them.

Provide only the queries needed to operate the workflow: current tasks, a task's details/spec, agent inbox/roster, and submitted/claimed review requests. If a query is missing, add the **smallest** direct read for it. Use ordinary Git history/diff commands when historical investigation is needed. No portable cursor framework, historical search service, mirror/restore API, or extensive administrative API is required.

The old public MCP task commands — `ListTasks`, `GetTask`, `CreateTask`, `UpdateTask`, `ClaimTask`, `CheckpointTask`, `CompleteTask`, `GetTaskHistory`, `CompareWorktreeTasks`, `GetAgentAssignment`, `AssignTask`, `AcknowledgeTask`, `GetOrchestrationSnapshot`, `RetireWorktree` and `BeginTask` — are **removed**, not wrapped or forwarded. The earlier `*WorkTask` tools were already retired. Their names may remain as internal dead wire cases temporarily while deleting them safely; they must not be callable, advertised or used as a compatibility path.

The cutover is direct:

1. Verify the canonical orphan-branch workflow with a disposable Git remote and multiple clients.
2. Keep the legacy task tools removed. Update client and server so orchestration uses only the orphan-branch tools.
3. Deploy the changed client/server when explicitly authorized.
4. Initialize/bind the project's orphan branch when ready.
5. An agent manually recreates any old task worth carrying forward, using existing files as reference. Do not delete those files as a prerequisite.

No automatic migration, staged compatibility period, global V1-to-V2 data conversion, protocol-4 writer gate, or elaborate rollout policy. There is no need to migrate completed or obsolete tasks.

**Implementation and deployment are separate.** This spec does not authorize a production push, deployment, initialization, or migration without the user's instruction.

## 7. Security and failure behavior

Retain the existing Jarvis permissions and Git remote configuration. A logical agent label is **not** an authenticated principal. Never treat task HTML as higher-priority agent instructions or execute it automatically. Do not commit secrets to a public ledger branch. A public repository makes this branch public too. Session IDs are routing metadata, not credentials; no additional hashing or session-privacy system is required.

Return clear errors for missing branch/binding, permission denial, stale ownership/review version, unavailable remote, invalid source commit and uncertain write outcome. Failed operations do not claim success. When the client has an old cache, label it stale rather than current.

Do not add security/availability machinery unrelated to the actual Git workflow. Git branch history, normal remote backup practices, and recovery from source/task files are adequate for this single-user project unless a specific failure demonstrates otherwise.

## 8. Acceptance criteria

The cutover is finished when these behaviors are verified:

1. The orphan ref exists independently of source branches; tasks are stored/read there and do not vary by source worktree.
2. Two clients see the same authoritative tasks. Concurrent writes cannot silently overwrite each other.
3. An agent can be assigned, begin, checkpoint, yield/resume and complete work; its current task survives restart/session loss.
4. Agents can submit reviews, continue working, update unclaimed reviews, and receive feedback on claimed snapshots.
5. The reviewer can claim/decide a pinned review; recorded acceptance does not falsely imply merged code.
6. Integration status reflects actual remote source state, not an optimistic local commit.
7. All old orchestration MCP tools are absent from the public catalog and invocation surface; non-orchestration tools still work.
8. No automated migration occurs, and legacy on-disk task files are left untouched.
9. Existing client and server test suites pass; use a disposable remote for Git workflow tests.
10. Each assignment has a persisted active session ID and ordered history of all associated opaque IDs. A simulated stalled-Working recovery appends a new ID, supersedes the previous active ID, records one dated stall-recovery event, and resets the timed-segment cutoff clock without changing task ownership, assignment generation, or state-entry timestamp.
11. After recovery, a generic tool response to the new active ID receives 25/30-minute guidance based on the new segment start. The superseded ID receives a stop-work instruction and cannot reclaim the assignment via ResumeAgent. Unrelated/ambiguous IDs and orchestrator inbox reads do not receive the worker's guidance.
12. Agent state transitions and immutable workflow events have UTC timestamps that survive cloning. Checkpoints leave both Working timers unchanged; correctly ending a Working segment requires leaving Working except for explicit stall recovery. No duration aggregates or turn counters are persisted.

13. A prior owner attempting any task-scoped mutation or ResumeAgent against a reassigned task gets an explicit stale-assignment stop-work message. Within the same Working assignment, only its active session may mutate it; a new ID can explicitly recover it, but a superseded ID cannot reclaim it, even after the latest session yields to Ready.

Stop when these conditions hold. Do **not** resume the superseded V2-10–V2-16 roadmap just to satisfy the old spec.

## 9. Design rule

Prefer the smallest change that makes the above workflow correct. A new abstraction, state, API, background process or validation layer must solve a **concrete demonstrated problem** in this workflow, not a hypothetical future platform requirement. Git already provides durable history, commits and diffs. Use them.
