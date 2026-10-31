# Jarvis coordination capabilities — Git-backed ledger

**Status:** Revised 2026-10-31. This is the normative **Jarvis capability** specification. A particular development/review/merge workflow is defined in agent instructions, not in Jarvis. Existing implementation and older V2 notes are superseded where they impose workflow rules; implementation alignment is separate.
**Authority:** `refs/heads/jarvis-orchestration`, an orphan branch of each project's existing Git remote.

## 1. Goal and boundary

The problem is simple: when tasks and assignment metadata live in normal source branches, every worktree can have a different copy. An orchestrator cannot reliably tell which one is current.

**Fix that by storing all live orchestration state in one orphan Git branch.** Ordinary source branches and worktrees contain source code, not authoritative task state. Multiple Jarvis clients fetch/update the same branch. The Jarvis server stays stateless and routes commands to the connected client.

This is a **replacement**, not an additive V1/V2 compatibility system.

**Agents are trusted developers.** Jarvis provides useful, independent operations on tasks, agents, reviews and Git-backed records. Agents perform ordinary Git work directly. Jarvis does not decide which operations are appropriate in a development workflow, or require a task to pass through a prescribed review sequence.

**Responsibility boundary:** Jarvis is a passive coordination service exposing explicit operations, **not a workflow engine**. The **orchestrator and other agents**, following their own instructions, decide when to start or close a task, request or withdraw a review, interpret findings, assign work, rebase or merge. Jarvis records what agents explicitly ask it to record. It never automatically selects workers, starts work, dispatches reviews, requests rework, withdraws reviews on handoff, infers approval, merges branches or cleans up source refs. Rules such as *review before merge*, *only merge the task branch* and *request a final review* belong in agent instructions, not API preconditions.

- Remove the old repository-local task and cross-worktree orchestration MCP tools from the public surface. Do not make them forward to the new system. Do not preserve fallback readers, dual writers or a per-project compatibility toggle.
- Keep normal Git, worktree, file, job and client tools. They are not legacy orchestration tools.
- Leave existing HTML/JSON task files on disk and in Git history. They are no longer authoritative once the orphan branch is in use. An agent may inspect and manually recreate whichever tasks are still relevant.
- **Do not implement an automated migration tool, scanner, manifest, conversion pipeline or migration protocol.** No automatic copying or two-way synchronization.
- Do not introduce a database, stateful server, scheduler, event bus, separate repository, or a generalized distributed workflow framework.

## 2. Git authority and layout

For each project, use exactly one configured remote and one canonical orphan ref:

```text
refs/heads/main                       ordinary source code
refs/heads/feature/...                mutable task implementation branches
refs/heads/jarvis-review/...          review snapshot refs published by review tools
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
ops/...
```

Exactly one current record for each task; Git history retains prior versions. Task IDs are stable within the project. Task records reference implementation attempts; reviews can carry a task ID as **correlation metadata**, not as a lifecycle dependency. Review records remain independently addressable after ownership changes, completion or closure. A source branch or worktree path is context, **not the task owner's identity**.

The Git remote is the sole source of truth. Local binding/configuration and caches exist only to choose and verify the remote; they are not alternative task stores. Existing explicit initialize/bind/health operations are sufficient.

### Updating the orphan branch

Every logical mutation:

1. Fetches the current canonical ref and reads the state at that commit.
2. Checks record shape, exact expected versions and the local consistency of the requested operation (for example, an atomic review claim). It does **not** impose cross-resource workflow rules.
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

An implementation attempt records its owner, not a reviewer. `AssignTask` accepts the task ID, fresh attempt ID, owner ID and operation UUID; `TransferOwner` changes that ownership only when explicitly called. No reviewer is assigned with the task. `ClaimReview` independently records a reviewer on a review request. **Review tools do not check task ownership or worker phase to decide who may request, claim, comment on or decide a review.** Agent instructions can recommend independent reviewers. Existing attempt JSON containing the historical reviewer field is readable, but new attempt writes omit it.

A registered idle agent starts Parked. Each agent has at most one Working assignment. Assignment, start, checkpoint, yield, resume, completion and reassignment update the canonical orphan branch. The assignment includes the task, instructions and enough source context to continue in the correct worktree. A checkpoint carries a summary and next action so a restarted agent session can recover work.

Changing task ownership or an assignment generation invalidates stale task-scoped mutations: reject writes against an outdated owner/attempt/version and explain the current assignment so the agent can stop or reassess. A changed **session ID alone** does not change task ownership, assignment generation, or whether an otherwise valid task mutation is authorized. An orchestrator explicitly decides to recover/reassign; elapsed time alone never steals ownership. Do not attempt to enforce execution discipline through generic file, job or command tools.

**Agent availability, task state and review state are independent.** `CompleteAgentWork` finishes that agent's attempt and frees their slot; it does **not** approve, close or otherwise change the task because of a review. `CloseTask` explicitly records a task as closed, whether or not the task has reviews. Review decisions do not reassign workers, reopen tasks or interrupt newer assignments. What counts as 'done' and whether review/merge is required are agent-instruction decisions.

Task descriptions are stored directly in the orphan branch as HTML. The assigned agent can read its pinned instructions/spec. Editing source-side HTML does not silently edit the canonical task. Use existing spec operations where useful; do not require special import/export workflows to make tasks usable.

### Working-turn timestamps and 25/30-minute guidance

An agent enters **Working** through BeginAttempt or a Ready-to-Working ResumeAgent. A normal turn **must end by leaving Working** (typically yield to Ready or complete to Completed) before writing its final response; ending a turn while still Working is invalid behavior. Checkpointing or submitting a review does **not** finish a turn. A stalled harness is the exception: the agent may remain Working until explicitly resumed.

The canonical agent record stores `stateEnteredAtUtc` (the start of its current state) and, while Working, `workingTurnStartedAtUtc` (the start of its current **timed execution segment**). Both are set on entering Working. Normally they remain equal. A reported stall recovery may begin a new timed segment **without changing state**, so the two timestamps deliberately differ. Prior state and segment boundaries remain reconstructible from the agent record's Git history and the recovery event; no saved durations, counters, server timer, or database are needed.

When a tool response can be associated with the agent's current Working session, check elapsed **wall-clock time** since the current timed Working segment began (`workingTurnStartedAtUtc`):

- **25 minutes (soft stop):** Tell the agent to finish its current small unit, avoid starting more work, persist/commit changes, then yield to Ready or complete to Completed as appropriate, recording a concrete next action, and write its response.
- **30 minutes (hard stop):** Tell the agent to start no further work, use only operations necessary to persist/commit and yield or complete, and write its response promptly. A checkpoint by itself does not end the turn.
- **Before 25 minutes, or outside Working:** Do not emit cutoff instructions. Repeated responses after a cutoff may repeat the applicable instruction. The 30-minute instruction supersedes the 25-minute instruction.

These are **instructions, not forced cancellation**: tool responses are the delivery point, so a silent or stalled session cannot receive one until another tool response arrives. Neither cutoff changes agent state automatically or steals an assignment. Deliver guidance where session/project context is available; do not introduce a global session resolver just to cover every possible tool. The current session ID, all IDs associated with the current assignment, and both timestamps belong in the orphan-branch agent record, not in a client-memory-only mapping.

### Working session identity and restart

Jarvis records execution context so cooperating agents can recognize interruptions and decide what to do. **Session IDs are information, not locks, credentials, or an authorization system.** Jarvis should surface superseded sessions and changed assignments rather than trying to make every possible stale action impossible.

- **Provider-independent IDs.** Use the opaque identifier supplied by the current agent provider (for OpenAI, `openai/session` MCP metadata). Do not parse UUID versions, infer time order from ID contents, or require a particular provider's format. An agent must not fabricate an ID.
- **Keep the associations.** Store `activeSessionId` and an ordered `associatedSessionIds` list in the canonical agent record for its current task/attempt and assignment generation. Record each newly associated ID once, in ledger order; retain older IDs rather than replacing or deleting them. Starting a different assignment/generation starts a fresh current list, with previous associations still recoverable from Git history.
- **Normal progress.** Beginning Working associates the incoming ID. Leaving Working clears `activeSessionId` and `workingTurnStartedAtUtc`, but keeps the associated IDs. A Ready-to-Working resume starts a new timed turn without recording a stall; the same session may resume normally, or a different session may become associated.
- **Reported stall recovery.** If an agent reports that its Working turn stalled, `ResumeAgent` can associate the continuing session, record an immutable `AgentStallRecovered` event with `occurredAtUtc`, and restart `workingTurnStartedAtUtc`. Preserve `stateEnteredAtUtc`, task ownership, assignment generation and Working state. Do not infer a stall solely from elapsed time or a different session ID. A repeat from the already active session need not record another recovery or reset the clock.
- **Inform, do not fence.** If a known earlier session makes a call, report that a later session is current, alongside the task/attempt, assignment state, latest checkpoint or next action where useful. Tell the agent to stop outdated work and re-read its assignment. Do not automatically switch the active association back on generic calls. If deliberate recovery is needed, the agent/operator can inspect the current record and use the explicit workflow. Do not apply a blanket session-ID precondition to every task mutation; existing task/attempt/generation/version checks provide the necessary consistency.
- **Where context is available.** Ordinary tool responses can include Working-duration guidance or a superseded-session notice when the project/session association is known. An inbox read must not associate the orchestrator's session with the worker. Generic calls do not create associations. Projectless tools and unrelated review bookkeeping do not need a global association lookup or a guaranteed session notice.
- **Work can outlive its execution.** An agent can finish Task A, start Task B, and still handle review feedback or finalization for Task A. Session information must not block that existing workflow.

Git gives durable history and orders successful association changes. No separate execution-generation counter, provider-ID ordering, server-side session registry, universal stale-session enforcement, or new recovery protocol is required.

### Timestamped workflow events and derived agent statistics

Every new immutable assignment issue/acknowledgment, checkpoint (including recovery and completion), explicit `AgentStallRecovered` event, review creation/replacement/claim/release/decision and task-state update records `occurredAtUtc` in its canonical Git record. Task closure records **when the explicit closure operation occurred**, not a claimed review result or Git integration time. These are UTC event times recorded when the Git mutation is prepared, not proof of productive computation. Git sequence still identifies existing events.

Agent-level state changes update `stateEnteredAtUtc` only when the state actually changes. Entering Working also sets `workingTurnStartedAtUtc`; only an explicit recorded stall recovery may reset that clock without a state transition. Ordinary checkpoints, reviews and unrelated revisions must reset neither. Prior boundaries remain recoverable through Git history.

Derive agent statistics on demand from timestamps and Git history: time spent in each state (including operator wait in Assigned/Ready and idle time in Parked), timed Working-segment durations and P50/P95, checkpoints per segment and time between checkpoints, tasks completed per day, review wait/decision times and verdict counts without assuming a review's purpose, explicit task close events, and explicit stall-recovery frequency. Group these by agent and over time as needed; no dashboard or statistics API is required for the initial cutover. **Do not persist durations, totals, averages, percentile summaries, or turn counters.** State occupancy and timed execution segments are distinct: a stalled Working interval may contain downtime whose start is unknown. Wall-clock metrics must not be presented as CPU or productive time. A changed opaque session ID is not itself evidence of a stall; count only explicit recovery events.

## 4. Independent task, review and Git capabilities

**Jarvis provides operations; agent instructions provide the workflow.** A review is a useful tool for examining a stable snapshot and recording feedback. It is **not** a lifecycle phase of a task. Tasks, agent slots, reviews and source branches are separate things.

### Tasks and agent slots

- `CreateTask`, `AssignTask`, `TransferOwner`, `CheckpointAttempt`, `CompleteAgentWork`, `CloseTask` and related tools change only the records explicitly named by their operations. Keep normal task/agent ownership and state consistency (e.g. do not assign a second Working slot to the same agent, or silently overwrite a stale version).
- **`CloseTask` records an explicit close request.** It does not require the worker to be Finished, any review to exist, a particular review verdict, a particular Git branch or an integrated commit. In particular, do not fetch source branches or run Git ancestry comparisons as prerequisites to recording task closure. The caller decides whether the work is ready to close.
- Finishing an assignment frees the agent's execution slot; it neither closes the task nor creates a mandatory review. The orchestrator may subsequently assign other work or explicitly transfer an earlier task for further work.
- An owner transfer changes task/assignment ownership and its associated agent slot only. **It must not change, withdraw, decide or invalidate reviews.** Claim and decision records outlive the attempt or agent that initially prompted the review. The orchestrator can explicitly manage a review separately if desired.
- A task's recorded source branch is useful handoff context, not a dependency on a particular Git history. Agents may rebase, amend, squash, switch branches or rewrite history using ordinary Git.

### Reviews as a standalone tool

A review request captures **a named, stable Git snapshot plus optional context and Markdown findings**. It can be associated with a task for organization, but neither its creation nor its subsequent management is conditional on the task's lifecycle, owner, attempt phase, completion status or existing verdicts.

The review tool surface needs to support the following ordinary actions:

1. **Create** a review at a supplied source commit. Publish a **new** review ref without overwriting existing refs, record the ref and available context (source ref, target ref, optional comparison base, related task ID/notes) and return the request identity. A review may be requested while an agent is working, after completion or for any other reason.
2. **List/read** requests and their metadata independently of the currently assigned worker, including requests associated with a task whose owner changed or which has closed.
3. **Replace** an unclaimed request's snapshot by creating another ref and changing that request's reference. Never move an existing snapshot ref through this tool; claimed requests remain stable.
4. **Claim** a review so agents can avoid accidental duplicate reviewing, and allow the claimant to **decide** or explicitly **release** the claim for another reviewer. An explicit withdraw operation can cancel an unwanted request; neither release nor withdrawal is triggered by a task change.
5. **Record feedback** as individually named Markdown findings plus an optional summary. List, read and update those documents independently of current task ownership. Review decisions and findings are persistent review history, not side effects that automatically modify tasks.
6. **Read current state** with versions and useful associations so an agent can decide what to do next. Optimistic version checks protect conflicting edits to *the same record*, not the surrounding development process.

There is **no Jarvis-enforced `Progress`/`Final` distinction**. A reason, purpose or free-text label may be included for the agents' convenience, but it must never select different submission permissions, verdict semantics, task transitions or closure requirements. One task may have any number of actionable reviews if agents find that useful; do not enforce one-active-review-per-task. Review tools do not forbid a task's current owner from opening, claiming or deciding a review; independent review is a convention the agents can choose to follow.

The stored review format should be kept small, reusing the existing ledger and file layout rather than introducing a new workflow database. For compatibility with the current organization, a task-correlated review can remain at:

```text
tasks/<task-id>/reviews/<review-id>/
  review.json
  summary.md
  findings/<descriptive-slug>.md
```

That path is an **indexing choice**, not a rule that a task must be Working or Finished to create a review, or that changing the task owner affects reviews. Fields such as `taskId`, `attemptId`, `ownerAgentId` or a caller-provided purpose are informative links only. Remove mandatory `kind = Progress|Final` and its special logic; keep the core ref, optional diff base, review status/claimant, record version and document paths. Do not introduce new mandatory metadata when a simple note suffices.

Keep the existing bounded document operations (`PutReviewDocument`, `ListReviewDocuments`, `ReadReviewDocument`), stable operation UUIDs and expected blob OIDs for replacing files. These protect file integrity, not review policy. A decided review's documents remain readable, and authorized participants may append responses without changing the decision. The simple review verdicts `Accepted` and `ChangesRequested` are **feedback labels**; neither is authorization for a Git merge or a task-state transition.

### Git and snapshot behavior

Normal Git remains available for developers and orchestrators. Jarvis may provide convenience operations to publish and inspect review refs, but does not control whether, when or how agents merge or rebase branches. An immutable review **snapshot ref published by Jarvis** means its publish/replace tools will not move an existing ref; it is not an instruction forbidding arbitrary normal Git commands.

A branch ref keeps a snapshot discoverable through a task-branch history rewrite. The ref identifies the reviewed source; the base commit helps a reviewer construct a diff. The tool should reject malformed refs and report missing/unavailable source objects, but **not implement a Git-equivalence protocol**, police post-review task-branch modifications or compare review snapshots with Git merge results at task closure. Agents perform whatever comparisons their instructions and the circumstances warrant.

Publishing a snapshot and recording review metadata are separate Git operations. Minimize dangling review refs using straightforward preflight checks and best-effort cleanup after a known failed write. A safe cleanup tool can be exposed for explicit use. Do not invent a distributed transaction coordinator, backup registry, proof-of-review, integration receipt or comprehensive ref-monitoring system.

### Workflow instructions (outside Jarvis)

A project's `AGENTS.md`, agent skills or other agent instructions define its review policy, acceptable evidence, Git merge practices and completion criteria. These are **not Jarvis configuration or data-model rules**. Jarvis does not parse them to authorize or deny task/review operations. Neither a review verdict nor Git state automatically alters task lifecycle.

## 5. Implementation alignment (31 October 2026)

The capability-only implementation removes the earlier workflow gates. Review creation no longer depends on an assignment or worker phase, and no mandatory `Progress`/`Final` distinction or one-active-review-per-task limit remains. Review claims, decisions and Markdown documents can be managed independently. Ownership transfer and administrative task actions do not withdraw reviews. `CloseTask` records an explicit task-state update without reading review verdicts or calculating Git integration ancestry.

Review purposes are optional free-text metadata (represented by the existing `kind` storage field, not an enum). An unassociated review uses the `_reviews` namespace under the existing ledger tree; source ref, comparison target and summary are optional. `ReleaseReview` explicitly reopens a claim for another reviewer. The specialized approval-gated non-source closure and obsolete review-phase wire fields have been retired.

The existing Git orphan ledger, task/agent records and separate Markdown findings remain the authoritative data. The client/server protocol is version **3.45**. Deployment and any changes to project-specific agent instructions are separate actions; no production deployment or live ledger migration is implied by this implementation.

## 6. Tool surface and cutover

Expose the orphan-branch operations through **unprefixed public MCP tool names** (`CreateTask`, `AssignTask`, `AgentInbox`, `SubmitReview`, `LedgerHealth`, etc.) without `V2` compatibility aliases. The internal versioned task/ledger/review wire commands retain their `V2` names; public names are independent of the transport protocol. Where a legacy public name is reused, it exclusively invokes the orphan-ledger implementation.

Expose straightforward reads for tasks/details/specs, agent inbox/roster and review requests/findings, including reviews whose associated task has no current worker. Do not compute 'awaiting final review', 'needs rework' or 'pending integration' as mandatory Jarvis workflow states. If a query is missing, add the **smallest** direct read for it. Use ordinary Git history/diff commands when historical investigation is needed. No portable cursor framework, historical search service, mirror/restore API, or extensive administrative API is required.

The old source-branch task command implementations — including `ListTasks`, `GetTask`, `UpdateTask`, `ClaimTask`, `CheckpointTask`, `CompleteTask`, `GetTaskHistory`, `CompareWorktreeTasks`, `GetAgentAssignment`, `AcknowledgeTask`, `GetOrchestrationSnapshot`, `RetireWorktree` and `BeginTask` — are **removed**, not wrapped or forwarded. The names `CreateTask` and `AssignTask` are deliberately reused by the new orphan-ledger implementation; they must never reach the old source-branch handlers. The earlier `*WorkTask` tools were already retired. Their names may remain as internal dead wire cases temporarily while deleting them safely; they must not be callable, advertised or used as a compatibility path.

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

The capability cutover is complete when these independent behaviors are verified:

1. The canonical orphan ref stores task, agent and review records consistently across ordinary source branches, worktrees and connected clients. Conflicting updates to the **same record** cannot overwrite each other silently.
2. Agents can explicitly create, assign, transfer, checkpoint, yield/resume, complete and **close** tasks. `CloseTask` does not consult reviews or Git source ancestry, and a review decision cannot start, stop, assign, close or reopen a task.
3. Any usable source commit can be published as a new review snapshot regardless of the associated task's agent state. Claimed snapshots remain stable under the review tool's own operations; task branches may be freely rewritten.
4. Agents can have multiple outstanding reviews on the same task, and reviews can remain active through task-owner changes or task closure. Handoff never automatically withdraws a review.
5. Reviews can be listed, claimed, released, withdrawn, decided and documented with separate Markdown files. Versions protect conflicting edits to the review record; a reviewer decision changes only that review.
6. A missing or deleted snapshot ref is reported when its contents are requested, but the canonical review verdict/findings and explicit task closure remain readable. Known failed publications receive best-effort cleanup, not a new transaction framework.
7. Git branching, rebasing, review comparisons and merging are performed normally by agents. Jarvis does not enforce a final-review gate, a review kind, a merge target, commit-SHA equivalence or post-merge verification before task closure.
8. Tests in disposable remotes cover independent operations, multiple reviews, owner transfer with claimed reviews, explicit closure without reviews, rewritten task branches and persisted findings. Existing client and server suites pass.
9. Agent assignment state, session associations, explicit reported-stall recovery, timestamps and existing 25/30-minute execution guidance remain functional; none of them becomes a review or merge policy. No automatic legacy task migration occurs.

Stop when these conditions hold. Do not revive retired V2 roadmaps or build a generalized workflow engine to satisfy the acceptance criteria.

## 9. Design rule

**Jarvis is a toolbox, not a process manager.** Keep individual operations predictable and records durable. Enforce data integrity **within** an operation; do not infer obligations **between** tasks, reviews, agent slots and Git commits. The orchestrator/worker/reviewer instructions choose a process and call the tools. When in doubt, remove a cross-resource precondition rather than building machinery to enforce it.
