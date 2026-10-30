# Migrating to Git-backed Jarvis orchestration

**Applies to:** the orphan-branch orchestration implementation on Jarvis client/server protocol 3.38. The source of truth is [the orchestration V2 architecture](specs/orchestration-v2-architecture.md).

**Scope:** Move an existing project's *active coordination* from source-branch task files to the Git-backed ledger. This is a **manual, selective cutover**. Updating Jarvis itself does not create a ledger or migrate any tasks.

## What changes

| Old workflow | New workflow |
| --- | --- |
| Task HTML/JSON files (commonly `docs/tasks/`) in ordinary source branches/worktrees | Current task, agent, review and integration state in `refs/heads/jarvis-orchestration` |
| Different worktrees may hold different copies of task state | A single canonical Git remote ref is shared by clients/worktrees |
| `CreateTask`, `ListTasks`, `AssignTask`, `BeginTask`, `CheckpointTask`, etc. | `V2CreateTask`, `V2AgentInbox`, `V2AssignTask`, `V2BeginAttempt`, `V2CheckpointAttempt`, etc. |
| Source worktree/branch can be confused with task ownership | Agent owner is a project-local label; source worktree/ref is separate |

The old orchestration MCP commands are **removed**, not forwarded to V2. `ListProjectTasks` and `RunProjectTask` are still local command-runner tools; they are **not** replacements for the retired task APIs. Normal file, Git, worktree and job commands still work.

The orphan branch is never merged into `main` or a source branch, and no source worktree needs to check it out. The Jarvis server remains stateless; Git remote history is the authority. The old task files can remain in Git indefinitely as historical reference.

## 1. Verify the release

1. Confirm the upgraded client is *responding* to `ListProjects`; a green SignalR connection alone is not sufficient.
2. Check `ListDevices` and `ListCommands`. For this deployment, both client and server should report protocol **3.38**, and the client should advertise `V2TaskRead`, `V2TaskWrite`, `V2LedgerRead`, `V2LedgerWrite`, `V2ReviewRead`, `V2ReviewWrite`, `V2IntegrationRead` and `V2IntegrationWrite`.
3. Confirm retired orchestration tools are absent. Do not start a migration through an out-of-date client.
4. Exercise the workflow on a **disposable Git remote** before initializing an important project's real ledger.

A client self-update is not verified until its *new* protocol and a successful command are observed; a reported connection or requested restart is insufficient.

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

Do **not** migrate every completed, obsolete or cancelled task for the sake of preserving history. Do not delete or silently rewrite the original task files. There is no automated importer, V1/V2 synchronization or compatibility fallback.

## 3. Initialize the first ledger or bind an existing one

**New ledger (only when the orphan ref is absent):** use `V2InitializeLedger` with:

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
3. Use `V2BindExistingLedger` with those IDs, the remote identity, `expectedTrustedHead`, visibility acknowledgment and a new operation UUID.
4. Check `V2LedgerHealth(projectName)` on each participating client and verify the same canonical project/ledger/epoch and compatible Git ancestry.

Jarvis stores the binding under the user's client configuration directory in `ledger-v2/`, keyed by the absolute project-root path. Bindings are **per checkout/client configuration**, not tracked source files. A different root or computer may require another explicit bind. A missing binding must cause the V2 tools to fail, never fall back to V1 data.

Do **not** manually create or merge the orphan branch by checking it out in a source worktree.

## 4. Recreate only active tasks

For each task still needing work:

1. **Create its current specification.** Call `V2CreateTask(projectName, taskId, title, html, operationId)` using the latest intent, acceptance criteria, and a brief reference to the old task file/branch. Keep a useful stable task ID; do not create duplicates.
2. **Register the agents.** `V2RegisterAgent(projectName, agentId, operationId)` creates a project-local agent label. Idle agents start `Parked`. Register intended worker/reviewer labels as needed.
3. **Assign ownership.** `V2AssignTask(projectName, taskId, attemptId, ownerId, reviewerId, operationId)` creates one implementation attempt and assigns exactly one owner. Use fresh UUIDs for `attemptId` and `operationId`.
4. **Read the authoritative assignment.** Use `V2AgentInbox(projectName, agentId)` and `V2GetAssignment(projectName, taskId, assignmentGeneration)` for the current generation, spec, owner and versions. Use `V2AcknowledgeAssignment` when applicable.
5. **Begin implementation.** The worker calls `V2BeginAttempt` with its current expected attempt version and assignment generation. Existing code stays in its source worktree/branch. When necessary, record the actual source ref and baseline via `V2BindAttemptSource` and `V2BindReviewBase`; do not pretend old task JSON already created a V2 attempt.
6. **Persist progress.** `V2CheckpointAttempt` records summary, evidence and next action, but does **not** end a Working turn. Before a normal agent turn finishes, use `V2YieldAgent` to enter `Ready`, or `V2CompleteAgentWork` to enter `Completed`.
7. **Resume deliberately.** `V2ResumeAgent` resumes a Ready agent. It also supports explicitly recovering a stalled Working session, recording its session association and recovery event without reassigning ownership.

Always read expected versions/generations from the latest ledger. Do not guess. Use a **new operation UUID for each new logical mutation**, but reuse the **same UUID and parameters** when retrying the *same* operation after a lost response. Provider session IDs are supplied automatically; agents must not invent them.

For in-progress legacy work, put the *remaining work and next action* in the new canonical spec/checkpoint. An old `done` flag is not proof of a V2 review decision or verified source integration.

## 5. Reviews and integration remain distinct

A review snapshot/ref points to a commit in ordinary **source history**. The task and review records live on the **orphan branch**.

- `V2SubmitReview` publishes the exact metadata for a source snapshot. The owner may continue working and replace an **unclaimed** review with `V2ReplaceReview`.
- Reviewers use `V2ReviewerInbox`, `V2ReviewTask`, `V2ClaimReview` and `V2DecideReview`. A claimed revision remains pinned; feedback is recorded without interrupting unrelated newer assignments.
- Acceptance is **not** a merge or code push. Source integration must be performed explicitly with normal authorized Git operations. Use `V2PlanIntegration` and only `V2RecordIntegration` when the *remote target* is verified. Closing an integrated task is separate (`V2CloseIntegratedTask`).

If old work is already awaiting review, verify its actual source commit and create a truthful V2 review only if review is still needed. Never fabricate historical approvals, claims or integrations.

## 6. Verify the cutover

Before calling a project migrated, verify:

- Two independently bound clients see the same canonical ledger state, even from different source worktrees.
- An agent can be assigned, begin, checkpoint, yield, resume and complete; the state survives restart.
- Reviews can be submitted/claimed/decided without automatically merging source code.
- The old MCP task tools are absent and the old `docs/tasks/` files remain untouched.
- The orphan branch has independent history and was never merged into source branches.

Optional **read-only** Git inspection without switching branches:

```bash
git fetch origin refs/heads/jarvis-orchestration
git ls-tree -r --name-only FETCH_HEAD
git show FETCH_HEAD:ledger.json
# Only for a task that exists:
git show FETCH_HEAD:tasks/TASK_ID/task.json
```

Use V2 APIs for live writes; do not push hand-edited ledger JSON or force-push the orphan branch.

## 7. Stalls, retries and failed cutovers

- **Stalled Working session:** read `V2AgentInbox` and the checkpoint before invoking an explicit `V2ResumeAgent`. Only an explicit recovery resets the Working-segment clock. Older associated sessions should reread their assignment when a newer one is active.
- **Long turns:** Jarvis advises a soft stop after **25 minutes** and hard stop after **30 minutes** of the current timed Working segment. These are instructions, not forced cancellation; checkpoint alone does not finish the turn.
- **Uncertain write:** inspect `V2GetOperationStatus`, `V2PendingOperations` and `V2ReconcilePendingOperations`. A verified `Applied` receipt confirms success; `NotFoundAtVerifiedHead` does not prove a timed-out push can never arrive. Reuse the original UUID and unchanged command when retrying. If a matching pending record remains, `V2AcknowledgePendingOperation` requires its verified request hash.
- **Stale version / unavailable remote:** reread the canonical assignment and stop speculative writes. Never let a timer automatically reassign a task.
- **Migration failure:** stop new ledger writes and preserve its Git history and pending-intent records. The old task files remain readable historical material, **not** an automatically synchronized fallback.

A project is migrated when its canonical ledger is bound, its **relevant active tasks** are recreated, agents use V2 for all new orchestration writes, and participating clients agree on its state. Other projects can migrate independently. Updating the Jarvis client/server by itself does not complete this per-project step.

See the [architecture spec](specs/orchestration-v2-architecture.md). The older [Git-backed tasks guide](GitBackedTasks.md) describes the retired source-branch workflow.
