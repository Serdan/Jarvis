# Migrating to Git-backed Jarvis orchestration


> **Current release note (protocol 3.47):** Internal command types are now `TaskRead`, `TaskWrite`, `LedgerRead`, `LedgerWrite`, `ReviewRead` and `ReviewWrite`; the version-prefixed command identifiers are no longer advertised. Public MCP names are unchanged. The historical protocol 3.45/3.46 cutover instructions below document previous migrations, not additional installed APIs. Trust bindings remain in `ledger-v2/` and previously saved pending operation journals remain readable. Upgrade server and client together, after operator approval.


**Applies to:** the orphan-branch coordination implementation (task/review capability protocol 3.47). The source of truth is [the canonical orchestration architecture](specs/orchestration-architecture.md).

**Scope:** Move an existing project's *active coordination* from source-branch task files to the Git-backed ledger. This is a **manual, selective cutover**. Updating Jarvis itself does not create a ledger or migrate any tasks.

## What changes

| Old workflow | New workflow |
| --- | --- |
| Task HTML/JSON files (commonly `docs/tasks/`) in ordinary source branches/worktrees | Current task, agent and review records in `refs/heads/jarvis-orchestration` |
| Different worktrees may hold different copies of task state | A single canonical Git remote ref is shared by clients/worktrees |
| `CreateTask`, `ListTasks`, `AssignTask`, `BeginTask`, `CheckpointTask`, etc. | `CreateTask`, `AgentInbox`, `AssignTask`, `ResumeAgent`, `CheckpointAttempt`, etc. |
| Source worktree/branch can be confused with task ownership | Agent owner is a project-local label; source worktree/ref is separate |

The old orchestration *implementations* are **removed**, not forwarded. Where public names such as `CreateTask` and `AssignTask` are reused, they invoke the new orphan-ledger workflow rather than a legacy handler. `ListProjectTasks` and `RunProjectTask` are still local command-runner tools; they are **not** replacements for the retired task APIs. Normal file, Git, worktree and job commands still work.

The orphan branch is never merged into `main` or a source branch, and no source worktree needs to check it out. The Jarvis server remains stateless; Git remote history is the authority. The old task files can remain in Git indefinitely as historical reference.

## 1. Verify the release

1. Confirm the upgraded client is *responding* to `ListProjects`; a green SignalR connection alone is not sufficient.
2. Check `ListDevices` and `ListCommands`. For this release, the client should report protocol **3.47**, and the deployed server must expose the matching independent task/review tools. The current client advertises `TaskRead`, `TaskWrite`, `LedgerRead`, `LedgerWrite`, `ReviewRead` and `ReviewWrite` (the original 3.45 implementation used version-prefixed internal commands). The old `V2IntegrationRead`/`V2IntegrationWrite` channels and the approval-gated integration workflow are retired. Public MCP tool names have no `V2` prefix.
3. Confirm the old source-branch command handlers are unavailable and that the MCP catalog contains only unprefixed orchestration tools (no duplicate `V2*` aliases). Do not start a migration through an out-of-date client.
4. Exercise the workflow on a **disposable Git remote** before initializing an important project's real ledger.

A client self-update is not verified until its *new* protocol and a successful command are observed; a reported connection or requested restart is insufficient.

## Explicit agent/attempt schema upgrade (protocol 3.46)

Protocol 3.46 replaces the stored agent state enum with `currentAssignment`, `execution` and `unassignedDisposition`, and separates attempt progress (`Queued`, `Started`, `Deferred`, `Blocked`, `Finished`, `Abandoned`, `Superseded`) from execution state. `Ready`, `Working`, `NeedsWork` and `Parked` are derived for the UI. This is a **different upgrade** from migrating old source-branch task files into the orphan ledger.

For a project **already using a protocol 3.47 orphan Git ledger**, its v2 agent/attempt records are not silently converted on read. After upgrading the client and server together to 3.46:

1. Inspect the project's existing `LedgerHealth` and any active agents/attempts using the pre-upgrade tools; preserve the Git remote history and plan a brief pause in writes.
2. Invoke `MigrateOrthogonalAgentState(projectName, expectedLedgerHead, operationId)` with exactly the inspected trusted head and a fresh operation UUID. It converts every v2 agent/attempt record in one verified ledger transaction. It refuses mixed versions, stale heads and ambiguous current ownership. New 3.46 ledgers need **no** conversion.
3. Re-read `LedgerHealth`, `AgentInbox` and `TaskOverview` using the new client. Verify Ready/Working/Needs work/Parked, original assignment generations and checkpoints, and deferred/blocked attempts.
4. Continue normal work. Reviews, source branches and task HTML are unchanged. If the conversion rejects inconsistent legacy data, inspect it and resolve the cause explicitly; do not hand-edit authoritative JSON or silently guess an owner.

The source implementation and test fixtures never invoke this tool against any existing live project ledger. Production cutover remains a separate decision.

## 2. Choose the project and inspect its remote

For each project to migrate:

1. Choose the writable, canonical Git remote (normally `origin`). Check both fetch and push URLs and permissions with `git remote -v`.
2. Determine who can read that remote. If public, the orphan branch's task descriptions and metadata will be public too. Do not store secrets there.
3. Check for an existing branch:

   ```bash
   git ls-remote --heads origin refs/heads/jarvis-orchestration
   ```

   If it exists, **bind** it; do not initialize another one. If Git reports a network/authorization error, resolve that first rather than interpreting the result as an empty branch.
4. Inventory just the work worth carrying forward: IDs, up-to-date HTML specs, owners, current branches/worktrees/commits, pending reviews and next steps. Look in source-side `docs/tasks/`, Git history and agent checkpoints.

Do **not** migrate every completed, obsolete or cancelled task for the sake of preserving history. Do not delete or silently rewrite the original task files. There is no automated importer, legacy task-state synchronization or compatibility fallback.

## 3. Initialize the first ledger or bind an existing one

**New ledger (only when the orphan ref is absent):** use `InitializeLedger` with:

- `projectName`: the existing Jarvis project.
- `projectId` and `ledgerId`: fresh, stable UUIDs identifying this project and ledger.
- `epoch`: positive integer (normally `1` for a new ledger).
- `remoteName`, `approvedFetchUrl` and `approvedPushUrl`: the *independently inspected* remote identity.
- `acknowledgeVisibility=true`: explicit acknowledgment of public or unknown remote visibility.
- `operationId`: a new caller-generated UUID.

The operation creates the orphan branch, refuses an already-existing ledger ref, and installs a local binding. This is an approved Git write, not a side effect of reading the project.

**Existing ledger (including a second machine/checkout):**

1. Inspect the actual remote branch and independently verify its trusted commit.
2. Obtain the **same** project UUID, ledger UUID and epoch from the initialized ledger; do not generate replacements.
3. Use `BindExistingLedger` with those IDs, the remote identity, `expectedTrustedHead`, visibility acknowledgment and a new operation UUID.
4. Check `LedgerHealth(projectName)` on each participating client and verify the same canonical project/ledger/epoch and compatible Git ancestry.

Jarvis stores the binding under the user's client configuration directory in `ledger-v2/`, keyed by the absolute project-root path. Bindings are **per checkout/client configuration**, not tracked source files. A different root or computer may require another explicit bind. A missing binding must cause the canonical ledger tools to fail, never fall back to V1 data.

Do **not** manually create or merge the orphan branch by checking it out in a source worktree.

## 4. Recreate only active tasks

For each task still needing work:

1. **Create its current specification.** Call `CreateTask(projectName, taskId, title, html, operationId)` using the latest intent, acceptance criteria, and a brief reference to the old task file/branch. Keep a useful stable task ID; do not create duplicates.
2. **Register the agents.** `RegisterAgent(projectName, agentId, operationId)` creates a project-local agent label. Idle agents start `Parked`. Register intended worker/reviewer labels as needed.
3. **Assign ownership.** `AssignTask(projectName, taskId, attemptId, ownerId, operationId)` creates one implementation attempt and assigns exactly one owner. Reviewers are chosen independently when they claim an individual review; the attempt does not contain a reviewer. Use fresh UUIDs for `attemptId` and `operationId`.
4. **Read the authoritative assignment.** Use `AgentInbox(projectName, agentId)` and `GetAssignment(projectName, taskId, assignmentGeneration)` for the current generation, spec, owner and versions. Starting a Queued attempt through `ResumeAgent` atomically acknowledges its assignment; no separate public acknowledgement call is needed.
**Simplified task tools (protocol 3.50).** `ReviseTaskSpec` accepts optional `expectedSpecBlobOid`: omit it for an unassigned Open task, supply it for an assigned task or exact-blob precondition. `CancelTask` accepts a task version and reason; omit attempt/agent/generation versions only for an unassigned Open task. Assigned task cancellation still requires all three versions. Public `AcknowledgeAssignment`, `ReviseTaskSpecPinned`, and `CancelOpenTask` are retired; their historical internal ledger actions remain supported.

**Queued assignment updates (protocol 3.51).** Use `ReissueAssignment` to issue replacement instructions, optionally with `newSpecHtml`. If the canonical task spec was revised separately, use the same tool with `expectedCurrentSpecBlobOid` and `expectedTaskVersion` to adopt it. These two spec-change inputs are mutually exclusive. This replaces the separate public `AdoptTaskSpec` tool; original immutable issue/acknowledgement records and historical ledger actions remain valid.

5. **Begin implementation.** The worker calls `ResumeAgent` with its current expected attempt version, agent version, and assignment generation. The operation starts a Queued attempt and atomically acknowledges its issuance. Existing code stays in its ordinary source branch/worktree. Any recorded source ref is context, not a permission gate: the agent may rebase or change branches and the orchestrator may update that context while the agent is Ready. Do not pin a review baseline for the entire task.
6. **Persist progress.** `CheckpointAttempt` records summary, evidence and next action, but does **not** end a Working turn. Before a normal agent turn finishes, use `YieldAgent` to stop execution and remain `Ready`, or `CompleteAgentWork` to finish the attempt, release its current assignment and become `NeedsWork`.
7. **Resume deliberately.** `ResumeAgent` also continues a previously Started Ready agent. It also supports explicitly recovering a stalled Working session, recording its session association and recovery event without reassigning ownership.

Always read expected versions/generations from the latest ledger. Do not guess. Use a **new operation UUID for each new logical mutation**, but reuse the **same UUID and parameters** when retrying the *same* operation after a lost response. Provider session IDs are supplied automatically; agents must not invent them.

For in-progress legacy work, put the *remaining work and next action* in the canonical spec/checkpoint. An old `done` flag is not proof of a review decision or source integration. Existing attempt JSON may retain old reviewer and integration fields; they are historical data, not new review assignments or mandatory workflow gates. No bulk rewrite of legacy records is required.

## 5. Task and review tools are independent

**Jarvis is a set of tools, not an implementation or review workflow.** Individual projects define their own coordination, review, merge and closure practices in agent instructions (e.g. AGENTS.md or agent skills).

- `TaskOverview` and `ReviewTask` read the canonical task ledger; `AgentInbox` reads an agent's assignment/availability. `AssignTask` and `TransferOwner` explicitly record who owns work. `CompleteAgentWork` releases the worker's slot, and `CloseTask` records that a task is closed. These tools do not infer or require a review decision, source merge or particular task-branch state. If a task is closed with an outstanding assignment, `AbandonAttempt` can explicitly release it without reopening or changing the task's Closed state.
- `SubmitReview` publishes a new named review snapshot at a supplied Git commit; it does **not** require an active task or Working/Finished agent. Use optional `taskId` to associate it with a task, or omit it for the shared `_reviews` namespace. Optional `sourceRef`, `targetRef`, `purpose` and `summary` are review context, not permission gates. `purpose` is an arbitrary label, not a special Progress/Final review type.
- `ReviewDetails`, `ReviewerInbox`, `ClaimReview`, `ReleaseReview`, `ReplaceReview`, `WithdrawReview`, `DecideReview`, `ListReviewDocuments`, `ReadReviewDocument` and `PutReviewDocument` manage review records and separate Markdown findings. Claims and verdicts affect the review, not its associated task or worker.
- Multiple reviews may coexist for a task. An explicit task-owner transfer, task cancellation or task closure never automatically withdraws a review. Releasing a claim leaves the same snapshot available for another reviewer.
- Git branching, rebasing, comparing, publishing and merging are ordinary agent-controlled operations. Agents choose what to review, what branches to merge and when to close a task; Jarvis neither enforces a "final review" gate nor verifies ancestry on `CloseTask`.

The review namespace under `tasks/<id>/reviews/` is an indexing convention, not an ownership or lifecycle dependency. The immutable review refs created by the review tools are useful checkpoints that remain reachable across a task-branch rewrite. A missing source ref does not erase persisted review metadata or findings.

There is no `FinalizeReview` operation, no `AcceptedPendingIntegration` queue, and no separate approval-gated `CloseNonSourceTask` route. See the [normative capabilities specification](specs/orchestration-architecture.md).

## 6. Verify the cutover

Before calling a project migrated, verify:

- Two independently bound clients see the same canonical ledger state, even from different source worktrees.
- An agent can be assigned, begin, checkpoint, yield, resume and complete; the state survives restart.
- Reviews can be submitted/claimed/decided without automatically merging source code.
- The public MCP catalog contains only unprefixed orchestration tools; reused names such as `CreateTask` operate on the orphan ledger, never on old `docs/tasks/` files.
- The orphan branch has independent history and was never merged into source branches.

Optional **read-only** Git inspection without switching branches:

```bash
git fetch origin refs/heads/jarvis-orchestration
git ls-tree -r --name-only FETCH_HEAD
git show FETCH_HEAD:ledger.json
# Only for a task that exists:
git show FETCH_HEAD:tasks/TASK_ID/task.json
```

Use the unprefixed canonical task/review tools for live writes; do not push hand-edited ledger JSON or force-push the orphan branch.

## 7. Stalls, retries and failed cutovers

- **Stalled Working session:** read `AgentInbox` and the checkpoint before invoking an explicit `ResumeAgent`. Only an explicit recovery resets the Working-segment clock. Older associated sessions should reread their assignment when a newer one is active.
- **Long turns:** Jarvis advises a soft stop after **25 minutes** and hard stop after **30 minutes** of the current timed Working segment. These are instructions, not forced cancellation; checkpoint alone does not finish the turn.
- **Uncertain write:** inspect `GetOperationStatus`, `PendingOperations` and `ReconcilePendingOperations`. A verified `Applied` receipt confirms success; `NotFoundAtVerifiedHead` does not prove a timed-out push can never arrive. Reuse the original UUID and unchanged command when retrying. If a matching **applied** pending record remains, `AcknowledgePendingOperation` requires its verified request hash. For a `NotFoundAtVerifiedHead` entry, the tool cannot acknowledge success. Resolve its outcome before any manual archival of a superseded local intent; do not replay it blindly or modify the canonical task merely to clear a journal entry.
- **Stale version / unavailable remote:** reread the canonical assignment and stop speculative writes. Never let a timer automatically reassign a task.
- **Migration failure:** stop new ledger writes and preserve its Git history and pending-intent records. The old task files remain readable historical material, **not** an automatically synchronized fallback.

A project is migrated when its canonical ledger is bound, its **relevant active tasks** are recreated, agents use the orphan-ledger tools for all new orchestration writes, and participating clients agree on its state. Other projects can migrate independently. Updating the Jarvis client/server by itself does not complete this per-project step.

See the [architecture spec](specs/orchestration-architecture.md). The older [Git-backed tasks guide](GitBackedTasks.md) describes the retired source-branch workflow.


## Administrative deferral (protocol 3.52)

Use `DeferAttempt` for a Queued or Started attempt that should stop holding its agent's assignment. Default `disposition: Deferred` retains the previous park behavior; `disposition: Blocked` retains the previous explicit block behavior. Both require a reason and exact task/attempt/agent versions and preserve immutable lifecycle history. The old `ParkAttempt` and `BlockAttempt` actions remain valid internally for historical ledger operations.

`ResumeAttempt` and `ReissueQueuedAttempt` deliberately remain separate for now: the former creates a new generation when resuming a deferred or blocked attempt; the latter assigns an unoccupied agent to an existing queued generation. This difference requires design/transition review before consolidating further.

## Attempt source context (protocol 3.54)

Use `SetAttemptSource` with an exact task, owner, version, generation, and `sourceRef`. For the first source binding, supply both `baseOid` and `targetRef` so Jarvis can validate ancestry and record source repository identity; optional `allowDetached` and `allowUncommitted` apply only to this initial binding. For later branch switches, omit both baseline fields to retain the original pinned context. Historical `BindAttemptSource` and `RebindAttemptSource` internal ledger actions remain available.

The old `BindReviewBase` public tool is retired: independent review requests have their own exact commit and comparison context. Historical baseline-only attempt bindings remain readable without rewriting Git history. Existing clients must update before calling this revised public API.
