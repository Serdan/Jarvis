# WQ10 — disposable two-project offline cutover rehearsal

**Date:** 2026-11-16
**Task:** `wq10-source-writer-target-ref-review-fix` (Worker 9; second-review source-lineage correction)
**Source baseline:** `6fcd0f8812b3957b2d4dd6c9baf754c9dd00d303` (verified `main` when isolated branch was created)
**Branch:** `refs/heads/fix/wq10-target-ref-lineage-worker9-20261116`
**Rejected historical sources:** `3bf142f22fc7a54b5e4c0b69e42f3b10e29f5376` (review 61318cb215db4d9b85ceadb75eaa1b8e) and `62d9b0f400a1e1c01519f73af1a6faeae963b2e7` (review 6d59b6f4ad284d2eb17a46c990e88a6c); neither integrated
**Production disposition:** **NO GO / NOT AUTHORIZED**

## Scope and evidence boundaries

The accepted WQ10 .NET 11 offline inspector stays unchanged. A small additional
`tools/Wq10MigrationPreview/rehearsal.py` consumes its *actual* read-only JSON
inventory and independently captured WQ08 read-only Scheduler-preview JSON
for **two distinct projects against one account authority**. It produces a
deterministic operator-facing Go/No-Go matrix, exact account/project pinned and
observed Git heads, Manager/old-writer fences, capacity, pending Task/Role
messages, immutable source identities, original uncertain Git/browser/job IDs,
and an explicit backup/diff/original-ID receipt plan. JSON and Markdown are
both available on stdout.

The wrapper does not write source/ledger files or fetch remote state itself;
the accepted WQ10 inspector only invokes bounded read-only Git commands
against explicitly supplied local clones. Its proof booleans, WQ08 JSON,
original-ID inventories and read-only pinned heads are **operator-supplied
evidence**, not server-certified live completeness. Missing/mismatched heads,
WQ08 issue inventories, dispatch-off assertions, role revisions, cap-one
observations or original source provenance fail closed. WQ08 emits PascalCase
JSON; the wrapper handles that and camelCase.

**The green status is `PREPARED_FOR_REVIEW`, not permission to migrate or
activate.** `AccountNotActivated` is tolerated once for a nonactivated account
at its matching exact Git head. The real host also unconditionally emits
`DispatchDisabled`; that diagnostic is tolerated once only at the matching
project Git head under `Mode=ReadOnlyDryRun`, `CanDispatch=false`.
Missing, duplicated, adverse or contradictory WQ08 diagnostics block readiness.
The host preview never calls a provider or authorizes dispatch.

## P1 review corrections — fail-closed gate

The earlier independent review rejected the frozen rehearsal source for two
confirmed P1 defects. The first correction addressed those findings but received
a second P1 source-lineage finding. This branch replays the two rehearsal commits
onto later verified `main`, then adds the second-review fix in isolation.

1. **Host diagnostic compatibility:** The real WQ08 `previewWith` always appends
   `DispatchDisabled`. A matching single issue is now an expected inert
   observation only when `Mode=ReadOnlyDryRun`, `CanDispatch=false`, and the
   issue's record matches the pinned project head. The host account phase and
   versioned project/role catalog remain checked; all adverse unknown,
   identity, capacity, browser and malformed issues remain `NO_GO`.
2. **Source provenance and candidate bypass:** A caller-controlled
   `requireCandidate=false` no longer suppresses checking. Even without the
   flag, `wouldLaunch=null` is non-green. Positive candidates must agree
   with the WQ10 inspected legacy task-stage inventory, their immutable
   WQ04 canonical work state, queue/generation/eligible set, role version,
   source-writer lineage and original source receipt. The wrapper reads
   `records/work-queue/origins/SHA256(workId).json` from the **same pinned
   local project Git commit**, and checks its real blob OID against
   `wouldLaunch.originBlobOid`. The pinned Git comparison is not a
   cryptographically authenticated upstream cutover receipt.

The reviewer-supplied adversarial shape is covered verbatim: **two** prepared
projects, each with one legacy task and pending Task/Role messages,
`wouldLaunch=null` in both captured previews, and `requireCandidate=false`.
It now yields `NO_GO`, with `CANDIDATE_BYPASS_REJECTED` and two
`SOURCE_PROVENANCE_UNVERIFIED` blockers. Other regressions independently
change a candidate work ID, generation, queue, role, ref, commit, original
operation ID/request hash, exact origin blob and pinned origin content.
A fake green WQ08 issue array with omitted `DispatchDisabled` also yields
`NO_GO`.

## P1 second independent review correction — original source-writer identity

Review `6d59b6f4ad284d2eb17a46c990e88a6c` (ChangesRequested seq4563)
reproduced a false green despite the prior source-origin checks: Alpha's
canonical `W-Alpha.sourceLineage.targetRef=refs/heads/unrelated` contradicted
the independently inventoried original attempt's `targetRef=refs/heads/main`.
The original WQ10 inspector's `sourceRefs` combines source and target refs for
display; a nonempty canonical target or membership in that flattened list is
not an identity proof. `sourceProvenanceVerified=true` was therefore false.

The corrected rehearsal re-reads **each inventoried legacy attempt** directly
from the same pinned project Git head, checks attempt path/task identity, and
requires one original attempt to witness the complete target ref + source ref +
source commit tuple. It independently reconciles the attempt's source repository
fingerprint (using recorded approved fetch/push endpoints) against the canonical
WQ03 repository fingerprint, and checks lineage ID when available in the
original attempt. Missing, split, contradictory, or incomplete evidence yields
`SOURCE_PROVENANCE_UNVERIFIED` / `NO_GO`, with exact refs and head in diagnostics.
Matching independent identity remains `PREPARED_FOR_REVIEW` in offline fixtures.

Five additional regression tests cover the exact two-project targetRef mismatch,
the matching positive case, an original attempt with no targetRef, split tuples
assembled from separate attempts, and wrong/missing source repository evidence.
No `requireCandidate=false` bypass or adverse WQ08 issue tolerance is added.

## Executable matrix: two separate pinned offline cases

The focused Python fixture creates local bare account and two project remotes
with two independent Git clones of each authority. Before mutating the blocked
scenario it preserves independent immutable bare remotes for the prepared case.
The fixture synthesizes **source-backed WQ08 host-contract JSON**, including
the mandatory `DispatchDisabled` and `AccountNotActivated` diagnostics.
The tests assert these behaviors against the actual WQ08 host source and run
the separate real-host `Client.Tests` slice. This does **not** claim that
the Python tests invoked the live WQ08 Git adapter or captured production JSON.

| Gate | Prepared two-project case | Blocked case |
|---|---|---|
| Account migration | Preparing, cap **1**, held **0** | Preparing, cap **1**, held **1** (original `launch-Beta-1`) |
| Project migration | Alpha/Beta `Prepared`, canonical coverage flags and origins present | Same account, Beta's legacy Manager still Working |
| Historical work | Legacy tasks preserved with source refs; available canonical stage with explicit origin/high-water | Beta has an additional Started Manager-owned legacy attempt |
| Manual communication | Original pending `Task-Alpha`, `Role-Alpha`, `Task-Beta`, `Role-Beta` retained | Same pending original Task/Role messages; nothing re-dispatched |
| Scheduler host preview | Default-off `ReadOnlyDryRun`, Planner v1, source UUID/hash/**exact matching Git origin blob**, mandatory inert diagnostics, cap=1 free=1 | Cap full, `BrowserJournalUnknown`, no dispatch permitted |
| Git writers | Exact pinned project/account heads equal observed heads; Manager stopped, old writers fenced (fixture claims) | Competing second-clone old writer advances Beta Git ref beyond pinned head; writer and Manager fences unproven |
| Uncertainty | Explicit empty original-ID inventories | Git operation `44444444-4444-4444-4444-444444444444` Unknown; browser effect `55555555-5555-5555-5555-555555555555`; source job `source-job-Beta-9` |
| Rehearsal verdict | **PREPARED_FOR_REVIEW** | **NO_GO** |
| Actual cutover | **NOT_AUTHORIZED** | **NOT_AUTHORIZED** |

The generated matrix records the **exact runtime Git SHA of every account
and project snapshot**, the observed refs, actors, source provenance and
manual recovery actions. It cannot embed stable fixture SHAs in this document:
the fixture creates fresh disposable commits per run and deletes the temporary
roots afterward. To preserve an audit packet for a separately authorized
operator exercise, redirect the Markdown and JSON reports into an approved
folder **outside** all clones before their cleanup.

## Manual reversible backup / diff / receipt plan (not executed)

1. Quiesce the approved old-client/Manager writers and independently verify
   that all sources of pending jobs, active assignments, browser operations and
   stale clients have been inventoried. Do not infer stopped state from a quiet
   connection, a missing receipt, or an elapsed timeout.
2. Record the **exact account and both project head SHAs**, ledger IDs, epochs,
   refs, original operation UUIDs, source refs/commits, pending message IDs,
   and held admission/launch IDs. Make separately stored and test-restorable Git
   bundles at those refs before any separately approved mutation.
3. Compare each pinned head with its currently observed Git ref; retain the
   resulting immutable diff and associated original actor/receipt evidence.
   On a changed head, independently inspect the competing writer's original
   action and re-run the preflight; never silently adopt the new tip.
4. For any explicitly authorized repair, write an auditable minimal JSON diff
   with the exact original ID and old/new SHA; use **nonforce** Git
   compare-and-swap against the inspected ref and reconcile its receipt. Do not
   choose a new UUID for an uncertain original effect, repeat a browser send,
   discard or release a held token without terminal proof, or overwrite a
   competing Git ref.
5. Keep those bundles and an explicit rollback/forward-repair decision until
   account and project proofs are re-verified. Since account and project Git
   authorities cannot be atomically committed together, no automatic two-ledger
   rollback/activation sequence exists. Epoch flip, Manager retirement,
   Scheduler enabling, deployment and restart require separate operator
   authority and a demonstrated compatible-client fence.

## Validation at corrected source (2026-11-16)

| Command | Observed result |
|---|---|
| `dotnet build tools/Wq10MigrationPreview/Wq10MigrationPreview.csproj -c Release` | Exit 0; .NET 11 Release build; **0 warnings, 0 errors** |
| `python3 -m unittest discover -s tools/Wq10MigrationPreview/tests` | Exit 0; **32 passed / 0 failed**. Includes 16 inspector tests and 16 rehearsal tests with original-writer target and repository identity regressions |
| `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter 'FullyQualifiedName~WorkQueueSchedulerHostPreviewTests' --logger 'console;verbosity=minimal'` | Exit 0; **12 passed / 0 failed / 0 skipped** under .NET 11 Release (existing F# compiler warnings in unrelated Client/Client.Tests code) |

The Python runner tests exact head pinning, clean-clone nonmutation, green and
blocked matrix generation, original uncertain IDs, old-writer drift, cap-one
reservation, dispatch-off and cap-mismatch rejection, mismatched original
operation/source request/ref/commit/blob/version, the exact adversarial
two-project `requireCandidate=false` no-candidate bypass, missing/duplicated/
wrong-head mandatory host diagnostics, adverse host issues, and native PascalCase
host JSON compatibility.
The separate real WQ08 test slice validates the *implementation contract*; the
Python fixture does not pretend to exercise authenticated host adapters.

## Remaining blockers to a real cutover

- An authoritative **WQ03 original browser-effect journal reader** is still
  required at the live WQ08 host seam; an Unknown result must not be converted
  into permission to replay or release.
- A real **two-project account migration epoch/CAS barrier**, verified original
  Task/Role conversion receipts, complete source high-water/index coverage,
  and independently proven old-client/Manager writer fences are not supplied
  by this rehearsal.
- The host preview is per project and per role, **not** an executable
  global Scheduler priority choice or provider dispatch. The real authority
  adapter and any cross-ledger change must be separately tested and approved.

No production account/project Git epoch was changed. No Manager was retired,
Scheduler enabled, browser effect replayed, deployment performed, service
restarted, GitHub Actions enabled, or Codex invoked through Jarvis. This
source branch is for **frozen, independent task-bound review only**; Worker 9
must not merge it into main.
