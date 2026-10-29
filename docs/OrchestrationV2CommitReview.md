# Orchestration V2 — independent source review workflow

**Status (2026-10-28):** The isolated F# prototype now implements the core single-owner review lifecycle and has disposable-remote tests. It is **not yet connected to MCP/TUI**, and the live V1 task system remains unchanged.

The authoritative orchestration state stays on one remote orphan Git ledger branch. Source review refs belong to the normal project source remote. The Jarvis server does not store review/task state.

## Core contract

Each task has exactly one implementation owner and one current implementation attempt. The attempt pins a **review baseline** once, prior to the first review. Every subsequent review of that task, including after a rejection, covers the *entire cumulative tree delta* from the pinned baseline to its submitted head. Previously rejected changes cannot disappear by using their rejected head as a new review base.

A review request is independent of the implementation attempt's Working state. Submitting it does not release the agent's slot. While it is Unclaimed, the sole owner may replace its revision. Once a registered, non-owner reviewer claims it, the exact revision is frozen. Claim and replacement use expected request versions and the ordinary ledger fast-forward publication race; the loser must refresh. Only one active review request per attempt is allowed.

Every code revision is reachable via a create-only `refs/heads/jarvis-review/<task>/<request>/r<n>` source ref. Publication verifies the actual remote OID and never advances an existing ref, even if normal Git would permit a fast-forward. A ref pushed before a losing ledger mutation is an unauthoritative orphan candidate; it must not enter reviewer queues. The source publication and ledger transaction cannot be atomic across refs. Stable operation UUIDs allow safe replay of accepted ledger changes without publishing another revision.

## Available isolated prototype operations

`CreateTask`, `RegisterAgent`, `AssignTask`, `BeginAttempt`, `BindReviewBase`, `SubmitForReview`, `UpdateReviewRequest`, `ClaimReview`, `WithdrawReview`, `ReassignReviewer`, `ReviewDecision` (Accept/RequestChanges), `FinalizeImplementation`, `AcknowledgeReviewFeedback`, `GetTask`, `GetReview`, `GetReviewerQueue`.

- Submit creates Unclaimed revision 1, sets `activeReviewRequestId` and keeps the owner Working. The review request and current revision are separate Git ledger records.
- Update appends a revision while Unclaimed. The older revision/ref remains unchanged. Retries discover their prior operation first.
- Claim is open to any *registered* reviewer except the task owner. The reviewer claims the exact version/revision and fetches the pinned source ref. Other reviewers cannot decide on it.
- A claimant may hand off its frozen review to another registered reviewer; the owner cannot silently reopen or alter it. An unclaimed request may be withdrawn by its owner; a claimed request may be withdrawn only by its claimant.
- Accept records approval of the exact cumulative source revision; RequestChanges records feedback. Neither transition interrupts an owner already Working. The active review pointer is cleared; earlier review history remains.
- FinalizeImplementation is separate: it requires acceptance of the **latest** review request, no active review, and the matching pinned cumulative source head. Only then does it mark Implemented and release the Working slot. Source integration and task closure remain separate.
- Feedback has a decision sequence and explicit acknowledgment sequence; ordinary task reads do not auto-acknowledge it.

## Coverage

The fixtures run against a disposable bare Git remote and two independent clones. They verify create-only refs, cross-device claim/fetch, replacement before claim, claimed revision immutability, agent-slot retention, claim/update race, owner/reviewer separation, source-ref mismatch rejection, reviewer handoff, withdrawal, operation UUID idempotency, cumulative resubmission after rejection, stale-finalization rejection, and regular ledger concurrency/rewrite handling.

## Explicitly incomplete

This is a **core domain prototype**, not the full protocol-4.0 feature. Do not expose it as production Jarvis task handling yet. Remaining work includes:

- Full spec HTML/assignment-generation/checkpoint and reviewer-evidence schemas (currently source summary and pinned base/head are tracked, not the full final checkpoint).
- Native MCP DTOs, reviewer/owner inbox views and tool permissions.
- Durable client configuration/trust binding, crash-time pending intent storage, checked orphan-ref cleanup and reviewer-release/reclaim behavior.
- Complete park/block/cancel/source integration transitions, migration and global V1 writer cutoff.
- More adversarial tests for remote outages, host ref protection, backup/restore, source rewrites and larger contention.

The older exclusive `ReadyForReview` attempt workflow and its tests have been replaced in this isolated client prototype. The pre-cutover scope above remains intentionally excluded.


## Experimental MCP bridge (protocol 3.20)

The branch includes explicit, **non-V1** MCP methods: V2ReviewTask, V2ReviewDetails,
V2ReviewerInbox, V2SubmitReview, V2ReplaceReview, V2ClaimReview, V2DecideReview,
V2WithdrawReview, V2ReassignReview and V2FinalizeReview. These are human-oriented
aliases for two typed wire commands (V2ReviewRead, V2ReviewWrite). Every mutation
takes a caller-owned **operationId** and the exact expected entity version.

Client routing requires an explicit local binding, stored **outside the repository** at
the path computed by ReviewV2Binding.pathForRoot. On Linux this is typically
$XDG_CONFIG_HOME/Jarvis/ledger-v2/<sha256-of-absolute-checkout-path>.json
(or ~/.config/Jarvis/... when XDG_CONFIG_HOME is not set). The config is created
only by explicit local setup; no tool writes or guesses it automatically. Example:

{
  "schemaVersion": 1,
  "enabled": true,
  "projectRoot": "/absolute/path/to/a/registered/project/checkout",
  "projectId": "existing-ledger-project-UUID",
  "ledgerId": "existing-ledger-UUID",
  "epoch": 1,
  "remoteName": "origin",
  "approvedFetchUrl": "exact-git-remote-fetch-URL",
  "approvedPushUrl": "exact-git-remote-push-URL"
}

These identifiers must be read from an **already approved, existing** V2 ledger;
the MCP bridge does not initialize a new remote ledger or select a source
remote without authorization. Both V2 reads and writes fail closed if the
binding is absent, disabled, malformed or mismatched. The client then performs
its normal remote/identity/ancestry verification before operations.

OAuth requires workspace:read for V2 query methods and git:write for V2
mutations. The Jarvis client applies its existing local Git permission policy.
All V1 task commands retain their original semantics; the bridge never
falls back to worktree-local task files.

This is a development capability, **not a complete V2 cutover**. Fresh projects
still need V2 task/spec creation, agent registration, assignment, baseline
binding and ledger initialization through the approved setup/migration workflow
before the review commands become usable. Native reviewer/owner TUI views,
complete checkpoint evidence schemas and protocol-4.0 migration remain pending.


## Experimental task setup and canonical agent inbox (protocol 3.21)

The same opt-in project binding now exposes:

- V2CreateTask atomically creates tasks/<id>/task.json and canonical tasks/<id>/spec.html in one remote ledger transaction. HTML must be nonempty and no more than 128 KiB. The caller's stable operation UUID makes retries safe.
- V2ReviseTaskSpec edits the title and HTML at an expected task version only while the task is Open and unassigned. Assigned-task changes require an explicit future assignment generation.
- V2RegisterAgent, V2AssignTask, and V2BeginAttempt register stable agent labels, create the sole implementation attempt, and acquire the one Working slot. The attempt pins the canonical spec blob OID and records issuance sequence and priority.
- V2BindReviewBase pins a verified source commit and target ref against the exact Working attempt and generation, preserving one cumulative review base.
- V2AcknowledgeReviewFeedback acknowledges a specific review decision sequence without changing Working state.
- V2AgentInbox returns a single snapshot projection containing working, runnableQueue, pendingReviews, availableReviews, parked, blocked, acceptedPendingIntegration, slotAvailable, unassigned, the agent record and ledger commit. It cross-checks the Working slot with the authoritative agent record; runnable queue sorting is deterministic (priority descending, issued sequence ascending, task ID, attempt ID).
- V2ReviewTask now returns the canonical spec HTML and exact blob OID alongside the task/attempt state.

V2TaskRead and V2TaskWrite are separate typed wire commands. Human-facing task and inbox MCP aliases require workspace-read or Git-write permissions respectively. All reads and writes still require an explicit local V2 binding and never fall back to V1.

The new operations were exercised on a disposable bare Git remote from two clones, including an opt-in client-adapter path from task creation to review submission. No production ledger initialization, migration or deployment was performed.

**Still missing before cutover:** versioned assignment instructions and receipts, durable local pending-intent journal, explicit park/block/reissue/cancel transitions, source integration evidence, full review checkpoint evidence and V1-to-V2 migration. Protocol 3.21 is an additive experimental capability, not the planned breaking protocol 4.0 deployment.


## Explicit agent lifecycle (experimental protocol 3.22)

V2 agents now have one authoritative state, independent of task phase and
review status. A registered agent begins Parked. The orchestrator assigns a
task to a Parked or Completed agent, atomically changing it to Assigned,
binding its focused attempt and retaining its task-spec OID. BeginAttempt
changes Assigned to Working and acquires the single execution slot.

V2YieldAgent is an explicit Working to Ready transition. It records an
immutable task checkpoint (summary, next action, generation, ledger sequence),
changes the attempt to Paused and releases the execution slot. The operator
may prompt a Ready agent to continue; V2ResumeAgent checks both the agent
version and attempt version/generation, then restores Working on the same
assignment. No inactivity heuristic or browser-session timeout changes state.

V2CompleteAgentWork is an explicit Working/Ready to Completed transition
with a final checkpoint. It changes the attempt to Finished, releases the
slot and signals that the orchestrator can allocate fresh work. Review
requests and source integration may remain outstanding. V2ParkAgent is a
deliberate orchestrator action from Completed/Parked to Parked, with a
reason; it refuses to steal an Assigned, Ready or Working agent.

V2AgentInbox includes the focused assignment, the five-state agent record,
operatorActionRequired (Assigned/Ready), and
needsOrchestratorAssignment (Completed), alongside the independently
projected task/review responsibilities. Task integration is a separate
operation: a reviewer may finalize an accepted Finished attempt after its
agent has already started a different task without modifying the agent's
current Working slot. Review acceptance itself never changes agent state.

Each mutation uses a caller-stable operation UUID and required expected
versions; task+agent+checkpoint changes are one canonical ledger transaction.
These transitions were verified with disposable two-clone Git fixtures and
the opt-in V2 command adapter. This remains isolated from V1 and has not
been deployed or migrated.


## Assignment reissuance and deliberate recovery (experimental protocol 3.23)

Review decisions now write distinct canonical notification records under each review request. The agent inbox exposes unacknowledgedFeedback across all owned tasks; acknowledging one exact decision sequence does not hide another. The notification record and its acknowledgment are Git-ledger state, not ephemeral UI state. A retry using the same operation UUID remains idempotent.

When changes are requested while the owner is Working or Ready on the same attempt, that agent keeps its focused assignment and receives a notification. If the attempt is Finished, the reviewer decision requeues **that same attempt** at assignmentGeneration + 1, preserving its owner, source base and pinned specification. It does not change the agent state or preempt work on another task.

V2ReissueQueuedAttempt is an explicit orchestrator selection for a queued generation. It requires exact attempt and agent versions, the current generation, an idle agent (Completed or Parked), and a reason. The agent becomes Assigned and can be started by the operator. Reissue never creates a second owner or overwrites a newer assignment.

V2RecoverAgentAssignment is an explicit orchestrator-controlled restart of an Assigned, Ready or Working agent. It writes an immutable Recovery checkpoint with the reason, advances the same attempt's assignment generation, releases the old Working slot and changes the agent to Assigned. This is not an inactivity detector or automatic response to a harness stall. Old-generation commands fail, while source and review records remain intact.

Both commands are opt-in V2 Git-write MCP tools with normal stable operation UUIDs and local permissions. Migration, live V1 and production server state are untouched.


## Explicit source integration tracking (experimental protocol 3.24)

The V2 source integration flow is separate from review acceptance, agent completion and closing a task. It introduces a durable integration action record pinned to the accepted review revision and immutable source review ref, the cumulative base/head, approved remote, normal target ref and the expected remote target OID.

- **V2PlanIntegration:** requires finalized Implemented task and an exact accepted review, with no existing integration action. The client verifies the approved immutable review ref, the currently advertised/fetched remote target OID and required Git ancestry, and writes a PendingSourceAction record in the canonical ledger. It does not change the source branch or push.
- **V2MarkIntegrationPendingPush:** explicitly records that source integration is waiting for a remote push. It never pushes by itself.
- **V2InspectIntegration:** read-only observation of the approved remote target, reporting PendingPush, ReadyToRecord, TargetChanged, Recorded or RecordedTargetMoved along with the durable plan and exact observed SHA.
- **V2RecordIntegration:** verifies the current source remote target equals the **exact reviewed and accepted commit SHA**, and revalidates the immutable review ref before recording UTC verification time, remote OID, method and ledger sequence in one transaction. Only then does the task become Integrated.
- **V2CloseIntegratedTask:** a distinct version-checked transition to Closed, with retained review/integration evidence and no effect on agent state.

**Strict fast-forward limitation:** This initial verifier accepts only the approved commit as the exact remote target, with the planned target an ancestor of that approved head. It deliberately rejects unreviewed later commits, different merge-commit SHAs, cherry-picks and unverified patch equivalence; an explicit equivalence-proof implementation is future work. A successful local merge or unpushed commit is not evidence of remote integration. If the source push succeeds but the ledger transaction fails, callers retry verification using the *same operation ID*, not the source push. The client fetches remote branches into disposable private refs for inspection and never modifies source working trees implicitly.

The five methods use separate V2IntegrationRead and V2IntegrationWrite wire contracts, with local binding validation and Git-write approval. Disposable bare-remote/two-clone tests verify the full plan/pending-push/push/record/close lifecycle, exact source refs, stale expected targets, extra unreviewed commits, idempotent replay and fail-closed opt-in. No live V1 migration or deployment was performed.


## Specification-backlog implementation foundation (experimental protocol 3.25)

The new backlog at `docs/tasks/2026-10-28-v2-backlog.md` remains authoritative for remaining V2 implementation outcomes. This slice begins tasks V2-01, V2-02 and V2-03, but **does not close any of those tasks**.

**Schema safety (V2-03, partial):** persisted task/attempt/agent/review/revision/checkpoint/feedback/integration records now include a canonical `schemaVersion: 2` marker. Readers reject absent/unsupported entity schema versions, duplicate or missing required JSON fields and unknown lifecycle/state/status values; no automatic compatibility interpretation is allowed. Task HTML is limited to 128 KiB UTF-8 consistently at the authoring and ledger-transaction layers; active browser elements, script URLs and event handlers are rejected when authoring. Full content pagination, malformed UTF-8 Git transport safeguards and the complete normative evidence schema remain open.

**Ledger trust (V2-01, partial):** explicit opt-in `V2InitializeLedger`, `V2BindExistingLedger`, `V2RebindLedgerRemote`, and `V2LedgerHealth` tools are routed through separate typed V2LedgerRead/V2LedgerWrite messages. An existing ledger bind requires caller-supplied project/ledger IDs, remote fetch and push endpoints, an independently supplied expected commit ancestry, and public/unknown visibility acknowledgment. A verified head is stored in per-user configuration and advanced on subsequent verified reads. Changed endpoints refuse ordinary read/write. Initialization is never implicit; explicit rebind leaves a local previous-binding backup and only permits the same logical project/ledger epoch. Provider branch-protection verification, source-remote visibility discovery and restoration audits remain open.

**Operation status (V2-02, partial):** `V2GetOperationStatus` looks up a caller-provided operation UUID on a freshly verified remote snapshot. It returns `Applied` with the canonical indexed record, or `NotFoundAtVerifiedHead` without pretending an uncertain push cannot later complete. Full original typed result retention, durable caller-side pending intents and typed error transport remain open.

These commands require their normal read or explicit Git-write permission scopes. They were exercised only against disposable test repositories. V1 task writers and the live server remain unchanged; no production ledger was initialized, pushed or migrated.

## Durable V2 operation recovery slice (experimental protocol 3.26)

V2-02 now includes a stable **original write receipt** in every generic or domain operation index entry, committed atomically with changed entities. Each operation records `schemaVersion: 2`, the canonical original request, the original result/receipt JSON and digest, affected entity paths/content hashes, operation ID and ledger sequence. Same-UUID retries on another clone recover the same result payload even after the remote ledger advances; the current remote head remains a distinct field. Canonical request hashes normalize JSON object key order and whitespace, and reject duplicate case-insensitive members.

The client now has an opt-in **pending-operation journal** for V2TaskWrite, V2ReviewWrite and V2IntegrationWrite. It persists exact original command input under user configuration before any source/ledger mutation; successful verified outcomes clear the entry. Remote uncertainty or validation errors keep the entry available for inspection and safe replay. `V2PendingOperations` is a read-only MCP view of these local pending intents, independent of remote connectivity; `V2GetOperationStatus` remains the authoritative remote operation UUID check. Different input under a reused local UUID is refused. Pending files are private (0700 directory/0600 file on Unix), checksum-validated, bounded and protected against symlinks. Journal entries are **not** an offline authoritative write queue.

Git contention now uses bounded exponential backoff with jitter. Ledger failure variants propagate through `V2LedgerFailure` typed MCP errors, retaining their code (and operation UUID on journaled mutations), instead of being collapsed to generic validation messages. The protocol is bumped to 3.26 because the AgentError schema changed.

**Remaining V2-02 work:** domain-specific original result structs (e.g. checkpointId/attempt version) rather than a generic content-hash receipt; explicit operation reconciliation/acknowledgment UX and deletion of already-accepted stale journal entries; full 8-writer contention and lost-response acceptance fixtures; verified originating commit evidence and structured observed head on all errors. Tasks 01 and 03 are still active as well. All work remains on the isolated branch without production migration or deployment.

## Pending-operation reconciliation and recovery (experimental protocol 3.27)

The V2 operation durability contract now includes two explicit MCP actions:

- `V2ReconcilePendingOperations` (read-only): verify the canonical Git ledger head once, then inspect every locally journaled V2 task/review/integration operation at exactly that head. Check project/ledger/epoch, operation UUID, canonical operation kind and all relevant command inputs. Report `Applied`, `NotFoundAtVerifiedHead`, `IdentityMismatch` or `BindingMismatch`. Do **not** rewrite or discard local pending evidence on inspection.
- `V2AcknowledgePendingOperation` (explicit, local write): require caller-supplied operation UUID and exact confirmed remote `requestHash`, re-fetch and validate the same canonical operation, then retire only its local journal entry. Repeated acknowledgments with that proof are safe. Missing/unaccepted or mismatched operations cannot be acknowledged.

Task, review and integration writes now also recover an `OutcomeUnknown` immediately if a follow-up remote fetch proves the *same* logical request was accepted (including full action input comparison). If no exact operation exists, return a structured `OutcomeUnknown` with caller UUID and the last verified canonical head while preserving the pending journal. No remote operation is inferred from a local branch or source worktree.

An eight-clone disposable bare-remote contention fixture verified all independent operations and their original receipts, while source HEAD/index remained unchanged. Additional tests cover exact remote acceptance after a lost response, unaccepted pending operations, changed payload on the same task, mismatched operation identity, foreign ledger ID, explicit acknowledgment/retry, and pinned review submission.

The full V2-02 task remains active until richer domain-specific result receipts, originating-commit provenance, full failure/restore acceptance, and complete operator reconciliation UX are verified. No production migration, rollout or V1 changes were performed.

## Immutable domain result receipts and original commit provenance (experimental protocol 3.28)

The V2-02 operation result contract now persists **schema-versioned entity results** together with the existing immutable operation receipt and change hashes. Each domain mutation includes a durable `entityResults` array: entity path/type; original task, attempt, agent or review version and lifecycle/phase/state; assignment generation; checkpoint kind and sequence; reviewed revision, head SHA and immutable review ref; and integration verification fields when applicable. These are captured directly from the canonical entity changes in the same transaction and **do not change** when later operations advance the ledger.

Successful V2 task, review and integration MCP mutation responses expose `originalResult` as a structured value rather than requiring callers to parse `originalResultJson`. `V2GetOperationStatus` also includes the structured original result and `originatingCommit`. The origin is independently recovered from the verified Git history of the immutable operation index entry, requiring its original Git blob to remain unchanged and the commit to contain matching operation UUID and request-hash trailers. This avoids self-referential commit hashes and distinguishes the original operation commit from a newer fetched ledger head.

Disposable two-clone tests verify unchanged original task state after assignment, review revision SHA/ref identity, assignment/agent state and checkpoint sequence, replay after subsequent operations, MCP status projection, and failure on forged commit-provenance hash. Result envelope schema is additive but the experimental protocol is advanced to 3.28.

**V2-02 remains active:** broader remote-failure/restore tests and a final spec acceptance audit remain. No live rollout, V1 mutation, production ledger initialization or deployment occurred.

## V2-02 remote failure recovery acceptance fixtures

The durable operation recovery tests now include two additional disposable-remote adversarial scenarios. First, the canonical bare remote is made unavailable during a journaled task write: Git returns a typed `RemoteUnavailable` error with the original UUID, `V2ReconcilePendingOperations` fails closed, and the original journal entry remains available for later retry. After restoring the remote, inspection reports the operation missing at the verified head; a retry with the **same UUID and original input** publishes the operation exactly once and clears the local journal only after confirmation.

Second, the remote ledger branch is force-rewritten to an unrelated source commit while the local client holds a trusted ledger head and a pending write. Reconciliation refuses the rewritten branch with typed `LedgerIntegrityError`; the pending entry is retained. Restoring the trusted ledger branch permits read-only reconciliation again without importing the rewritten history or mutating a source checkout.

Together with the existing eight independent writer, mismatched payload, lost-response, hash-gated acknowledgment, original domain result and provenance fixtures, these establish the core V2-02 durable recovery contract. Security/authorization and future TUI presentation remain independently tracked by V2-01 and V2-11; broader systemwide restore/cutover acceptance belongs to V2-13/V2-16. No production migration/deployment was performed.

## Ledger trust anchor serialization and remote policy diagnostics (V2-01, protocol 3.28)

The V2 checkout-local approved binding and `highestTrustedHead` are now updated under an exclusive **cross-process file lock**, not by competing unconditional replace operations. Bound reads re-read and verify the current identity/remote under the lock before fetching; a verified fast-forward is persisted via an atomic private temporary file replacement, with Linux/macOS 0600 file mode. Concurrent readers cannot lower the stored trusted head. BindExisting rechecks absence under the same lock. Rebind re-reads the original binding under the lock and requires **both** its persisted previous trusted head and the independently supplied new trust anchor to be ancestors of the new remote head. It cannot silently trade an existing client’s trust history for an independent ledger that happens to share UUIDs.

`V2LedgerHealth` now reports conservative remote protection diagnostics. A local bare Git destination can expose whether `receive.denyNonFastForwards` and `receive.denyDeletes` are `Enforced`, `NotEnforced`, or `NotConfigured`; for hosted/unknown destinations, branch protection is `Unknown`. Git transport cannot prove repository visibility or commit-signing requirements, so these fields are always reported as unknown rather than claiming verified protection. Explicit public/unknown visibility acknowledgment remains mandatory on initialization, binding and rebinding. Approved HTTPS remote URLs containing user information or query parameters, or SSH URL password/query credentials, are rejected before they can enter the local binding.

Disposable Git tests cover six simultaneous binding readers behind a held lock, monotonic trust advancement across later writes, refusal of a divergent remote with matching ledger IDs, bare remote policy changes, secret-bearing endpoint refusal, changed push URL, remote ledger deletion and restored verified reference. The source working tree remains unchanged. Outstanding work: provider-specific visibility/branch protection verification, signed-commit enforcement or explicit refusal when required, and audited epoch/restore rebind (V2-13). The production V1→V2 cutover is not authorized.

## V2-04 immutable assignment generations and acknowledgment (experimental protocol 3.29)

Assignment ownership is still one implementation owner per task. Every issued assignment generation now has a canonical immutable ledger record under `tasks/<task>/attempts/<attempt>/assignments/<generation>.json`, including owner, issuer, issue sequence, reason, instructions, pinned spec blob OID, previous generation and any pinned source baseline/target. Initial assignment, review-requested changes after Finished, explicit recovery, and completion-triggered requeue issue a new generation rather than modifying older instructions.

`V2AcknowledgeAssignment` atomically records an immutable receipt under `assignments/<generation>.ack.json`, tied to the exact owner, generation, issuance sequence and spec. The new `V2GetAssignment` read API shows the historical or current issuance plus its optional receipt from a verified Git snapshot. `BeginAttempt` from an explicitly Acknowledged generation verifies its receipt; direct Begin from Queued writes the receipt atomically in the same transaction. Every Working assignment thus has durable acknowledgment evidence without requiring a separate preliminary RPC. Resume and other transitions verify the current generation. Same-UUID retries remain idempotent.

`V2ReissueAssignment` replaces only an **unstarted** Assigned generation, with new immutable instructions and optional canonical HTML spec adoption. If the spec changes, the task SpecVersion/Version, HTML blob, attempt pin and next generation are updated in one Git-CAS transaction. Prior generation instruction and acknowledgment records are preserved, along with the old spec blob reachable from Git history. A Working or Ready owner is never preempted; review changes made while Working remain feedback without immediate reissue. V2-09 retains separate responsibility for retrieving a historical spec body by blob OID.

Verification includes exact acknowledgment and implicit begin receipts, stale version/generation failures, recovery from Ready, historical spec pin after replacement, idempotent explicit reissue, reviewer-driven requeue, and independent concurrent assignments. The V2 MCP server catalog now contains `V2GetAssignment`, `V2AcknowledgeAssignment` and `V2ReissueAssignment` with appropriate permission metadata. Full client **329/329**, server **62/62**, and `git diff --check` pass. Protocol bumped to 3.29; no production initialization, migration, push or deployment.

## V2-05 immutable checkpoints and review evidence (experimental protocol 3.30)

V2-05 now has a Working-only `CheckpointAttempt` mutation with exact task/attempt/agent versions and generation. Each progress checkpoint is an immutable task-local record with sequence, owner, summary, next action, optional verified source-commit OID, and optional **owner-reported** evidence. The mutation advances the attempt version while keeping its `Working` phase and occupied execution slot; it does not yield or falsely complete the agent. `V2CheckpointAttempt` and `V2GetCheckpoint` expose this through the opt-in MCP interface. Checkpoint sequence is an `int64` end to end.

`SubmitForReview` and `UpdateReviewRequest` now atomically append a distinct immutable `Submission` or `Revision` checkpoint with their review request/revision ledger changes. Each revision pins the attempt's spec blob OID, independently pinned source endpoint fingerprint, approved fetch/push URLs, exact base/head/source and published review ref, exact published source OID, checkpoint sequence, and optional owner-reported test evidence. If no test evidence was provided, the revision explicitly records `NotProvided`; it cannot masquerade as verified tests. Existing submission methods continue with `NotProvided`, while `V2SubmitReviewWithEvidence` and `V2ReplaceReviewWithEvidence` allow explicit reports. Replacements create a distinct r2 record and checkpoint and leave historical r1 evidence intact. A claimed revision cannot be replaced.

Review decisions now append immutable `ReviewDecisionEvidence` under `reviews/<request>/decisions/<sequence>.json` in the same ledger commit as the verdict, task feedback, and current review state. The evidence binds the exact claimed revision, reviewer ID, immutable review ref/head, spec OID and source remote. It separately records review **verdict**, reviewer-reported **inspection**, reviewer-reported **tests**, and test details. A report of tests requires nonblank details; accepting code without reporting inspection/tests records both flags `false`. `V2ReviewDecisionEvidence` and `V2DecideReviewWithEvidence` expose these facts. The legacy decision method remains supported with `inspected=false; tested=false` and does not invent execution evidence.

Tests verify progress checkpoints preserve the Working slot, stale generations fail, unverified source hashes fail, r1/r2 checkpoint isolation and idempotent replay, pinned source and spec metadata, reviewer decisions cannot claim tests without details, acceptance does not imply tests, and deleting a published review ref prevents claim/acceptance. Source repository identity was finalized in V2-08 as a verified approved-endpoint fingerprint, independent from the ledger project UUID, including source base/remote ancestry, cross-clone identity and immutable review provenance. No live migration or deployment.

The V2-05 recovery adapter also recognizes pending review commands written before explicit test-evidence fields existed: absent fields are treated as equivalent only to a blank/false evidence declaration. An earlier pending command **cannot** be reconciled against a later operation that claims inspection or testing it never declared. This preserves same-UUID crash recovery through experimental protocol changes without fabricating evidence. The retained operation request hash and original result remain authoritative.

**Verification and status:** targeted progress/review/ref-loss/decision fixtures passed; the final full client suite passed **333/333**, and server suite passed **62/62**. `git diff --check` was clean. V2-05 is **completed** following V2-08 independent source provenance and adversarial verification. Overall V2 orchestration is not yet complete.


## V2-08 source repository and worktree provenance (commit b4ff0dd, protocol 3.31)

Source binding now records an independent versioned fingerprint of approved Git
fetch/push endpoints, exact source branch/base and target ref, without assigning
ownership to a workstation checkout. Git has no standardized cryptographic
remote-repository UUID, so this identifies approved endpoints; explicit
remote trust remains the security boundary. The base must be an ancestor of the
source branch and the fetched approved remote target. Changed endpoints fail
closed. Source review revision and integration records retain the source ID
and both approved remote URLs. Historic frozen revisions remain reviewable
after working-branch moves or explicit version-guarded source rebinds.

Worktree inspection is read-only, distinguishes missing source refs, missing
or prunable Git worktree registrations, checked-out/dirty worktrees, detached
HEAD and stale detached HEAD, and never frees an agent's Working slot. Separate
MCP methods provide BindAttemptSource, RebindAttemptSource and
InspectAttemptSource. Stale generation conflicts and unapproved remote changes
are rejected. Disposable Git test cases cover raw directory moves, Git repair,
normal worktree deletion/recreation, branch deletion/rename, claimed review
frozen provenance, two-clone source identities and stale detached HEAD.

Verification: Client.Tests 336/336, Server.Tests 62/62, git diff --check clean.
V2-05's remaining source provenance acceptance is satisfied; both V2-05 and
V2-08 are now completed source-backed backlog tasks. No remote push, server
upgrade, production ledger initialization or V1 migration occurred.

## V2-06 frozen review claim recovery and orphan ref cleanup (commit d518ac5, protocol 3.32)

The reviewer claim may be explicitly released, with the request remaining
Claimed and pinned to its exact original revision while awaiting orchestrator
reassignment. Reviewer action never changes an implementation agent's
Working execution slot. Claim, release, reassignment, withdrawal and
decision now append immutable event records with operation UUID, actor,
request-version precondition, revision/ref/head/spec and reason. The legacy
reviewer handoff request form retains its original idempotency payload.
Reviewer claim and reassignment projections are exposed in opted-in V2 MCP.

An unindexed source review ref may be reserved for cleanup only by a
successful, independent canonical ledger transaction with an exact observed
SHA, actor and reason. This permanent tombstone prevents a delayed
Submit/Update from later adopting that source ref. Explicit remote deletion
is separately requested, uses a Git expected-OID lease and verifies the
remote afterward. Claimed/accepted/withdrawn/superseded refs represented by
immutable ledger review revisions are never orphan candidates. Inspection
also retains the tombstone after source deletion and reports absent or
retargeted source refs. No implicit garbage collection or broad ref deletion.

Disposable Git tests cover competing reviewers, released-claim recovery
despite missing source ref, stale decisions, immutable audit, source-ref
retargeting, accepted-ref protection, late submission after reservation,
source deletion retry and end-to-end opted-in client command routing.
Final client suite **340/340**, server **62/62**, whitespace audit clean.
The earlier quiet client suite reported one unidentified failure (339/340),
not reproduced on the full TRX rerun; do not represent it as a proven fix.
Security remains a V2-11 concern: actor labels are not cryptographic
authorization. V2-06 is completed in source-backed task files, not imported
to a production orphan Git ledger. No deployment, push or cutover.
