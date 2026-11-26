# Jarvis Work epic — current implementation alignment with stateful architecture

**Date:** 2026-11-17

> **Current status amendment (2026-11-26):** Section 9 is the current source/task snapshot at main 142f2bd4b2728ea61e18a9d51f7c663c7951e182 and supersedes the 2026-11-17 task counts and status labels below. The original matrix and frozen source/review/integration history remain preserved as historical evidence. The companion orchestration spec's Section 16 is the controlling target architecture. No production cutover is authorized.
**Status:** Lead alignment and migration execution plan, documentation only. No production authorization. No source integration or live Scheduler activation.
**Pinned target contract:** [work-stateful-orchestration-2026-11-17.md](work-stateful-orchestration-2026-11-17.md), commit `bd2f5e18a0850fcdc1077ee5d9f7d163110f83a5`.
**Observed task snapshot:** canonical Work task overview, 172 tasks / 356 reviews at verified project ledger head `5a5827652ecb1fbdbd7d743561088a79dd0cd33f`. This is a time-bounded inventory, not a claim that pending reviews remained unchanged. The implementation inspected was from the current project workspace; the docs branch was forked from `origin/main` at `9940fad1789f81006a8cd05db24d4798c6abe931`.

## Executive decision

Stop treating the project Git orphan ledger and account-global Git CAS ledger as the **future hot-path transaction authority**. Those are a reviewed, default-off *interim implementation baseline*. Keep the engineering properties they proved; replace the persistence mechanism.

A single authoritative Jarvis-host SQLite transaction domain coordinates account capacity, Work graph, stages, roles, priorities, reservations, claim generations, idempotency, terminal decisions and queue projection for all included projects. The frontend, external clients, and execution tools call authenticated server commands. Source Git continues to store code and exact frozen source/review/integration references. Periodically export coherent deterministic JSON snapshots to a *separate backup Git ledger* asynchronously. Recovery-mode Work, executed by the Recovery role, reconciles lost local coordination with independently persistent artifacts after a restore.

**The operator's current priority:** finish architectural alignment first. Do not consume the lead's attention reviving other Working/Ready agents, starting new implementation lanes, or attempting a live cutover until this design/task plan is recorded and independently reviewable. Existing frozen reviews and work history remain valid.

## Domain invariants to preserve

1. Durable Project / recursive System / Feature composition; containment and cross-system hard dependencies are distinct. Required children do not automatically accept the parent.
2. Task proposal lifecycle `Draft -> Proposed -> Open`; approval produces authorized downstream demand only after versioned scope and priority decisions. The current unconditional CreateTask->Open behavior is interim.
3. Flat derived runnable projection; eligibility first; rank by `(isBlocking, leadPriority, -availableSequence)` descending. Requeue/yield gets a fresh sequence. Derive blocking relevance from hard downstream dependencies and propagate through required unfinished descendants of genuinely blocking Systems; do not treat every required child as blocking just because its parent cannot yet complete.
4. One role per launched ephemeral execution; a role may subscribe to multiple queues. Claim the highest-ranked **feasible** compatible Work while preserving injective remaining demand-credit matching for already admitted specialized launches. One primary claim per launch.
5. Review exact frozen SHA, accepted source, separate explicit Integration authorization/result and independent parent acceptance. Do not let raw Git ancestry masquerade as a lost decision.
6. Explicit CSPRNG UUIDv4 scheduled bearer credentials and scoped verifiers, minimal bootstrap, source/role/graph lineage binding, provenance, replay, STOP and one-shot unknown-outcome fences.
7. Operator-approved manual grants remain transient server-memory and bind to original device plus verified canonical project instance; no persistence in SQLite or backup.
8. In-memory runnable indexes and derived graph evaluation may accelerate work but **the local SQLite transaction** is the authoritative mutation / concurrency check, not an eventual cache and not GitHub.
9. Readable JSON backup is restorable, not automatically authoritative while live. Backup lag is a deliberately accepted disaster-recovery risk, not a substitute for durable local commits.
10. Default-off and explicit operator authorization for new live scheduler, data migration, service restart/deployment and legacy Agent cutover.

## Impact matrix (source-grounded and task-grounded)

| Component / canonical work | Current evidence / boundary | Disposition in new architecture | Concrete integration consequence |
|---|---|---|---|
| **WQ01** `wq-01-contract-fixtures` — Closed | Typed Work stages, role/demand contracts, replay and negative-space tests in `Server/WorkQueueContracts.fs` | **Retain semantic core** | Reuse pure state machine and tests. Change storage-specific type bindings/index maintenance; new database tests replace only transport assumptions. |
| **WQ02** `wq-02-canonical-work-ledger` — Closed; `Client/Modules/WorkQueueGitLedger.fs` and `LedgerStore.fs` | Project Git CAS is currently authoritative for Work records; `LedgerStore.transact` fetches, writes commit, pushes, confirms | **Replace hot-path persistence** | Extract a stable storage-neutral Work command/transition API. Implement transactional SQLite rows, versioned commands, idempotent receipts and durable outbox/external-effect metadata. Preserve old Git ledger in immutable import/audit, not simultaneous live authority. |
| **WQ03** `wq-03-source-handoff-lineage` — Closed; original browser/side-effect journals and source binding modules | Exclusive source-writer lineage, reachable SHA/ref and one-shot external effects | **Retain invariant, adapt receipts** | Persist durable source journal and operation/retry evidence in SQLite; actual source remains in Git. A local transaction cannot make an external Git push atomic; retain unknown-effect reconciliation. |
| **WQ04** `wq-04-queue-producers-priority-roles` — InProgress but legacy attempt Finished; `work-wq04-original-producer-authority` — InProgress Worker 4; `Server/WorkQueueProducers.fs` and `Client/Modules/WorkQueueReceiptProducer.fs` | Pure producers plus original TaskCreated/review/integration source/role/graph binding currently require Git CAS. Earlier frozen review requested corrections for revoked stages. | **Retain validated producer/effect semantics, adapt and extend** | Publication of authorized workflow transition, its stage generation/lineage and graph/queue state becomes one SQLite transaction. Add Draft/Lead approval publication gate and graph-change revocation across all original-bound stages. Preserve tested typed conflicts. |
| **WQ05** `wq-05-review-integration-adapters` — InProgress, attempt Finished; `WorkQueueReviewIntegrationAdapter.fs` | Frozen review, decision/Integration gates and source evidence | **Retain** | Port decision state and stage link to DB. Keep immutable frozen source/review evidence and explicit finality. Correct task lifecycle integration—not per-step Git commit. |
| **WQ06** `wq-06-task-operator-conversations` — InProgress, attempt Finished; `WorkQueueOperatorConversations.fs` | Task/Role conversations and role handler leases | **Retain concepts, adapt state** | Move canonical messages/handler claims into DB, maintain passive Task attention vs runnable Role-message distinction and replay fences; do not create permanent agent owners. |
| **WQ07** `wq-07-account-global-capacity` — Closed; `Client/Modules/AccountCapacityLedger.fs` and `AccountAdmissionAdapter.fs` | Separate account-wide Git CAS token pool and two-ledger admission protocol | **Supersede remote transaction protocol; retain semantics** | One SQLite transaction covers projects, global limit, admission reservation and compatible work credit; keep capacity/release, STOP, replay and competing-claim race proofs as ported tests. No second authoritative account Git ledger at runtime. |
| **WQ08** `wq-08-manager-scheduler-bootstrap` — InProgress, attempt Finished; `WorkQueueSchedulerHostPreview.fs`, `WorkQueueSchedulerHostLoop.fs`, `WorkQueueCrossProjectPlanner.fs` | Deterministic default-off planner/reconciler currently verifies remote Git heads on reads | **Retain scheduler logic, change authority and host** | Read local committed state/index; react to DB commits. Role-compatible global feasibility, one-shot launch and unknown-effect reconciliation remain. No authenticated model Manager, no permanent agent roster. |
| **WQ09** `wq-09-task-first-client-ui` — InProgress, attempt Finished; `TaskFirstUiProjection.fs`, `TaskFirstUiSnapshotAdapter.fs`, `WorkQueueDashboardHost.fs` | Work-first, Git-backed read-only view and task/role interfaces | **Retain UI concepts, adapt provider** | Show Draft/Approval/Open, Lead priority, effective blocker reason, Recovery state and DB-backed snapshots. Keep historical Agent views for audit only. |
| **WQ10** `wq-10-audited-migration` — InProgress, attempt Finished; multiple `WorkQueueCanonicalMigrationPrepare.fs` / `WorkQueueProjectMigrationPrepare.fs` and account Preparing CAS follow-ons Closed | Old-to-new dual-ledger Git epoch/Preparing/Prepared fences are partly implemented as default-off | **Supersede target, retain useful evidence and safety lessons** | New versioned **verified project/account Git ledger -> SQLite** importer and source/role/claim mapping; explicitly fence old writers before new authority. Avoid dual-write distributed protocol where no longer needed. No live data migration from this plan. |
| **WQ11** `wq-11-end-to-end-validation` — InProgress, attempt Finished; current `work-final-merged-defaultoff-release-gate` Worker 10 InProgress | Existing whole-tree .NET 11 Release checks and two-project default-off fixtures cover Git-backed implementation only | **Keep baseline tests; extend target acceptance** | New independent SQLite transactional races, JSON export/restore, recovery-after-backup-lag, priority/approval/graph propagation and full merged Release gates. Report Git-baseline PASS separately from stateful migration GO. |
| **Composable Work** `composable-work-units` — Closed; `Server/WorkComposition.fs` and `WorkCompositionGitLedger.fs` | Pure Project/System/Feature graph and acceptance adapter, original Git persistence | **Retain graph semantics; adapt persistence** | Port graph/acceptance/required descendant traversal and atomic Work publication. Explicitly test containment-vs-blocking propagation and parent acceptance. |
| **Credentials** `work-uuidv4-explicit-credentials` — Closed; `work-reservation-credentials` and `work-reservation-permissions` Closed | Explicit bearer token/verified reservation, source- and generation-scoped permissions | **Retain** | Scheduled token verifier/launch authority moves into durable SQLite. Do not store plaintext token; enforce tool-bound exact reservation/permission checks. |
| **Manual grants** `work-operator-approved-manual-grants` — Worker 13 InProgress | Operator-approved transient scoped UUIDv4; correction for two devices with same-named projects | **Retain separately** | Server-memory-only state by design: no SQLite/JSON export for ephemeral manual grants. Keep original approved device/project-instance check. |
| **Priority queue** `work-lead-prioritized-ready-queues` — Worker 1 InProgress | Explicit Lead priority, blocker tier, FIFO, currently Git version mutation | **Retain ordering/API semantics; adapt persistence** | Versioned priority update in SQLite; add inherited required-descendant blocker derivation. Do not preserve remote Git write dependency. |
| **Original producer** `work-wq04-original-producer-authority` — Worker 4 InProgress; latest known review ChangesRequested `5fd240db69f24a219e7cb75ac8c418d9` | Correct original source/role/composition CAS, and review/integration stage revocations on graph change | **Retain correctness proof, then port** | No need to finish new remote-Git abstractions unrelated to transitional safety; defer changed target to SQLite transaction. Preserve accepted correction evidence only after new review. |
| **Agent retirement** `work-persistent-agent-entity-retirement` — Worker 8 InProgress, currently Ready/blocked by full cutover evidence | No permanent Agent identity in new Work; old task spec requires empty-server replacement with no state loss | **Retain aim, revise acceptance** | SQLite must persist across normal restart; loss of host restores backup and triggers Recovery rather than empty-server implicit continuity. Preserve historical Agent records, no live cutover. |
| **Release gate** `work-final-merged-defaultoff-release-gate` — Worker 10 InProgress | Read-only full merged Git Work Release matrix | **Retain transitional baseline only** | Finish/read evidence if useful, but mark **not** the stateful architecture acceptance gate. No auto activation. |
| **Other Work closures** native claim facades, host bridge, scheduler wake, source handoff, two-project fixture, prepared migration, UI | Most canonical tasks Closed; independent accepted snapshots integrated or pending historical records | **Do not re-open completed source just to mark new design implemented** | Reuse algorithms/fixtures, fork small DB adapter and migration work with fresh reviews. |

A canonical task lifecycle marked **InProgress with an attempt already Finished** is a state/closure bookkeeping issue, not evidence of an active worker; do not chase it in this architecture phase. In-flight Worker4/10/13 and Ready Worker1/8 are **not** prerequisites for publishing this alignment. They can be reviewed/integrated at their existing frozen target when appropriate, without implying stateful design completion.

## Proposed boundaries and owner lanes (no assignments launched)

A. **Contracts / migration spec** — Lead-owned architecture and review: update old Work architecture, repository AGENTS.md, Work epic/GO-NO-GO and unambiguous new constraints. Preserve old rules as historical semantics in reviewed docs. Resolve source compatibility with current accepted heads. No source-state mutation.

B. **SQLite substrate** — separate new implementation source lane. Define internal storage-neutral command API and SQLite schema with durable transaction sequence and project-account-global atomic boundary. Version checks, idempotent receipts, ordering and pure transition application are reused from Work contracts. Start with disposable database and false-on-default host flag; separate DB from existing disposable telemetry FeedbackStore.

C. **Verified one-time importer / fence** — source lane after B. Read approved project and account Git ledger heads immutably; verify ancestry, source/graph/role/receipt coverage, complete migration mapping and conflict policy. Freeze old writers at cutover; no concurrent old/new live authorizers. Test source duplicates, stale heads, broken historical chains and restart midway. New DB transaction initializes authority exactly once.

D. **Domain producers and planning lifecycle** — after B and core importer contract. Implement Draft/Proposed/Open (existing `ReviewWorkflow.createTaskWithSpecAs` currently creates Open; current `QueueProducerOrigin.TaskRequested` is a planned producer event). In one transaction, publish versioned Lead approval priority, spec pin, source/role/graph/binding and readiness stage. Cross-system prerequisite changes revoke all original-bound review/integration stages atomically. Detached revisions require explicit successor/generation policy.

E. **Scheduler adaptation** — after B; can be developed in parallel with D against new pure interfaces, but integrated only after verified handoff. Direct DB-backed global account capacity and project Work claims; global feasibility matching and role subscriptions; `(isBlocking, leadPriority, -availableSequence)` descending; reactive wake after committed DB transaction; no GitHub fetch/push on hot path. Validate fixed source/credential/STOP and unknown external effects.

F. **Readable backup/export/restore** — after B schema. One read snapshot to canonical JSON with manifest containing schema version, database sequence, source/time and per-file checksums; deterministic ordering; omit plaintext bearer and transient manual grants. Import from JSON only into an empty/explicitly quarantined DB, with strict completeness/version checks and tested round-trip. Async remote Git push with retry/backlog/alert; GitHub outages never stall claims.

G. **Recovery role and operational mode** — after F + E. Restore older backup, fail-closed scheduling fence for ambiguous launches/claimed work, inspect newer Git source/review/integration artifacts, emit explicit provenance-graded recovery evidence, rerun missing decisions (no invented approvals), release projects/Work only after conflicts resolved. Start with focused fake-only two-project tests.

H. **Work-first API/UI & security** — once B+D+E overlap, adapt tools, dashboards, role/Work views and explicit credentials. Server must enforce authorization, not caller-supplied role names. Report current database revision, backup lag and Recovery fences. Manual grants stay transient memory.

I. **Release/rollout** — after all previous pieces: .NET 11 Release full Server.Tests+Client.Tests, Linux and independently verified Windows proof as available, cross-device two-client fixture, source reviewer verdicts, backend performance under realistic queued workloads, emergency restore and reversibility, independent exact-frozen review and explicit operator GO/NO-GO. Still default-off; deployment/cutover separately authorized.

## Checkpoints and strong acceptance gates

**G0 — Architecture/documentation.** New pinned contract, this impact matrix, conflict-free task breakdown and guidance correction reviewed. Existing Git Work remains functional and default-off. No rewrites of accepted immutable review documents. This is the only gate the Lead should finish *before* turning attention back to other agents.

**G1 — Storage semantics.** SQLite transaction covers global capacity plus project claim in one commit; competing claims/launches have one winner, replay returns same receipt, stale project/graph/source/role/generation rejects. No remote Git network call required for the scheduler/claim/priority hot path.

**G2 — Migration compatibility.** Verified old ledgers import into SQLite with exact canonical ID/sequence/lineage/terminal evidence semantics, old writers fenced, no source/review/Integration approval fabricated, rollback can restore known old default-off mode before activation.

**G3 — Workflow and scheduler.** Draft doesn't emit implementation, submitted approval emits Lead demand, Open emits ready stages if eligible; direct/cross-system blocker propagation correct; Lead priority override > FIFO within tier; specialist matching and global capacity work across projects; review/Integration Stage graph edits never expose unauthorized Available.

**G4 — Backup and recovery.** Deterministic JSON diffable, export consistent at one sequence, JSON->empty DB round-trip, missing/corrupt backup rejected. Restore older snapshot with newer source/accepted review/integration artifacts; Recovery resolves evidence without duplicate side effects, with explicit no-evidence / re-review path.

**G5 — User-visible validation.** No persistent new Agent objects; authenticated Work-first API and dashboard show truthful stages, role demand, effective priority, backup age and recoverability. Unapproved ad hoc sessions denied; approved manual grants device/project-scoped and nonpersistent. Full .NET11 Release and negative-space tests pass on exact integrated source.

**G6 — Cutover.** Explicit operator approval only after real backup and restore drill, verified old-writer fencing, possible multi-device old-client behavior, recovery from interrupted migration and rollback. Do not equate any Git-backed baseline Release pass with G6.

## Specific decisions to record before code freeze

- Target deployment: **one authoritative SQLite writer on Jarvis host** for Work and global capacity; external clients and models submit authenticated command RPCs. SQLite WAL, indexes and serialized writes (not replicated live across devices) are initial defaults.
- **No dual live authority** during migration. A verified importer operates on frozen old Git heads and writes the new local DB, with precise provenance.
- **Backup is not a source-code side effect.** Coordination commits return after SQLite success; an async exporter captures later coherent revision, publishes to independent Git backup history and exposes lag/failed publication.
- **Recovery is permissioned Work**, but deterministic server fences must run before Recovery agents claim anything.
- **Unknown policy choices** remain explicit: backup interval and maximal loss, historical credential verifier portability, how priority seeds newly generated review/integration demand, partial parent scope edge cases, and importer schema details. These must be chosen with tests, not silently guessed.
- **External source pushes** still require a durable prepared intent/outcome journal. SQLite transactions cannot atomically include Git pushes, browser submissions or remote host effects.
- **No GitHub Actions**; use actual local hermetic .NET 11 Release tests. No Codex through Jarvis.

## Lead next actions after recording this plan

1. Put a narrowly scoped superseding note in the canonical Lead task: **architecture alignment above Ready-agent resume/launch loop** until G0 is completed. Do not invoke or recover Worker4/10 while realigning.
2. Make a docs-only, independently reviewable changeset correcting `AGENTS.md` statelessness, old Work spec supersession and this impact matrix. No source code or live feature switches.
3. Link this plan to the canonical epic and to `work-stateful-orchestration-alignment`; do not mark new SQLite/backup/Recovery code complete.
4. After G0 review, begin the small staged implementation tasks B-I with separate source ownership and original-frozen independent reviews. Update canonical task specs only when planned changes apply to unstarted work; never modify an in-flight frozen source review's scope.
5. Integrate accepted baseline Work source on its own evidence merits, but cease creating gratuitous remote-Git-only correctness infrastructure designed solely to support a contract we are replacing.


---

## 9. 2026-11-26 current implementation and Jarvis 2.0 transition amendment

**Precedence.** This section is the current source/task snapshot and supersedes the 2026-11-17 task counts, owner statuses, and implementation labels above. The original matrix and frozen evidence remain historical records; they are not silently rewritten. The architecture amendment in Section 16 of the companion orchestration spec is the controlling target. This section is documentation-only and does not authorize activation or cutover.

### 9.1 Verified baseline and live-authority boundary

The inspected source baseline is current main at **142f2bd4b2728ea61e18a9d51f7c663c7951e182**; the assigned registered documentation worktree was clean at that exact commit. A remote comparison reported main identical to that SHA.

The live-authority rule remains explicit in [AGENTS.md](../../AGENTS.md): existing Git-backed project/account ledgers remain authoritative until independent review and separate Operator approval of cutover. Proposed SQLite storage, Scheduler, backup/restore, and Recovery code are not live authority. Do not deploy, restart, migrate, activate the unattended Scheduler, or introduce parallel old/new writers.

This distinction is important: the server-side SQLite modules are present in the solution and have accepted/integrated slices, but the inspected current source has no production host registration or MCP/SignalR route that makes WorkSqliteAuthority, WorkSchedulerSqlite, WorkStatefulApiRead, or WorkBackupReplication the live control plane. Presence in the Server project is not activation. Likewise, the project Git-CAS Work queue adapter explicitly says it is isolated and non-activated and that existing task/orchestration handlers are intentionally not redirected.

### 9.2 Accepted and integrated source history

Accepted review, integration, and current task completion are separate facts. The following table records the exact frozen source and integration boundaries that materially affect the 2.0 plan.

| Slice | Accepted frozen source and review | Integrated main commit | What that evidence establishes — and does not establish |
|---|---|---|---|
| B: SQLite authority substrate | 7c74128a6c71c74e19d60f9f117736938bfe2fec; review f4173cb9e52448978576edc6b018c6fe | 2a26edb7d81caf45c5482c5924a9e46cb703078d | Transactional schema/authority substrate. Not production activation or a completed Work graph port. |
| B: v2 source-provenance columns | 5d872d41ba7259bc84ebafebdf9e5d83e52353d3; review 5f4fd7a89b2143b298873ce98e6fbf50 | a0d70675370fdf9cbc5024dbb1c98ec44c31415a | Physical v2 schema and negative regressions. The review explicitly did not accept authenticated positive ingress, F1/G1b, cutover, or migration. |
| E0 account capacity/reserve/claim | 571217c8202d47c6371cff826447ec7b994fa66d; review 6f7c5a88e2744f7db431a083d954270b | e86e0133886f4f51b703a76167384071245e103c | Default-off account-level capacity and claim provenance. Not physical host/resource capacity. |
| E1a one-shot journal and replay fence | ee175b410958c4f0fc682791c2baf5e849adfaad; review 71d8e66d39084dc8a11ecc076188df62 | 2a51920bcedf3fed185a2956b7dc812474107881 | Default-off prepared/unknown/replay semantics. Not a live external-effect dispatcher. |
| G1a Scheduler/Recovery replay correction | c2c1e371b148fbf664353b0d8d7f5e66f3e0311a; review 5a6fdc1d31934b5c8eeb7f43209a197d | d51ca6b4bcd89413efd5112f5a7db5c93becfd8f | Accepted same-operation reserve/claim replay fence. Still no live Scheduler or resource lease. |
| C0/C1 offline importer preflight | ef3ade4ee4fd5a54419bc26c046ab4134abdb6da; review 5bc8c4d1e2d14afea0948e3cf15144bc | f96aaa1236567e8fa0b8e08d8462117649f061a3 | Offline import validation only; explicitly no live writer fence or cutover. |
| C2 default-off original-Git writer fence fixture | 528b72659a6865aa1b8bb47875a2c4bbb4fdd4a9; review d4a29f0b7c9a4b5db14746b2f8ae1974 | 9c290e299faf7ee40061f262f682c36b30d0771a | Fenced offline fixture and audit. Not a production all-client writer-admission gateway. |
| C3 original-Git publisher prerequisite proposal | d025da38fa0b3606714e44214ee6e41870a09475; review 5d0ab698b92b41fea7ce62d4e6c102ab | 2cf1aa3625661aa24aba5c2118764afafe31e6ca | Documentation-only protocol proposal, not a source adapter or live migration approval. |
| C1 normalized scheduler/import projection | 3e795e008295f800a3010ca4bf67cb9a52be2977; review eb29c792f15c4be78f82d1284d67a1b5 | 51b67bf1d5ba7e16512b915f8004456440b3489c | Default-off normalized fixture projection. Not trusted production source ingress. |
| Trusted source ingress preflight | 0071ed73057cfb91697396ab56e17977eb55cf9e; review 0071ed73057c4b91897396ab56e17977 | 6fd5c569d2d20324db901916b233471ae1cff809 | Negative-only typed preflight. The review explicitly says no authenticated positive authority and no production/live GO. |
| Backup projection contract | 60cacc98152da3f66cc13f63224d32854d400c53; review 9f386cc72d004deba5b4ad79bf7be3e2 | 51b67bf1d5ba7e16512b915f8004456440b3489c | Accepted design/field map and no-secret/restore contract, not a complete F1 implementation. |
| G0/G1a Recovery foundation | f84cc33e2a94a81a2d5215f3728dcf47ab8d1403; review 6c524ed92bc84794b81ba717c5015f90; then 49d618ecee7eaadc084002805785277d4e59c054; review 7e6c0cbf3c4e49faa8776accf23a3c74 | 9c290e299faf7ee40061f262f682c36b30d0771a; then 4d540e3d7c7f6bcd98af5803d68ebead10fbf286 | Default-off seed/recovery foundation. A later corrective G1a review was ChangesRequested and was not integrated; do not treat the current lane as release-ready G1b. |
| H0/H1a read-only API projection | 27a96794bbef0fe95f8a5c73be553ce6e517948f; review 9bd4c776687c4c17a17141c54d119929; then 2d37c72a78b4fa024427ac578a3844d08f2085cb; review d9a4bbda62984749a6dc8fb8d35f337a | e86e0133886f4f51b703a76167384071245e103c; then a6f3e7249b9e7f6dac99f4d2f571133f5cd7e195 | Read-only projection library. Not a live API route or complete UI/cutover. |
| Legacy source-witness interface | 90f67128fd3a3bc97e64c8c55d5b0eaa9a1e01d2; review 82bf0ba3d6d84bd0890e96a25422cbd1 | 171b13af38d124c5b2fb066a3534dc5e7a56efb0 | Interface/witness foundation. The current production authenticated reader and writer-admission tasks remain blocked. |

The older orchestration-alignment document was integrated at 7712bc547c264873036975e30194da03a05cc0ba via submission b4d21df548aa4d2498c288e528ce17b2, but that receipt has an empty ReviewId. Its frozen task spec records an earlier G0 review accepted on a previous immutable SHA and a later review as pending; do not relabel the integrated 7712 revision as independently accepted on that evidence. The task was subsequently closed by the Operator. This 2026-11-26 amendment is the new controlling architecture revision.

### 9.3 Current implementation matrix

| Area | Current source evidence | Current status | 2.0 treatment |
|---|---|---|---|
| SQLite transaction substrate | [WorkSqliteAuthority.fs](../../Server/WorkSqliteAuthority.fs) contains the B schema, WAL/transaction boundary, and opt-in provenance-v2 fields. | Accepted/integrated substrate; not wired as live authority. | Reuse and complete behind one server-owned Work control plane. Preserve the existing Git authority until a separate cutover GO. |
| Trusted source provenance | [WorkTrustedSourceIngress.fs](../../Server/WorkTrustedSourceIngress.fs) is a negative-only preflight; the v2 source fields are syntactically validated metadata, not proof that an authenticated source reader verified them. [WorkLegacySourceWitness.fs](../../Server/WorkLegacySourceWitness.fs) defines the reader boundary but the production authenticated reader remains blocked. | Partial and default-off. No authenticated positive source authority. | Complete the authenticated reader and bind its immutable source/ref/commit/evidence to the same SQLite transaction before any trusted import or producer write. |
| Scheduler and account capacity | [WorkSchedulerSqlite.fs](../../Server/WorkSchedulerSqlite.fs) contains default-off account capacity, reservation, claim, and replay logic. [WorkSchedulerSqliteDispatch.fs](../../Server/WorkSchedulerSqliteDispatch.fs) keeps external dispatch disabled without a verified writer fence. | E0/E1a/G1a slices accepted and integrated; whole task remains Blocked; no live Scheduler. | Reuse deterministic admission and idempotency. Add provider and resource compatibility plus fenced resource leases before enabling external dispatch. |
| Legacy Git import | [WorkLegacyImportSqlite.fs](../../Server/WorkLegacyImportSqlite.fs), [WorkLegacyImportFence.fs](../../Server/WorkLegacyImportFence.fs), and [WorkLegacyImportProjection.fs](../../Server/WorkLegacyImportProjection.fs) implement offline/disposable fixture paths and fenced projection steps. | C0–C3 and normalized projection are integrated fixtures/docs; production source reader and writer gate remain blocked. | Do not make this the default cutover. Use only for Operator-selected historical Work with authenticated evidence; an empty 2.0 start does not require bulk import. |
| Backup and restore | [WorkSnapshotCodec.fs](../../Server/WorkSnapshotCodec.fs) is F0's storage-independent format and explicitly warns that generic fields are not a credential-secrecy boundary. [WorkSqliteBackup.fs](../../Server/WorkSqliteBackup.fs) currently captures/restores the schema-1 record/capacity/receipt/audit projection into an empty database. | F0 and an initial F1 slice are integrated; full typed F1a projection across B/E/G and v2 provenance remains Blocked. | Complete the trusted typed no-secret projection before production use. Preserve the empty-target restore and post-restore Recovery requirement. |
| Backup replication | [WorkBackupReplication.fs](../../Server/WorkBackupReplication.fs) provides a durable local outbox and an injected Git sink. It explicitly creates no background pump or production Git credentials. | Default-off library only. | Reuse the separate asynchronous backup history; add an approved host pump only after the full backup contract and operations policy are accepted. |
| Recovery | [WorkRecoverySqlite.fs](../../Server/WorkRecoverySqlite.fs) provides a default-off seeded Recovery path. The integrated G1a correction is not the same as the later ChangesRequested corrective review; no trusted F1 restore witness or G1b production Recovery exists. | Partial; current Recovery lane Blocked. | Complete F1a first, then trusted restore provenance, stale-generation fencing, and post-restore reconciliation before cutover. |
| Stateful API/UI | [WorkStatefulApiRead.fs](../../Server/WorkStatefulApiRead.fs) is a read-only projection library. The inspected source has no live endpoint registration for it. | H0/H1a slices integrated; current API/UI task Blocked. | Reuse as a read model after the SQLite authority and authorization boundary are complete. Add writes and resource/job projections only through explicit server-owned contracts. |
| Work graph and producers | [WorkQueueGitLedger.fs](../../Client/Modules/WorkQueueGitLedger.fs) labels itself isolated and non-activated; existing orchestration handlers are intentionally not redirected. It still uses the Git-backed LedgerStore transaction boundary. | The proposed Work queue is not the live control plane. Current Git-backed project/account ledgers remain authoritative. | Preserve graph, role, stage, priority, source, review, and integration semantics; port producer mutations to one SQLite transaction rather than activating a second Git or SQLite writer. |
| Host/resource plane | [IHubService.fs](../../Common/SignalR/IHubService.fs) registers DeviceId, DeviceName, protocol/client versions, and SupportedCommands only. [UserService.fs](../../Server/Services/UserService.fs) stores process-local sessions keyed by user/device. [ClientService.fs](../../Server/Services/ClientService.fs) routes commands to a selected connected device. | Command routing exists; authenticated host/resource identity, capability registry, and leases do not. | Add separate host, client-instance, and resource identities plus authenticated enrollment, versioned capabilities, and fenced leases. Do not treat SupportedCommands as resource capability evidence. |
| Jobs and processes | [JobManager.fs](../../Client/Modules/JobManager.fs) starts local processes and retains job/tool-operation records in process memory with bounded eviction. The server dispatches commands over SignalR; there is no durable server-side remote Job record in the inspected path. | Local execution and selected-device dispatch exist; durable cross-execution Job lifecycle does not. | Add durable Job identity, progress, terminal evidence, Unknown/reconciliation, and host lease binding. A job may outlive the model session. |
| Repository/worktree | [ProjectPaths.fs](../../Client/Modules/ProjectPaths.fs) resolves project/worktree selectors on the client; [Worktrees.fs](../../Client/Modules/Worktrees.fs) invokes local Git worktree operations. | Useful local primitives, not a host/resource registry or portable resource identity. | Bind registered repository/checkouts/worktrees to authenticated host/resource IDs and verify source lineage; never use a local path as identity. |
| Browser | [BrowserBridge.fs](../../Client/Modules/BrowserBridge.fs) authenticates local browser-extension connections with a local pairing key and tracks registrations/windows/features per connection. | Useful local browser bridge; its feature set is not a server-published resource capability or lease. | Adapt it as a browser resource adapter with profile/origin identity, availability, exclusivity, and fenced operation leases. |
| Challenges and continuations | The Jarvis 2.0 Challenge/job/timer work remains open/deferred; current JobManager retention is not durable Challenge state. | Not yet a durable Work continuation mechanism. | Add durable Challenges after Work and Job identities are authoritative; use them for blockers, timers, approvals, and uncertain job completion. |

### 9.4 Host/resource findings from the current client path

The current client path is a useful transport and local-execution foundation, but it is not yet the requested resource plane:

- DeviceIdentity persists a local deviceId and ClientRegistration sends it as the server routing key. ClientRegistration has no separate enrolled host credential, client-instance ID, resource IDs, repository inventory, OS/toolchain profile, or typed capability document.
- UserService keeps a process-local session map keyed by (userId, deviceId), with a single current connection/generation for that key. A second process presenting the same deviceId is not a separately identified host instance. The multi-client identity task remains Deferred.
- ClientService selects a connected device and sends an AgentCommand over SignalR. SupportedCommands gates protocol operations; it does not establish that a particular repository, browser, filesystem, or process resource is available or exclusively leased.
- JobManager starts the OS process on the selected JarvisClient. Process handles, output, and tool-operation records live in client memory; completed entries are bounded/evicted. This provides local asynchronous jobs and stable operation IDs within a bounded client-retention window, not a durable server-owned job that survives client restart or can be reconciled from another host.
- ProjectPaths and Worktrees resolve and mutate local checkouts on that client. Repository/worktree selectors and filesystem paths are location details, not authenticated host/resource identity.
- BrowserBridge has a sound local pairing/authentication boundary and per-extension feature tracking, but those registrations remain local to the client service and are not a server-side capability registry.
- Therefore the current remote-execution equivalent is selected-device command dispatch plus local process/browser execution. It is not a durable remote job queue, host capability matcher, or resource lease service. Do not describe the existing device routing as the Jarvis 2.0 resource plane.

The current inspected source is ChatGPT/browser-launch oriented and does not establish a provider-neutral execution adapter contract. ChatGPT and Codex both need real-provider coverage for 2.0; this is a required new acceptance boundary, not a claim that Codex support already exists. A headless JarvisClient daemon/service mode is also an open implementation choice, not an existing capability assumed by this plan.

### 9.5 Current task-state overlay

These are current canonical task states, not inferred from whether a prior source commit was integrated:

| Task | Current state | Interpretation |
|---|---|---|
| work-sqlite-authority-substrate | Closed | B substrate accepted/integrated; no live authority activation. |
| work-sqlite-scheduler-capacity | Blocked | E0/E1a/G1a accepted/integrated slices; Scheduler remains default-off and the full lane is incomplete. |
| work-verified-git-sqlite-import | Blocked | C0–C3 accepted/integrated offline fixtures and proposal; production reader/writer admission and live handover are not complete. |
| work-trusted-source-ingress-adapters | Blocked | Accepted/integrated negative-only preflight; no positive authenticated authority. |
| work-trusted-sqlite-domain-provenance | Blocked | Accepted/integrated v2 schema and negative regressions only. |
| work-sqlite-domain-backup-projection-contract | Closed | Accepted design contract, not F1 implementation. |
| work-c1-normalized-scheduler-import-projection | Closed | Accepted/integrated default-off fixture projection, not production ingress. |
| work-json-backup-restore | Blocked | Earlier F0/F1 foundation is integrated; complete F1a domain projection and trust boundary remain incomplete. |
| work-recovery-role-fences | Blocked | Earlier G0/G1a source is integrated; later corrective review is ChangesRequested and G1b is absent. |
| work-stateful-api-ui | Blocked | Read-only H1a projection is integrated; live API/UI authority is not. |
| work-stateful-i0-acceptance-harness-prep-20261119 | Blocked | No complete independent end-to-end acceptance gate. |
| work-legacy-import-source-witness | Blocked | Earlier source-witness interface is integrated; production authenticated reader is not. |
| work-c-original-git-authenticated-reader-20261118 | Blocked | No production original-Git authenticated reader. |
| work-persistent-agent-entity-retirement | Blocked | Prior retirement/fence source is not a completed 2.0 cutover; active historical Agent lifecycle remains. |
| task-cff673cd81474ccf9b3932bf0ea33109 | Deferred | Multi-client/host identity work is not implemented. |
| draft-execution-resources-client-daemon-and-routing | Open, explicitly held | Candidate draft only. Do not promote it unchanged or treat it as approved implementation scope. |
| work-final-merged-defaultoff-release-gate | Task InProgress; latest attempt Finished | A finished attempt is not a completed task or a 2.0 release GO; no integration receipt exists for this gate. |
| work-stateful-orchestration-alignment | Closed | Historical design document integrated at 7712…; its receipt has no linked review ID for that exact revision. |

No existing task assignment is changed by this amendment. Do not reassign, cancel, reopen, or fan out work from this document; the canonical Lead/Operator workflow owns that sequencing.

### 9.6 WQ01–WQ11 impact overlay

The original WQ01–WQ11 decisions remain the semantic baseline. Their backend implementation must be updated as follows:

| Lane | Preserve | Adapt, replace, or defer |
|---|---|---|
| WQ01 — Work contracts and graph | Composable Work, explicit dependency edges, independent acceptance, required/optional scope | Keep the pure state-machine contract; add milestone scope/progress/priority without coupling Work to an Agent or provider session. |
| WQ02 — Canonical Work ledger | Versioned transitions, idempotency, immutable receipts, project isolation | Replace the hot-path Git-CAS preview with the single server-owned SQLite authority. The current Git adapter remains non-activated. |
| WQ03 — Source handoff and provenance | Exact source repository/ref/commit, review/integration lineage, one-shot effect identity | Bind verified source evidence and durable Job/Challenge evidence to Work transactions; do not trust caller-supplied provenance strings. |
| WQ04 — Producers and authority | Role, source, graph, and parent acceptance checks | Port producer mutations as transactional SQLite commands; do not activate parallel Git producers. |
| WQ05 — Review/integration | Frozen reviewed SHA, explicit decision, exact integrated SHA | Preserve the immutable review/integration boundary and record it in server authority; a review approval is not a host lease or Work completion by itself. |
| WQ06 — Conversations/attention | Useful context and Operator attention | Adapt to provider-neutral execution records and durable Challenges; do not make conversation history the durable Work owner. |
| WQ07 — Capacity | Account-level model-execution admission and specialist capacity semantics | Keep account execution capacity separate from physical host/resource capacity, per-resource concurrency, and exclusivity. |
| WQ08 — Scheduler | Deterministic eligibility, blocker/priority/FIFO ordering, role-compatible matching, replay fences | Reuse E0/E1a/G1a foundations; add provider/resource feasibility and fenced leases before any external dispatch. |
| WQ09 — API/UI | Work-first projections and explicit authorization | Adapt the read model after server authority; add Job, Challenge, host, and lease visibility without exposing secrets or local paths. |
| WQ10 — Migration | Source verification, all-writer fencing, uncertainty handling | Replace bulk conversion as the default with an empty 2.0 start. C0–C3 are fixture evidence only; production selective import is optional and separately gated. |
| WQ11 — Acceptance/release | Independent frozen review, hermetic tests, exact source and integration evidence | Build a full 2.0 fresh-start, host-loss, lease-fence, job-reconciliation, backup/restore, ChatGPT/Codex acceptance gate. The Git-backed baseline gate is not this GO signal. |

Manual grants remain ephemeral and narrowly scoped; scheduled credentials must not become bearer secrets in generic backup fields. Persistent Agent retirement follows the Work/execution/job cutover rather than being treated as an isolated cleanup.

### 9.7 Transition and dependency map

1. **Keep the current runtime safe.** Git-backed ledgers remain authoritative. All SQLite, Scheduler, importer, API, backup, and Recovery slices stay default-off.
2. **Freeze the 1.0 release boundary.** Finish Automation acceptance, independent review, integration, Release validation, and uncertain-effect/job reconciliation. Record the exact deployable SHA and artifact/configuration, then create stable/1.x from that SHA. Until then there is no approved 1.0 branch point.
3. **Complete the 2.0 authority and Work model.** Reuse B and the accepted Work semantics; finish authenticated source ingress, transactional producer/role/priority operations, and the one-authority API. No live Git/SQLite dual writer.
4. **Define host/resource identity before remote dispatch.** Resolve enrollment, client-instance identity, typed capabilities, repository/worktree binding, lease generations, expiry, and revocation. The existing deviceId route is only a compatibility transport.
5. **Add durable Jobs and provider-neutral executions.** Build the host-side process/build/test/browser adapters and server-owned Job records. Require ChatGPT and Codex provider adapters; model sessions and host jobs remain independently reconcilable.
6. **Add Challenges and event-driven resumption.** Persist timers, dependencies, human decisions, and unknown job outcomes so Work can wait without an open model session.
7. **Finish F1a/G1b backup and Recovery.** A trusted typed backup/restore and post-restore fencing/reconciliation gate is required before production authority cutover.
8. **Finish H1 API/UI and I0/end-to-end acceptance.** Validate fresh database startup, multiple host instances, host loss/reconnect, stale leases, long-running jobs after model exit, unknown external effects, backup loss/restore, and both providers.
9. **Cut over cleanly.** Pause 1.0 dispatch and writers, reconcile external effects, preserve old Git ledgers read-only, initialize the 2.0 database empty, selectively import only Operator-chosen Work with verified provenance, enroll hosts, and obtain a separate explicit GO before enabling Scheduler.

Production authenticated original-Git readers and C0–C3 live import gates are dependencies only if selective historical import is chosen; they are not a reason to block an otherwise empty 2.0 database start. F1a/G1b, host identity/leases, durable Jobs, and the I0/E2E gate are required for a safe 2.0 production cutover.

### 9.8 Reuse / adapt / replace / defer

- **Reuse:** Work composition and dependency semantics; independent parent acceptance; role subscriptions and deterministic ordering; versioned transitions and idempotency; immutable source/review/integration provenance; one-shot external-effect journals; ephemeral manual grants; and hermetic tests.
- **Adapt:** B transaction substrate, E scheduler admission, C selective importer, F backup/restore, G Recovery, H read models, WQ producer/role APIs, and existing JarvisClient local process/worktree/browser adapters.
- **Replace:** Git CAS as the live Work hot path; Agent/provider conversation as Work owner; a single selected device as the whole resource model; and client-memory JobManager state as the only record of a long-running operation.
- **Defer:** GPU/CPU cluster scheduling until measured need; Kubernetes; a separate resource-manager database/service; provider quota optimization; blanket historical migration; and any promotion of the held execution/resource draft without a new reviewed specification.

### 9.9 Verification and change boundary

This amendment is based on the clean registered documentation worktree at 142f2bd4b2728ea61e18a9d51f7c663c7951e182, verified identical to current main. Only these two specification files are in scope. No source code, database, feature flag, task assignment, Scheduler state, host, or deployment is changed. The author must record the final documentation diff check and relative-link validation result, then commit locally and submit that exact frozen commit for independent review. Do not push the contributor branch or integrate the amendment as part of this task.


### 9.10 Review-history precision and documentation verification

The earlier G0 review referenced by the frozen orchestration-alignment task spec is 7c840b0da65b4f03acb05b9b4e43d671 and applied to a previous immutable revision. The later review recorded as pending in that task spec is 7ac5fc2f31284e58a0726f3c012a1b74. The integration receipt for 7712bc547c264873036975e30194da03a05cc0ba is b4d21df548aa4d2498c288e528ce17b2 and has an empty ReviewId. Therefore the exact 7712 revision is evidenced as integrated and the task as Operator-closed, but not as independently accepted by a linked review.

Documentation checks on this amendment: git diff --check completed with exit code 0 and no output. Relative Markdown file-link validation checked 23 file links across the two amended specifications and found no missing targets. These checks validate the documentation change only; they are not a source build, end-to-end acceptance, or cutover GO.
