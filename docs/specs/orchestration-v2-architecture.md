# Jarvis orchestration — orphan-branch cutover

**Status:** Revised 2026-10-31. This is the normative orchestration specification. Existing implementation and older V2 notes are historical wherever they conflict with this corrected task/review workflow; implementation alignment is separate.
**Authority:** `refs/heads/jarvis-orchestration`, an orphan branch of each project's existing Git remote.

## 1. Goal and boundary

The problem is simple: when tasks and assignment metadata live in normal source branches, every worktree can have a different copy. An orchestrator cannot reliably tell which one is current.

**Fix that by storing all live orchestration state in one orphan Git branch.** Ordinary source branches and worktrees contain source code, not authoritative task state. Multiple Jarvis clients fetch/update the same branch. The Jarvis server stays stateless and routes commands to the connected client.

This is a **replacement**, not an additive V1/V2 compatibility system.

**The agents are trusted developers.** Jarvis helps them coordinate an ordinary team workflow; it does not govern how they use Git. Branching, committing, rebasing, reviewing, resolving conflicts and merging are normal Git operations performed by the agent or orchestrator. Jarvis records assignments, availability, checkpoints and review outcomes. Do not invent authorization rituals, source ancestry protocols or task lifecycle gates for actions a developer can already perform with Git.

**Responsibility boundary:** Jarvis is a passive coordination service with explicit read/write tools, not an autonomous orchestrator. The **orchestrator agent** decides when to review, who should review, whether rework is needed, who should do it, when to assign/resume a worker, and when to merge. Jarvis only records state changes in response to explicit tool calls and exposes state for the orchestrator to interpret. No automatic worker selection, assignment, review dispatch, rework scheduling, task resumption, Git merge, or branch cleanup.

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
refs/heads/jarvis-review/...          read-only review snapshots, NEVER merged
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

An implementation attempt records an owner, not a reviewer. `AssignTask` accepts the task ID, fresh attempt ID, owner ID and operation UUID; `TransferOwner` likewise selects only the new owner. No reviewer is assigned with the task. Any registered agent other than the owner may claim an unclaimed review; only that review's `reviewerId` identifies the claimant. Existing attempt JSON containing the historical reviewer field is readable, but new attempt writes omit it.

A registered idle agent starts Parked. Each agent has at most one Working assignment. Assignment, start, checkpoint, yield, resume, completion and reassignment update the canonical orphan branch. The assignment includes the task, instructions and enough source context to continue in the correct worktree. A checkpoint carries a summary and next action so a restarted agent session can recover work.

Changing task ownership or an assignment generation invalidates stale task-scoped mutations: reject writes against an outdated owner/attempt/version and explain the current assignment so the agent can stop or reassess. A changed **session ID alone** does not change task ownership, assignment generation, or whether an otherwise valid task mutation is authorized. An orchestrator explicitly decides to recover/reassign; elapsed time alone never steals ownership. Do not attempt to enforce execution discipline through generic file, job or command tools.

**Worker completion and task acceptance are independent.** A worker can finish implementation, become Completed or Parked, or start another task while the original task awaits final review. Final rejection never preempts that newer assignment: Jarvis merely records the verdict and findings. The orchestrator decides whether, when and whom to assign for rework, then makes the necessary explicit calls.

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

Every new immutable assignment issue/acknowledgment, checkpoint (including recovery and completion), explicit `AgentStallRecovered` event, review submission/replacement/claim/release/decision, and task lifecycle event stores an `occurredAtUtc` timestamp in its canonical Git record. Task closure records the time the task-branch merge was confirmed. These are UTC event times recorded when the Git mutation is prepared, not proof of productive computation. Existing events remain identifiable by their ledger sequence; timestamps add elapsed-time measurements.

Agent-level state changes update `stateEnteredAtUtc` only when the state actually changes. Entering Working also sets `workingTurnStartedAtUtc`; only an explicit recorded stall recovery may reset that clock without a state transition. Ordinary checkpoints, reviews and unrelated revisions must reset neither. Prior boundaries remain recoverable through Git history.

Derive agent statistics on demand from timestamps and Git history: time spent in each state (including operator wait in Assigned/Ready and idle time in Parked), timed Working-segment durations and P50/P95, checkpoints per segment and time between checkpoints, tasks completed per day, review wait/decision times and acceptance rates by progress/final review type, time from final acceptance to task-branch merge, and explicit stall-recovery frequency. Group these by agent and over time as needed; no dashboard or statistics API is required for the initial cutover. **Do not persist durations, totals, averages, percentile summaries, or turn counters.** State occupancy and timed execution segments are distinct: a stalled Working interval may contain downtime whose start is unknown. Wall-clock metrics must not be presented as CPU or productive time. A changed opaque session ID is not itself evidence of a stall; count only explicit recovery events.

## 4. Ordinary Git workflow, review snapshots and final acceptance

**Only task branches are merged into the integration target. Review branches are immutable, read-only checkpoints for inspection; they are NEVER merged.** Jarvis coordinates trusted developers using ordinary Git, rather than managing source history or introducing a new integration protocol.

### Task and review lifecycle

1. **Develop.** One worker owns implementation at a time and works on a normal **task branch**. The branch is mutable: workers may amend, squash, rebase, rename, or rewrite history. Jarvis records the current task-branch ref/source context when explicitly supplied, so the orchestrator can arrange a later handoff; branch identity is not agent identity.
2. **Progress review.** At any point the worker can publish a new review branch at the task branch's current commit and record a `Progress` request with its diff base. This branch is a stable, named, read-only checkpoint. The worker may immediately continue, including rebasing or rewriting the task branch. A review branch must never be automatically updated to follow the task branch.
3. **Progress feedback.** A reviewer claims the checkpoint, inspects it and writes `summary.md` and separate `findings/*.md`. `Accepted` means no issues in *that snapshot*, not that the task is complete. `ChangesRequested` records findings for the worker and orchestrator to read. The worker can address them **on the task branch** and optionally submit another progress review; Jarvis does not dispatch work or messages automatically. Neither progress verdict authorizes a merge.
4. **Worker completion.** When the worker believes the task is done, they commit their work and call `CompleteAgentWork`. The worker's execution slot is released; the orchestrator can explicitly park them or assign another task. The task remains **awaiting final review** (the existing `Implemented` label can represent this stage). Completion is not approval or integration.
5. **Final review.** Publish a distinct `Final` review branch from the completed task branch. Its scope is the **entire task change relative to the target**, not just changes since the last progress review. The original worker need not be available. Review findings remain attached to the task even if the worker changes.
6. **Final accepted.** A `Final` verdict of `Accepted` is recorded as approval for integration. The orchestrator decides when to proceed, examines the **current task branch** against the accepted final snapshot, uses normal Git to merge the **task branch** into the target, validates and pushes, then explicitly closes the task after confirming the result. Jarvis does not initiate any of these actions. Never merge the review branch.
7. **Final changes requested.** A `Final` verdict of `ChangesRequested` records rejection and findings; **it does not automatically reopen, reassign or resume anything**. The orchestrator decides how to proceed and explicitly assigns an available worker to continue the **same task branch** when appropriate. This may be the previous worker if available, but never steals a new assignment. Any task-state transition for rework is driven by that explicit orchestrator action. After corrections, the worker finishes again and submits a **new final review**.
8. **Retention.** Preserve decisions and Markdown findings in the orphan ledger. Preserve the accepted final review branch through integration verification. Review branches may be cleaned up afterward; deleting a review branch must not discard an accepted decision or make a completed, merged task impossible to close.

A *review branch*, rather than a bare commit ID, keeps a reviewed snapshot reachable and discoverable after a task-branch rebase, squash or forced update. Each snapshot has a distinct ref. Even an unclaimed request is replaced by creating a **new** ref and updating its metadata; do not move historical snapshot refs. Claimed snapshots never change. The orchestrator may explicitly release or withdraw an abandoned claimed review via Jarvis tools without changing task ownership or losing findings. There is no time-based automatic theft.

Example (ordinary Git operations, not Jarvis-controlled procedures):

```bash
# Worker on task/320: snapshot while continuing task development
git branch jarvis-review/320/progress-1 HEAD
git push origin jarvis-review/320/progress-1
# The worker may now amend, squash, or rebase task/320
git rebase origin/main

# Worker has finished; publish a SEPARATE final snapshot
git branch jarvis-review/320/final-1 HEAD
git push origin jarvis-review/320/final-1

# After final acceptance, orchestrator merges the TASK branch, never review refs
git fetch origin
git switch main
git merge --no-ff origin/task/320
# Validate/resolve conflicts and push the real task result
git push origin main
```

Names are illustrative; Jarvis may use review UUIDs. The mutable task branch must be fetchable by whoever integrates it, and its association with the unfinished task must remain discoverable even if the former worker has another assignment. A reviewer should not assume the task branch still points to the reviewed commit.

### Review scope, history rewriting and final merge

A review has a fixed `baseCommit` and a read-only `reviewRef`. For a progress review, the base may be a preceding snapshot to focus on new changes, or an appropriate merge-base against the target. For a **final review**, the base must expose the *entire* not-yet-integrated task result relative to the intended target. Earlier progress reviews do not subtract portions of the task from final review, because none of them were merged.

Final acceptance identifies **reviewed content**, not a permanent task-branch SHA. A rebase or squash may change commit hashes without materially changing the code. Compare using ordinary Git, e.g. tree/diff inspection between the final snapshot and the task branch. Do not require identical SHA identities or create a new Git-equivalence protocol. If the task branch has materially changed after final approval, obtain another final review before merging. If changes to the target create substantive conflict-resolution edits, review those edits appropriately. Only then push the task-branch merge. Never cherry-pick an intermediate review snapshot into the target as a shortcut.

A task's `InProgress` state permits any number of decided progress reviews over its lifetime, but one *actionable* review at a time is enough. Withdrawn and superseded snapshots do not invalidate earlier findings or decisions; they must not block final acceptance. No progress review is an integration candidate.

### Canonical review record and findings

Keep the existing orphan-branch storage model:

```text
tasks/<task-id>/reviews/<review-id>/
  review.json
  summary.md
  findings/<descriptive-slug>.md
```

The authoritative `review.json` uses the existing simple identity/claim/verdict record, with a new review `kind` (Progress or Final; bump schema version as appropriate):

```json
{
  "schemaVersion": 3,
  "reviewId": "<uuid-n>",
  "taskId": "320",
  "attemptId": "<uuid-n>",
  "ownerAgentId": "Agent 3",
  "kind": "Progress",
  "taskRef": "refs/heads/task/320",
  "reviewRef": "refs/heads/jarvis-review/320/<uuid-n>/r1",
  "baseCommit": "<git-oid>",
  "targetRef": "refs/heads/main",
  "status": "Unclaimed",
  "reviewerId": null,
  "version": 1,
  "createdSequence": 123,
  "decisionSequence": 0,
  "acknowledged": false
}
```

`Unclaimed -> Claimed -> Accepted | ChangesRequested` and `Unclaimed -> Withdrawn` are normal transitions. Permit explicit orchestrator release/reassignment of a stuck claim, preserving its findings. The current record contains the reviewer identity, one verdict, ordinary optimistic version and feedback acknowledgment; Git history supplies earlier versions. No independent revision/decision entities, reviewer assignment at task creation, or duplicate stored snapshot SHA are needed. `reviewRef` keeps reviewed commits reachable even if task history is rewritten.

Each review can contain any number of independent Markdown findings. `PutReviewDocument` updates one blob with a stable operation UUID and expected blob OID for replacements; `ListReviewDocuments` and `ReadReviewDocument` expose them. Paths are `summary.md` and `findings/[a-z0-9_-]+.md`, max 512 KiB per file. Implementation responses may be added after verdict. Findings are durable **task history**, not owned by the agent's current work slot. Keep review acknowledgment as a single field on the review.

### Task completion, visibility and cleanup

- `CompleteAgentWork` releases the worker and records implementation completion; it does not imply acceptance. `Implemented` can denote awaiting final review/merge; `Closed` is the terminal state after final acceptance and the **task-branch** merge.
- `DecideReview` on a final request is the final acceptance decision. The extra `FinalizeReview` step is redundant and should be **retired**, rather than imposed as a second gate. `CloseTask` checks the final accepted review, current task branch and actual pushed target outcome; review-branch ancestry is **never** the integration test. If historical `Integrated` remains readable, it must not be derived from a review branch.
- Final `ChangesRequested` is visible to the orchestrator as a reason to consider rework; it causes no automatic assignment, reopening or resumption, and never interrupts a worker's newer task. Preserve the same task branch, review files and task history across ownership transfers. Worker replacement is not a new source project.
- The project task listing must reveal tasks awaiting final review, with requested final changes, and accepted final reviews awaiting **task-branch merge**, independently of agent focus. Provide a small read-only canonical task/review listing API if absent. Do not retain `AcceptedPendingIntegration` as a queue of accepted progress-review refs.
- A review branch may be deleted after its usefulness ends. Missing ref contents are an error **when a new inspection needs them**, but do not retroactively invalidate a recorded verdict or block closure when the real task branch has already been merged. Publication and ledger updates are separate; preflight and best-effort cleanup of failed review publication are sufficient. No cross-ref transaction coordinator, merge receipt or integration-plan ledger entity.

## 5. Implementation alignment (31 October 2026)

**This document defines the intended behavior, not the current deployed implementation.** Current code incorrectly treats accepted review branches as independently mergeable; `AcceptedPendingIntegration`, `FinalizeReview` and `CloseTask` assume review-branch ancestry. It also does not distinguish Progress/Final reviews and can obscure previous reviews after owner changes. Update client/server commands, read views, tests and Wayfold agent instructions accordingly as a **separate implementation task**. Remove the obsolete merge semantics instead of layering special cases on them.

The Wayfold ledger was previously inspected at sequence 118 with no review/integration records at that point. Do not build an elaborate review migration protocol or revive retired V2 work solely for this correction. Preserve canonical orphan Git authority, unprefixed public task tools and separate Markdown findings. Updating this spec **does not** authorize a code deployment, remote ledger mutation or data migration.

## 6. Tool surface and cutover

Expose the orphan-branch operations through **unprefixed public MCP tool names** (`CreateTask`, `AssignTask`, `AgentInbox`, `SubmitReview`, `LedgerHealth`, etc.) without `V2` compatibility aliases. The internal versioned task/ledger/review wire commands retain their `V2` names; public names are independent of the transport protocol. Where a legacy public name is reused, it exclusively invokes the orphan-ledger implementation.

Provide only the queries needed to operate the workflow: current project task listing/details/spec, agent inbox/roster, and progress/final review queues and findings. Show final reviews awaiting assignment, rework or task-branch merge independently of agent focus. If a query is missing, add the **smallest** direct read for it. Use ordinary Git history/diff commands when historical investigation is needed. No portable cursor framework, historical search service, mirror/restore API, or extensive administrative API is required.

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
4. Workers can create immutable named progress snapshots, continue rebasing/squashing/amending task branches and replace unclaimed requests with new immutable refs without changing claimed snapshots.
5. Progress review decisions deliver findings to workers and never authorize merging; subsequent progress reviews preserve earlier findings.
6. Worker completion releases the slot while the task awaits a whole-task final review; a worker can be Parked or assigned new work without losing the old task's source context or review evidence.
7. A failed final review records findings for the orchestrator, which can explicitly assign an available worker to rework the same task branch without preempting existing work; Jarvis performs no automatic assignment or rework scheduling. A new final review follows the fixes.
8. The orchestrator merges **only the task branch** after accepted final review, verifies the pushed target, and closes the task. No review branch is merged. Rebases that alter hashes but not code do not automatically invalidate review; material changes require re-review.
9. Review ref deletion cannot block closure of an accepted, merged task. Stuck review claims can be explicitly released, withdrawn requests do not block final review, and project-level listings expose work regardless of current agent focus.
10. Test in disposable Git remotes: progress feedback, final accept/reject, reassignment, rebases, task-branch merge, review-ref cleanup and closure. Existing client/server tests pass; no automatic migration occurs and legacy task files stay untouched.
11. Each assignment persists its current session ID and the ordered history of associated opaque IDs. A reported stalled-Working recovery records a dated event, associates the continuing session and resets the timed-segment cutoff clock without changing task ownership, assignment generation, or state-entry timestamp.
12. Where project/session context is available, the current Working session receives its 25/30-minute guidance and an earlier associated session is told about the newer execution and advised to stop outdated work. Generic calls and orchestrator inbox reads never create session associations; projectless tools do not require a special cross-project resolver.
13. Agent state transitions and immutable workflow events have UTC timestamps that survive cloning. Checkpoints leave both Working timers unchanged; correctly ending a Working segment requires leaving Working except for explicit stall recovery. No duration aggregates or turn counters are persisted.

14. A task mutation against a reassigned or stale owner/attempt/version fails with clear current-assignment context. Session association changes alone do not impose a blanket write fence; agents can read the current session history and decide how to resume. Completing Task A does not prevent ordinary review bookkeeping for A while the same agent works on Task B.

Stop when these conditions hold. Do **not** resume the superseded V2-10–V2-16 roadmap just to satisfy the old spec.

## 9. Design rule

**Jarvis makes an ordinary team workflow easy for trusted agents.** Preserve canonical task state and useful coordination, but never block normal Git branching, rebasing, reviewing or merging merely to satisfy Jarvis-specific protocol constraints. Prefer removing restrictions over building new mechanisms around them. A new abstraction, API or validation layer must solve a concrete demonstrated problem. Git already handles source history, reviews and merges.
