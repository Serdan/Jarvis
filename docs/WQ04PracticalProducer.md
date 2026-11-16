# WQ-04 practical project producer (not a scheduler cutover)

The opt-in entry point is `Client.WorkQueueCanonicalCasBinder.WorkQueueCanonicalCasBinder.publishPractical`.
It recognizes original `CreateTask`, an unclaimed `SubmitBranchReview`,
and accepted-review-backed `SubmitIntegration` (Pending). No arbitrary
caller-supplied `WorkStage` can pass this entry point.

For each event, the binder reads the original operation, its request hash,
historical commit and typed original entity through
`WorkQueueCanonicalSourceEvidence.read`. The WQ project writer then checks
the same pinned Git head/high-water, original operation and entity again in
the winning `LedgerStore.transact` planner. It atomically writes:

- `records/work-queue/project.json` (the next project version and work);
- `records/work-queue/origins/<sha256(work-id)>.json` (source operation,
  original historical entity, selector, source ref/SHA, role/credit status,
  and explicitly unresolved external effects).

The recorded origin is **inspectable data**, not an opaque permission.
`WorkQueueGitLedger.readOriginAt` gives agents its exact source and
conflict history. Source operation UUID and the producer UUID are distinct.
A duplicate source/stage under a different producer UUID reports a conflict.
A retry with the *same* producer UUID reads the original immutable producer
receipt and provenance and returns `AlreadyApplied = true` without
re-executing a stale source assertion.

An integration work item has a **hard prerequisite** on the exact previously
published frozen-review stage and its matching original provenance.
It remains blocked until the review prerequisite resolves. The existing
blocking-first, FIFO and cycle graph rules stay in `WorkQueueContracts`.
The explicit test-only `executeInertFixtureAt` path can demonstrate
planning claim/completion on a disposable frozen ledger.

`WorkQueueRoleAuthority.selectPracticalDemand` reads the current
versioned role catalog and queue from one trusted Git head, then returns
`Chosen`, `MatchingSkips`, and cycle diagnostics using the existing
blocking-first/FIFO ranking. It does **not** create demand, reserve credit,
admit a role, or launch an agent. This is deliberately separate from
the older high-water-assertion preflight and is appropriate for an agent
to inspect and reconcile without an exhaustive cross-ledger certificate.

## Recovery / ordinary Git inspection

On lost acknowledgement, first fetch the approved orchestration remote,
inspect the original producer UUID, the current work and its origin
record. If the original UUID exists with the same request, replay it; if a
different UUID already published that stage, reconcile the existing
provenance instead of silently duplicating it. A changed Git head or
project version is a normal optimistic conflict: re-read canonical
task/review/integration state and decide the next action.

For corruption, keep the known-good ledger commit SHA, inspect the
before/after difference, and repair the ledger directly with a reviewable
normal Git commit rather than requiring a new software bypass. Useful
read-only commands against the **approved** remote include:

```sh
git fetch origin refs/heads/jarvis-orchestration
git rev-parse FETCH_HEAD
git show FETCH_HEAD:records/work-queue/project.json
git show FETCH_HEAD:records/work-queue/origins/<origin-file>.json
git diff <known-good-sha> FETCH_HEAD -- records/work-queue/project.json
```

A missing or divergent origin is reported as a recovery issue, never
silently treated as completed work. Keep before/after SHAs and diffs in the
task. There is no need to lock down the Git ledger against trusted agents.

## Deliberately separate work

Project publication **does not reserve account credit**, admit a role,
claim work, verify an external push effect, authorize integration, start
a Scheduler, or perform migration. The role ID/version is empty on
project-only origins and the credit status is `NotReserved`. Later
role/credit/execution wiring must use those visible facts rather than
mistaking a produced stage for a launched worker.

The existing legacy `inspect` and `attemptUnderCas` pessimistic
analysis paths remain unchanged for their earlier tests; the positive
path is `publishPractical`. Review publication currently requires the
canonical review to be unclaimed; source corrections/replaced reviews and
late event backfills need an explicit follow-up path.
