# WQ-04 review corrections: authority and activation boundary

Source: isolated Worker 4 correction branch `fix/wq04-authority-role-once-worker4`.
Task: `wq-04-queue-producers-priority-roles`; updated spec seq3581.
Both independent rejected source `6f0686720e56e198e085111cd8f22f5c6a6d7b83`:
- Task-bound review `d289b8ac73b749a99cdfc0eefce0bbd1`, ChangesRequested seq3577.
- Generic review `_reviews/b8832f9d17bd4af79555274b17091c36`, ChangesRequested seq3566.

## Security judgment: fail closed, not a completed authority adapter

`WorkMutation.ProduceEvent` had been accepting caller-authored canonical-event claims (including
source identity, frozen review/AcceptedIntegration verdict claims and challenge claims) and
publishing actionable work under project Git CAS. Project CAS only serializes competing
writes. It does *not* validate that a source event exists, has the claimed generation, or
is an Accepted review for the precise SHA/target. The high-water proof compared asserted
counters to asserted indexes; equal counts and booleans cannot prove exhaustive event
membership. The source production adapter lacks the same-pinned-snapshot event/index
link and the WQ-03 trusted source terminal/host protection evidence is unresolved.

Accordingly, `WorkQueueGitLedger.ProduceEvent` now rejects unconditionally with
`IncompleteCoverage("WQ-04 canonical event/index authority missing; recovery blocked")`
before any project transition, regardless of apparently valid counters/claims.
A defense-in-depth second source-proof branch rejects the same mutation.
`PublishStage` and `PostMessage` cannot smuggle a derived `wq04-` stage;
admission and claim are denied when an unverified `wq04-` stage is present.
This does not disable the separate accepted WQ-02 normal-stage model or its
ordinary task attention. It prevents treating WQ-04 prototype projections
as verified canonical source events.

**Required future prerequisite to lift this fence:** a reviewed same-snapshot authority
adapter (potentially WQ-05) must bind each source task/review/challenge/message event
to immutable source ID, typed lifecycle decision, exact target/ref/SHA, stage generation
and immutable publication receipt; derive exact source/queue/message event/index
coverage from pinned canonical roots; reject missing/stale/duplicate origins; and
commit the projection atomically with the project event CAS. The adapter must
honor unresolved WQ-03 source job/push journals and WIP host protection as blockers.
No claim of an authenticated producer or verified high-water is made here.

## Role-v1/v2 admission drift

The pure `RoleCatalog` retains version history, but it is not a CAS-visible
project ledger source. Existing WQ-02 `Contracts.compatible`, `admitProject`,
`reconcileCredits` and `claimNext` still consult unversioned
`Project.Subscriptions`, while the prototype WQ-04 selector consults
`RoleCatalog.Versions`. They cannot be made mutually authoritative merely by
supplied `RoleCatalogRevision` or coverage flags.

`WorkQueueProducers.selectRoleDemand` therefore always fails closed after
elementary consistency checks with `IncompleteCoverage` explaining absent pinned
role admission/source-index proof. Tests assert disabled/changed/missing v1/v2
configurations cannot return even an empty-healthy demand.
Before activation, add and independently review a durable immutable role
history and current version in the *same* project CAS snapshot as admission,
compatible credit rematching, role-message checks and primary/ancillary claims.
Older launches must reference immutable pinned historical versions. Do not
interpret this fail-closed prototype as completed role-admission integration.

## Exactly-once integration-policy successor

Pure `WorkQueueProducers.workId` now uses the immutable preceding integration
work ID plus project/token as the unique logical successor key, independent
of untrusted caller SourceId, receipt and StageGeneration. Only generation 1
of an authorized, cleared, terminal, integrated predecessor for the same
task can produce the successor. Duplicate publication with distinct operation
IDs hits an immutable stage-ID collision under project CAS. Same-operation replay
uses a full-event semantic receipt to reject changed payloads; no duplicate
logical successor is emitted. **This is a pure fixture property only**, because
the actual Git producer route stays uncallable until authoritative terminal
policy-resolution receipt validation is available.

## Validation and review boundary

Executed .NET 11 Release in this isolated worktree:
- Server focused `ServerWorkQueueProducerTests`: **10/10 passed**, 0 failed.
- Server `FullyQualifiedName~WorkQueue`: **98/98 passed**, 0 failed.
- Client focused `WorkQueueProducerGitTests`: **5/5 passed**, 0 failed.
- Client `FullyQualifiedName~WorkQueue`: **30/30 passed**, 0 failed.
The broader counts include focused tests. Existing compiler warnings remain
outside modified files; all project tests in these filtered runs passed.

Disposable two-clone Git tests explicitly exercise
forged undecided reviews, mismatched AcceptedIntegration identity/target,
stale event generations, changed replay payloads, equal fabricated high-water
counters with missing source proof, role v1/v2 drift/disablement and contested
operations. Report exact test counts alongside this document in the frozen
task-bound review. All WQ-04 runtime Scheduler launch, live token reservation,
integration/deployment/migration and production source handoff remain blocked.
