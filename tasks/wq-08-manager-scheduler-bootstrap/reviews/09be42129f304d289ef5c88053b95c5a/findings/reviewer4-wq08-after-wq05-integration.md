# Reviewer 4 independent findings — WQ08 after WQ05 main

**Task:** `wq-08-manager-scheduler-bootstrap`  
**Review request:** `09be42129f304d289ef5c88053b95c5a`  
**Frozen head:** `380a6cab474311809397ba80afeb1057b4505546`  
**Disposition:** Accepted for **inert-source integration only**; no Scheduler/runtime activation implied.

## Exact-commit and merge audit

- Read the canonical frozen review details and checked the local clean `integration/wq08-after-wq05-main` checkout. The merge has exactly two parents: first `273e2eabbe01f3158185c949618d8c5de071a8ad` (current main including WQ05), second `649880085891f0b446add1ab99f87b2c347dd934` (independently Accepted WQ08 correction; original review `2bf5da0f847c4efa8e5e17dc3485d4ed`, explicit Accepted seq3822).
- `git diff --name-status 273e2eab..380a6cab` contains exactly five paths: the two inert WQ08 source/test files, the two F# project files, and `docs/specs/wq08-inert-scheduler-review-notes.md`. No existing WQ04 producer/contract/guard files, WQ05 client adapters, browser routes, live host paths or Scheduler registration are changed.
- `git diff --exit-code 64988008..380a6cab -- Server/WorkQueueSchedulerInert.fs Server.Tests/WorkQueueSchedulerInertTests.fs docs/specs/wq08-inert-scheduler-review-notes.md` returned exit 0: reviewed WQ08 source/tests/notes are byte-identical to the prior Accepted second parent.
- The two F# project edits add the inert compile file, its test file, and `InternalsVisibleTo Server.Tests`; inspected their complete first-parent diff. `git diff --check 273e2eab..380a6cab` returned exit 0. Worktree was clean.

## Source-level safety and semantics

- The normal `InertScheduler.plan` (lines 219-228) uses `RuntimeGuarded` and `Contracts.canDispatch`. The current WQ04 guard in `Server/WorkQueueContracts.fs` lines 1634-1640 is unconditionally false until a separately reviewed same-head canonical source/high-water/role adapter exists. WQ02 migration and index booleans, forged stage IDs, and hypothetical role entries cannot promote it.
- The only positive planning route is deliberately named `planHypotheticalFixtureOnly`, `internal` with friend visibility only to the test assembly. Its `canDispatchPrototype` is used in disposable projected models, not the production guard. Repository-wide `git grep` for `InertScheduler` and that test seam found only the inert source and explanatory notes outside the test file—no runtime call site, service registration, host effect or browser dispatch wiring.
- Pure planner ranking uses existing `Contracts.orderAvailable` and `Contracts.admitProject`: blocking predecessor first, then deterministic FIFO, role compatibility and pinned role version/instruction digest in a length-prefixed SHA-256 request fingerprint. Capacity=1 Worker does not spawn a model Manager or create an eager UUID.
- `reserveAccount` and `admitAfterReserve` are distinct pure steps and the latter requires original reservation receipt, token, matching project version and work generation. C01 abort-only terminal tombstones precede token release; `releaseAfterTerminal` requires matching project/role/token and project terminal receipt through WQ07. No claim of atomic cross-ledger Git authority is made.
- Uncertain original browser submission maps to `ReconcileOriginal`, never a second send. First-connect UUID is allocated only for eligible new registration; same-session replay returns the original stored lease, distinct session is rejected, terminal and stale CAS are fenced. Deadline expiration requires original dispatch record plus an asserted settlement flag. These remain *pure fixture adapters* without trusted host journal evidence.
- Four disposable two-clone local Git tests cover final-slot account CAS, C01 vs admission, first-connect different sessions, and same-launch receipt replay. Their synthetic `state.txt` commits demonstrate non-fast-forward rejection/re-read plus pure model behavior, **not** production Git ledger/source/host attestation.

## Validation

- Ran independent `.NET 11` Release `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore --nologo` against exact frozen branch: **255 passed, 0 failed, 0 skipped** (job `843da14c8e284f4db74eb91f887792ef`). This includes the three formerly failing old-base WQ08 cases reconciled with current WQ04 fail-closed semantics, and existing Server.Tests WQ04/WQ07 integration coverage.
- Source changes are append-only to the inert implementation/test surface relative to WQ05 main, so WQ05 existing application/client behavior is not edited by this merge.

## Boundaries and final assessment

**No blocking integration finding for the frozen inert-only merge.** The source identity, merge shape, guards, no-wiring isolation and Release Server.Tests result support **Accepted**. This is **not** authority to integrate a live Scheduler or admit actual sessions. A future, separately reviewed runtime adapter must prove complete authenticated account/project ledgers, canonical WQ04 producer/index high-water tied to the same Git head, immutable role witnesses, WQ05 hard-edge review provenance and durable host-owned one-shot journal/uncertainty receipts. Never treat the fixture booleans, test-only prototype or a server SQLite store as authoritative. No hosted CI, production scheduler, migration, deployment or main push was performed during this review.
