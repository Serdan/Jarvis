# Jarvis Orchestration V2 — remote Git task ledger

**Status:** Proposed replacement architecture, 2026-10-27
**Target:** Jarvis `design/orchestration-v2`
**Decision:** A project's existing Git remote is the sole authority for its task management state. Jarvis's server remains a small stateless authenticated MCP router; no orchestration database, Git server, external lease service, or server-owned scheduler.
**Supersedes:** The server/SQLite proposal previously contained in this file and the owner-worktree reconciliation direction in `docs/specs/task-owner-scope-remediation.md` on `fix/task-owner-scope`.

## 1. Problem and design choice

Jarvis currently keeps task HTML and mutable task JSON together in `docs/tasks` on each ordinary source branch. Every new worktree copies the current task JSON. Agents then update the same logical task in separate branches. Global assignment discovery scans all registered worktrees and tries to distinguish genuine claims from inherited copies by `owner.worktree`, timestamps and branch locality. Source integration can merge obsolete state into main. The task-management system has no single authoritative timeline.

The solution is to **move all task-management records to one dedicated Git branch per project remote**, separate from implementation branches. A branch's tree is an atomic snapshot of the entire task ledger; its commit history is the audit log.

Use the configured project's existing remote (normally `origin`) and the dedicated ref:

```text
refs/heads/jarvis-orchestration
```

Create it as an **orphan branch** with no parent from main. It is never checked out in source worktrees, merged into main, or used as a source-worktree base. Ordinary source branches remain ordinary; the ledger branch is the independent coordination history.

This is still entirely Git: no SQLite, PostgreSQL, Redis, cloud database, special server process, or additional repository service. The remote is the coordination authority, the authenticated Jarvis server routes requests, and clients perform Git operations with their existing credentials and permissions.

### Non-negotiable invariants

1. One remote ledger ref is authoritative for a project; local and source-branch copies never outrank it.
2. One task ID maps to one current task state in that ledger, no matter how many worktrees exist.
3. Task assignments/acknowledgements/checkpoints/reviews are **not** versioned on feature branches.
4. Every successful mutation produces exactly one descendant commit on the canonical ref. Commits are append-only and never force-updated or rebased.
5. The remote must accept the commit before the operation reports success.
6. Competing writes must be revalidated against the newer remote tip; never resolve with a blind overwrite.
7. Task ownership belongs to an agent; branch and worktree location are optional execution metadata.
8. An unavailable Git remote is a coordination outage: never claim that an unpushed local update succeeded.
9. Source-branch merges, renames, rebases and worktree retirement do not mutate task state.
10. All task lookups use the same ledger snapshot, independent of the currently selected source worktree.

## 2. Branch and file layout

Example project remote:

```text
refs/heads/main                     Source code
refs/heads/feature/305-world-store  Source code
refs/heads/feature/308-store-open   Source code
refs/heads/jarvis-orchestration     Canonical task ledger (orphan)
```

Tree on `jarvis-orchestration`:

```text
ledger.json                   Schema version and settings
agents.json                   Explicitly registered agent names (optional)
tasks/
  305/
    spec.html                 Canonical, human-readable task requirements
    state.json                One current state, assignments, checkpoint and review
  308/
    spec.html
    state.json
  2026-10-24-runpod-benchmark-lifecycle/
    spec.html
    state.json
migration/
  manifest.json               Optional, immutable v1 migration evidence
```

All paths use validated and escaped logical IDs, not arbitrary user-supplied paths. Numeric and date-prefixed IDs remain valid. Each task spec is Git-versioned: an assignment can store the exact spec blob ID and ledger commit SHA that it was issued against. A changed `spec.html` does not silently alter an existing assignment; reissue the assignment or explicitly adopt a newer spec revision.

The state schema can evolve independently of the source repo. Initial V2 retains the existing familiar lifecycle and assignment vocabulary to avoid an unrelated workflow rewrite:

- `open`, `active`, `parked`, `blocked`, `completed` status;
- `owner.agent`, `nextActor`, assignment revision, acknowledged revision, and instructions;
- checkpoint summary, blocker, next action, evidence, final commit;
- review and integration evidence as separate fields/events where applicable.

**Remove `owner.worktree` from authority checks.** An optional `execution` section records `sourceBranch`, `baseCommit`, `lastCommit`, and other Git source references. It is *not* the owner's identity. A local checkout path is an observation resolved through normal registered-worktree discovery and is never required to interpret a task's ownership.

Old source branches may temporarily retain historic `docs/tasks/*.json`. V2 must ignore them for all current task reads/writes. Those files are migration evidence only. A separate reviewed source cleanup can remove old JSON from main later; no automatic sweeping rewrite of worktrees.

## 3. Remote-serialized update protocol

The critical design is a **single Git ref with ordinary fast-forward-only pushes**. Do not use force, `+<refspec>`, `--force-with-lease`, or host-specific locks. A candidate commit has exactly one parent: the remote ledger SHA that the client read and validated.

### Read

1. Resolve and validate the project Git remote and ledger ref. Do not infer project identity from the current worktree basename.
2. Fetch the ledger ref into local Git object storage. Use the fetched commit SHA as the consistent snapshot identity. Prefer a command-scoped snapshot; do not trust a stale local tracking ref as authoritative.
3. Read `ledger.json` and task blobs from **that exact commit** with Git plumbing (`cat-file --batch`, tree walking or equivalent), rather than reading a filesystem checkout.
4. Build the task list, inbox, roster, review queue and pagination from that one snapshot. Return the ledger commit SHA with the result.
5. Local caching is a performance optimization only. A cached result must carry its known ledger SHA and freshness, and cannot authorize new writes without a remote refresh.

### Mutate

1. Generate a durable `operationId` and canonical `requestHash` before attempting the command.
2. Fetch the current remote ledger head `H` (or an absent-ref state during first initialization).
3. First check whether `operationId` was already committed on the ledger history. If present with the same request hash, return the original committed result; if reused with different content, reject.
4. Read only state from `H`. Validate expected task revision, current assignment generation, agent conflict, state transition and source references.
5. Build a new tree using a **temporary Git index outside ordinary source worktrees** (`GIT_INDEX_FILE`, `read-tree`, `hash-object`, `update-index`, `write-tree`). Never check out the ledger branch, change `HEAD`, touch source files, or stage a user's existing index.
6. Construct commit `C` with `git commit-tree <tree> -p H`. For an initial orphan commit, omit `-p`. Include `Jarvis-Operation-Id`, `Jarvis-Request-Hash`, actor, affected task IDs and a short audited reason in the commit message.
7. Push `C:refs/heads/jarvis-orchestration` **without force**. If it succeeds, verify the remote contains `C` and return committed state, ledger SHA and operation ID.
8. If the push is rejected as non-fast-forward, fetch the new head and **reapply the logical operation**, not a textual merge of old JSON. Recheck invariants and expected versions. Return a conflict if its preconditions no longer hold. Bound retries and report contention explicitly.
9. If push result is uncertain (timeout/disconnect), query the remote on reconnection before deciding to retry. Never report success from a merely local commit.

There is no need for a separate distributed lock. A remote's fast-forward validation serializes accepted updates to one ref. Two clients can prepare sibling commits, but only one can advance the ref; the second must fetch and retry.

**Idempotency:** Git history is the durable operation log. Exact operation-ID and request-hash trailers identify committed commands. To recover an original result after an uncertain push, locate the commit by its operation ID and read its recorded post-state and hash. A local index of operation IDs can speed this up, but must be rebuildable from Git and may not be a second authority. A routine push must never rewrite ledger ancestry; history rewriting defeats this guarantee.

**Implementation note:** Git's `commit-tree` permits constructing commits without checking out a branch. The client must use the repository's common Git object store and an isolated temporary index. Local command concurrency may be serialized for efficiency, but remote fast-forward enforcement remains the cross-process and cross-device authority.

### First-time initialization

If the ledger ref is absent, initialize a deterministic versioned schema in an orphan root commit, then attempt a normal ref-creation push. If another client initialized first, fetch and adopt the winner. Never overwrite or reset an existing ledger branch. Refuse work if `origin` is missing, unwritable, misconfigured, or the dedicated ref is protected against required normal fast-forward pushes; provide a setup diagnostic instead.

## 4. Logical task ownership and agent inbox

Task ownership is project-scoped and independent of physical worktrees. Keep existing agent labels such as `Agent 3` and stable task IDs.

A task may be:
- assigned with a fresh revision and waiting for agent acknowledgement;
- acknowledged/working, with the agent's current turn;
- waiting for reviewer, not consuming an agent execution slot;
- parked or blocked, retaining its owner and checkpoint;
- completed, retaining audit and implementation evidence.

Use the ledger itself to enforce the desired scheduling rule: at most one **agent-executable current turn** per agent by default. Parked, blocked, and reviewer-owned handoffs do not occupy that slot, so the agent may be assigned new work while retaining responsibility for older tasks.

`GetAgentAssignment(project, agent)` may remain as a convenience for the current actionable turn. Provide `GetAgentInbox(project, agent)` when returning all responsibilities: current turn, queued instructions, review-pending, parked and blocked. It is a projection over the single ledger, **not** a second inbox database. Explicit `agents.json` entries allow genuinely known-but-free agents to appear even when they have no task history; otherwise the roster can derive names from task records.

Acknowledging and resuming work always refer to the current assignment generation. Reissuing instructions creates a fresh generation, so stale receipts cannot imply Working. A reviewer returning changes queues a new agent turn without stealing an agent's already active work. State changes are checked against the latest ledger state during the remote update.

No agent can claim another agent's assignment by editing an inherited source branch, since those copies have no authority. A Git remote credential with push access to the ledger *can* write it; this is an explicitly trusted single-user developer setup, not cryptographic per-agent authorization.

## 5. Worktree and source-control integration

Worktrees are **execution contexts**, not task ledgers.

- An assignment may point to an optional source branch and base commit. The client resolves the branch against registered local worktrees when executing source operations.
- A checkout can move, disappear, or be recreated without making the task Free or changing its owner. Missing source binding is a source-operation error, not a task-state error.
- A source commit is created in the working branch as usual. The agent checkpoints its SHA in the remote ledger as a separate write.
- A code review decision is distinct from Git merge. Acceptance updates the ledger; integration verifies the target commit or documented patch equivalence and then records that evidence in the ledger.
- Push to main and ledger updates are separate Git ref operations. There is no claim of atomicity across the two refs. If source push succeeded but ledger publication fails, a retry/reconciliation operation verifies the source refs and records evidence; do not undo the accepted source push.
- Worktree retirement checks source cleanliness, ignored/untracked artifacts, jobs and integration evidence; task state does not disappear when the checkout is retired.
- Ledger operations must never silently stage, commit, merge or push changes to a source branch. Source operations must never silently write the ledger unless explicitly part of the requested workflow.

### Example

1. An orchestrator writes Task 315 to `tasks/315/{spec.html,state.json}` on `jarvis-orchestration` and pushes.
2. Agent 1 sees the assignment by fetching the ledger from any registered checkout of the project.
3. Agent 1 implements in `feature/315-...`, commits source, and checkpoints the commit SHA in the ledger.
4. The reviewer returns changes or accepts; both are ledger commits. The agent can meanwhile take another task.
5. The orchestrator integrates/pushes source, verifies the result, and records integration evidence in the ledger.
6. Creating a worktree from main copies **no live task claims**. The task record remains one remote ledger entry.
7. Removing or moving the source checkout does not affect task state.

## 6. Reads, query consistency and UI

`ListTasks`, `GetTask`, `GetAgentAssignment`, `GetAgentInbox`, `GetOrchestrationSnapshot`, and review/agent views all read a fetched **ledger commit SHA**, not worktree-local task JSON. Snapshot counts, reviewer pages, suspended/blocked/free dispositions, and conflict diagnostics therefore come from the exact same tree.

Return `snapshotCommit` on paginated views. Follow-up pages read that commit directly from local objects, preserving a stable snapshot even if newer ledger pushes have occurred. If the local object was pruned, refetch that exact reachable object from the ledger history where supported or return SnapshotUnavailable; never silently substitute a different commit.

The Agents screen can follow the main Activity screen visually. It no longer needs a worktree sweep for ownership; optional separate source-inspection calls enrich rows with branch, dirty status, integration and running job data. Failure to inspect a checkout cannot erase an assignment.

The user may deliberately request **historical branch-local task files** through normal Git/file APIs, but these are clearly labeled historical and never included in current ownership counts.

When offline, a client may display a previously fetched snapshot only with a conspicuous `stale/offline` flag. No mutation, new claim, or acknowledgement may be accepted as authoritative without a successful remote push.

## 7. Stateless server and security boundary

Keep Jarvis's existing server design:

- OAuth and MCP authentication.
- Device selection and command routing.
- Existing local permission and activity facilities.

**Do not add orchestration persistence to the server.** No new server database, scheduler process, Git mirror, remote credentials, centralized agent session state, or task cache. Command schema/routing may change, but execution and Git credentials stay with the selected client. Clients on different devices coordinate by using the same configured Git remote.

A client cannot be asked to read or write an arbitrary untrusted remote URL as a substitute for a registered project. The ledger remote is resolved from the project Git configuration (with explicit user-approved override), using Git's existing credential provider, transport policies, and local permission checks. Do not let untrusted task HTML inject Git arguments or paths. Bound per-task spec/checkpoint sizes and ledger query output.

Protect the remote ledger ref from force-pushes and deletion where the host permits, while allowing authenticated normal fast-forward updates. This branch is *not* a PR-gated feature branch. A malicious or administratively forced history rewrite is treated as a ledger integrity incident: stop normal writes, inspect remote and backups, and explicitly reconcile. Keep an independent mirror backup of the Git remote and periodically verify it can restore the ledger.

The Git remote is a required dependency for authoritative state. If its host is down, Jarvis local source editing may continue, but coordination writes wait; no invisible offline assignment queue becomes a second source of truth.

## 8. Breaking migration from protocol 3.19

We can make breaking changes. Do not maintain two mutable task authorities for convenience.

1. Prepare a remote backup and capture all existing task JSON/HTML and relevant branch heads. Dry-run import across every registered worktree and preserve conflicts, checkpoints, assignment revisions, spec blobs, and ambiguous owners in an explicit report.
2. Freeze V1 task mutations. Normal source editing may continue; task state may not be updated during cutover.
3. Group task copies by logical task ID. Prefer the authoritatively scoped copy when unambiguous; **never** choose a winner solely by latest `updatedAt`. Explicitly resolve genuinely independent histories or active conflicts.
4. Import the selected current state and HTML spec to the orphan ledger tree. Preserve prior branch-local JSON and Git commits as audit evidence, and optionally store an immutable migration manifest inside the ledger.
5. Push the initial remote ledger commit with a normal ref-creation push. Verify its SHA and task/agent/reviewer counts by fetching it from a second disposable client checkout.
6. Switch client task tools and TUI to ledger-only state. Remove V1 writes to `docs/tasks/*.json` entirely. Keep normal repository/file tools for historical inspection.
7. Bump the client/server command protocol and refresh MCP commands. The stateless server only routes the updated typed tools; it still stores no orchestration data.
8. Validate all agent inboxes, parked/blocked tasks, reviewer queues, task specs and checkpoints against the migration report, then resume task mutations.
9. Remove obsolete task-state JSON from source main in a separate reviewed cleanup; old source branches retain their historic versions unless retired normally.

Rollback **before** V2 accepts writes may restore V1 task tooling after resetting client releases; keep the ledger backup. After V2 accepts writes, never silently return to stale per-worktree JSON. Restore the ledger remote or replay committed V2 history deliberately.

## 9. Acceptance tests and failure injection

Build disposable **bare remote + two independent clone** tests, not just multiple worktrees of one checkout.

1. A main-owned Task 315 can be copied into 20 feature worktrees; exactly one current assignment exists in the remote ledger.
2. Two clients read the same ledger SHA and prepare distinct task updates. The first pushes, the second receives a non-fast-forward rejection, fetches, revalidates and retries without losing the first mutation.
3. Conflicting assignments to the same agent cannot both succeed after replay against a newer ledger head.
4. Two different task mutations can both succeed after retry and preserve both records.
5. Push succeeds but response is lost; identical `operationId` retry detects the committed operation and returns its original result without a second commit.
6. Reusing an `operationId` with different input is rejected.
7. An offline remote cannot produce a false assignment/checkpoint success. Cached reads are visibly stale.
8. Build ledger commits using a temporary index outside source worktrees: verify source `HEAD`, index, tracked files, staged changes, and untracked files are exactly unchanged.
9. Feature branch merges and worktree retirement leave every task and assignment unchanged.
10. Reviewer handoff, Parked, Blocked and Free agent states are consistent across two clients and all snapshot pages.
11. An agent with a Parked task can execute a new task; a returned review creates a queued generation without interrupting that execution.
12. A task's pinned HTML spec revision survives changes to a similarly named file in source branches.
13. Worktree move/recreate/rename does not change authoritative assignment; invalid source branch produces only a source binding diagnostic.
14. Migration retains numeric and date-prefixed IDs, checkpoints, historical evidence and genuinely conflicting copies for explicit resolution.
15. Deletion or unexpected forced rewrite of the remote ledger ref stops writes with an integrity diagnostic.
16. Restore a full mirror backup of the remote into a new bare repo, fetch from an independent clone and verify ledger SHA, task content and audit ancestry.
17. Host permissions preventing normal fast-forward pushes produce actionable setup diagnostics.
18. Large task catalogs remain bounded in result payload and paginate by exact Git snapshot SHA.
19. Multiple devices using the same remote observe the same task state without requiring the Jarvis server to keep any orchestration state.
20. All previous source Git, file, job, client permission and ordinary activity tools continue to work.

Acceptance includes cross-platform builds, full client/server test suites, a real remote race fixture, restore drills, and no force pushes.

## 10. Implementation sequence

**A — Git ledger storage:** implement `LedgerStore` in the client; fetch, read immutable snapshot, create orphan branch, isolated-index commit, fast-forward push, bounded conflict/retry handling and operation-ID recovery. Prove the cross-clone race first.

**B — Port task operations:** preserve the existing task-domain commands where practical but swap out worktree-local state reads/writes for ledger state. Migrate ownership to agent/task, with source branch references separate. Add consistent inbox/reviewer/parked views.

**C — Source integration:** attach checkpoints to source commit SHAs and use normal worktree discovery solely for execution/provenance. Update TUI and reviewer integration workflow.

**D — Migration:** dry run, conflict reconciliation, single authoritative cutover, explicit protocol bump and coordinated rollout. Remove obsolete task-state writes, not old Git history.

**E — Recovery and verification:** remote-force-update detection, restore test, integration tests with separate clones, offline semantics and audited first live release.

**Done:** an agent can query/update a single task ledger from any of the project's registered worktrees or devices; inherited source-branch JSON never duplicates or hides ownership; competing writes are serialized by remote fast-forward rules; task history is inspectable in Git; the Jarvis server remains stateless.

## 11. Explicit design limits

- Git remote availability and credentials are necessary for authoritative task reads/writes. We accept normal network latency rather than add a central service.
- A single ledger ref serializes writes across a repository. At current project sizes this favors simplicity and correctness. If high write volume becomes an observed issue, optimize fetching/tree construction before sharding authority.
- Git push and source branch push are not atomically coupled; record precise Git evidence and reconcile partial workflows.
- Agent identity strings are orchestration keys, not an independent authentication system. Git remote write rights and Jarvis's local permission policy remain the trust boundary.
- An orphan ledger branch is a Git branch, not an independently hosted service. No source worktree checks it out during normal operation.
- A read-only local cache is not a second authoritative store.
