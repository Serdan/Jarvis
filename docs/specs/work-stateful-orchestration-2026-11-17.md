# Jarvis Work System — consolidated architecture and migration contract

**Captured:** 2026-11-17
**Status:** Operator design direction recorded for alignment. This document does **not** authorize production activation, migration, deployment, live unattended scheduling, or retroactive reinterpretation of accepted reviews.
**Relationship to existing work:** Complements and corrects docs/specs/work-queues-and-ephemeral-executions.md, the canonical work-queues-ephemeral-executions epic, and composable-work-units. Preserve independently accepted and integrated work as evidence; migrate selectively rather than rebuilding it blindly.

> **Current controlling amendment (2026-11-26):** Section 16 is authoritative for Work/execution/resource boundaries, Jarvis 1.0/2.0 branch strategy, and clean-slate cutover. Sections 1–15 are preserved historical decisions; where they conflict with Section 16, the dated amendment supersedes them. This document still does not authorize production activation, migration, deployment, or Scheduler operation.

## 1. Purpose and governing principles

Jarvis manages durable engineering work, not persistent named agents. A non-model Scheduler coordinates replaceable, ephemeral model executions against canonical work. Lead exercises engineering judgment—scope, approval, priority, dependencies—rather than performing mechanical rounds of assignment and resumption.

Keep:
- One authoritative description of work, engineering intent, constraints, requirements, and history.
- A declarative graph of outcomes and dependencies, plus a flat derived projection of actionable, eligible work.
- Narrow, deterministic scheduling and state transitions; intelligence for planning, engineering, review, investigation, and recovery.
- Versioned and idempotent mutations, explicit decision evidence, Git source provenance, and recovery of uncertain external effects.
- Minimal launch prompts. Executions retrieve the canonical task, role guidance, checkpoint, context, and approved scope from Jarvis instead of receiving Lead-rewritten specifications.
- Operator control of deployment and live cutover. The Work implementation remains default-off until explicitly approved.

Do not preserve server statelessness at the cost of slow and complex remote-Git transactions. A small authoritative local database is now warranted.

## 2. Durable work graph

One general composable Work model presents the domain names Project, System, and Feature. Systems can contain recursive subsystems. These are durable outcomes with identity, specification, scope, acceptance criteria, constraints, decisions, evidence, history, and child work. Ephemeral model sessions are not work owners.

Composition is not itself a scheduling dependency:
- Each required child must reach the required accepted/completed outcome for its parent to be accepted.
- Completing every child does not automatically accept its parent; parent-specific validation remains independent.
- Required versus optional/cancelled scope must be explicit and versioned.
- Directed hard prerequisites can cross Systems. Other informational relationships remain distinct.
- Cycles and graph/source-coverage problems must be surfaced, never silently treated as runnable.
- Progress and blockers derive from canonical graph and outcome evidence, not manually entered percentages or the number of running sessions.
- A parent may spawn justified child work; such graph changes are explicit, reviewable and versioned. Out-of-scope substantial blockers become linked Work, not recursive uncontrolled execution.

A Project or System can be fully described and dormant until dependencies make it actionable. No persistent agent or execution slot is required for dormant Work.

## 3. Task proposal and authorization lifecycle

Proposed tasks/work units pass through:

    Draft -> Proposed -> Open -> active work / verified outcomes -> Closed
                 |
                 +----------> Draft (Lead returns for revision)

- Draft: editable and durable; the operator/agent can iterate on the spec; no automatic Lead review demand and no execution demand.
- Proposed: explicit submission produces a Lead-compatible review/planning work item. Lead assesses scope, acceptance criteria, dependencies and, primarily, priority; Lead may approve or return to Draft.
- Open: approved and eligible to produce execution stages when readiness prerequisites are met. Open does not mean immediately runnable.
- Active/Completed/Closed/Cancelled: existing domain and stage lifecycle semantics continue, with distinct reviewed, integrated and parent-accepted evidence.

Default interactive creation to Draft. Deliberately pre-authorized/internal work producers may create an already-Open item through an explicit authorized path, not by accident. Routine review and integration stages created by an approved workflow should not require redundant Lead approval.

Publication/approval is an atomic, versioned transition. The scheduling producer reacts to approval/publication, not merely to a Draft creation event. Approved specs are pinned appropriately for each execution; later revisions never silently alter work in progress. All draft and approval history remains inspectable.

Lead is not a persistent person-like process. Its approval and planning work is itself queued and may be handled by ephemeral Lead-role executions.

## 4. Work items, stages, queues and roles

A Work item is a single actionable unit such as planning/approval, implementation, frozen independent review, integration, validation, recovery, challenge triage, or an actionable Role message. Work-item state is separate from the parent Work unit and from the provider session.

Each executable stage has:
- Stable identity and canonical subject (Project/System/Feature/task/review/integration/etc.).
- Queue identity; matching role subscriptions; Lead-assigned priority; derived blocking status.
- Authoritative readiness prerequisites and source/role/generation constraints.
- Available/Blocked/Claimed/Completed/Cancelled semantics, immutable or versioned evidence and checkpoints as relevant.
- One exclusive primary claim per launch; claim/requeue/conflict resolution uses local transactional CAS/version checks.
- Frozen source SHA/ref for independent review and accepted integration when applicable.

Default subscriptions:
- Lead: planning, proposals awaiting approval, actionable Lead messages.
- Worker: implementation.
- Reviewer: independent review of exact frozen source.
- Integrator: integration of accepted source.
- Recovery: reconstruction/investigation after restored or uncertain state.
- Other configured roles: compatible additional queues.

One role is pinned per launch; a role may subscribe to multiple queues. A configurable generalist role can cover several queues; it need not turn into multiple simultaneous role identities. Role subscription selects *compatible work*, not a separate authorization principal. Explicit reservation-scoped permissions remain authoritative for actual operations.

Reviews, integration and validation use the same Scheduler/claim lifecycle, not hand-directed named Reviewer N/Integrator agents. Independence and accepted-source constraints are enforced by workflow/provenance, not by a role label alone.

## 5. Prioritization and flat queue projection

The graph is authoritative. The Scheduler consumes a flattened, indexed set of runnable candidates produced by evaluating that graph. Each candidate has compatible role/queue information and a computed sort key:

    (isBlocking, leadAssignedPriority, -availableSequence)

Sort lexicographically descending:
1. All *eligible* blocking work before ordinary work.
2. Higher Lead-assigned priority first within each tier.
3. Oldest available item first at equal blocking/priority (FIFO).

This is conceptually one priority queue; two physical queues are not required. On yielding/requeueing into Available, the item receives a fresh availability sequence. A local database can supply stable transaction-ordered availability sequence across projects; use stable identity for any last-resort tie. Lead can change priority through an explicit versioned mutation; changed graph facts recompute the derived blocking flag.

Blocking must mean something operationally:
- A direct unsatisfied hard prerequisite of other unfinished work is blocking if its completion would release downstream work.
- If System A depends on completion of System B, B is blocking A. The importance propagates to B's *required, unfinished, actionable descendants*, recursively, so the Scheduler helps complete B.
- Merely being a required child of any unfinished System does **not** automatically make every item a blocker. Parent containment alone would collapse the distinction.
- Derive this from canonical dependency/required-completion relationships; do not hand-tag every child or create per-System worker pools.
- A blocked item is not dispatchable merely because it would block others; eligibility is checked first.

Lead priority is meaningful across queues and is the primary manual ordering signal. Systematically generated review/integration items must have a predictable starting priority (inheritance from their origin is a plausible default, but needs an explicit implementation decision); no role-specific hidden priority scale.

The goal is useful verified progress, not scheduling every low-priority item eventually. Expose stale, never-claimed Work to Lead for reconsideration; do not add automatic aging/fairness policies without evidence they are needed.

## 6. Scheduling, admission and claim selection

The Scheduler is deterministic, not an LLM:
1. Observe committed state, derive up-to-date eligibility and priority projection.
2. Account for compatible roles, global concurrency limit, in-flight reservations, work lineage and prerequisites.
3. Choose the highest-ranked *feasible* uncovered item and dispatch a compatible ephemeral role execution.
4. On connect, authenticate its CSPRNG UUIDv4 bearer and return role/context; the execution claims one primary Work item and receives its canonical spec and checkpoint.
5. Completion/yield/block atomically updates Work and releases or reconciles capacity; state changes awaken eligible new Work.
6. Reconciler handles uncertain one-shot launches and external effects; the Scheduler does not invent approval/review judgments.

A launched role can subscribe to several queues. It claims the highest-ranked feasible item across their union, not one queue at a time. Preserve matching for other admitted launches: if execution A can do implementation or review and B can only do review, do not let A take the sole review and strand B if a feasible alternative exists. Reservation coverage is *not* permanent task assignment.

Launches have one primary claim per lifetime. Successor executions resume from durable checkpoints and exact source evidence. Uncertain launch, claim or push outcomes must be reconciled, not blindly retried.

No dedicated worker slots per System, permanent named Agent entities, continuous Lead attendance loop, or detailed Lead-authored launch prompt. The Scheduler and reconciler consume no model execution slot.

## 7. Bootstrap, credentials and manual grants

- Scheduled execution credential: CSPRNG-generated UUIDv4, distinct from public launch ID and operation IDs.
- The execution model explicitly presents this UUIDv4 in tool arguments; do not assume vendor-independent session binding or automatic secret injection.
- Store only a verifier/hash and launch/permission state in durable orchestration state. Validate server-side and derive authority from verified reservation, not caller-provided role or agent label.
- Preserve relevant idempotency, generation/STOP/terminal fences, and one-shot delivery/dispatch recovery. Credentials and verifier semantics must survive routine server restart (for scheduled Work).
- Granular authorization is scoped to the reservation's project/work/source and permitted operations; task/scheduling compatibility is not itself permission.

For an ad-hoc model conversation not launched by Jarvis:
- It may request a manual grant, receives a CSPRNG UUIDv4, and awaits operator approval.
- Only the operator may approve or deny. Once approved, the session uses that explicit credential for authorized operations without per-action approvals.
- A prospective named project can be approved for one coherent setup; after creation, bind future mutations to the verified canonical project identity **and the originally approved device**, not another same-named project on another device.
- Manual grant state/verifier is **server-memory only**, not exported to backups. It vanishes on server restart; the session can request a new grant.
- No speculative leases, expiry calendar, persistent manual-grant DB, or general-purpose permission engine.

## 8. Stateful authoritative storage

Change the former stateless-server contract: the Server may own a **durable authoritative local SQLite database** (WAL mode is the initial preference). This cost is justified by autonomous, high-frequency orchestration.

SQLite stores and transactionally updates:
- Work graph, tasks, stages, dependencies, priorities and derived queue/index state.
- Launch/reservation/claim/generation/capacity records and idempotent operation receipts.
- Role subscriptions and versions, role/task conversations, acceptance/review/integration workflow evidence and orchestration audit events.
- Consistent local sequence/order for commits and availability.

Source repositories, frozen SHA/ref evidence and the actual code artifacts remain in Git; SQLite records references and authoritative orchestration decisions. Use local transactions and indexed queries for the hot path. Do **not** round-trip every mutation to GitHub. Multiple clients talk to one authoritative service; a single SQLite writer/transaction domain handles what previously required project and account Git CAS.

Retain sound invariants from the current implementation—exclusive claims, exact source/review provenance, idempotency, immutable terminal decisions, generation/STOP fences, and not releasing global capacity on unverified external effects. Replace the remote Git transaction *mechanism*, not these guarantees.

Server replacement with an empty data directory is no longer expected to preserve coordination state automatically. Routine process restart preserves the database; host loss requires a restore and possible recovery reconciliation.

## 9. Readable asynchronous Git backups

Keep Git for source provenance and for **remote versioned readable backups**, not synchronous orchestration authority.

- Export a consistent snapshot from one SQLite read transaction to deterministic, human-readable JSON, with stable IDs, file paths, ordering and serialization.
- Include an export schema version, database revision/sequence, snapshot timestamp and a manifest/checksums. Represent required state and history so the importer can reconstruct an internally coherent database.
- Prefer per-project/per-work JSON and bounded structured event/history files rather than a single constantly rewritten giant document. Exact on-disk layout is an implementation decision.
- Commit and push exports asynchronously, in batches. GitHub unavailability delays backup replication, not ordinary scheduling.
- Export only what is appropriate to persist; never export plaintext credentials or ephemeral manual grants. Scheduled credential verifiers and durable authority must have recoverable representations.
- Implement and test JSON -> empty SQLite restore, schema validation/migration, stable round-trip, corruption/missing-file rejection, and production-sized export/restore performance.
- Git history shows snapshots and diffs; audit events explain transitions between snapshots. The live database is the sole current authority.

Snapshot interval, retention, schema layout, and loss budget are operational choices to make after measurement. Avoid promising zero loss for a destroyed host between backups.

## 10. Recovery as specialized Work

The backup may precede actual work committed after its snapshot. Treat missing orchestration history as uncertainty, not proof that actions did not occur.

Recovery flow:
1. Restore the last verified JSON snapshot into SQLite.
2. Enter recovery mode for affected projects/Work; deterministically fence duplicate scheduling, stale grants, and uncertain in-flight external operations.
3. Compare restored records with actual source repositories, commits, branches, frozen review artifacts, integrations, and available external receipts. Identify gaps.
4. Create Recovery queue items. Ephemeral Recovery-role executions inspect evidence and propose or record supported reconstructed progress.
5. Re-run review/validation where a decision cannot genuinely be reconstructed. An implementation commit is not itself proof of a lost approval.
6. Resolve discrepancies with explicit versioned recovery evidence and release affected queues progressively.
7. Resume normal scheduling once unknown effects cannot generate duplicate or contradictory actions.

Human-level inference belongs to Recovery executions; low-level fences, snapshot provenance, deduplication and authority boundaries remain deterministic.

Recovery is not required to reproduce every lost state mutation exactly; it must restore a truthful, safe understanding of valuable completed work. Missing uncommitted/unreachable work can remain lost and must be reported rather than fabricated.

## 11. Evidence, review, integration and external effects

- Implementation work may produce source on isolated branches/worktrees. Checkpoints carry exact reachable source refs/SHAs and evidence.
- A review Work item is pinned to an immutable frozen revision; independent Reviewer records a canonical verdict/findings. A verdict and the execution's terminal claim are distinct transitions.
- Accepted review can produce an integration Work item pinned to accepted source and target. Integration verifies actual branch ancestry, semantic compatibility and exact result.
- No double review decision, duplicate integration or unsafe work requeue when claims/reviews/integration have uncertain outcomes.
- Dependency edits, changed graph versions, and interrupted external effects must not permit stale downstream completion.
- Parent Work acceptance is independently evidenced after required child outcomes, not automatically inferred from task closure.
- Test cases should exercise concurrency, stale generations, replay, restart, source mutation while claimed, graph edits, role compatibility and independent review.

## 12. Migration and interaction with ongoing Work epic

This document updates **target architecture**. Existing integrations, accepted frozen reviews, branches and canonical receipts remain valid historical evidence. Do not silently rewrite their accepted contracts, pretend Git-backed code implements SQLite, or interrupt active agents without a scoped migration plan.

Alignment sequence:
1. Publish and explicitly approve a revised architecture contract and impact matrix. Distinguish operator decisions from still-open implementation choices.
2. Inventory existing WQ modules/active assignments and categorize each as *retain largely unchanged*, *adapt state backend*, *supersede/retire*, or *new*. Keep in-flight review/integration work independently verifiable.
3. Implement and test the SQLite authority/transaction substrate and conversion/import path in isolation. Preserve source/ref correctness and operation semantics.
4. Adapt task Draft/Lead approval/Open workflow, Lead priority mutation, graph-derived blocker propagation and flat queue projection.
5. Adapt WQ02/WQ04/WQ07/WQ08 state clients, role subscription/claim matching and capacity to local SQLite, retiring remote project/account Git-CAS from the hot path when compatibility is proven.
6. Implement deterministic JSON Git export, verified restore and Recovery role/workflow; exercise post-snapshot source changes and uncertain side effects.
7. Extend Work-first UI, testing and default-off regression gates. Perform full .NET 11 Release suites and independent exact-frozen review of migration changes.
8. Prepare an explicit, reversible activation/cutover plan for operator approval. No automatic production migration/restart/host deployment/hosted Actions/Codex.

Expected mapping:
- WQ01 typed invariants/state machine: **retain**, adapt storage.
- WQ02 project Git-backed Work ledger and WQ07 account Git capacity: **replace hot-path storage**, preserve important CAS/idempotency semantics locally.
- WQ03 one-shot browser and external-effects journal: **retain semantics**, reconcile with local durability and recovered states.
- WQ04 original producer source/graph/role authority: **retain invariant**, adapt event publication to SQLite; add Draft/approval gating.
- WQ06 Task/Role conversations: **retain concept**, move orchestration records to SQLite.
- WQ08 scheduler, admission, reservations and wake: **retain logic**, use flat priority projection and local transactions; no busy-loop polling requirement.
- WQ09 Work-first UI: **adapt data source**, show Draft/approval, priority, recovery and derived readiness.
- WQ10 migration: **revise target**, include old Git ledger -> SQLite import and transparent compatibility history.
- WQ11 acceptance: **extend**, test current accepted baseline plus new backend/backup/recovery and default-off rollout.
- Composable-work-units: **retain graph semantics**, adapt persistence and downstream blocking derivation.
- UUIDv4/manual grants: **retain explicit token and grant model**, ensure durable scheduled verifier versus ephemeral in-memory manual grants.

Existing open/active work should be allowed to reach safe review/checkpoint boundaries. Lead must decide which pending Git-specific changes remain useful for transitional correctness and which should not consume further implementation effort. Avoid conflicting edits across workers.

## 13. Explicit unresolved engineering choices

The following should be decided through implementation evidence or Lead engineering judgment, not quietly asserted as already approved:
- SQLite schema and exact tables/indexes/migration tooling; backup/restore file layout and event granularity.
- Backup frequency/retention and acceptable maximum state loss on total host failure.
- Default priority for automatically generated review/integration stages and optional inheritance from parent Work.
- Whether root Project/System acceptance is modeled as a separate schedulable validation stage or an explicit verified transition (the independent acceptance requirement is fixed).
- Precise definition and indexing of propagated blocking descendants in edge cases involving optional work, partial System acceptance and cycles.
- Migration/cutover mechanics from two active Git-ledger domains, including dual-read transitional mode if needed.
- Notification policy, diagnostics and operational monitoring for failed backups/recovery mode.
- Recovery evidence confidence levels for decisions not directly recoverable from repository artifacts.

## 14. Acceptance examples for revised architecture

A. Draft -> Proposed creates only Lead review demand; rejected/returned Draft creates no implementation demand. Approved Open work becomes eligible only when prerequisites hold.
B. Blocking review of priority 3 sorts ahead of ordinary implementation priority 9. Within a tier, higher Lead priority wins; equal priority FIFO; yielded work receives a new availability sequence.
C. System B blocks A; the unfinished required actionable descendants of B inherit blocking importance. Unrelated required children of a nonblocking System do not all become blockers.
D. A role subscribed to implementation and review chooses the best feasible candidate across both; admission matching avoids stranding a review-only execution.
E. A finished implementation emits frozen review work; Accepted leads to integration subject to exact source/graph checks; no permanent reviewer or integrator identity.
F. Concurrent claims/capacity changes are atomic SQLite transactions, without remote Git fetch/push; request retries are idempotent and stale generations rejected.
G. A consistent deterministic JSON export restores a fresh SQLite database to the same snapshot semantics; server crash restarts from persisted SQLite.
H. On restoring an older backup with newer Git commits, Recovery fences uncertain work, reconstructs proven source progress and requests fresh verification for unrecoverable decisions, without duplicate launch/integration.
I. A scheduled UUIDv4 credential is explicit in tool arguments and durable verifier survives process restart; an operator-approved manual grant is device/project-scoped and disappears on restart.
J. All newly introduced paths remain default-off until their review, tests, and operator-approved activation plan are complete.

## 15. Decision summary

Durable Work Graph -> derived flat prioritized Work items -> deterministic non-model Scheduler -> ephemeral role execution -> durable evidence/next work.

Priority sort: (blocking, Lead priority, FIFO); queues are role-compatible sources of work, not persistent people.

Storage: SQLite is live authority. Git is source provenance plus asynchronous human-readable JSON backup. Recovery is a specialized Work queue handled by intelligent ephemeral executions behind deterministic uncertainty fences.

**This is the new target contract. The existing Git-CAS implementation is an interim baseline, not evidence that this migration has already occurred.**


---

## 16. 2026-11-26 amendment — Work, inference execution, resources, and clean-slate transition

**Authority and precedence.** This dated amendment is the controlling current architecture direction for Work, model execution, JarvisClient resources, branch strategy, and cutover. Sections 1–15 remain the preserved 2026-11-17 design history and continue to govern retained Work semantics. Where older wording implies that an agent, provider conversation, connected device, or long-running process owns Work, or that cutover means bulk-converting the existing Git task/Agent state, this amendment supersedes that interpretation. It does not authorize production activation, deployment, migration, or live Scheduler operation.

### 16.1 Work, execution, resources, jobs, and Challenges are separate domains

The continuity boundary is durable Work. A provider session, Agent record, connected JarvisClient, resource lease, and OS process are not Work owners.

| Domain | Owns | Lifecycle relationship |
|---|---|---|
| **Work** | Durable intent, scope, specification, constraints, acceptance, dependencies, priority, milestones, decisions, checkpoints, evidence, and history | Persists while executions and resources come and go. May be dormant, blocked, or actionable without a running model. |
| **Inference execution** | One ephemeral model/provider session acting on a bounded Work claim | Can start, stop, fail, or be replaced without closing or transferring ownership of Work. Provider conversation IDs and URLs are diagnostics, not Work identity. |
| **Host and resource** | An enrolled JarvisClient host and its independently addressable capabilities: checkout, worktree, filesystem, process runner, browser, and later other measured resources | Exists independently of any particular model execution. A host may serve several providers or Work items subject to explicit capacity and exclusivity. |
| **Resource lease** | Temporary, fenced authority to use a particular resource for a particular Work/stage/execution and bounded operations | Can expire or be revoked while the physical process remains alive. Expiry is not proof that a job stopped. |
| **Job** | A concrete long-running operation such as a process, build, test, browser action, or artifact transfer | Can outlive the inference execution that requested it. Its result must be independently observable and reconciled. |
| **Challenge** | A durable blocker or continuation condition: dependency, timer, job completion, unknown outcome, approval, or human decision | Suspends or reactivates Work without requiring a model session to remain open. It is not an Agent conversation or a notification. |

These are separate versioned state machines. Their exact enum vocabularies remain implementation choices; they must not be collapsed into one generic Running/Finished status. In particular, Work completion does not imply that every process has stopped, a model session has not ended, and lease expiry does not imply that a job is terminal.

Work remains composable Project/System/Feature intent with explicit required scope, hard prerequisites, parent-specific acceptance, and derived actionable projections. Extend that model so milestone scope, priority, and progress are first-class, versioned Work data. Progress should derive from accepted required deliverables and explicit scope, not the number of tasks, sessions, or agents. Splitting one deliverable into more tasks must not increase the milestone denominator or manufacture progress. Scope changes and optional/required status changes remain explicit and reviewable.

### 16.2 Serdan-Home is a resource host, not an inference location

A model session may run in ChatGPT or Codex on a different machine while Jarvis assigns work to a capable JarvisClient host such as Serdan-Home. The Scheduler selects a compatible host and resource, grants a scoped lease, and dispatches a durable job. The host performs the local operation against its registered repository/worktree or browser resource and reports bounded progress and terminal evidence to the Jarvis control plane. The model session may end while the job continues; a Challenge can reactivate the relevant Work when the job completes, fails, times out, or requires a human decision.

The model provider, Jarvis server, resource host, and browser are not assumed to be co-located. A JarvisClient is a resource provider and authenticated command endpoint, not the owner of Work and not the model execution itself.

### 16.3 Host identity, resource identity, capabilities, and leases

The target must distinguish at least:

- **Host identity:** stable identity of an enrolled JarvisClient machine/service, with an independently authenticated credential and explicit enrollment, rotation, and revocation.
- **Client-instance identity:** one running client/service instance, so two processes on one machine are not silently treated as one connection.
- **Resource identity:** a stable host-scoped identity for a particular checkout, worktree, filesystem boundary, process runner, browser profile, or other capability.
- **Routing identity:** the existing public deviceId label used to select a connected client. It is a compatibility/routing key, not proof of host identity.
- **Repository/worktree identity:** verified source repository and checkout identities, not local path strings or host identity.
- **Provider/execution identity:** provider, account/profile, and one ephemeral model execution; never inferred from a deviceId or conversation URL.

A host reconnecting with valid enrollment retains its host identity. A replacement machine is explicitly enrolled. Multiple client instances must be distinguishable. Do not copy the existing deviceId file and treat the resulting duplicate as a new authenticated host.

A host advertises a versioned, typed, bounded capability description. Initial useful dimensions include supported operation kinds, repository/source availability, registered checkout and worktree references, OS and toolchain versions, permitted filesystem roots, process/build/test support, browser profiles and allowed origins, locality, current availability, concurrency limits, and exclusive-resource constraints. Capability freshness and trust must be explicit; a client-supplied string list is not an authorization proof. The exact schema and attestation mechanism remain open. Do not introduce Kubernetes or a general-purpose cluster manager.

A resource lease is server-issued and scoped to the Work item, stage, execution, host/resource, repository/worktree where applicable, and allowed operations. It carries a monotonically fenced generation and bounded expiry/renewal semantics. Revocation or a newer generation prevents stale executions from acquiring or mutating the resource. The server must distinguish lease expiry from verified process termination and reconcile jobs whose host disconnects or whose acknowledgement is lost.

Jobs need stable IDs and operation IDs, idempotent admission, bounded stdout/stderr and progress, terminal evidence, explicit cancellation semantics, and an Unknown/reconciliation path. A model execution ending must not erase the job record. A reconnecting host must not cause an uncertain operation to be blindly repeated.

### 16.4 One control plane; no parallel resource subsystem

The intended Jarvis-host control plane owns the Work graph, role/queue demand, priority and admission, provider execution records, host/resource registry, capability snapshots, leases and fencing generations, durable jobs, Challenges, idempotency receipts, external-effect journals, audit, and queue projections. In-memory indexes are disposable accelerators, never authority.

Use one authoritative SQLite transaction domain on the Jarvis server for the Work/control-plane state. JarvisClient hosts connect through authenticated RPC and execute only the operations covered by their current leases. Do not replicate the authority database across resource hosts or create a second SQLite database and independent Scheduler for resources. Resource admission belongs inside the same Work/Scheduler authority boundary.

Git remains the source-code and immutable source/review/integration provenance system, plus a readable asynchronous backup target. Remote Git CAS is not the hot-path coordination transaction. Backup lag and restore uncertainty remain explicit operational risks and Recovery work. The existing account-level Work capacity limit is model-execution admission; it is not a physical host, CPU, GPU, browser, or repository capacity model. Add resource-specific capacity only where real scheduling evidence requires it.

Scheduler selection remains deterministic: first determine eligible Work and compatible role/provider demand, then require a compatible available resource and lease. Preserve the existing blocker/Lead-priority/FIFO semantics within feasible demand. A host-availability event should trigger reevaluation; it is not itself authority to claim or execute Work. Keep manual grants transient and separate from durable scheduled credentials.

### 16.5 Provider coverage

Jarvis 2.0 requires real-provider execution coverage for both **ChatGPT and Codex**. Do not design the execution model around ChatGPT conversation IDs or a ChatGPT-only browser launch path. Provider adapters own the provider-specific launch/session/termination details; the Work and resource contracts remain provider-neutral. Claude and other providers can follow as adapters, not as assumptions embedded in Work identity or resource ownership.

The exact provider authentication, account/profile selection, quota handling, and session lifecycle APIs remain open implementation decisions. No provider adapter or host resource may fabricate a completed Work outcome without durable evidence.

### 16.6 Jarvis 1.0 / 2.0 branch strategy

- **main is the Jarvis 2.0 development line.** It carries the new Work-first control plane, provider-neutral execution model, resource identity/lease model, durable Jobs and Challenges, and their tests.
- Do not create the maintenance branch until the current Automation milestone has passed independent acceptance, integration, full release validation, and reconciliation of uncertain external effects and long-running jobs. Record the exact deployable release SHA plus the tested artifact and configuration.
- At that point, create **stable/1.x** from that exact accepted 1.0 SHA. Keep it a maintenance line for critical correctness and security fixes, independently tested and cherry-picked as needed. Do not back-merge the 2.0 architecture into 1.x or use a moving main branch as the 1.0 release reference.
- The 1.0 release SHA and stable/1.x branch do not yet exist as approved cutover artifacts. Do not guess a branch point or create the branch prematurely.

### 16.7 Clean-slate cutover contract

Clean-slate means a clean 2.0 coordination state, not deletion of source repositories, source history, accepted reviews, or audit evidence.

1. First complete and freeze the 1.0 release as described above. Keep the stable/1.x artifact and rollback instructions immutable.
2. Before 2.0 activation, pause 1.0 Work dispatch, new claims, and old live writers. Reconcile or explicitly quarantine every running, pending, and uncertain process/browser/Git side effect. Do not infer that a lost connection means an operation did not happen.
3. Preserve the old Git-backed ledgers, task/Agent history, source refs, reviews, integration receipts, and external-effect evidence as read-only historical material. Do not run old and new schedulers or writers against the same live Work.
4. Initialize the 2.0 authoritative database empty. Do not bulk-import persistent Agents, old task queues, claims, conversations, notification history, model sessions, process handles, or stale reservations/leases. Historical Agent records may remain readable for audit but are not active 2.0 owners.
5. Start new 2.0 Projects and Work with explicit scope and acceptance. The Operator may selectively migrate a specific useful Work item or specification, with its verified source/review/integration lineage and an explicit new acceptance decision. The C0–C3 fixture work is not permission for automatic or blanket migration.
6. Enroll JarvisClient hosts under the new authenticated host/resource identity contract. Do not equate a legacy deviceId, project name, worktree name, or local path with an enrolled host/resource.
7. Validate fresh-database startup, backup/restore, host reconnect/replacement, lease expiry and stale-generation rejection, jobs that outlive model sessions, unknown job outcomes, and ChatGPT/Codex provider execution while the new Scheduler remains default-off.
8. Activate 2.0 only after a separate explicit Operator GO. Rollback selects the frozen 1.0 artifact; it does not replay 2.0 state into 1.0 or permit both versions to mutate the same Work.

No production migration, deployment, host restart, Scheduler activation, or data conversion is authorized by this architecture amendment.

### 16.8 Open design decisions

Resolve these with focused implementation evidence rather than silently selecting a schema or protocol in this document:

- Host enrollment/authentication, credential rotation/revocation, multi-instance identity, and recovery after reinstall or machine replacement.
- Capability schema, version negotiation, advertisement freshness, trust/attestation, locality, and resource exclusivity.
- Lease TTL, renewal, revocation, fencing generation, and what evidence proves that an expired host job actually stopped.
- Durable job protocol, output/artifact transfer, retry/idempotency, cancellation, retention, and Unknown-outcome reconciliation.
- Provider authentication and profile selection for ChatGPT and Codex, with provider-specific limits kept outside Work identity.
- Resource capacity/fairness and whether any GPU/CPU/memory dimensions are justified by measured demand. Defer GPU scheduling until there is a concrete workload.
- Backup frequency, retention, acceptable loss window, and the format/schema transition from the current schema-1 backup implementation to the full typed domain projection.
- Selective historical Work migration policy and how a migrated item is re-accepted in 2.0.
- Whether JarvisClient needs a headless daemon/service mode in addition to its current interactive client process; do not assume that such a daemon or durable remote-job service already exists.
- The milestone progress/weighting contract and how scope changes affect a milestone without making task decomposition a progress hack.

**Interpretation correction for the 2026-11-17 decision summary:** references to SQLite as the live authority describe the intended post-cutover 2.0 state. The current runtime remains Git-backed until the separately reviewed and Operator-approved cutover. The 2026-11-17 plan to import the existing active Git ledgers is superseded as the default cutover path by this clean-slate start with optional, selective, evidence-preserving migration.
