# Jarvis Orchestration V2 — canonical remote Git ledger

**Status:** V2 design and implementation contract (2026-10-28). Experimental MCP exposes core task/agent/review workflow, exact fast-forward integration, and explicit ledger bind/initialize/rebind/health. Full evidence/assignment schemas, privacy and backup hardening, TUI, migration and protocol 4.0 cutover remain incomplete.
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
5. One ledger commit atomically changes all related task/agent/review/operation records; a successful operation is **published** only after the remote accepts its commit. Source review refs are published separately, with explicitly reconciled outcomes.
6. No force pushes, resets, rebases, deletes, or rewrite/merge commits on the ledger ref; each normal update commit has exactly one parent, the just-fetched canonical tip.
7. Every write has a caller-stable idempotency key and per-entity optimistic versions; a rejected push causes a **logical revalidation**, never a blind file merge.
8. At most one **Working** attempt occupies an agent's execution slot. A Working agent may submit, update or hold a review request without yielding the slot; review lifecycle and implementation lifecycle are independent.
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

**Experimental V2 trust hardening (2026-10-28):** the client serializes per-checkout binding and monotonic trusted-head updates through an exclusive cross-process lock and an atomic private binding file. Rebind verifies the previously persisted anchor **and** the independently supplied new expected anchor. Local bare Git remotes expose read-only non-fast-forward and deletion rejection diagnostics; hosted branch policies, repository visibility and signed-commit requirements are reported `Unknown` when ordinary Git transport cannot verify them. This is **not** evidence that hosted policy is enforced. Remote URLs with embedded HTTPS credentials/query parameters or SSH passwords/query parameters are refused. Implementation of provider-specific branch/signing controls, restore epoch audit and any non-implicit policy enforcement beyond explicit operator consent is still pending (V2-01/V2-11/V2-13).

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
      <uuid>/checkpoints/
        <sequence>.json              Immutable checkpoint evidence
    integrations/
      <action-uuid>.json             Pinned source-integration plan and proof
    reviews/
      <request-uuid>/
        request.json                Mutable current status/version/claim/current revision
        notification.json           Independently acknowledged review decision notification
        revisions/
          <revision-number>.json    Immutable snapshot, source ref, base/head, evidence
        decisions/
          <sequence>.json           Immutable claim/release/decision/feedback evidence
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

Names use strict percent-encoded safe IDs; decoded strings can never change the path root, introduce separators/traversal or collide after case-folding. The initial V2 uses **one primary nonterminal attempt and one implementation owner per task**. Two implementation agents are never intentionally assigned to develop or submit the same task concurrently. Orchestrator-authorized ownership transfer revokes the previous assignment generation before assigning a new owner. Old attempts remain as separate immutable/auditable records. Creating another live attempt requires an explicit future multi-attempt feature.

`agents/<id>.json` is the **only** slot materialization, e.g. `{"agentId":"Agent 3","activeAttemptId":null,"version":12}`. It stores no redundant assignment queue. The queue is a deterministic projection of tasks/attempts in the same ledger tree; the full mutation validates that slot and attempt views agree. Registration of an agent does not establish a ChatGPT session or authenticate that label.

Task lifecycle values: **Open → InProgress → Implemented → Integrated → Closed**, plus **Cancelled**; progression can be reopened through explicit audited commands. `Implemented` means explicitly finalized against an Accepted review/approved evidence, not merely that a reviewer accepted a snapshot and not that code was merged. `Integrated` requires recorded source evidence; `Closed` is an explicit final administrative state. Documentation-only tasks may close via explicit approved non-source criteria and never claim a source merge.

Attempt phase values: **Queued, Acknowledged, Working, Paused, Finished, Parked, Blocked, Accepted, Superseded, Abandoned**. Paused means an agent explicitly yielded at a durable checkpoint; Finished means an agent explicitly completed its implementation responsibility but review/integration may still be pending. **ReadyForReview is not an attempt phase**: implementation may remain Working while an independent review request is Unclaimed, Claimed, Accepted, ChangesRequested, Withdrawn, or Superseded. Attempts also include `ownerAgentId`, `assignmentGeneration` (monotonic per attempt), `currentSpecBlobOid`, `issuedSequence`, `priority`, `reviewerId`, `checkpointId`, `resumeTarget` and optional `source` binding (remote/ref/base/head/last commit). **No `owner.worktree`.** Review request/revision records, reviewer claims, decisions, and verified source integrations are append-only evidence within the ledger/history; they must not be confused with task/attempt phases.

An assignment is **one immutable generation of instructions**, stored in the attempt's generation list or separate task-local blob, including its content, generation number, issued sequence, spec blob OID, expected base commit, actor and receipt fields. Review request status/revision/claim is a separate task-local ledger entity and does not overwrite this assignment. A stale generation never becomes current again. Reissue creates a new generation, retaining old instructions/receipts for history. Source checkout locations/paths are local observations, never owner keys.

**Current V2-05 evidence implementation (experimental 3.30):** `CheckpointAttempt` appends immutable Working progress evidence without releasing the agent slot; each checkpoint includes exact assignment generation/sequence, summary and next action, optional verified source commit, and optional owner-reported evidence. `SubmitForReview` and `UpdateReviewRequest` atomically append distinct immutable Submission/Revision checkpoints, exact pinned assignment spec blob OID, approved source-remote identity, base/head/source and published review refs, immutable source SHA, checkpoint sequence, and separate owner-reported test evidence status (`OwnerReported` versus `NotProvided`). A claimed revision cannot be replaced. Reviewer decisions atomically append a separate `ReviewDecisionEvidence` tied to the exact claimed revision/ref/head, recording verdict, inspection flag, reported-test flag and details without inferring tests from acceptance. `V2GetCheckpoint` uses 64-bit sequences. The project-scoped source repository identifier is not a cryptographically independent remote identity; stronger source binding remains assigned to V2-08. Legacy submission/decision aliases do not fabricate test or inspection evidence.

### Review request / revision contract

The authoritative task-local review tree has one mutable `request.json` record and append-only `revisions/<n>.json` and decision/audit records. Revision 1 is created at submission; each owner replacement appends a new immutable revision record rather than overwriting an old base/head/summary. The parent request's current revision/version selects the actionable **revision**, and the task's current attempt has an `activeReviewRequestId` pointer selecting the sole actionable **request**. Creating/terminating a request changes that pointer atomically with the review record. A second actionable request is rejected until the prior one is resolved. The pointer is not a separate authority or task queue.

A minimum current request shape is:

```json
{
  "schemaVersion": 2,
  "requestId": "review-request-uuid",
  "taskId": "315",
  "attemptId": "attempt-uuid",
  "ownerAgentId": "Agent 3",
  "version": 4,
  "currentRevision": 2,
  "status": "Unclaimed",
  "preferredReviewerId": null,
  "claimantId": null,
  "claimedRevisionId": null,
  "claimSequence": null,
  "createdSequence": 24
}
```

Revision records contain `requestId`, `revision`, `revisionId`, `taskId`, `attemptId`, `sourceRepositoryId`, `sourceRemote`, `sourceRef`, `targetRef`, `baseCommitOid`, `headCommitOid`, `reviewRef`, `specBlobOid`, `summary`, `checkpointId`, `evidence`, `publishedSourceOid`, `createdSequence`. The revision itself is immutable. Its effective status (current, superseded, claimed, accepted, returned) is determined from the current request and append-only decision records, never by rewriting its snapshot content. `ClaimReview` must atomically write current request status/claimant and immutable claim evidence; a reviewer can query both unclaimed queue entries and their own claimed queue entries. `UpdateReviewRequest` changes the parent request version and current revision **together**, and appends one new revision.

Validation is strict: unknown statuses, colliding/unsafe request IDs or revision IDs, missing source object, absent assigned attempt, mismatched owner, incorrect generation, non-reachable base/head, or source remote differing from approved binding must fail closed. Task worktrees remain observations, not review ownership keys. Review status transitions are `Unclaimed → Claimed → Accepted | ChangesRequested | Withdrawn` and `Unclaimed → Withdrawn`; replacement keeps the request Unclaimed and supersedes the old revision. Claimed → reviewer reassignment preserves Claimed and its frozen revision. No implicit expiration or lease-based claim stealing.

### Normative JSON minimum

```json
{
  "task": {
    "schemaVersion": 2,
    "id": "315",
    "version": 4,
    "lifecycle": "InProgress",
    "currentAttemptId": "attempt-uuid",
    "title": "Improve orchestration"
  },
  "attempt": {
    "schemaVersion": 2,
    "attemptId": "attempt-uuid",
    "activeReviewRequestId": null,
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
    "schemaVersion": 2,
    "agentId": "Agent 3",
    "version": 12,
    "activeAttemptId": null,
    "focusAttemptId": null,
    "state": "Parked",
    "stateReason": "No work assigned",
    "stateSequence": 24
  }
}
```

These illustrate fields from **three separate files**, not a single combined JSON response.

**Current V2 schema hardening:** each persisted task/agent/attempt/review/notification/checkpoint/integration JSON entity is written with `schemaVersion: 2`. Readers reject missing/unsupported entity schema markers, absent required fields, duplicate JSON member names, and unknown state/phase/status values. The implementation currently permits canonical task HTML up to **128 KiB UTF-8**, which is validated at both domain and ledger-transaction boundaries; paged larger descriptions and a unified content limit remain an open implementation task. HTML is untrusted and active elements/inline event handlers are rejected at authoring, but display surfaces must still render it safely. Implement typed records with schema validation, explicit parsers and deterministic UTF-8 serialization; reject unknown lifecycle/phase values. Non-authoritative optional extension data may be preserved but must not bypass validations.

## 4. Scheduling contract and state machine

### Explicit agent-level states (2026-10-28)

The **agent** has exactly five authoritative states. They describe orchestration/operator obligations, **not** task or review acceptance. State, version, focused attempt, Working slot, transition reason, and ledger sequence are stored together in the canonical agent record; never infer state from silence, elapsed time, browser inactivity, or harness health.

| Agent state | Meaning | Next action |
|---|---|---|
| **Ready** | Agent yielded an existing Working assignment with a durable checkpoint and next action | Operator prompts the agent to resume; ResumeAgent returns to Working |
| **Assigned** | Orchestrator assigned a task, but the agent has not begun its current generation | Operator prompts the agent to start; BeginAttempt returns to Working |
| **Working** | Agent has explicitly begun/resumed its focused assignment | Agent continues; may checkpoint/yield/complete; submitting reviews does not alter Working |
| **Completed** | Agent explicitly completed its implementation responsibility and released the Working slot | Orchestrator assigns new work or parks agent; task/review may remain open |
| **Parked** | Orchestrator deliberately has no work assigned to the agent | Orchestrator assigns new work; no operator action is required |

Initial RegisterAgent → Parked. AssignTask transitions Parked/Completed → Assigned, binds the sole focused attempt and refuses to replace a currently Assigned, Ready or Working agent. BeginAttempt transitions Assigned → Working and occupies the one execution slot. YieldAgent is Working → Ready and **atomically** writes an immutable checkpoint (summary, next action, task/attempt and generation), changes the attempt to Paused, and clears the Working slot. ResumeAgent is Ready → Working on the **same** attempt and generation. CompleteAgentWork is Working/Ready → Completed, writes a final checkpoint, changes its attempt to Finished and clears its slot; review decisions remain independent. ParkAgent is Completed/Parked → Parked with an explicit orchestrator reason, and cannot interrupt Assigned, Ready or Working.

All transitions require a caller-stable operation ID and expected agent/attempt versions (where applicable). The agent's focused attempt is its current operator-facing assignment; the execution slot is occupied **only** in Working. The old completed attempt may remain in ledger history while a new assignment is underway. A reviewer can accept/finalize the old task without preempting a different current Working assignment.

Ready and Assigned both need an **operator prompt**, but Ready resumes a checkpointed assignment whereas Assigned starts a new one. Completed needs an **orchestrator assignment decision**, not an operator prompt; Parked needs no action until the orchestrator deliberately assigns work. Review requests and integration status must be projected separately and never cause implicit agent-state changes.

### Capacity and queues

- Each agent has one execution slot by default. **Only Working occupies it.** A queued assignment is runnable but does not reserve the slot; Acknowledged also does not occupy it.
- Agent workload may include many Queued, Acknowledged, Parked, Blocked or Accepted attempts alongside unclaimed, claimed and resolved review requests. No task or review automatically preempts a current Working attempt.
- `GetAgentInbox(projectId, agentId)` returns `agent.state`, `focused`, `operatorActionRequired`, `needsOrchestratorAssignment`, `working` (0..1), `runnableQueue`, `pendingReview` (reviews submitted for the agent's attempts and reviews claimed by that agent), `parked`, `blocked`, `acceptedPendingIntegration`, plus `slotAvailable`, `unassigned` and optional `presence` (only with real external evidence). `slotAvailable` means the execution slot is empty; **unassigned** means no outstanding tasks, including parked/blocked/review items. These must never be confused.
- Runnable queue ordering is deterministic: **priority descending, issuedSequence ascending, taskId ordinal, attemptId ordinal**. Queued and Acknowledged are runnable; an acknowledged item still requires Begin to acquire the execution slot. ChangesRequested is a reviewer decision, not a runnable attempt phase: it creates a Queued generation only for a nonworking owner; when the owner is already Working it provides durable feedback without invalidating the current Working generation.
- Reviewer queue is a separate projection over **current Unclaimed review requests** and claimed reviewer responsibilities, ordered by review submission sequence and task ID. Review requests must not be derived from attempt phase; claiming a review does not consume an implementation slot.
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
| BeginAttempt | Owner, current generation, Queued/Acknowledged; agent Assigned/focused and slot empty | Agent Working and attempt Working; acquire slot; record begunAt |
| YieldAgent | Owner Working, exact agent/attempt versions and generation; checkpoint and next action provided | Immutable checkpoint; agent Ready, attempt Paused, release slot and preserve focus |
| ResumeAgent | Owner Ready, focused Paused attempt, exact versions/generation | Agent and attempt Working; reacquire slot, keep same assignment |
| CompleteAgentWork | Owner Working/Ready with final summary and matching versions/generation | Immutable completion checkpoint, attempt Finished, agent Completed, release slot; review unaffected |
| ParkAgent | Orchestrator decision, agent Completed/Parked with matching version and reason | Agent Parked, clear focus; do not preempt an unfinished assignment |
| ReissueQueuedAttempt | Queued reviewed attempt; idle Completed/Parked owner; exact versions/generation | Focus same attempt, agent Assigned, preserve task owner and source |
| RecoverAgentAssignment | Explicit orchestrator reason and current Assigned/Ready/Working focus | Immutable recovery checkpoint, increment attempt generation, agent Assigned; never infer from inactivity |
| AcknowledgeReviewFeedback | Owner and exact unacknowledged decision sequence | Mark only that decision notice acknowledged |
| CheckpointAttempt | Owner, current generation, Working | Append immutable checkpoint, advance attempt version; keep phase/slot |
| SubmitForReview | Current Working attempt and verified publishable base/head snapshot | Publish distinct immutable source review ref; atomically create Unclaimed review request/revision with final checkpoint and keep attempt Working/slot unchanged |
| UpdateReviewRequest | Request Unclaimed, same owner of nonterminal attempt, expected request version and current revision/generation | Publish a new immutable review ref/revision; atomically advance current revision and supersede the prior revision; keep original request ID, owner slot and original history |
| ClaimReview | Eligible registered reviewer, request Unclaimed, exact expected request version/revision | Atomically mark Claimed, pin claimed revision/base/head/ref and claimant; no source edits or agent-slot change |
| ReleaseReviewClaim / ReassignReviewer | Authorized reviewer/orchestrator with reason and expected version | Release or transfer claimant while keeping the claimed revision frozen; owner cannot silently replace it |
| RequestChanges | Claimed current revision, claimant reviewer, expected version | Append durable notification; leave Working/Paused attempt unchanged, or queue new generation of Finished attempt; never preempt current agent work |
| AcceptReview | Claimed current revision, claimant reviewer, expected version, verified pinned source | Append acceptance evidence for exact frozen patch; no implicit slot change, task finalization, source merge or push |
| FinalizeImplementation | Accepted review, Finished attempt and no unreviewed work | Mark task Implemented and pin accepted review/patch; no agent-state or execution-slot change |
| WithdrawReview | Unclaimed and current owner (or authorized cancellation); Claimed requires reviewer/orchestrator consent | Retain immutable audit evidence; invalidate reviewer queue entry without modifying attempt Working slot |
| ParkAttempt / BlockAttempt | Queued/Acknowledged/Working with condition/blocker | Parked/Blocked; release slot if held; review requests remain independently tracked and require explicit decision when affected |
| ResumeAttempt | Parked/Blocked and condition resolved or explicit override | New Queued assignment generation; does not automatically acquire slot or revise a claimed review |
| PlanIntegration | Accepted and code requires integration | Persist target ref, source commit, expected target head and actionId; pending source action |
| RecordIntegration | Current planned action, verified immutable review ref, remote target exactly the accepted head | Persist verified remote evidence and method; task Integrated; no implicit source push |
| CloseTask | Integrated or explicitly approved non-source closure policy | Closed; retain all attempts, evidence and audit |
| CancelTask / AbandonAttempt | Explicit authorized cancellation and expected version | Terminal state; remove queue/slot membership; retain history |
| BindAttemptSource | Registered project workspace, verified Git source reference | Update source observation only; never change agent owner or phase |

`BeginAttempt` starts an Assigned queued attempt and `ResumeAgent` resumes a Ready paused attempt; both acquire the execution slot with exact generation checks. ReissueQueuedAttempt selects the queued attempt first, without acquiring the slot. Attempting to start a second attempt for the same agent fails `AgentBusy`. A reviewer returning Task 315 after its owner Finished and began Task 316 queues a new generation of Task 315, without interrupting Task 316 or marking Task 315 Working. A reviewer returning Task 315 while Agent 3 is already Working on Task 315 leaves that generation Working and adds durable review feedback.

### Reissued assignments and durable feedback

Requested changes are a review outcome, not a command to take over the owner's current ChatGPT session. Every review decision creates a separate auditable notification in the canonical ledger containing owner, attempt, decision sequence, verdict and feedback. The agent inbox derives unacknowledgedFeedback from these records across all owned tasks, including Finished tasks. Later decisions do not replace earlier notifications. AcknowledgeReviewFeedback targets an exact decision sequence and atomically marks only that notice acknowledged.

When the owner is Working or Ready/Paused on the reviewed attempt, RequestChanges preserves the phase, agent state, focus and slot and simply delivers feedback. If the reviewed attempt is Finished, RequestChanges increments its existing assignment generation, makes the attempt Queued, and retains the original owner, pinned spec and review base. The agent remains in its existing state, even when Working on another task.

The orchestrator explicitly invokes ReissueQueuedAttempt with expected attempt version/generation, agent version and a reason. It works only for an owner in Completed or Parked with an empty execution slot. Reissue changes the agent to Assigned and focuses the same queued attempt; BeginAttempt acquires the slot. Stale prior generations are rejected.

RecoverAgentAssignment is a separate explicit orchestrator repair for an Assigned, Ready or Working session chosen for restart. It writes an immutable Recovery checkpoint, advances the same attempt generation, queues that attempt, changes the agent to Assigned and clears the Working slot. This is never inferred from inactivity or frontend errors. Source and claimed review records remain intact.

Review acceptance and FinalizeImplementation operate only on the pinned patch and task state, never on the agent's current slot. Finalizing a Finished attempt after its owner begins another task must not interrupt that newer assignment.

### Independent review requests and immutable review branches

**Agent work does not pause on review submission.** The implementation attempt can remain Working and keep its execution slot while any review is Unclaimed or Claimed. The reviewer request is a distinct ledger entity, not a replacement attempt phase. An agent may keep committing to its working branch after a snapshot has been submitted.

A review request has a stable `requestId`, task/attempt IDs, owner, optional preferred reviewer/eligibility policy, `version`, `currentRevision`, `status`, `createdSequence`, and optional claimant. Each revision has an immutable `revisionId` and monotonically increasing number, independent `sourceRepositoryId`, approved source remote, source/target refs, `baseCommitOid`, `headCommitOid`, pinned spec blob, final checkpoint/evidence, creation sequence, and exact **review ref**. **Cumulative coverage rule:** each task pins a canonical review base exactly once via BindAttemptSource before its first code review. Every code review submitted, updated, or resubmitted for that task uses that same base and reviews the entire base-to-head tree delta, including all prior rejected changes and corrections. The base is not silently moved to the head of a rejected or accepted-but-unintegrated review. An explicitly verified integrated source target may become a new base only through a separately audited rebase/baseline-change operation; incremental review chains are out of scope for initial V2. FinalizeImplementation and PlanIntegration pin the *accepted revision's exact base/head/ref*, reject a different integration head, and require no active review. Code committed after the accepted head is not included or implicitly approved. The reviewable change is the pinned base-to-head tree delta, not just the last commit. Source refs must be sufficiently retained for a different reviewer client to fetch and verify the exact source objects.

Each published revision uses a **new, immutable source Git ref**, e.g. `refs/heads/jarvis-review/315/<request-id>/r1`, `.../r2`. Publish create-only: an absent ref can be created, and an already-present ref is valid only when it resolves to the exact same expected SHA. An existing ref must never be advanced to a different OID, including by normal fast-forward, even after an ambiguous push response. Its remote advertised OID must match the pinned head. A replacement never moves, force-pushes, deletes or rebases a prior review ref. These refs live in normal **source history**, not in the orphan ledger. Their publication and the ledger mutation are separate; see the crash/recovery contract below. A review ref cannot be reused for a different OID. Later cleanup is explicit, after no active or integration-dependent review references it; old ledger revision/evidence records remain immutable.

**SubmitForReview:** First verify and publish the source review ref (never push to main implicitly). Then write one ledger transaction setting the current attempt's `activeReviewRequestId`, creating `Unclaimed` request revision 1 and the final implementation checkpoint. Reject a second active request and use operation-id deduplication for identical retries. The attempt remains Working; the agent retains its slot. The reviewer queue projects only the current Unclaimed request, not an older revision.

**UpdateReviewRequest / Replace:** The submitting owner may submit a newer revision (new commits, changed base/head, revised description/evidence, or revised target) **only while the request is Unclaimed**. The request keeps its stable ID and gets `version + 1`, `currentRevision + 1`; its task/attempt active-review pointer stays unchanged; the previous revision is auditable and marked Superseded, not overwritten. The caller must supply the expected request version and current revision; updating after a claim returns `ReviewAlreadyClaimed`, with no ledger changes. Allow metadata-only replacement through the same versioned revision rule to avoid invisible changes to a reviewer's view. One request is actionable per task at a time; a Claimed request cannot be replaced unilaterally, though the agent may continue working and submit another request after the claimed review reaches a terminal decision.

**ClaimReview:** A reviewer explicitly claims the latest Unclaimed revision using `requestId`, `expectedVersion` and `expectedRevision`. One ledger transaction marks it Claimed, records `claimantId`, `claimedRevisionId`, `claimSequence`, and freezes the exact base/head/ref and evidence. A preferred/assigned reviewer is **not** equivalent to a claim. A reviewer must not decide a request they have not claimed; self-review is disallowed at the logical agent-label level. The owner may update an Unclaimed request while their attempt is Working, Queued or Parked, provided it remains nonterminal and its ownership/generation is current; updating a review is **not** itself a BeginAttempt or slot acquisition. The claim does not acquire the reviewer's implementation execution slot.

**Concurrency:** The only required same-task race is the **single implementation owner updating versus a reviewer claiming** (plus duplicate delivery/retry of an operation). No separate implementation agents concurrently submit the same task. ClaimReview and UpdateReviewRequest compete on the same remote request record/version. If replacement publishes first, a stale claim fails and the reviewer must read the new revision. If claim publishes first, replacement fails with `ReviewAlreadyClaimed`; it must never silently create a newer active revision. Pending remote publication of a losing revision may leave a detached, immutable review ref; it is **not** an authoritative review request and can be reclaimed by a separately verified cleanup operation. All mutations use caller-stable operation UUIDs; retries check the ledger operation index before rechecking claim/revision state. Exactly one current revision may be actionable, and exactly one reviewer claim may win.

**Claimed review:** From Claim, the revision remains frozen until AcceptReview, RequestChanges, explicit Withdraw with reviewer/orchestrator consent, or audited reviewer reassignment. Reassignment changes the claimant but retains `status=Claimed`, `claimedRevisionId`, and the immutable claim history. Releasing the claimant without immediate reassignment leaves the request `Claimed` with `claimantId=null` and an explicit `AwaitingReassignment` reviewer-work substate; **it does not set status back to Unclaimed** and cannot be picked up by ordinary ClaimReview. Only an authorized ReassignReviewer may select the replacement reviewer. This does **not** re-enable owner replacement; the new reviewer examines the same frozen snapshot. A terminal review decision clears the attempt's `activeReviewRequestId` atomically with the review outcome, preserving all prior requests in Git history. A decision references the claimed revision and exact pinned base/head. AcceptReview marks that review Accepted and retains its approved patch; it does **not** automatically terminate a still-Working agent or declare all subsequent unreviewed work complete. RequestChanges records findings and notifies the owner: an already Working agent keeps working, whereas a nonworking owner receives a new Queued generation to resume. FinalizeImplementation is separate and explicitly selects an accepted review as the completion/integration basis. Any later code must go through its own review and cannot ride along on a moving branch tip.

**V2-06 recovery contract (2026-10-29):** A released reviewer claim
retains `status=Claimed`, `claimedRevision` and immutable source evidence,
with `claimantId=null` awaiting orchestrator reassignment. Release,
reassignment, withdrawal and decision append immutable claim events.
`V2AwaitingReviewReassignment` reports these frozen unoccupied reviews,
never as Unclaimed. Unindexed review refs from losing source publication
operations require a two-phase cleanup: an immutable Git-ledger tombstone
first, then exact-SHA source deletion by lease. Submit/Update verify no
matching tombstone during their CAS transaction. Existing immutable
review revisions always protect their refs from orphan deletion, even
when withdrawn or superseded; approval/integration refs are never
implicitly cleaned. The cleanup reservation is durable, while remote
deletion is separately confirmed and may be retried after uncertainty.

**Publication/recovery:** Source review-ref push, ledger Submit/Update, and eventual source integration are **not** a cross-ref atomic transaction. On transport uncertainty, first inspect the source remote ref and then check the canonical ledger operation ID; never move an existing review ref in an attempt to “repair” a race. A ref published without a successful ledger update remains an orphan candidate and must not appear in reviewer inboxes. Verify preservation of any accepted/claimed revision before cleanup. When the reviewer fetches or accepts, it must verify the remote review ref still resolves to the pinned SHA (or fail closed with a source-integrity error); the review does not track a moving branch. Git credentials/permissions remain the trust boundary, not agent labels.

An agent disappearing mid-turn leaves the slot occupied and the attempt marked Working until explicitly paused, blocked or reclaimed through a reviewer/orchestrator action with an audit reason. Checkpoints require Working so an agent cannot bypass the capacity rule by progressing an Acknowledged item without beginning it. Presence is not inferred from Git; no lease expiry or automatic stealing. Admin release must atomically revoke the old generation and free the slot, preventing a stale client from continuing authoritatively. Two simultaneous Begin calls are checked on the same remote snapshot and only one can publish a slot change.

### 4.x Immutable assignment generations and acknowledgment (experimental 3.29)

The V2 ledger now stores `tasks/<task-id>/attempts/<attempt-id>/assignments/<generation>.json` as immutable instructions for each issued generation: sole owner, issuer, reason, full instructions, pinned spec SHA, optional baseline/target, prior generation, and canonical issue sequence. Initial assignment, review-requested requeue after Finished, completion requeue, recovery and explicit reissue create new records without replacing old ones. Review feedback while Working never advances the current generation or preempts the owner.

`V2AcknowledgeAssignment` writes an immutable `<generation>.ack.json` receipt for the exact owner, issue sequence and pinned spec, moving Queued to Acknowledged via version/generation CAS. `BeginAttempt` accepts both Queued and Acknowledged: beginning directly from Queued **atomically creates the acknowledgment receipt**; beginning from Acknowledged verifies the existing evidence. A Working generation therefore has durable acknowledgment. `V2GetAssignment` returns historical/current issue and receipt from one verified snapshot. Begin/Resume/recovery/reissue enforce current-generation guards and do not create concurrent task owners.

`V2ReissueAssignment` can replace an *unstarted Assigned* generation with new instructions and optionally a revised HTML specification. It atomically updates the task's spec version, pins the new Git blob OID to the next generation and keeps the previous generation/receipt immutable. Working and Ready agents cannot be preempted by this operation. Reading historical spec content by blob OID is delegated to V2-09; V2-04 exposes the exact historical blob identity. Permission/actor identity hardening remains V2-11; this does not activate production V2.

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

**Current experimental implementation (protocol 3.27):** ledger transaction records now persist `schemaVersion: 2`, canonical operation input, and an immutable `originalResultJson` receipt identifying the operation, sequence, changed entity paths and their content digests (plus Git blob ID for generic PutRecord). A retry returns the same receipt even if `currentLedgerHead` has advanced. JSON intent members are sorted deterministically; duplicate case-insensitive keys and invalid JSON are rejected. This is a generic **write receipt**, not yet the domain-specific `taskVersion/checkpointId/phase` result illustrated above; that remaining mapping is tracked in V2-02.

The local client journals review, task and integration mutation requests before execution under its user configuration directory, with private file permissions and a caller-stable operation UUID. `V2PendingOperations` can inspect those original commands even when the remote is unavailable; the agent/operator must explicitly retry the **same** request after inspecting `V2GetOperationStatus`. Confirmed writes clear their journal entry, but uncertain outcomes remain journaled. Error codes such as `OutcomeUnknown`, `RemoteUnavailable`, `LedgerContended`, and `OperationIdReused` are now preserved through a typed V2 MCP failure. Jittered exponential retry delays are bounded; the journal does **not** queue offline writes.

**Explicit pending-operation reconciliation (3.27):** `V2ReconcilePendingOperations` reads all local pending requests against one verified Git ledger head, comparing each durable request with its canonical published operation type and validated action parameters. It reports `Applied`, `NotFoundAtVerifiedHead`, `IdentityMismatch` or `BindingMismatch` without deleting any local evidence. Missing at the current head does **not** mean a lost push can never succeed. On an uncertain response, the V2 task/review/integration adapter fetches the remote operation index and returns the original receipt immediately **only** when the recorded canonical request matches the original command; otherwise it preserves `OutcomeUnknown`, the caller's UUID and any verified head. `V2AcknowledgePendingOperation` explicitly retires one local entry only after matching a verified remote operation and the exact remotely observed request hash; repeat acknowledgments re-verify the same hash without creating Git commits. A mismatched command, a different ledger binding, or an operation missing from the verified head cannot be acknowledged. No local journal record is authoritative task state.

**Durable domain results and originating-commit evidence (3.28):** domain mutations persist schema-versioned `entityResults` alongside their original content hashes and sequence in the same operation commit. Each effect identifies the touched task/agent/attempt/review/revision/checkpoint/integration record and projects its **original** entity version and relevant lifecycle/phase/state, assignment generation, review revision and immutable source head/ref, or checkpoint kind/sequence. The exact result remains unchanged even after subsequent writes. V2 task, review and integration mutation responses expose this original result as structured JSON; `V2GetOperationStatus` also returns that structure directly. The operation's **originating Git commit** is recovered read-only using the first-parent addition history of its immutable index file, requiring unchanged blob identity and matching `Jarvis-Operation-Id` / `Jarvis-Request-Hash` commit trailers. The origin is never written inside its own commit. The returned `originatingCommit` and `snapshotCommit` are distinct and independently tested after ledger advancement. This is a projection of original persisted entity state, not an inferred present-day task state.

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

**V2-08 implementation detail (2026-10-28):** `BindAttemptSource` pins
source repository identity by a versioned SHA-256 fingerprint of the
explicitly approved fetch/push endpoints, independent of project UUID and
local checkout path. Git does not provide a globally immutable remote
repository identifier; the fingerprint proves equality of approved endpoints,
not hosting-provider repository attestations. The client checks Git's
registered worktrees, the source branch and base ancestry against the
approved remote target. `InspectAttemptSource` reports read-only local
checkout/ref availability (including `SourceUnavailable`) without changing
owner or execution slot. `RebindAttemptSource` explicitly changes a working
branch at an exact attempt version/generation while preserving the frozen
review revisions and original source base/target. Reviewer and integration
operations verify pinned repository/endpoints but need not resolve a review
revision's historical working branch after it has disappeared.

Existing V1 task JSON must not be imported as executable current state by ordinary source reads. If a new source branch accidentally inherits V1 task JSON, current task APIs ignore it. The worktree status/retirement tooling may still report it as historical files without granting those copies authority.

## 7. Source integration and cross-ref recovery

**Ledger ref updates and source branch pushes are separate Git operations.** A transaction spanning both refs does not exist. Model integration as an explicit, auditable workflow:

1. Reviewer **AcceptReview** records acceptance of a claimed immutable review revision, including base/head, reviewed patch scope and verification evidence. **FinalizeImplementation** explicitly selects an accepted revision as the task's completion basis; acceptance alone neither stops the implementation agent nor asserts merge/push.
2. Orchestrator calls **PlanIntegration**, recording an integration action ID, **accepted review ID and revision**, its immutable review-ref/base/head OIDs, target ref, canonical source remote and expected target head in the ledger. The finalized attempt is Accepted/PendingSourceAction. A moving implementation branch tip is never the integration target.
3. Client performs an explicit integration of only the accepted immutable review head or verified equivalent patch and pushes the target as instructed by the orchestrator. **Neither the ledger nor the server performs source Git edits implicitly.**
4. Client performs a fresh **read-only remote Git verification**: target ref and commit SHA; evidence that the target contains the **exact approved delta and no later unreviewed source changes**, or a separately documented verified patch-equivalence proof (including source and target OIDs). The verifier records which remote, timestamp, method and exact target head were inspected. A merge present only in a local checkout remains **PendingPush**, not remote-integrated.
5. **RecordIntegration** publishes verified **remote-target** evidence to the ledger with the original integration action ID. Only then does the task become Integrated. If source push is pending, preserve an explicit **PendingPush** integration substate and do not mark the task Integrated until the remote ref is verified.
6. Orchestrator **CloseTask** separately when closure policy is satisfied. Worktree cleanup is its own checked Git operation with no task-state deletion.

Integration progress is a separate typed substate on the Accepted attempt (`NotPlanned`, `PendingSourceAction`, `PendingPush`, `PendingVerification`, `VerifiedRemote`, `Recorded`); it does not overload `attempt.phase` or task lifecycle. Local-only push status cannot be inferred from a Git commit in the ledger.

If step 3 succeeded but the client lost its response, do **not** blindly repeat the merge/push. Re-read the source target and recognize whether the intended commit or patch is already integrated. If the source push succeeded but step 5 fails due to ledger outage, the ledger remains PendingSourceAction/Verification. On retry, verify the Git remote again and publish only the missing ledger evidence using the *same operation ID*.

If a remote target has moved further, accept only if current remote evidence still demonstrates the required integration. If the target history was rewritten, require explicit operator reconciliation. No operation infers accepted/integrated status from old `status=completed` JSON, and no source push automatically marks a task Closed.

### First V2 integration verifier: strict fast-forward only (2026-10-28)

The isolated implementation adds a separate integration record at
`tasks/<taskId>/integrations/<actionUuid>.json`, referenced by the accepted
attempt's `integrationActionId`. The record contains the accepted review ID
and revision, immutable review ref, cumulative base/head SHA, target ref, exact
expected remote target SHA, approved source remote URL, state/version, ledger
planning sequence and optional verification evidence. The task remains
**Implemented** while the integration is pending; review acceptance and agent
completion never imply a source push.

`PlanIntegration` requires a Finished, finalized, Accepted attempt with no
active review and no prior integration action. It independently re-fetches the
immutable review ref and the live target ref from the *approved* remote; the
caller-supplied expected target SHA must equal the advertised/fetched remote
head. Both the pinned review base and the planned target must be ancestors of
the accepted commit. Planning never changes the source branch, working tree,
or the remote target. One integration action is current per task.

`MarkIntegrationPendingPush` records the explicit PendingPush substate but
does **not** push anything. `InspectIntegration` performs a fresh read-only
remote observation and distinguishes PendingPush, ReadyToRecord, TargetChanged,
Recorded, and RecordedTargetMoved. A commit only in the local checkout remains
PendingPush. Remote source target fetches use temporary private refs, verified
against their advertised OIDs, and never update a working branch.

`RecordIntegration` requires the current action/version and independently
re-verifies the exact immutable review ref and target. **The remote target must
be exactly the accepted commit SHA.** A target with extra unreviewed commits,
a missing ref, a rewritten or diverged target, a local-only merge, or a
different merge/cherry-pick SHA is not accepted. The ledger transaction records
the observed SHA, approved remote, verified UTC time, method
`RemoteExactFastForwardCommit` and ledger sequence and changes the task to
Integrated. Retrying with the same operation UUID does not duplicate the
record. `CloseIntegratedTask` separately moves the task to Closed with an
expected version; no agent state or execution slot changes.

This narrow verifier intentionally does **not** yet attest equivalent merge
commits or cherry-picked patches. Those require a separate, explicit and
cryptographically/payload-verifiable patch-equivalence model, not an implicit
assumption from source ancestry. A target moved after a successful recording
is reported by later read-only inspection as RecordedTargetMoved, without
rewriting the historical proof.

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
SubmitForReview / UpdateReviewRequest / ClaimReview / GetReviewRequest / GetReviewerQueue
ReleaseReviewClaim / ReassignReviewer / AcceptReview / RequestChanges / WithdrawReview / FinalizeImplementation
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
  "submittedReviews": [],
  "claimedReviews": [],
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
| `ReviewAlreadyClaimed` | Owner attempted to revise an already claimed request | Keep claimed snapshot; continue work or await reviewer outcome |
| `ReviewVersionConflict` | Review claim/replacement targeted an obsolete request version/revision | Re-read current request and explicitly retry new intent |
| `SourceReviewRefUnavailable` | Pinned review ref is missing or differs from published SHA | Fail closed; recover the published object/ref before review or integration |
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
14. Agent 3 works on Task 316 while reviewer returns Task 308 with a new generation; Task 308 is Queued and does not preempt 316. Reviewer feedback on Task 316 itself does not preempt or invalidate its Working generation.
15. Verify deterministic priority/sequence queue order, exact-generation acknowledgements and stale Begin/Checkpoint rejection after reassignment.
16. Agent disappears mid-turn; no inferred presence or auto-reassignment. Authorized explicit reclaim invalidates old generation and releases slot.
17. Task spec edited in a source HTML file after assignment; pinned spec blob remains unchanged until explicit revise/reissue.
18. Task/agent checkpoint schema fuzzing: invalid IDs, paths, oversized JSON, unknown phase/status, unsafe content and incompatible schema fail without Git writes.
19. Remote repository public/unknown visibility requires explicit consent; Git ref protection preventing normal push gives a clear setup diagnostic.
20. Integration source push succeeds but ledger publish fails; retry verifies source target, records evidence once and never repeats merge blindly.
21. Successful AcceptReview does not imply Implemented, Integrated, Pushed or stopped Working; explicit FinalizeImplementation and PlanIntegration select exact approved revision. A documented no-source task may close with separately approved closure policy.
22. Move/rename/delete/recreate worktree and remove source branch; assignment persists with SourceUnavailable where appropriate.
23. Migration dry run contains all old worktree copies and conflicts; approved root import preserves exact IDs, spec contents, attempts, acknowledgements and checkpoints.
24. After 4.0 cutover, a connected V1 task writer is rejected by stateless MCP router while ordinary read-only Git/file commands remain available.
25. Restore a mirror backup to a fresh bare remote and verify project identity, ledger history, operation records, trusted head and every task record.
26. Rapid 10-client distinct updates satisfy bounded contention/retry/latency targets and never modify source checkout indexes or HEADs.
27. Cross-platform test of alternate Git paths, long task IDs, Windows filesystem case behavior, remote URL changes, and git command argument safety.
28. Server restart/upgrade cannot lose the task state; a client fetches the same remote ledger before and after restart.

29. A single owner submits revision r1, then updates to r2 before reviewer claim. Each immutable source review ref is published create-only; retrying cannot change an existing ref's SHA. Exactly r2 appears in the Unclaimed queue and r1 remains inspectable.
30. ClaimReview(r1/version 1) races UpdateReviewRequest(r2/version 1). Precisely one wins the ledger fast-forward race: if update wins, stale claimant must read r2; if claim wins, owner receives ReviewAlreadyClaimed and cannot overwrite the reviewed SHA.
31. Claim is committed after r2 publication succeeded but before its ledger mutation. The unmatched r2 source ref never appears as an active review; retries do not move r1 or falsely publish r2; cleanup retains the claimed revision.
32. The agent continues Working and checkpointing after submitting or updating a review. The reviewer claims/accepts the frozen r2 while the agent makes later commit r3; acceptance neither takes the agent's slot nor integrates unreviewed r3.
33. Two reviewers race to claim the same request; exactly one ClaimReview succeeds. No reviewer may accept without claim; reviewer release/reassignment retains the exact claimed revision and does not re-enable owner replacement.
34. The reviewer fetches revision r1 by its immutable published ref across independent clones and verifies exactly the pinned base/head, including a multi-commit diff. A moved/deleted review ref fails verification instead of silently using another head.
35. Agent cancels unclaimed review, reviewer rejects claimed review, and orchestrator explicitly finalizes accepted review; resulting reviewer queue, agent slot, task lifecycle, and integration basis remain consistent across clients.
36. Submit/Update source publication succeeds but ledger push is rejected, times out, or gets superseded by another writer. Replaying the original operation ID resolves its ledger outcome without reusing or force-moving review refs; safely identify orphaned candidate refs for later cleanup.
37. The reviewer claims while viewing an old UI snapshot. The expected request version/revision guards the decision; stale Claim or Update gets a typed error and a fresh view, never an automatic second review.
38. Review schema fuzzing covers invalid path encoding, duplicate revision numbers, malformed review refs, unknown status, malicious evidence, reviewer identity collisions and required source binding fields.
39. First review of task base A..C requests changes; resubmission A..E must include A..C and corrections. Attempting C..E is rejected as mismatched pinned base.
40. Accepted A..C followed by additional unreviewed commits D/E: FinalizeImplementation explicitly selecting C may integrate only A..C; never the later moving implementation branch head. Finalizing E requires a new cumulative A..E acceptance.
41. Claim and replacement with one implementation owner racing one reviewer use exact expected request versions; previously rejected changes cannot be hidden by selecting a new base.
42. Register two independent reviewers and verify either eligible reviewer may claim an Unclaimed request; the loser cannot decide. Claimed revision handoff retains the same pinned head.
43. Retrying an already accepted UpdateReviewRequest UUID after its revision is claimed returns the original durable result and does not publish an unintended next source ref.
44. Fresh agent starts Parked, becomes Assigned on new task, Working on Begin, Ready only through a durable Yield checkpoint, Working again on Resume, Completed on explicit completion, and Parked only by orchestrator decision; verify the agent record and inbox on a second clone.
45. Inactivity, a lost frontend connection, an unclaimed review, or a reviewer decision must not change the agent state. Stale agent/attempt versions cannot resume or complete work.
46. A Completed agent receives another task and becomes Working while a reviewer accepts the previous task; finalization of the previous Finished attempt cannot steal the new Working slot.
47. A ParkAgent request while Assigned, Ready, or Working fails without changing focused assignment, slot or phase; Completed to Parked succeeds with a reason.
48. Late changes requested on Finished Task A while owner works on Task B queue the same Task A attempt with a new generation, without preempting B. Reissue becomes possible only after the owner is idle.
49. Multiple unacknowledged review decisions remain individually discoverable; acknowledging one does not acknowledge another. Repeated operation UUIDs are idempotent.
50. Explicit orchestrator recovery of a Working assignment records a checkpoint and invalidates the former generation without changing task ownership or source reviews.
51. Plan accepted reviewed A..C against remote target A; ensure it does not push C and inspection reports PendingPush while remote remains at A.
52. Pushing exact reviewed C changes inspection to ReadyToRecord; RecordIntegration re-verifies remote head and atomically records proof once; CloseTask is independently version-gated.
53. An unapproved remote target head D following approved C is rejected, as is an incorrect expected target SHA at planning. Neither causes task Integrated, source edits or ledger evidence forgery.
54. Source ref/target rewrite or missing ref causes fail-closed verification; verification is never inferred from a local-only commit.

Run full client/server tests, full Git integration fixtures, format checks and an actual **backup restore** before live migration. Never simulate acceptance by a passing mock push alone.

## 12. Implementation sequence and completion gate

1. **LedgerStore prototype:** approved project remote binding; orphan init; exact fetch/read; temporary-index commit; fast-forward push; operation-index dedup; lost-response recovery; trusted ref pinning. Prove with two clones *before* exposing write tools.
2. **Domain model:** task/attempt/agent and independent versioned review request/revision/claim records, transition validators, per-agent slot atomicity, deterministic inbox/reviewer queues, immutable review refs/specs/checkpoints and schema-versioned serializers.
3. **Task tools and UI:** full MCP command surface, read snapshot pagination, spec editing/import/export, source bindings and local retry key storage for TUI; replace V1 read paths.
4. **Integration workflow:** separately verified source and ledger commits, reviewer acceptance, pending source actions, push verification and worktree retirement.
5. **Migration and protocol 4.0:** dry-run with explicit conflict resolution, Git mirror backup, global old-writer gate, controlled client rollout, independent clone verification and source cleanup later.
6. **Hardening/acceptance:** forced rewrite and outage recovery, cross-platform security tests, 10-client contention, full restore drill and live smoke tests.

**Ready for implementation** means the schema, transitions, tool expectations, failure behavior and migration gates above are implemented in this order. The isolated V2 prototype now uses independent review requests rather than ReadyForReview as an attempt phase; native MCP exposure still requires full task/checkpoint/evidence schemas, permissions and migration gates. **Done** means a task can be managed from any authorized project client/worktree without branch-derived ownership duplicates; concurrent agents cannot violate slot/assignment rules; lost pushes are recoverable by operation ID; historic task Git state remains intact; and the Jarvis server contains no new orchestration database or scheduling state.

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

**Review redesign (RV3):** Claimed review scope is immutable, Unclaimed request revisions are replaceable, and agent Working status is independent. RV3-01/02/03 are addressed in the *design* by pinned base/head plus durable review refs and integration restricted to accepted patch sets. RV3-04/05/06/07/08/09 are design requirements in Sections 3–4, 7–8 and tests 29–38; core implementation is verified by focused disposable-remote tests; full protocol implementation and an independent review remain pending. See `docs/reviews/2026-10-28-commit-review-spec-review.md`.

**Remaining deliberate tradeoffs:** a single remote ref serializes project writes; remote connectivity is required for authoritative updates; user-controlled Git credentials—not logical agent names—define trust; no client can prove a remote's full history without previously trusted state/host protection/backups; source push and ledger push are separate, explicitly reconciled operations. These are accepted design constraints, not unresolved questions.
