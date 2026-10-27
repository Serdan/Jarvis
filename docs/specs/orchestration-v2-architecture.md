# Jarvis Orchestration V2 — canonical remote Git ledger

**Status:** Implementation-ready design specification (2026-10-27)
**Branch:** `design/orchestration-v2`
**Authority:** One append-only orphan ref, `refs/heads/jarvis-orchestration`, on a configured canonical Git remote per project.
**Non-goals:** Server database, server-side scheduler, separate Git repository, per-task authoritative refs, source-branch task state, backward-compatible dual writers.
**Supersedes:** The V2 draft at `6ff2c26` and the `owner.worktree` remediation approach. Review `docs/reviews/2026-10-27-orchestration-v2-spec-review.md` records the ten issues resolved by this revision.
**Source-of-truth rule:** The client conducts Git operations; the existing Jarvis server only authenticates/routes MCP requests. The remote Git ref is the **only** mutable authority for orchestration state.

## 1. Problem, architecture, and invariants

V1 stores HTML tasks and mutable JSON ownership/checkpoint metadata beside source files. New worktrees inherit that JSON; separate branches modify different copies of the same logical assignment. Orchestration then scans all worktrees, which cannot reliably distinguish independent work from stale copies and creates conflicts during source integration.

V2 moves **all live task specifications, state, assignments, reviews, agent slots, and deduplication records** to an orphan ledger branch of the project's existing Git remote. Source branches remain normal development branches, never inherit ledger files, and never need to merge the ledger. Git commits supply durable snapshots and history. **Ordinary fast-forward pushes** serialize updates from multiple clients without external locks.

Invariants:

1. Every registered project has exactly one configured authoritative **remote endpoint + project UUID + ledger ref**. A local checkout or worktree never provides an alternative authority.
2. Exactly one current task record exists for each project task ID; there may be many historical Git versions and implementation attempts.
3. Agent ownership is independent of which branch, worktree, machine, or ChatGPT conversation implements it.
4. Worktree retirement, renaming, branch creation, source integration and rebasing never modify task ownership.
5. One ledger commit atomically changes all related task/agent/operation records; a successful operation is **published** only after the remote accepts its commit.
6. No force pushes, resets, rebases, deletes, or rewrite/merge commits on the ledger ref; each normal update commit has exactly one parent, the just-fetched canonical tip.
7. Every write has a caller-stable idempotency key and per-entity optimistic versions; a rejected push causes a **logical revalidation**, never a blind file merge.
8. At most one **Working** attempt occupies an agent's execution slot. Multiple Queued, Parked, Blocked, review-pending and completed responsibilities may coexist.
9. Task implementation acceptance, source integration, source push, and worktree retirement are separately recorded facts.
10. Remote outage or unverified history yields a typed unavailable/integrity outcome, **not** a locally committed fake success or a silent Free agent.
11. Both task history and audit evidence are preserved in Git. Reading does not mutate source or ledger.
12. Project/spec/checkpoint/operation data can be exported and restored entirely from Git and client configuration; the stateless server is replaceable.

### Git layout

```text
refs/heads/main                     Ordinary source history
refs/heads/feature/305-world-store  Ordinary source history
refs/heads/jarvis-orchestration     Orphan, canonical ledger history
```

The orphan ref has an independent root commit and is **never checked out** in a normal source worktree. A normal source Git branch never includes ``tasks/`` files from the ledger. A project's remote may be GitHub, a private bare remote, or another trusted host that supports normal fast-forward push.

## 2. Identities, remote binding, and trust

### 2.1 Persisted project identity

`ledger.json`, in the ledger root commit and every later tree, contains:

```json
{
  "schemaVersion": 2,
  "projectId": "f4b75afb-5e07-4c21-97ab-695de270b61d",
  "ledgerId": "fab82e33-1667-4841-824a-e74b5ff88f4a",
  "epoch": 1,
  "nextSequence": 1,
  "createdAt": "2026-10-27T00:00:00Z",
  "status": "active",
  "settings": {
    "maxWorkingPerAgent": 1,
    "allowDirectSourcePush": true
  }
}
```

UUIDs are examples. `projectId` binds a logical project across clones and devices, not to a project name or folder. `ledgerId` distinguishes a fresh ledger history, and `epoch` changes only after **explicitly approved** ledger restoration/reinitialization. `nextSequence` increments once per successful mutation commit (including multi-task mutations); the resulting sequence orders ledger events and assignments, not wall-clock timestamps.

The local Jarvis client stores a **non-authoritative** binding under its persistent per-user configuration, separate from any source checkout:

```json
{
  "projectName": "Wayfold",
  "projectId": "...",
  "ledgerId": "...",
  "epoch": 1,
  "remoteName": "origin",
  "approvedFetchUrl": "git@host:owner/Wayfold.git",
  "approvedPushUrl": "git@host:owner/Wayfold.git",
  "ledgerRef": "refs/heads/jarvis-orchestration",
  "highestTrustedHead": "<git-oid>",
  "initialized": true
}
```

Never store credentials in this file. Multiple local clones can bind to the same canonical remote/project ID, but different URL/SSH/HTTPS spellings are **not** assumed equivalent. A provider repository identifier can be an additional verified hint; it is not required for ordinary Git remotes. Changes to fetch URL, push URL, remote name, project/ledger UUID or epoch require an explicit **RebindLedgerRemote** action, with old/new values and verification shown to the user. Never guess by source branch, matching name, or newest timestamp.

Before each authoritative read/write, verify the configured remote fetch/push endpoints still match approved values and that the fetched `ledger.json` matches pinned identity. Do not accept a pushURL different from the approved writable endpoint without explicit consent. A client bound to `A` must refuse a forked remote `B` even if it serves a valid ledger ref with the same task IDs. A fresh clone joins through **explicit Trust/Bind Existing Ledger** and verifies a supplied expected project ID or trust anchor. No hard guarantee that independently configured clients use the same remote exists without this shared binding; the setup workflow must enforce and display it.

### 2.2 Initialization and history trust

**InitializeLedger** is a separate explicit command, never an implicit side effect of GetTask/AssignTask. It verifies source Git identity, remote access, deliberate repository visibility consent, and that the target ledger ref is absent. It creates an orphan root tree with initial `ledger.json` and optional agent registry, then pushes without forcing. If someone wins the initial-ref creation race, fetch and verify their project/ledger IDs; adopt only if explicitly the same intended initialization, otherwise stop.

For every bound client, pin `highestTrustedHead` after a verified remote fast-forward and check `git merge-base --is-ancestor <trusted> <observed>` before accepting a new head. A non-descendant, absent ref on an already-initialized project, unexpected project ID, or changed epoch is a **LedgerIntegrityError**. No automatic reset/recreation. Handle intentional verified restore/remote transfer through RebindLedgerRemote, with an audit event and backup anchor.

A brand-new clone cannot independently prove the remote's complete historical integrity. It must trust a current protected remote tip or an independently supplied checkpoint/backup. Host branch rules should prohibit force pushes and deletion but **permit direct fast-forward pushes** to this ledger branch (no mandatory PR review). Check signed-commit requirements during initialization: if the host enforces them, either produce correctly signed `commit-tree` commits or refuse with a setup error; never circumvent protection.

**Privacy:** an orphan branch on a *public* Git repository is public. Initial setup must explicitly confirm exposure of task descriptions, instructions, checkpoints and retained history; warn if remote visibility cannot be verified. Refuse tokens, secrets, private keys and credentials in persisted task fields by policy/validation; history cannot be made confidential simply by deleting a later blob. For confidential projects use a private remote.

## 3. Ledger tree and state ownership

Every snapshot is a Git commit with a tree like:

```text
ledger.json                          Project identity, schema, epoch, sequence
agents/
  Agent%203.json                     Registered identity, execution slot, version
tasks/
  305/
    spec.html                        Current spec, Git history retains older blobs
    task.json                        Task lifecycle, current attempt, version
    attempts/
      <uuid>.json                    One implementation attempt and current handoff
      <uuid>.checkpoints/
        <sequence>.json              Immutable checkpoint evidence
  315/
    spec.html
    task.json
    attempts/...
ops/
  9d/
    9d8f...uuid.json                 Immutable deduplication/result record
migration/
  manifest.json                      Immutable selected V1 source evidence
```

Names use strict percent-encoded safe IDs; decoded strings can never change the path root, introduce separators/traversal or collide after case-folding. The initial V2 uses **one primary nonterminal attempt per task**. Old attempts remain as separate immutable/auditable records. Creating another live attempt requires an explicit future multi-attempt feature.

`agents/<id>.json` is the **only** slot materialization, e.g. `{"agentId":"Agent 3","activeAttemptId":null,"version":12}`. It stores no redundant assignment queue. The queue is a deterministic projection of tasks/attempts in the same ledger tree; the full mutation validates that slot and attempt views agree. Registration of an agent does not establish a ChatGPT session or authenticate that label.

Task lifecycle values: **Open → InProgress → Implemented → Integrated → Closed**, plus **Cancelled**; progression can be reopened through explicit audited commands. `Implemented` means accepted by a reviewer, not merged. `Integrated` requires recorded source evidence; `Closed` is an explicit final administrative state. Documentation-only tasks may close via explicit approved non-source criteria and never claim a source merge.

Attempt phase values: **Queued, Acknowledged, Working, ReadyForReview, Parked, Blocked, Accepted, Superseded, Abandoned**. Attempts also include `ownerAgentId`, `assignmentGeneration` (monotonic per attempt), `currentSpecBlobOid`, `issuedSequence`, `priority`, `reviewerId`, `checkpointId`, `resumeTarget` and optional `source` binding (remote/ref/base/head/last commit). **No `owner.worktree`.** Reviewer decisions and verified source integrations are append-only evidence within attempt records/history; they must not be confused with task/attempt phases.

An assignment is **one immutable generation of instructions**, stored in the attempt's generation list or separate task-local blob, including its content, generation number, issued sequence, spec blob OID, expected base commit, actor and receipt fields. A stale generation never becomes current again. Reissue creates a new generation, retaining old instructions/receipts for history. Source checkout locations/paths are local observations, never owner keys.

### Normative JSON minimum

```json
{
  "task": {
    "id": "315",
    "version": 4,
    "lifecycle": "InProgress",
    "currentAttemptId": "attempt-uuid",
    "title": "Improve orchestration"
  },
  "attempt": {
    "attemptId": "attempt-uuid",
    "version": 7,
    "ownerAgentId": "Agent 3",
    "phase": "Queued",
    "assignmentGeneration": 2,
    "issuedSequence": 24,
    "priority": 0,
    "specBlobOid": "<oid>",
    "source": {
      "remote": "origin",
      "sourceBranch": "refs/heads/feature/315",
      "baseCommit": "<oid>",
      "lastCommit": null
    }
  },
  "agent": {
    "agentId": "Agent 3",
    "version": 12,
    "activeAttemptId": null
  }
}
```

These illustrate fields from **three separate files**, not a single combined JSON response. Implement typed records with schema validation, explicit parsers and deterministic UTF-8 serialization; reject unknown lifecycle/phase values. Non-authoritative optional extension data may be preserved but must not bypass validations.

## 4. Scheduling contract and state machine

### Capacity and queues

- Each agent has one execution slot by default. **Only Working occupies it.** A queued assignment is runnable but does not reserve the slot; Acknowledged also does not occupy it.
- Agent workload may include many Queued, Acknowledged, Parked, Blocked, ReadyForReview or Accepted attempts. No task automatically preempts a current Working attempt.
- `GetAgentInbox(projectId, agentId)` returns `working` (0..1), `runnableQueue`, `pendingReview`, `parked`, `blocked`, `acceptedPendingIntegration`, plus `slotAvailable`, `unassigned` and optional `presence` (only with real external evidence). `slotAvailable` means the execution slot is empty; **unassigned** means no outstanding tasks, including parked/blocked/review items. These must never be confused.
- Runnable queue ordering is deterministic: **priority descending, issuedSequence ascending, taskId ordinal, attemptId ordinal**. Queued and Acknowledged are runnable; an acknowledged item still requires Begin to acquire the execution slot. ChangesRequested is a reviewer decision, not a runnable phase: it creates a new Queued generation.
- Reviewer queue is a separate projection over ReadyForReview attempts, ordered by submission sequence and task ID; reviewer work does not consume the implementation agent's slot.
- `GetAgentAssignment` (if retained at all) returns exactly the current Working attempt or first runnable item with an explicit `selectionKind`, never an arbitrary parked/review item. `GetAgentInbox` is canonical.
- Agent identity is a stable label within a project, not a credential or a live ChatGPT session. No automatic reassignment based only on staleness; interruption is recorded/acted upon explicitly by an orchestrator.

### Transitions

All mutations require a stable operation ID, expected version(s), a valid actor, and the relevant current generation. Every transition below is a **single ledger commit**, including agent-slot changes and any immutable evidence. A transition not shown is invalid unless a separate explicit administrative repair action is defined.

| Command | Preconditions | Atomic effects |
|---|---|---|
| CreateTask | Unique task ID and valid spec | Open task, version 1, spec blob |
| ReviseTaskSpec | Existing task and matching version | New spec blob; pin existing attempts to old spec; no automatic handoff |
| AssignTask | Open or explicitly reopened task, agent registered, no other primary live attempt | New attempt (or explicitly selected existing one), InProgress task, Queued assignment generation, owner and issuance sequence |
| AcknowledgeAssignment | Owner, current generation, Queued | Record receipt, phase Acknowledged; do not occupy slot |
| BeginAttempt | Owner, current generation, Queued/Acknowledged; slot empty | Occupy agent slot; Working phase; record begunAt; acknowledge implicitly **only if caller supplies the exact generation** |
| CheckpointAttempt | Owner, current generation, Working | Append immutable checkpoint, advance attempt version; keep phase/slot |
| SubmitForReview | Working, valid checkpoint and current generation | ReadyForReview; release slot; reviewer queue record through phase |
| RequestChanges | ReadyForReview and authorized reviewer | Persist review result; new Queued generation (new instructions and issuance sequence); same agent ownership; slot remains free |
| AcceptAttempt | ReadyForReview and authorized reviewer | Append acceptance evidence; phase Accepted; task Implemented |
| ParkAttempt | Queued/Acknowledged/Working/ReadyForReview with reason/condition | Parked; release slot if held; record resumeTarget (agent or reviewer), checkpoint preserved |
| BlockAttempt | Queued/Acknowledged/Working/ReadyForReview with concrete blocker | Blocked; release slot if held; record resumeTarget and dependency, checkpoint preserved |
| ResumeAttempt | Parked/Blocked, condition resolved or explicit override | New Queued generation for agent-target; or ReadyForReview for reviewer-target; no automatic slot occupation |
| PlanIntegration | Accepted and code requires integration | Persist target ref, source commit, expected target head and actionId; pending source action |
| RecordIntegration | Verified source evidence and current target remote ref | Persist verified integration; task Integrated; no implicit push |
| CloseTask | Integrated or explicitly approved non-source closure policy | Closed; retain all attempts, evidence and audit |
| CancelTask / AbandonAttempt | Explicit authorized cancellation and expected version | Terminal state; remove queue/slot membership; retain history |
| BindAttemptSource | Registered project workspace, verified Git source reference | Update source observation only; never change agent owner or phase |

`BeginAttempt` is the only slot-acquisition command. It can select the top runnable assignment or a specific queued attempt/generation. Attempting to start a second attempt for the same agent fails `AgentBusy`. A reviewer returning Task 315 while Agent 3 works on Task 316 creates a new Queued generation for 315; it does not interrupt 316 or mark 315 Working.

An agent disappearing mid-turn leaves the slot occupied and the attempt marked Working until explicitly paused, blocked or reclaimed through a reviewer/orchestrator action with an audit reason. Checkpoints require Working so an agent cannot bypass the capacity rule by progressing an Acknowledged item without beginning it. Presence is not inferred from Git; no lease expiry or automatic stealing. Admin release must atomically revoke the old generation and free the slot, preventing a stale client from continuing authoritatively. Two simultaneous Begin calls are checked on the same remote snapshot and only one can publish a slot change.

## 5. Git write transactions, retries, and idempotency

### 5.1 Caller-owned operation identity

Every mutating MCP tool **requires** a caller-stable UUID `operationId`. It is created once per logical intent, *before* the first MCP invocation, and reused unchanged on every retry, including after a lost response or chat reset. A changed intent must use a new ID.

For a TUI-triggered operation, the client generates a key and writes its pending request to local persistent configuration **before** starting Git/network work; it retries or resolves that exact request after a crash. For orchestrator/agent calls, the agent/orchestrator supplies and retains the UUID in its task/action checkpoint until acknowledged. The client cannot manufacture exactly-once behavior across independent tool calls if the caller supplies a fresh ID. This limitation must be explicit in tool descriptions.

The canonical `requestHash` is SHA-256 over **schema-versioned, deterministic UTF-8 serialization** of (projectId, operation type, validated input, target IDs, expected entity versions, actor, and operationId), excluding transient transport metadata. Formatting or JSON property order cannot produce different hashes for the same logical request; different validated input cannot collide without a SHA-256 collision.

Each successful mutation writes an immutable operation record in the **same commit** as the changed task/agent files:

```text
ops/<first-two-hex-characters>/<operation-uuid>.json
```

```json
{
  "operationId": "uuid",
  "requestHash": "sha256",
  "sequence": 25,
  "type": "CheckpointAttempt",
  "actor": "Agent 3",
  "taskIds": ["315"],
  "result": {
    "attemptVersion": 8,
    "checkpointId": "checkpoint-uuid",
    "phase": "Working"
  }
}
```

The operation record is the O(path) idempotency index, not a second authority. On retry, read this path from the just-fetched remote tree: same hash returns the **original durable logical result**, with `alreadyApplied=true` and current fetched ledger SHA; different hash fails `OperationIdReused`. The original mutation commit's SHA is not stored inside its own tree (self-reference is impossible); if needed for audit, obtain it from Git history using the operation trailer, never invent it. The response must distinguish `currentLedgerHead` from `originatingCommit` when known.

Operation records are retained for the entire live ledger epoch; do not prune them while a retry might occur. Commit trailers (`Jarvis-Operation-Id`, `Jarvis-Request-Hash`, actor and entity IDs) provide readable history but are not the primary deduplication lookup. Reattempted operations do not append new commits.

### 5.2 Consistent fetch and write algorithm

**Read**:

1. Select an approved canonical remote and `refs/heads/jarvis-orchestration`; reject invalid/unbound configs.
2. Fetch from that remote to an isolated staging ref under `refs/jarvis/fetch/<nonce>` (not shared `FETCH_HEAD`); capture the returned commit OID and verify project/ledger/epoch identity and ancestry against `highestTrustedHead`. Only then promote the verified commit to the trusted cache ref and clean up the staging ref. A fetch rejected by the remote or local ref protection is not interpreted as proof of remote rewrite without further inspection.
3. Read all requested paths from that **one exact OID**, using Git object plumbing without checkout.
4. Return `snapshotCommit`, freshness metadata and bounded results. Reads never write task files or source refs.

**Mutate**:

1. Validate explicit `operationId` and prepared request. Fetch current remote head `H` and verify identity + ancestry against the local trusted anchor; absent head on a bound project is an error.
2. Check `ops/.../operationId.json` for deduplication, **before** checking expected state versions.
3. Validate every affected task version/assignment generation, agent slot, transition, dependencies, source evidence and project constraints from `H`. Unrelated task changes on a newer tip do not invalidate an operation unless its own expected preconditions or global invariants changed.
4. Create a fresh secure temporary index *outside source checkout files*, keyed by invocation. Populate from `H^{tree}` and replace only intended paths via `hash-object`, `update-index` and `write-tree`. Preserve all unrelated tree paths verbatim; never check out the orphan branch, change a source HEAD, or stage an existing user index.
5. Build one commit via `commit-tree` with parent `H`, deterministic record contents and Git audit trailers; do not use force, `--force-with-lease`, or ledger merges.
6. Push `C:refs/heads/jarvis-orchestration` without force. Re-fetch/read current remote head `R` and require `C` to be an ancestor of `R` for **confirmed success**; exact tip equality is not required because another writer may have advanced it.
7. If the push was rejected as non-fast-forward, fetch newer `H2`, check operation record again, then revalidate and rebuild the **logical operation** with parent `H2`. Never blindly merge two old task JSON files or push the rejected candidate verbatim.
8. If push timed out, transport failed, or the confirmation fetch is unavailable, return `OutcomeUnknown(operationId)`. On a later retry, **fetch and inspect the operation index first**. Never claim failure merely because one push invocation lost its response; never claim success until the remote is verified.
9. Retry on non-fast-forward contention at most **8 attempts** per invocation with jittered exponential backoff (initial 50 ms, cap 800 ms); report `LedgerContended` with operation ID and latest observed head. Validation conflicts are not retried automatically.

Local per-repo serialization is permitted to reduce collisions among commands in one client process but is not a correctness requirement. Across clients, only an accepted fast-forward update of the **one** authoritative ref decides the order of operations.

**Read-only calls** never create commits. Every successfully accepted **mutating** command, even one whose requested state already holds, writes an operation result record and a descendant ledger commit so its outcome is durably deduplicated. A retry that encounters its operation record never writes again. Rejecting validation, stale-version, unauthorized, and transport-unavailable requests does not create a commit. Source Git merge/push steps are **separate** actions and cannot be made atomic with the ledger ref.

### 5.3 Remote outages and restored connectivity

If the canonical remote is unreachable, return `RemoteUnavailable`/`OutcomeUnknown` rather than a successful mutation. Local cached snapshots may be shown only as **STALE** with last confirmed ledger commit and fetch timestamp. Do not queue authoritative writes offline or allow them to mutate local branch task JSON.

If the remote was accepted but the client crashed before persisting the trusted anchor, retry fetches the current branch and checks its ID and the existing operation index. A verified operation result is recoverable from Git; the local config is a trust cache, never the primary state source.

### 5.4 Snapshot object retention

Maintain private local `refs/jarvis/ledger-cache/<projectId>` pointing to the **last verified** fetched ledger head. Maintain temporary `refs/jarvis/snapshots/<token>` for active paginated snapshot commits until their 30-minute expiry, then clean them up. These refs keep Git objects reachable for GC; they are caches only, never writable authority.

For pagination, the client returns a **portable, self-contained continuation token** containing **projectId, ledgerId, snapshotCommit, canonical query/filter hash, offset and expiry**. It contains no credentials, private paths, or write authority. Every client validates the token fields, size, expiry, query equality, project/ledger identity, and that the snapshot commit is an **ancestor of the currently verified canonical ledger head**; it may then read that exact snapshot. No shared server signing key or client-local token database is required, because the token authorizes only a bounded read of an already-authorized project snapshot. A client may optionally MAC local-only tokens, but that must not be required for cross-device continuation. Every page reads the exact pinned commit, not whichever remote head happens to be current. When a different client receives the token, it fetches the canonical ledger ancestry if the exact object is absent. If that is unavailable, return `SnapshotUnavailable`, never substitute a new head. Bound page size to 100 records and response payload to 1 MiB, with additional cursors for task history.

Shallow/partial clone support must be explicit: clients fetch the complete ledger ancestry or fail with `LedgerHistoryUnavailable` when required validation/audit objects are missing. Source history may remain shallow independently. Do not infer trust merely from `FETCH_HEAD`. Any fresh clone must pass explicit remote binding/trust checks before writing.

## 6. Task/spec authoring and source bindings


### Specification editing

Task specs live canonically at `tasks/<id>/spec.html` in the ledger tree. Existing source `docs/tasks/*.html` may remain in source history but have **no automatic two-way synchronization**. Provide:

- **CreateTask**: create validated HTML + task metadata in one ledger mutation. HTML and title are supplied explicitly; the task ID can be a caller-supplied legacy numeric/date ID or a collision-resistant generated ID.
- **GetTask**: return current task, currently pinned spec, lifecycle, attempts, and exact snapshot commit.
- **ReviseTaskSpec**: write new HTML after expected task version/spec blob verification. Existing assignments retain their pinned prior blob until an explicit reissue or adoption operation.
- **ImportTaskSpec**: read a file from a registered worktree using existing path/symlink/security checks, preserve source ref/path/blob/commit as provenance and import the *content* into the ledger. The source file must not change during import: check its hash again before publishing; caller may supply an expected Git blob OID.
- **DiffTaskSpec**: compare two pinned blob OIDs from ledger history without a filesystem checkout.
- **GetTaskHistory**: page ledger commits affecting task paths, including immutable checkpoint/generation history, rather than scanning source branch HTML/JSON. Historical V1 file history remains accessible via normal Git tooling.
- **ExportTaskSpec**: optionally write a clearly marked, **read-only-authority** HTML copy to a chosen worktree. Editing this copy never edits the live ledger until explicitly imported/revised.

A source HTML file may be a convenient draft, but only the ledger's committed spec blob and any explicitly selected assignment generation are authoritative. A pending assignment cannot silently adopt spec modifications. Keep HTML safe for display (no active script execution or untrusted instructions promoted to system policy).

### Source execution and worktree binding

An attempt records `source.remote`, fully-qualified branch ref, base commit, submitted source commits and optional implementation commit. The local client resolves a corresponding checkout using **registered Git worktrees**; checkout path, name and HEAD are observations, not task-owner identity. A source branch can be renamed, worktree moved, destroyed or recreated without changing the task/agent.

`BindAttemptSource` explicitly verifies the registered source repository and intended source ref/base SHA. If the source ref cannot be found, mark the binding unavailable in query results, not the agent Free. Detached/uncommitted worktrees require explicit binding; never infer a branch from an agent label or task ID. Git source operations remain on the selected device/client with existing permissions and command approval.

Existing V1 task JSON must not be imported as executable current state by ordinary source reads. If a new source branch accidentally inherits V1 task JSON, current task APIs ignore it. The worktree status/retirement tooling may still report it as historical files without granting those copies authority.

## 7. Source integration and cross-ref recovery

**Ledger ref updates and source branch pushes are separate Git operations.** A transaction spanning both refs does not exist. Model integration as an explicit, auditable workflow:

1. Reviewer **AcceptAttempt** records acceptance and the source commit(s) being approved. This does not assert that code has merged or been pushed.
2. Orchestrator calls **PlanIntegration**, recording an integration action ID, source branch/ref, base/head OIDs, target ref, canonical source remote and expected target head in the ledger. The attempt is Accepted/PendingSourceAction.
3. Client performs its normal explicit Git merge/rebase or equivalent source action and pushes the target as instructed by the orchestrator. **Neither the ledger nor the server performs source Git edits implicitly.**
4. Client performs a fresh **read-only remote Git verification**: target ref and commit SHA; ancestry of accepted commits, or a separately documented patch-equivalence proof (including source and target OIDs). The verifier records which remote, timestamp, method and exact target head were inspected. A merge present only in a local checkout remains **PendingPush**, not remote-integrated.
5. **RecordIntegration** publishes verified **remote-target** evidence to the ledger with the original integration action ID. Only then does the task become Integrated. If source push is pending, preserve an explicit **PendingPush** integration substate and do not mark the task Integrated until the remote ref is verified.
6. Orchestrator **CloseTask** separately when closure policy is satisfied. Worktree cleanup is its own checked Git operation with no task-state deletion.

Integration progress is a separate typed substate on the Accepted attempt (`NotPlanned`, `PendingSourceAction`, `PendingPush`, `PendingVerification`, `VerifiedRemote`, `Recorded`); it does not overload `attempt.phase` or task lifecycle. Local-only push status cannot be inferred from a Git commit in the ledger.

If step 3 succeeded but the client lost its response, do **not** blindly repeat the merge/push. Re-read the source target and recognize whether the intended commit or patch is already integrated. If the source push succeeded but step 5 fails due to ledger outage, the ledger remains PendingSourceAction/Verification. On retry, verify the Git remote again and publish only the missing ledger evidence using the *same operation ID*.

If a remote target has moved further, accept only if current remote evidence still demonstrates the required integration. If the target history was rewritten, require explicit operator reconciliation. No operation infers accepted/integrated status from old `status=completed` JSON, and no source push automatically marks a task Closed.

## 8. Client/server tools, security and usability

### Native tool surface and versioning

Use a **breaking protocol 4.0** for orchestration tools; source Git, local process, filesystem, jobs, client activity, and existing device routing remain functional. The stateless server only forwards/validates typed commands to the connected client. It stores no orchestration state, Git mirror, task cache, assignments, leases or queue.

Canonical tasks/orchestration operations:

```text
InitializeLedger / BindExistingLedger / RebindLedgerRemote / GetLedgerHealth
ListTasks / GetTask / GetAttempt / GetTaskHistory / CreateTask / ReviseTaskSpec / ImportTaskSpec
DiffTaskSpec / ExportTaskSpec
RegisterAgent / GetAgentRoster / GetAgentInbox / GetOrchestrationSnapshot / GetOperationStatus
AssignTask / AcknowledgeAssignment / BeginAttempt / CheckpointAttempt
SubmitForReview / RequestChanges / AcceptAttempt
ParkAttempt / BlockAttempt / ResumeAttempt / CancelTask
BindAttemptSource / PlanIntegration / RecordIntegration / CloseTask
InspectLegacyTaskMigration / ApplyLegacyTaskMigration
ExportLedgerArchive / VerifyLedgerBackup
```

Every state-changing command includes required `operationId`, relevant expected entity versions and generation, and a declared actor. Model the project selection as an explicit local registered project plus its pinned ledger identity; a `projectName` containing `::worktree` can be accepted for convenient source context but **never changes** which ledger provides task state.

Make `GetAgentInbox` the canonical agent entrypoint. It returns all runnable, working, parked, blocked, and review-related work by agent name (no need for a conversation ID or user session metadata). Legacy singular `GetAgentAssignment` can be removed, or retained as a **read-only convenience projection** with unambiguous `selectionKind`; do not perpetuate the V1 one-task-per-agent invariant.

### Representative native request and response contracts

A mutation's logical wire request contains the following validated fields (individual typed MCP tools may flatten this envelope in their public arguments):

```json
{
  "projectName": "Wayfold",
  "operationId": "00000000-0000-4000-8000-000000000315",
  "actor": { "kind": "agent", "id": "Agent 3" },
  "command": "CheckpointAttempt",
  "expected": {
    "taskVersion": 4,
    "attemptVersion": 7,
    "assignmentGeneration": 2
  },
  "payload": {
    "taskId": "315",
    "attemptId": "attempt-uuid",
    "summary": "Implementation tests passed",
    "sourceCommit": "<git-oid>",
    "evidence": ["Test suite passed"]
  }
}
```

A successful response carries the exact durable logical result; `ledgerHead` refers to the **verified current** remote head and may be newer than the originating commit:

```json
{
  "operationId": "00000000-0000-4000-8000-000000000315",
  "disposition": "Applied",
  "projectId": "<uuid>",
  "ledgerHead": "<verified-oid>",
  "ledgerSequence": 25,
  "result": {
    "taskId": "315",
    "attemptId": "attempt-uuid",
    "attemptVersion": 8,
    "checkpointId": "<uuid>",
    "phase": "Working"
  }
}
```

`GetOperationStatus(projectName, operationId)` fetches the canonical head and returns `Applied` plus the stored result, or `NotFoundAtVerifiedHead` (which is **not** proof that an earlier timed-out push will never arrive), or a typed remote/integrity error. A repeated mutation with an existing operation record yields `AlreadyApplied` and the **same logical result/ledger sequence** without a new commit. Uncertain transport returns `OutcomeUnknown` with original operationId and last verified ledger head, never a fabricated result.

A read-only `GetAgentInbox` response has this conceptual shape:

```json
{
  "projectId": "<uuid>",
  "snapshotCommit": "<verified-oid>",
  "agentId": "Agent 3",
  "slot": { "available": false, "workingAttemptId": "attempt-316" },
  "runnable": [
    { "taskId": "315", "attemptId": "attempt-315", "phase": "Queued",
      "assignmentGeneration": 3, "priority": 0, "issuedSequence": 24 }
  ],
  "parked": [],
  "blocked": [],
  "readyForReview": [],
  "acceptedPendingIntegration": [],
  "unassigned": false,
  "presence": "Unknown"
}
```

The inbox shows responsibility even when the slot is occupied. Full instructions and checkpoint history may require `GetTask`/`GetAttempt` to keep inbox payload bounded; reading an inbox never implicitly acknowledges or begins work. The server never uses agent name, chat/session ID, or selected device to infer presence.

### Fail closed on old clients at cutover

The server router is stateless but knows **supported protocol major versions**. Once protocol 4.0 is activated, explicitly reject all V1 (3.x) task mutation methods, irrespective of connected device or capabilities. The 4.0 server and 4.0 client must agree on task-command schema before execution; a stale client cannot silently commit obsolete `docs/tasks/*.json` via Jarvis task tools. Read-only Git file operations continue to expose historical files; manual direct Git edits to old JSON remain possible but have no live effect.

For a sole-user installation, a global 4.0 task-command cutover is acceptable and simpler than a per-project migration switch that would require server persistence. Freeze V1 mutations, migrate every actively orchestrated project, then activate 4.0 tools; old clients must be updated/reconnected before task work resumes. Incompatible task-write requests return `ProtocolUpgradeRequired` with the supported version, not a best-effort compatibility fallback.

### Permission, trust, and privacy

- OAuth/MCP authentication and existing Jarvis device selection stay unchanged. Agent labels are **not credentials**. A user authorized to perform Git writes through the connected client can impersonate a logical agent; no cryptographic per-agent authorization is claimed.
- Task creation/checkpointing, remote Git fetch and remote push follow the established permission policy; push requires version-control/network permission and any configured local approval. Do not treat task metadata or HTML as executable instructions for Jarvis itself.
- The remote must be an approved registered project's canonical Git destination, not a caller-supplied arbitrary URL. Reject unapproved URL changes, transport rewrites, symlinks, traversal, invalid Git paths and unsafe Git option strings. Use argument-array invocation and known command verbs, not shell interpolation.
- First-time initialization requires explicit confirmation that task descriptions/checkpoints will be pushed with repository visibility. A private ledger branch **does not exist** inside a public Git repository.
- Limit task spec HTML to **512 KiB**, one checkpoint's serialized evidence to **64 KiB**, operation request metadata to **16 KiB**, and normal task list/snapshot responses to **1 MiB** with pagination. Reject maliciously oversized records before Git object construction. Do not duplicate credentials or secret-bearing local paths in ledger audit records.
- Audit messages record actor label, operation type, affected IDs and operation UUID. Git remote access permissions are the real trust boundary; Git commits provide an audit history, not per-agent authentication or a tamper-proof record against repository administrators.

### User experience and operating semantics

The Agents screen shows each registered agent's **slot availability** separately from their **responsibilities**. A row can be "Available; 2 Parked, 1 Review" without being labeled unassigned. Explicit Busy means Working in the ledger, **not** evidence that a ChatGPT conversation is still alive. If a source checkout is unavailable, show `Source unavailable` without deleting the task.

UI queries return `snapshotCommit` and fetched timestamp. A failed fetch can show a clearly marked stale cached roster; it cannot present it as current. Task/spec revisions are reviewable through ledger history and DiffTaskSpec; legacy source files remain ordinary historical documents.

## 9. Migration, backup, and recovery

### Backup and remote protection

Before a live ledger is initialized or migrated, verify remote fast-forward push permissions and, where supported, protect the ledger branch against force-push and deletion. Host-specific protection setup can be documented separately; do not assume that protection exists on a generic bare Git remote. Require an explicit operator acknowledgement when reliable protection cannot be configured.

Produce an independent **Git mirror backup** of the complete repository including the orphan ledger ref and a manifest of project ID, ledger ID, epoch and trusted tip. Keep it in a protected destination outside the source checkout and canonical Git remote, with tested restore. Because Git retains all ledger history, deleting a task record later does not redact it from the remote backup or committed history. Document the privacy consequence.

### Breaking cutover from v1 (3.19)

1. **Freeze** V1 task mutation tools and pause active agent assignments. Capture remote refs, clone/mirror backup, client versions and all registered worktrees. This is a global task-command pause, not a ban on ordinary source editing.
2. Run `InspectLegacyTaskMigration`: enumerate HTML and JSON in every registered worktree, normalize numeric/date-prefixed IDs, record file/commit hashes, assignment revisions, owners, nextActor, checkpoints and completion evidence. Preserve malformed/ambiguous source records as diagnostics, not invisible exclusions.
3. Group candidate copies by logical task ID. Prefer an explicitly authoritative old owner.worktree copy only when unambiguous, then solicit explicit resolution for competing active claims, divergent checkpoints, legacy unscoped ownership or missing owners. **Never** pick a winner solely by newest updatedAt.
4. Produce a deterministic migration **manifest** (source path/ref/commit, selected state, rejected alternates, checksum and decision rationale), with a dry-run summary of pending agent inboxes, parked/blocked tasks, reviewers and unresolved conflicts.
5. Convert selected live records to V2 task lifecycle + attempt phases + agent slot state. Preserve assignment generation, acknowledged receipt and checkpoint evidence. If old Working state is ambiguous, park it with a migration reason rather than invent a live slot. Every task spec HTML is imported into the ledger with its source blob provenance.
6. Explicitly approve the report. Initialize the orphan ledger ref in the canonical remote, including migration manifest and complete selected state in **one root commit**. Verify it by fetching from an independent disposable second clone and checking task IDs, content hashes, inbox/roster counts and the root commit SHA. Do not overwrite an existing unrelated ledger ref.
7. Deploy and activate protocol 4.0 task commands; old 3.x task mutations are rejected. Switch all clients, task tools, TUI and agents to ledger-only reads/writes. Historical source task JSON is **never** interpreted as current truth.
8. Perform an end-to-end agent handoff, reviewer return, Parked/Blocked lookup, and integration evidence test on a disposable task, then resume normal activity.
9. Remove obsolete `docs/tasks/*.json` from source main **only through a separate reviewed code/documentation cleanup**. Old branches retain their history. Worktree retirement no longer has to preserve task-only source commits.

**Rollback:** before accepting any V2 writes, V1 tooling may be restored from the migration backup after re-freezing activity, subject to explicit approval. After the first accepted V2 mutation, V1 copies are stale; do **not** silently revert to them. Recover the Git remote ledger or replay its verified history instead. An explicit restoration increments ledger epoch after authoritative reconciliation and rebinds trusted clients.

### Server outage, Git outage, forced rewrite

- Jarvis server outage: remote ledger remains intact; local source Git work can continue. Normal MCP routing may be unavailable, but server restart cannot lose the canonical task state.
- Git remote outage: local source editing may continue; authoritative task updates return `RemoteUnavailable` or `OutcomeUnknown`. No background queue becomes a second writer.
- Remote rewrite: detect non-descendant head or ref deletion relative to a trusted client anchor. Stop writes and require backup/remote reconciliation. A never-before-bound clone cannot independently detect an older rewrite, which is a documented limitation.
- Restored remote: verify full ancestry, manifest and project IDs from a trusted backup. Explicitly adopt a new epoch if the restored history differs. Do not automatically overwrite clients' trusted heads.

## 10. Failure codes, limits, and contention objectives

Typed errors must be distinguishable and preserve the requested operation ID where present:

| Error | Semantics | Caller behavior |
|---|---|---|
| `RemoteUnavailable` | Cannot contact canonical remote | Retry same operation ID after connectivity returns |
| `OutcomeUnknown` | Push may have succeeded; confirmation unavailable | Retry same ID and inspect remote operation index |
| `LedgerContended` | Eight bounded non-FF retries exhausted | Retry same ID with backoff or report contention |
| `LedgerIntegrityError` | Ref deletion/rewrite, epoch or project identity mismatch | Stop; explicit repair/rebind only |
| `LedgerHistoryUnavailable` | Required reachable Git objects missing | Fetch full history/repair; never silently switch snapshot |
| `SnapshotUnavailable` | Requested pinned snapshot expired/pruned | Start a new read snapshot explicitly |
| `OperationIdReused` | Same ID, different canonical request hash | Reject, new operation ID for a new intent |
| `StaleEntityVersion` | Affected task or generation changed | Re-read and obtain a new explicit operator decision |
| `InvalidTransition` | Illegal attempt/lifecycle change | Report allowed transitions |
| `AgentBusy` | Another Working attempt holds the slot | Queue work; do not preempt |
| `SourceUnavailable` | Bound branch/worktree cannot be verified | Repair source binding; preserve ownership |
| `ProtocolUpgradeRequired` | Task writer uses obsolete protocol | Upgrade/reconnect client |

A remote's non-FF rejection is an **internal retry event**, not a domain conflict, until logical revalidation detects one. Distinguish authentication/permission failures from transient connectivity. Failed validation must not create ledger commits.

Recommended contention/performance gates for a disposable **local bare Git remote** with 10 independent clients and 10 simultaneous distinct checkpoint writes:
- All writes ultimately succeed or return a typed contention error; no lost updates or duplicate operation IDs.
- At least 95% complete within **5 seconds** on a standard development host without artificial network delays.
- Bounded max **8** retry attempts per operation; jitter/backoff does not create an indefinite retry loop.
- Typical single-task read fetch/query after warm cache completes within **500 ms** locally; source worktrees remain byte-for-byte unchanged.
- Task listings return at most 100 rows or 1 MiB per response; exact snapshot pages do not duplicate/miss IDs under concurrent pushes.

These are **local harness acceptance targets**, not guarantees for public hosted remotes with variable latency. If they fail, first optimize fetch batching, tree operations, Git object lookups and lightweight local scheduling. Do **not** shard the authoritative branch or add a database as an unapproved workaround.

## 11. Acceptance tests and adversarial fixtures

Use disposable **bare Git remotes, at least two independent clones, and multiple registered worktrees**; never use live user branches for tests. Assertions cover both state and **absence of mutations** to source working trees, indexes and HEADs.

1. Initialize an orphan ledger branch and verify no shared ancestry with main; clone/fetch from another client; match project/ledger identity.
2. Clone source main into 20 worktrees containing old V1 task JSON; GetAgentInbox and snapshot show exactly one authoritative task per ID.
3. Two clients build sibling ledger commits; first push succeeds, second non-FF fails, second logical replay succeeds with both changes preserved.
4. Two agents race Assign/Begin on the same execution slot; exactly one Working attempt survives; other is Queued/AgentBusy.
5. Same operation ID concurrently submitted from independent clients creates **one** committed operation; a later retry with the same hash returns original result.
6. A push succeeds but its response is lost; retry from a new process/clone with same ID finds the indexed operation and does not add a commit. Reused ID with a changed payload fails.
7. Client TUI crash after persistent pending request but before/after accepted push replays the **same** operation ID and resolves once.
8. Simulate remote outage, refused credentials and read-only remote; no false success or local task-state writes, and stale UI clearly labeled.
9. Simulate forced rewrite/deletion after a trusted head; client detects non-descendant/missing ref and stops. First-time unbound client requires explicit trust.
10. Two remotes named origin with different project UUIDs or divergent ledger histories; client rejects unintended rebinding and mismatched pushURL.
11. Verify partial/shallow clone handling; snapshot SHA pinned through local GC; missing object produces typed error, not substitute data.
12. Read concurrent pages from exact snapshot commit while writers push, including continuation from **a different client**: stable total, no duplicate/omitted rows, no token that can select arbitrary source-branch commits; global roster agrees with snapshot.
13. Park Agent 3's Task 308, then assign Agent 3 Task 316; slot is available before Begin, occupied after Begin, and Task 308 remains Parked.
14. Agent 3 works on Task 316 while reviewer returns Task 308 with a new generation; Task 308 is Queued and does not preempt 316.
15. Verify deterministic priority/sequence queue order, exact-generation acknowledgements and stale Begin/Checkpoint rejection after reassignment.
16. Agent disappears mid-turn; no inferred presence or auto-reassignment. Authorized explicit reclaim invalidates old generation and releases slot.
17. Task spec edited in a source HTML file after assignment; pinned spec blob remains unchanged until explicit revise/reissue.
18. Task/agent checkpoint schema fuzzing: invalid IDs, paths, oversized JSON, unknown phase/status, unsafe content and incompatible schema fail without Git writes.
19. Remote repository public/unknown visibility requires explicit consent; Git ref protection preventing normal push gives a clear setup diagnostic.
20. Integration source push succeeds but ledger publish fails; retry verifies source target, records evidence once and never repeats merge blindly.
21. Successful AcceptAttempt does not imply Integrated or Pushed; documented no-source task may close with separately approved closure policy.
22. Move/rename/delete/recreate worktree and remove source branch; assignment persists with SourceUnavailable where appropriate.
23. Migration dry run contains all old worktree copies and conflicts; approved root import preserves exact IDs, spec contents, attempts, acknowledgements and checkpoints.
24. After 4.0 cutover, a connected V1 task writer is rejected by stateless MCP router while ordinary read-only Git/file commands remain available.
25. Restore a mirror backup to a fresh bare remote and verify project identity, ledger history, operation records, trusted head and every task record.
26. Rapid 10-client distinct updates satisfy bounded contention/retry/latency targets and never modify source checkout indexes or HEADs.
27. Cross-platform test of alternate Git paths, long task IDs, Windows filesystem case behavior, remote URL changes, and git command argument safety.
28. Server restart/upgrade cannot lose the task state; a client fetches the same remote ledger before and after restart.

Run full client/server tests, full Git integration fixtures, format checks and an actual **backup restore** before live migration. Never simulate acceptance by a passing mock push alone.

## 12. Implementation sequence and completion gate

1. **LedgerStore prototype:** approved project remote binding; orphan init; exact fetch/read; temporary-index commit; fast-forward push; operation-index dedup; lost-response recovery; trusted ref pinning. Prove with two clones *before* exposing write tools.
2. **Domain model:** task/attempt/agent records, transition validators, per-agent slot atomicity, deterministic inbox/reviewer queues, immutable specs/checkpoints and schema-versioned serializers.
3. **Task tools and UI:** full MCP command surface, read snapshot pagination, spec editing/import/export, source bindings and local retry key storage for TUI; replace V1 read paths.
4. **Integration workflow:** separately verified source and ledger commits, reviewer acceptance, pending source actions, push verification and worktree retirement.
5. **Migration and protocol 4.0:** dry-run with explicit conflict resolution, Git mirror backup, global old-writer gate, controlled client rollout, independent clone verification and source cleanup later.
6. **Hardening/acceptance:** forced rewrite and outage recovery, cross-platform security tests, 10-client contention, full restore drill and live smoke tests.

**Ready for implementation** means the schema, transitions, tool expectations, failure behavior and migration gates above are implemented in this order. **Done** means a task can be managed from any authorized project client/worktree without branch-derived ownership duplicates; concurrent agents cannot violate slot/assignment rules; lost pushes are recoverable by operation ID; historic task Git state remains intact; and the Jarvis server contains no new orchestration database or scheduling state.

### Review resolution checklist

| Review item | Contract |
|---|---|
| RV2-01 — End-to-end idempotency | `5.1–5.3`: caller UUID, pending TUI storage, indexed ops, replay/unknown outcomes |
| RV2-02 — Scheduling and queues | `3–4`: attempt phases, slot, ordering, normative transition table |
| RV2-03 — Canonical remote identity | `2.1`: pinned project/ledger UUID, approved endpoints, explicit rebind |
| RV2-04 — Rewrite detection | `2.2` and `9`: trusted ancestry, host protection, acknowledged first-clone limits |
| RV2-05 — Snapshot retention | `5.2–5.4`: private refs, GC pinning, ancestry confirmation and exact pagination |
| RV2-06 — Old task writers | `8–9`: global 4.0 command gate and no dual state authority |
| RV2-07 — Privacy/permissions | `2.2` and `8–9`: required opt-in, public-remote warning, protection and limits |
| RV2-08 — Spec authoring | `6`: Create/Revise/Import/Diff/Export with pinned blob |
| RV2-09 — Integration recovery | `7`: explicit Plan/Verify/Record source action across refs |
| RV2-10 — Contention | `5.2` and `10–11`: bounded retry/backoff, ten-agent test and metrics |

**Remaining deliberate tradeoffs:** a single remote ref serializes project writes; remote connectivity is required for authoritative updates; user-controlled Git credentials—not logical agent names—define trust; no client can prove a remote's full history without previously trusted state/host protection/backups; source push and ledger push are separate, explicitly reconciled operations. These are accepted design constraints, not unresolved questions.
