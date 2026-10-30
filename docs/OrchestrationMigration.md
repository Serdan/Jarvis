# Migrating to Git-backed Jarvis orchestration

**Applies to:** the orphan-branch orchestration implementation on Jarvis client/server protocol 3.39. The source of truth is [the orchestration V2 architecture](specs/orchestration-v2-architecture.md).

**Scope:** Move an existing project's *active coordination* from source-branch task files to the Git-backed ledger. This is a **manual, selective cutover**. Updating Jarvis itself does not create a ledger or migrate any tasks.

## What changes

| Old workflow | New workflow |
| --- | --- |
| Task HTML/JSON files (commonly `docs/tasks/`) in ordinary source branches/worktrees | Current task, agent, review and integration state in `refs/heads/jarvis-orchestration` |
| Different worktrees may hold different copies of task state | A single canonical Git remote ref is shared by clients/worktrees |
| `CreateTask`, `ListTasks`, `AssignTask`, `BeginTask`, `CheckpointTask`, etc. | `CreateTask`, `AgentInbox`, `AssignTask`, `BeginAttempt`, `CheckpointAttempt`, etc. |
| Source worktree/branch can be confused with task ownership | Agent owner is a project-local label; source worktree/ref is separate |

The old orchestration *implementations* are **removed**, not forwarded. Where public names such as `CreateTask` and `AssignTask` are reused, they invoke the new orphan-ledger workflow rather than a legacy handler. `ListProjectTasks` and `RunProjectTask` are still local command-runner tools; they are **not** replacements for the retired task APIs. Normal file, Git, worktree and job commands still work.

The orphan branch is never merged into `main` or a source branch, and no source worktree needs to check it out. The Jarvis server remains stateless; Git remote history is the authority. The old task files can remain in Git indefinitely as historical reference.

## 1. Verify the release

1. Confirm the upgraded client is *responding* to `ListProjects`; a green SignalR connection alone is not sufficient.
2. Check `ListDevices` and `ListCommands`. For this deployment, both client and server should report protocol **3.39**, and the client should advertise internal `V2TaskRead`, `V2TaskWrite`, `V2LedgerRead`, `V2LedgerWrite`, `V2ReviewRead`, `V2ReviewWrite`, `V2IntegrationRead` and `V2IntegrationWrite` capabilities. The public MCP names have no `V2` prefix.
3. Confirm the old source-branch command handlers are unavailable and that the MCP catalog contains only unprefixed orchestration tools (no duplicate `V2*` aliases). Do not start a migration through an out-of-date client.
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

Jarvis stores the binding under the user's client configuration directory in `ledger-v2/`, keyed by the absolute project-root path. Bindings are **per checkout/client configuration**, not tracked source files. A different root or computer may require another explicit bind. A missing binding must cause the V2 tools to fail, never fall back to V1 data.

Do **not** manually create or merge the orphan branch by checking it out in a source worktree.

## 4. Recreate only active tasks

For each task still needing work:

1. **Create its current specification.** Call `CreateTask(projectName, taskId, title, html, operationId)` using the latest intent, acceptance criteria, and a brief reference to the old task file/branch. Keep a useful stable task ID; do not create duplicates.
2. **Register the agents.** `RegisterAgent(projectName, agentId, operationId)` creates a project-local agent label. Idle agents start `Parked`. Register intended worker/reviewer labels as needed.
3. **Assign ownership.** `AssignTask(projectName, taskId, attemptId, ownerId, reviewerId, operationId)` creates one implementation attempt and assigns exactly one owner. Use fresh UUIDs for `attemptId` and `operationId`.
4. **Read the authoritative assignment.** Use `AgentInbox(projectName, agentId)` and `GetAssignment(projectName, taskId, assignmentGeneration)` for the current generation, spec, owner and versions. Use `AcknowledgeAssignment` when applicable.
5. **Begin implementation.** The worker calls `BeginAttempt` with its current expected attempt version and assignment generation. Existing code stays in its ordinary source branch/worktree. Any recorded source ref is context, not a permission gate: the agent may rebase or change branches and the orchestrator may update that context while the agent is Ready. Do not pin a review baseline for the entire task.
6. **Persist progress.** `CheckpointAttempt` records summary, evidence and next action, but does **not** end a Working turn. Before a normal agent turn finishes, use `YieldAgent` to enter `Ready`, or `CompleteAgentWork` to enter `Completed`.
7. **Resume deliberately.** `ResumeAgent` resumes a Ready agent. It also supports explicitly recovering a stalled Working session, recording its session association and recovery event without reassigning ownership.

Always read expected versions/generations from the latest ledger. Do not guess. Use a **new operation UUID for each new logical mutation**, but reuse the **same UUID and parameters** when retrying the *same* operation after a lost response. Provider session IDs are supplied automatically; agents must not invent them.

For in-progress legacy work, put the *remaining work and next action* in the new canonical spec/checkpoint. An old `done` flag is not proof of a V2 review decision or verified source integration.

## 5. Ordinary review and merge workflow

Working branches, review branches and merges are normal Git development operations. The orphan ledger is for coordination, not for constraining Git.

1. The agent commits on its working branch and creates a **separate review branch** at the commit it wants reviewed. `SubmitReview` records that stable snapshot. The agent can keep working on its original branch immediately.
2. The reviewer uses `ReviewerInbox`, `ClaimReview` and `DecideReview` to inspect and accept/request changes. A claimed review branch remains unchanged; an unclaimed request can be replaced.
3. The orchestrator merges accepted review work into `main` with ordinary Git and pushes. **No cherry-picking.** A fast-forward or merge commit is fine. If the target moved, resolve conflicts and validate as normal. Review acceptance does not perform a Git merge.
4. Record the actual merged remote target in the ledger. Do not require that the target SHA match the review commit or that the whole task be finalized. **Multiple review/merge cycles are normal while a task remains InProgress.**
5. The agent keeps its working branch, rebases onto updated `main` when useful, and submits later reviews containing only work not yet merged. Rebinding source context must not require an artificial Ready-to-Working-to-Ready cycle. Only mark the task completed when its actual work is done.

The older `PlanIntegration`, `RecordIntegration` and fixed cumulative review-baseline restrictions are existing implementation limitations, **not** requirements for the corrected workflow. Refer to [the normative spec](specs/orchestration-v2-architecture.md) until they are simplified.

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

Use V2 APIs for live writes; do not push hand-edited ledger JSON or force-push the orphan branch.

## 7. Stalls, retries and failed cutovers

- **Stalled Working session:** read `AgentInbox` and the checkpoint before invoking an explicit `ResumeAgent`. Only an explicit recovery resets the Working-segment clock. Older associated sessions should reread their assignment when a newer one is active.
- **Long turns:** Jarvis advises a soft stop after **25 minutes** and hard stop after **30 minutes** of the current timed Working segment. These are instructions, not forced cancellation; checkpoint alone does not finish the turn.
- **Uncertain write:** inspect `GetOperationStatus`, `PendingOperations` and `ReconcilePendingOperations`. A verified `Applied` receipt confirms success; `NotFoundAtVerifiedHead` does not prove a timed-out push can never arrive. Reuse the original UUID and unchanged command when retrying. If a matching pending record remains, `AcknowledgePendingOperation` requires its verified request hash.
- **Stale version / unavailable remote:** reread the canonical assignment and stop speculative writes. Never let a timer automatically reassign a task.
- **Migration failure:** stop new ledger writes and preserve its Git history and pending-intent records. The old task files remain readable historical material, **not** an automatically synchronized fallback.

A project is migrated when its canonical ledger is bound, its **relevant active tasks** are recreated, agents use V2 for all new orchestration writes, and participating clients agree on its state. Other projects can migrate independently. Updating the Jarvis client/server by itself does not complete this per-project step.

See the [architecture spec](specs/orchestration-v2-architecture.md). The older [Git-backed tasks guide](GitBackedTasks.md) describes the retired source-branch workflow.
