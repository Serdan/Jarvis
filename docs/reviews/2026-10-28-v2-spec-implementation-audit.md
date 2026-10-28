# Jarvis V2 — specification-to-implementation audit

**Date:** 2026-10-28  
**Reviewed worktree:** `Jarvis::orchestration-v2-ledger` / `feature/orchestration-v2-ledger-store`  
**Implementation baseline:** `7b5375d`  
**Normative source:** `docs/specs/orchestration-v2-architecture.md`, §§1–12 (including 54 acceptance scenarios)  
**Code reviewed:** `Client/Modules/LedgerStore.fs`, `ReviewWorkflow.fs`, `V2ReviewApi.fs`, client/server MCP routing and permissions, existing task storage, tests and `docs/OrchestrationV2CommitReview.md`.  
**Disposition:** **Core prototype is functional; V2 specification is not fully implemented and no production cutover is authorized.** This is a static code/spec audit, not a claim that every acceptance test was independently executed in this turn.

## Operating assumptions

- The server remains small and stateless; one authoritative orphan Git ledger ref per configured project.
- Exactly **one implementation agent owns a given task**. No protocol for simultaneous implementation owners of the same task is needed.
- An agent can keep Working while independent reviewers examine immutable revisions; unclaimed review requests remain replaceable.
- Agent states are Ready / Assigned / Working / Completed / Parked and are not inferred from inactivity.
- Source implementation, review approval, source integration, and task closure are separate facts.
- V2 task files created by this audit under `docs/tasks/` are **source-controlled backlog artifacts**, not live orphan-branch records. No V2 production binding, migration, or orphan-branch mutation was performed.

## What is implemented

| Area | Evidence | Assessment |
|---|---|---|
| Orphan Git ledger init, snapshot fetch, identity checks, operation-ID hash, CAS retry | `LedgerStore.fs:159–264,375–415,698–817` | **Core implemented**; independent persistent trust, intent recovery and complete operation result still absent |
| Task HTML + metadata creation and Open-only revision | `ReviewWorkflow.fs` create/revise/get functions | **Partial**; source import/diff/export, full content paging and assigned-spec adoption missing |
| Five agent states, explicit yield/resume/complete, checkout-independent slots | `ReviewWorkflow.fs` agent transitions/inbox | **Core implemented**; full durable generations, receipts and administrative attempt transitions missing |
| Versioned review requests, claim, replacement, decisions, handoff, feedback | `ReviewWorkflow.fs` review requests and per-request notices | **Core implemented**; complete review evidence, immutable decision audit and release/reclaim/cleanup missing |
| Nonpreemptive rejected-review reissuance and explicit recovery | `ReviewWorkflow.fs` reissue/recover | **Core implemented**; immutable updated instructions/receipts missing |
| Exact accepted-head fast-forward verification | `ReviewWorkflow.fs` integration actions; `LedgerStore.fs:632–695` | **Implemented only for exact commit-OID fast-forward**; no merge/cherry-pick equivalence or complete cross-ref recovery |
| Experimental typed MCP tools, opt-in local binding, Git-write permission | `Server/McpTools.fs`, `V2ReviewApi.fs`, `PermissionPolicy.fs` | **Additive prototype**, not native 4.0 task API, integrated TUI, migration or global V1 writer cutoff |
| Disposable two-clone review and integration fixtures | `Client.Tests/ReviewWorkflowTests.fs`, `LedgerStoreTests.fs`, `Server.Tests/ServiceTests.fs` | Valuable regression baseline, **not** full §11 acceptance coverage |

The prior implementation milestone reported **304 client / 60 server tests passed**. That is evidence for its existing fixtures, not evidence for all 54 normative acceptance scenarios.

## Concrete gaps and divergences

### Critical contract/operational gaps

**A01 — Canonical trust/onboarding is not production-ready (Task 01, P0).**  
The local configuration (`V2ReviewApi.fs:14–73`) is loaded from a manually supplied per-checkout JSON file; it has project/ledger IDs and approved remote URLs but no persisted independent `highestTrustedHead`, no exposed BindExistingLedger/RebindLedgerRemote/GetLedgerHealth or initialization UX. `LedgerStore.fetch` (`231–264`) checks ancestry against its local cache ref, which is not the complete configured trust/recovery contract from §2. Public-remote/privacy and branch protection consent are not enforced by this setup.

**A02 — Idempotence is not a complete recovered logical-result contract (Task 02, P0).**  
`LedgerStore.transact` (`768–806`) persists an operation record consisting of kind, actor, paths and sequence; it does **not** persist the full result data required by §5.1. A repeated call can return an operation record but not reliably replay the originally promised typed task/checkpoint output. No GetOperationStatus or crash-persistent pending-intent journal is exposed. `V2ReviewApi` converts distinct `LedgerFailure` cases into generic `Client.ValidationError` strings, erasing typed caller remedies. The retry delay (`LedgerStore.fs:815`) is exponential but not jittered as specified.

**A03 — Runtime schemas and size gates disagree (Task 03, P0).**  
`ReviewWorkflow.fs:11–155` records omit many mandatory versioned schema/source/evidence fields; general deserialization allows defaults/unknown values in several paths. The normative layout's checkpoint/audit and path-escaping rules are not consistently represented. **Direct mismatch:** task HTML validation allows 128 KiB in `createTaskWithSpec`, but `LedgerStore.transact:751–755` rejects every changed record over **64 KiB**. A valid-looking task may therefore be rejected at commit time. The schema/version/serialization and content-limit contract must be reconciled before import.

**A04 — Complete review/checkpoint evidence is missing (Task 05, P0).**  
`ReviewRevision` (`ReviewWorkflow.fs:70–83`) tracks request, base/head, source/target refs and summary, not immutable spec blob, source repository identity/remote, final checkpoint, test evidence, reviewer evidence scope and claimed source publication OID from §§3–4. A standalone Working `CheckpointAttempt` and atomic final submission checkpoint are absent. Yield/Complete/Recovery checkpoints do not constitute the specified full record.

**A05 — Trust/permission/privacy/operator surface incomplete (Task 11, P0).**  
There is local Git-write confirmation, but no complete operator onboarding, privacy warning/sanitization of append-only task history, reviewer identity policy, actionable Ready/Assigned TUI, pending-intent recovery UI or typed failure presentation. Actor labels are supplied by tool callers; they are not themselves credentials. V2 MCP opt-in is not equivalent to production authorization and cutover.

**A06 — Backup/restore cannot be demonstrated (Task 13, P0).**  
No ExportLedgerArchive/VerifyLedgerBackup, protected independent mirror, manifest-based restore, epoch reconciliation, or restore drill. Local cache refs and a root initializer are insufficient to satisfy §§2.2, 9, 11.

**A07 — V1 migration is unimplemented (Task 14, P0).**  
No InspectLegacyTaskMigration/ApplyLegacyTaskMigration, cross-worktree competing-copy diagnostics, deterministic manifest, explicit conflict resolution, approved one-root import, or independent-clone verification. Existing V1 source branch task files remain active for V1.

**A08 — Protocol 4.0 writer gate/cutover is unimplemented (Task 15, P0).**  
`Common/Messages/AgentCommand.fs` advertises experimental protocol **3.24** with additive V2 commands. V1 task mutations remain available in server/client routing. No stateless major-version gate globally rejects old task writers, no TUI switch, and no one-way post-first-V2-write rollback policy.

### Remaining feature and lifecycle gaps

**A09 — Immutable assignment-generation instructions and receipts (Task 04, P1).** Mutable `AssignmentGeneration` and pinned `SpecBlobOid` exist; generation-specific instruction documents, issuance actor, receipt, AcknowledgeAssignment, adopt/reissue spec, and the full history contract from §3 do not.

**A10 — Frozen reviewer claim recovery and audit (Task 06, P1).** Review claim/reassign are implemented, but no ReleaseReviewClaim/Reclaim frozen revision flow, append-only per-claim/release/decision evidence, or verified cleanup/retention of abandoned source review refs. Claimant handoff is narrower than §4.

**A11 — Lifecycle transition matrix incomplete (Task 07, P1).** Missing ParkAttempt, BlockAttempt, ResumeAttempt, CancelTask, AbandonAttempt, explicit owner transfer/reopen and no-source close. Agent Parked is implemented, but not a replacement for task/attempt Parked.

**A12 — Source/worktree binding incomplete (Task 08, P1).** BindReviewBase stores local base SHA and target ref, but the attempt lacks a complete registered source-repository/remote/ref binding and source observation/recovery; no BindAttemptSource with worktree move/detach handling.

**A13 — Task spec lifecycle incomplete (Task 09, P1).** No ImportTaskSpec/DiffTaskSpec/ExportTaskSpec or stable paged content read. Open-only Revise is safe for a prototype, but explicit adoption by an assigned generation is unspecified/absent.

**A14 — Query/history/pagination absent (Task 10, P1).** Missing native ListTasks, GetAttempt, GetAgentRoster, GetOrchestrationSnapshot, GetOperationStatus, task history and snapshot-bound continuation tokens. `getAgentInbox` (`ReviewWorkflow.fs:1275–1403`) scans all task/review/notification records on one snapshot, without the specified 100-row/1 MiB bounded page API.

**A15 — Strict integration verifies only a narrow topology (Task 12, P1).** Exact remote reviewed-commit fast-forward is intentionally correct for that case. Merge/cherry-pick/squash equivalence, cross-ref ambiguous-outcome reconciliation and safe worktree retirement remain absent. Do not weaken the approved patch identity to bypass verification.

**A16 — Acceptance coverage short of completion gate (Task 16, P1).** Existing tests cover the prototype's main transitions but not all 54 adversarial cases, including multi-client contention, cross-platform path/case handling, forced rewrite with persistent anchors, restore drill, migration conflict resolution, public-remote checks or the 4.0 writer gate.

## Spec reconciliation required during implementation

The spec has accumulated append-only revisions and some outdated examples. In particular:

- Normative ledger paths/field examples differ from the current implementation (e.g. `<attempt>.checkpoints/` vs `attempts/<id>/checkpoints/`, the encoded-agent sample vs actual base64url name, immutable decision files vs mutable notification status).
- The early `OrchestrationV2CommitReview.md` sections describe pre-MCP limitations even though later sections record the MCP bridge. Treat that document as chronological notes, **not** a current acceptance statement.
- §4's broader `ReviseTaskSpec` and `AcknowledgeAssignment` contracts exceed the prototype's unassigned-only revisions and Begin behavior.
- Keep the approved single-implementation-owner assumption. Do not introduce speculative concurrent-owner machinery while closing these gaps.

Task **03** owns schema/path/spec clarification first; the final authoritative contract must be synchronized with code and tests, not silently changed to match an incomplete prototype.

## Dependency-aware backlog

The 16 paired JSON/HTML backlog tasks are enumerated at `docs/tasks/2026-10-28-v2-backlog.md`. The immediate unblocked foundation is **01 trust, 03 schema**; operation durability (02) follows trust, while evidence (05) follows assignment generation (04) and schema. Source integration, migration and cutover are deliberately later.

Parallelizable outcomes do not require parallel agents implementing the **same task**. Each task is a separate ownership unit.

## Completion conditions

This audit and the task files are finished when all 16 HTML/JSON pairs exist and validate, dependencies resolve, the report is committed in the source branch, and the source worktree is clean. **Completing this audit does not complete V2.** Closing the V2 program additionally requires all required spec acceptance checks, an independent backup restore, approved migration and global protocol 4.0 cutover. No production ref should be initialized or mutated as part of this audit.
