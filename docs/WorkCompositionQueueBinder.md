# Composition-to-WQ02 same-head binding: fail-closed boundary

Status: **not a live binder**, not Scheduler authority, not a Work publisher.

The accepted Work composition and WQ02 queue implementations currently have
independent original mutation planners. The generic Git-ledger transaction can
atomically publish multiple file changes, but the approved graph acceptance
verifier, queue candidate validation, canonical origin and queue operation
receipts are encapsulated in **different writer paths**. Combining two existing
public mutators invokes two separate Git CAS operations. That would allow a
visible intermediate head where the composition graph is Blocked and WQ02 is
Available (or the reverse). The new
`Client/Modules/WorkCompositionQueueBinder.fs` refuses to claim otherwise.

## Explicit immutable request

The read-only assessment binds one verified Git head to the original
`(projectId, unitId, unitSpecVersion, graphVersion, integrationSubmissionId,
sameHeadWorkOriginOid, stageGeneration)`, plus the exact WQ02 Work ID and
project version. It checks a canonical task linkage, hard prerequisite current
acceptances, original review purpose/spec closure, integrated review and source
receipts, WQ02 stage identity, pinned provenance blob, original event identity,
source/role evidence and queue dependency-cycle diagnostics. A caller-supplied
acceptance flag, arbitrary UUID, stale spec/graph/work generation, mismatched
origin OID or missing integration is never treated as positive evidence.

The return type has **only** `RecoveryBlocked`; it cannot be mistaken for a
Scheduler capability. `attemptUnderCas` repeats the audit inside the existing
Git-CAS planner, then returns an explicit error without writing either state,
creating an operation receipt, consuming account credit or dispatching a launch.
The same UUID cannot turn an unsuccessful attempt into a replayed success.

## What is still missing before a positive binding

1. One accepted internal composition verifier and WQ02 validated stage
   transition planner usable **inside the same** `LedgerStore.transact`
   callback. Both must use the original canonical operation/replay receipt,
   original WQ04 Work ID/generation/source UUID and expected entity blob OIDs.
   Sequential calls, projected DTOs, an unverified caller-supplied `Accept`,
   or separate Git writes are **not** substitutes.
2. A canonical, durable mapping from each composed unit's hard prerequisites
   and accepted proof versions to WQ02 stage gates. Queue availability must
   remain false until all graph and source gates pass at the pinned winning
   head. Future graph revisions/reopens must close eligibility atomically
   before another scheduler observation; existing `setHardPrerequisite`
   and `removeHardPrerequisite` transitions alone cannot prove acceptance.
3. Same-head cross-system role/source authority, original operation provenance,
   terminal/observed external-effect reconciliation, frozen review and
   integration receipt checks. An externally committed effect under an unresolved
   gate must remain on hold, never be reclassified as a successful integration.
4. The WQ08/WQ09 read-only projection and the default-off WQ10 Scheduler must
   consume the resulting original WQ02 stage and graph gate at the **same
   approved head**. No persistent Agent ID may be assigned as a Work owner.
   Project Work readiness does not reserve account capacity or authorize launch.
5. Two independent disposable clones and the complete fake-only WQ11 vertical
   slice must show **no contradictory readiness**, including cancellation,
   stale graph/spec/generation, wrong reviewPurpose, wrong source/origin,
   competing writer, lost acknowledgement and cycle scenarios. No live cutover
   before these positive cases are verified.

## Current negative acceptance

`WorkCompositionQueueBinderTests` uses two disposable Git clones and verifies
that cross-System/Feature prerequisites remain diagnostic blockers when WQ02
has already published an Available frozen-review stage. Random or stale evidence,
competing attempts and changed canonical heads leave both original ledger
records, operation sequence and queue state untouched. These tests demonstrate
**refusal**, not successful graph-to-stage activation.

No real scheduler activation, live WQ02 mutation, model/browser provider
dispatch, Account mint, Agent cutover, migration, deployment/restart, or hosted
GitHub Actions is performed by this implementation.
