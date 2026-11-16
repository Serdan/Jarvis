# WQ10 — disposable two-project offline cutover rehearsal

**Date:** 2026-11-16  
**Task:** `wq10-offline-cutover-readiness-rehearsal` (Worker 9)  
**Source baseline:** `275dce20cd0d3b51bb8369d27cdb6f03a8942691` (current `main` at worktree creation)  
**Branch:** `refs/heads/feat/wq10-two-project-rehearsal-worker9-20261116`  
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
activate.** Only `AccountNotActivated` is tolerated as an expected inert
Scheduler-preview diagnostic. Any other WQ08 issue is a blocker. The real
host preview never calls a provider or authorizes dispatch.

## Executable matrix: two separate pinned offline cases

The focused Python fixture creates local bare account and two project remotes
with two independent Git clones of each authority. Before mutating the blocked
scenario it preserves independent immutable bare remotes for the prepared case.
The fixture synthesizes WQ08-*shaped* JSON; it does not claim that a real WQ08
host was invoked during the Python test.

| Gate | Prepared two-project case | Blocked case |
|---|---|---|
| Account migration | Preparing, cap **1**, held **0** | Preparing, cap **1**, held **1** (original `launch-Beta-1`) |
| Project migration | Alpha/Beta `Prepared`, canonical coverage flags and origins present | Same account, Beta's legacy Manager still Working |
| Historical work | Legacy tasks preserved with source refs; available canonical stage with explicit origin/high-water | Beta has an additional Started Manager-owned legacy attempt |
| Manual communication | Original pending `Task-Alpha`, `Role-Alpha`, `Task-Beta`, `Role-Beta` retained | Same pending original Task/Role messages; nothing re-dispatched |
| Scheduler host preview | Default-off `ReadOnlyDryRun`, Planner v1, source UUID/hash/blob, cap=1 free=1 | Cap full, `BrowserJournalUnknown`, no dispatch permitted |
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

## Validation at source baseline

| Command | Observed result |
|---|---|
| `dotnet build tools/Wq10MigrationPreview/Wq10MigrationPreview.csproj -c Release` | Exit 0; .NET 11 Release build; **0 warnings, 0 errors** |
| `python3 -m unittest discover -s tools/Wq10MigrationPreview/tests -v` | Exit 0; **20 passed / 0 failed**. Includes 16 accepted inspector tests and 4 new rehearsal tests |
| `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter 'FullyQualifiedName~WorkQueueSchedulerHostPreviewTests' --logger 'console;verbosity=minimal'` | Exit 0; **12 passed / 0 failed / 0 skipped** under .NET 11 Release |

The Python runner tests exact head pinning, clean-clone nonmutation, green and
blocked matrix generation, original uncertain IDs, old-writer drift, cap-one
reservation, dispatch-off and cap-mismatch rejection, missing original source
provenance, missing WQ08 issues, and native PascalCase host JSON compatibility.
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
