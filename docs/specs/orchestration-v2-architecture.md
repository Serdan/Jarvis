# Jarvis orchestration — orphan-branch cutover

**Status:** Revised 2026-10-30. This is the current normative orchestration specification. Earlier V2 implementation notes and designs are historical, not requirements to preserve.
**Authority:** `refs/heads/jarvis-orchestration`, an orphan branch of each project's existing Git remote.

## 1. Goal and boundary

The problem is simple: when tasks and assignment metadata live in normal source branches, every worktree can have a different copy. An orchestrator cannot reliably tell which one is current.

**Fix that by storing all live orchestration state in one orphan Git branch.** Ordinary source branches and worktrees contain source code, not authoritative task state. Multiple Jarvis clients fetch/update the same branch. The Jarvis server stays stateless and routes commands to the connected client.

This is a **replacement**, not an additive V1/V2 compatibility system.

**The agents are trusted developers.** Jarvis helps them coordinate an ordinary team workflow; it does not govern how they use Git. Branching, committing, rebasing, reviewing, resolving conflicts and merging are normal Git operations performed by the agent or orchestrator. Jarvis records assignments, availability, checkpoints and review outcomes. Do not invent authorization rituals, source ancestry protocols or task lifecycle gates for actions a developer can already perform with Git.

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

Exactly one current record for each task; Git history retains prior versions. Task IDs are stable within the project. Task records reference their implementation attempts and review requests. A source branch or worktree path is an implementation detail, **not the task owner's identity**. Agents and the orchestrator can move between branches/worktrees at normal handoffs without changing task ownership or restarting the assignment.

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

Changing task ownership or an assignment generation invalidates stale task-scoped mutations: reject writes against an outdated owner/attempt/version and explain the current assignment so the agent can stop or reassess. A changed **session ID alone** does not change task ownership, assignment generation, or whether an otherwise valid task mutation is authorized. An orchestrator explicitly decides to recover/reassign; elapsed time alone never steals ownership. Do not attempt to enforce execution discipline through generic file, job or command tools.

**Task completion and agent availability are different.** An agent can complete its work, receive another assignment, and still have an earlier task awaiting review or integration. No review decision interrupts the agent's newer work.

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

Every new immutable assignment issue/acknowledgment, checkpoint (including recovery and completion), explicit `AgentStallRecovered` event, review revision, review claim/release/decision, and task lifecycle event stores an `occurredAtUtc` timestamp in its canonical Git record. Integration plans store `plannedAtUtc` and verified integration retains `verifiedAtUtc`. These are UTC event times recorded when the Git mutation is prepared, not proof of productive computation. Existing events remain identifiable by their ledger sequence; timestamps add elapsed-time measurements.

Agent-level state changes update `stateEnteredAtUtc` only when the state actually changes. Entering Working also sets `workingTurnStartedAtUtc`; only an explicit recorded stall recovery may reset that clock without a state transition. Ordinary checkpoints, reviews and unrelated revisions must reset neither. Prior boundaries remain recoverable through Git history.

Derive agent statistics on demand from timestamps and Git history: time spent in each state (including operator wait in Assigned/Ready and idle time in Parked), timed Working-segment durations and P50/P95, checkpoints per segment and time between checkpoints, tasks completed per day, review wait/decision times and acceptance rates, time from acceptance to integration, and explicit stall-recovery frequency. Group these by agent and over time as needed; no dashboard or statistics API is required for the initial cutover. **Do not persist durations, totals, averages, percentile summaries, or turn counters.** State occupancy and timed execution segments are distinct: a stalled Working interval may contain downtime whose start is unknown. Wall-clock metrics must not be presented as CPU or productive time. A changed opaque session ID is not itself evidence of a stall; count only explicit recovery events.

## 4. Ordinary Git workflow and reviews

Agents are trusted to manage their source branches and worktrees like developers on a normal team. **Jarvis is a coordination layer, not a Git workflow engine.** A task may require several rounds of commits, reviews and merges. None of those rounds requires the task to be completed first.

1. **Develop.** An agent works on an ordinary feature branch in its own worktree and commits as needed. There is one current implementation owner per task.
2. **Submit.** To request review, create a separate review branch at the commit being submitted and push it. Record its ref, task, owner and summary in the orphan ledger. The review branch is a stable snapshot; it is **not** the agent's working branch.
3. **Keep working.** The agent can immediately continue committing on its working branch. Review does not take its work slot or require it to yield. An agent can also yield to Ready while the review is pending.
4. **Review.** The reviewer checks out or fetches the review branch, examines the changes and accepts or requests changes. Once claimed, that review branch must not move. An unclaimed request may be replaced with a new review snapshot. Preserve the claimed snapshot until the review is resolved.
5. **Integrate.** The orchestrator merges accepted work into the target branch with ordinary Git commands, resolves any conflicts and runs appropriate tests. It pushes the result to the remote. **Do not cherry-pick.** Fast-forward and merge commits are both normal outcomes; Jarvis must not demand that the target's final SHA equal the reviewed SHA.
6. **Continue.** The agent keeps its working branch, rebasing onto updated `main` when useful. It can submit another review branch covering the remaining changes. Repeat until the task is genuinely finished.

For example, after committing on an agent's feature branch:

```bash
# Agent: publish a stable review snapshot; keep working on the feature branch
git branch jarvis-review/320/r1 HEAD
git push origin jarvis-review/320/r1

# Orchestrator, in its own main worktree: integrate the reviewed snapshot
git fetch origin
git merge origin/jarvis-review/320/r1
# Resolve any conflicts, validate the result, and commit the merge if needed
git push origin main

# Agent, when appropriate: catch up while retaining its own branch
git fetch origin
git rebase origin/main
```

These are illustrative Git commands, not operations Jarvis must orchestrate or wrap. If the target branch advanced during review, the orchestrator performs an ordinary merge; if it changes the reviewed result while resolving conflicts, use developer judgment and review those changes appropriately. Do not require a bespoke Git-equivalence proof.

A review's base is **the relevant unintegrated work for that review**, not a permanent base pinned for the lifetime of the task. After a slice is merged, subsequent reviews must not resubmit that slice. Derive the next diff through normal Git history/merge-base and the already integrated work. An agent's rebases can change commit hashes; a **previously claimed review branch remains fixed**, while new reviews may point to new commits.

One actionable review request per task at a time is sufficient. Until claimed, the owner can replace it; after claim, the reviewer sees the original stable branch. Review metadata and decisions live in the orphan ledger; code and review branches live in ordinary source history. If publishing a review branch or recording its metadata fails, report the problem and let the agent reconcile it. No cross-ref transaction coordinator is needed.

### Review files and findings

**A review is a task-associated, stable Git branch with a defined review base.** Its essential identity is the task, review ref, base commit and decision. A review ref is a snapshot of source code, not the place where reviewers write findings. Git owns commit history and ancestry; the orphan ledger owns the review decision and notes. Do not invent further commit identifiers or another integration state machine merely to represent information Git already knows. The attempt does not duplicate the accepted review head, and a review revision does not duplicate its already-pinned source OID. Remaining historical provenance and integration fields in the current schema are bookkeeping for the existing workflow, not additional reviewer responsibilities.

Keep extensive review prose in **separate Markdown files** on `jarvis-orchestration`, not in bounded JSON feedback strings or on the reviewed source branch. Files are ordinary Git blobs, so they can be individually diffed and edited, and their history is the orphan branch's history. For example:

```text
tasks/320/reviews/<review-id>/
  request.json                   # existing workflow status
  revisions/1.json               # review ref and diff base
  summary.md                     # overall assessment (optional)
  findings/
    allocation-in-hot-path.md
    missing-regression-test.md
```

Each finding is a Markdown document with a descriptive filename; the body can contain reproduction steps, code blocks, benchmarks, suggested changes and resolution notes. **No per-finding status machine, embedded finding array, or duplication in `request.json`.** The single review verdict is separate from individual findings. A reviewer can create/edit these notes while reviewing; the implementing agent can read or update the same files to document resolution. Do not tie document writes to the agent's Working slot or require a new review submission for a prose update.

Expose minimal review-document commands to list filenames, read one file, and write one file on the orphan ledger. Allow `summary.md` and one-level `findings/<slug>.md`; reject arbitrary paths and traversal. Use the existing ledger's operation-id replay behavior and optional Git blob precondition for edits; creating a new file needs no existing blob. One ledger write commits one document rather than forcing extensive findings through tool parameters, verdict messages or task descriptions. A review decision need not bundle the files again. Git history provides old versions; the current branch provides current content.

The reviewed diff is conceptually `git diff <base> <review-ref>`. The base is frozen for that review; the published ref is frozen after claim. Normal Git merges and rebases after review do not retroactively change its stated scope. A subsequent review starts from the remaining unmerged work, not automatically the same task-wide base.

## 5. Integration, source context and task lifecycle

**Integration is per reviewed slice, not per completed task.** Accepted commits can be merged while the task is InProgress and the agent continues working. Acceptance does not itself merge code, and integration does not itself complete or reassign the agent.

Jarvis should record enough to answer: which review was approved and which source branch was integrated. Git can identify the reviewed commit and resulting merge from the refs/history. It must verify or observe that the actual remote target contains the integrated work before claiming it was merged. This is bookkeeping after ordinary Git operations, not a requirement to use a special source-push protocol or exact-SHA fast-forward. Remove the current `PlanIntegration`/`RecordIntegration` restrictions that require a finalized task, one fixed cumulative review base or a target SHA identical to the approved review head. Keep the simplest operation(s) necessary to record each integration and avoid recording it twice.

The agent's working branch and worktree are **context, not authorization**. An agent may rebase, rename or reuse its branch normally. The orchestrator may update the recorded source branch/worktree when the owner is Ready, without artificially resuming it to Working and yielding again. An implementation attempt, task owner, checkpoints and existing review history persist across such changes. No new branch-rotation/handoff protocol is needed; relax the existing source-binding restrictions or replace them with ordinary editable context. Claimed review branches are unaffected.

Keep task states sufficient to distinguish **Open, InProgress, Implemented, Integrated, Closed, Cancelled**, but reserve final implementation/integration/closure status for actual **task completion**, not every intermediate merge. Individual merged slices are tracked in review/integration history. Non-code work can close without inventing source integration. Cleanup of a worktree or review branch is ordinary Git housekeeping, separate from task ownership.

## 6. Tool surface and cutover

Expose the orphan-branch operations through **unprefixed public MCP tool names** (`CreateTask`, `AssignTask`, `AgentInbox`, `SubmitReview`, `LedgerHealth`, etc.) without `V2` compatibility aliases. The eight internal versioned wire commands retain their `V2` names; public names are independent of the transport protocol. Where a legacy public name is reused, it exclusively invokes the orphan-ledger implementation.

Provide only the queries needed to operate the workflow: current tasks, a task's details/spec, agent inbox/roster, and submitted/claimed review requests. If a query is missing, add the **smallest** direct read for it. Use ordinary Git history/diff commands when historical investigation is needed. No portable cursor framework, historical search service, mirror/restore API, or extensive administrative API is required.

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

The cutover is finished when these behaviors are verified:

1. The orphan ref exists independently of source branches; tasks are stored/read there and do not vary by source worktree.
2. Two clients see the same authoritative tasks. Concurrent writes cannot silently overwrite each other.
3. An agent can be assigned, begin, checkpoint, yield/resume and complete work; its current task survives restart/session loss.
4. Agents can create stable review branches, continue developing on working branches and replace unclaimed reviews without changing claimed snapshots.
5. The orchestrator can merge an accepted review branch into a concurrently advancing target with ordinary Git, including a merge commit, without cherry-picking or exact-reviewed-SHA restrictions.
6. Multiple reviewed slices can be integrated while the task stays InProgress; later reviews contain only remaining work. The recorded integration identifies the actual remote target merge result, not an optimistic local commit.
7. A Ready agent's source context can be updated without temporarily setting it Working; rebases and branch changes preserve ownership, checkpoints and review history.
8. All old source-branch orchestration implementations are absent from the public surface (including unprefixed name reuse); non-orchestration tools still work.
9. No automated migration occurs, and legacy on-disk task files are left untouched.
10. Existing client and server test suites pass; use a disposable remote for Git workflow tests.
11. Each assignment persists its current session ID and the ordered history of associated opaque IDs. A reported stalled-Working recovery records a dated event, associates the continuing session and resets the timed-segment cutoff clock without changing task ownership, assignment generation, or state-entry timestamp.
12. Where project/session context is available, the current Working session receives its 25/30-minute guidance and an earlier associated session is told about the newer execution and advised to stop outdated work. Generic calls and orchestrator inbox reads never create session associations; projectless tools do not require a special cross-project resolver.
13. Agent state transitions and immutable workflow events have UTC timestamps that survive cloning. Checkpoints leave both Working timers unchanged; correctly ending a Working segment requires leaving Working except for explicit stall recovery. No duration aggregates or turn counters are persisted.

14. A task mutation against a reassigned or stale owner/attempt/version fails with clear current-assignment context. Session association changes alone do not impose a blanket write fence; agents can read the current session history and decide how to resume. Completing Task A does not prevent ordinary review bookkeeping for A while the same agent works on Task B.

Stop when these conditions hold. Do **not** resume the superseded V2-10–V2-16 roadmap just to satisfy the old spec.

## 9. Design rule

**Jarvis makes an ordinary team workflow easy for trusted agents.** Preserve canonical task state and useful coordination, but never block normal Git branching, rebasing, reviewing or merging merely to satisfy Jarvis-specific protocol constraints. Prefer removing restrictions over building new mechanisms around them. A new abstraction, API or validation layer must solve a concrete demonstrated problem. Git already handles source history, reviews and merges.
