# WQ-10: offline migration preview (not a cutover tool)

This is a separate, read-only .NET 11 CLI. It inspects **pinned, local Git commits** and projects only a deterministic, inert migration inventory. It cannot call the Jarvis server, fetch/push/commit/merge, update Git refs, reserve account capacity, change epoch, start a Scheduler, stop a Manager, or deploy. Both JSON and Markdown reports are printed to stdout; the inspector never writes any files.

This is the operator-directed **offline track** of WQ-10, not a claim that the broader section 14 production migration gates or all 80 architecture scenarios have passed.

## Use

Build with:

    dotnet build tools/Wq10MigrationPreview/Wq10MigrationPreview.csproj -c Release

Provide one JSON manifest with this shape (replace all paths and SHAs with disposable clones or explicitly approved offline exports):

    {
      "schemaVersion": 1,
      "account": {
        "name": "account", "repo": "/safe/read-only/account-clone",
        "head": "<exact-40-or-64-hex-SHA>",
        "ref": "refs/heads/jarvis-account-admission"
      },
      "projects": [
        {
          "name": "Demo", "repo": "/safe/read-only/project-clone",
          "head": "<exact-40-or-64-hex-SHA>",
          "ref": "refs/heads/jarvis-orchestration"
        }
      ],
      "observations": {
        "legacyManagerStopped": false,
        "oldWritersFenced": false,
        "clientProtocolsCompatible": false,
        "pendingOperations": [
          { "id": "original-operation-uuid", "project": "Demo",
            "status": "Unknown", "actor": "Worker 2" }
        ],
        "inFlightSourceJobs": [],
        "uncertainBrowserSubmissions": []
      }
    }

Run:

    dotnet run --no-build -c Release --project tools/Wq10MigrationPreview -- /tmp/wq10-manifest.json --format markdown > /tmp/wq10-report.md

The machine-readable JSON report goes to stdout by default; --format markdown prints a human-readable report. Shell redirection is the operator's responsibility and should target a path outside all source or ledger clones. A blocked preflight exits successfully so the report remains inspectable; malformed input exits nonzero. Reports contain no wall-clock timestamp or random ID, allowing deterministic diff. Do not edit manifest heads to hide a competing write; retain both pinned and observed SHA.

The observations fields are explicitly **operator-supplied claims**, not cryptographic or server-authenticated evidence. Omitting any of the three proof booleans blocks readiness. All three evidence inventories (`pendingOperations`, `inFlightSourceJobs`, `uncertainBrowserSubmissions`) must be explicitly supplied as arrays; omitted or null inventories raise `EVIDENCE_INVENTORY_MISSING` and block readiness. Pending operation IDs, uncertain browser results, and jobs must come from actual owner/client journals; `[]` is an explicit assertion that a source was inventoried and found empty, not an auto-discovery result. The preview never treats missing evidence as proof of a settled operation.

The inspector reads exact Git blobs (no remote calls) from account state.json, and project ledger.json, tasks/*/task.json, attempts, agents, review records, integration submissions, notifications and optional records/work-queue/project.json. It surfaces original IDs, attempt source refs/commits, pending Role/Task messages, claimed reviews, active agents, canonical launch statuses, held account tokens, recorded high-water, historical operation counts, duplicate work and head conflicts. The **pinned** SHA is the historical read boundary, while the specified local ref tip is only a conflict observation; it is **not** a live remote-head or protocol attestation.

Tasks already represented by canonical work with matching subject IDs are shown as ALREADY_PRESENT rather than converted again. Their existing work IDs, kinds, stage generations, states and source lineages are preserved in JSON and Markdown; normal completed-predecessor/successor history is not a duplicate. The immutable work ID identifies each stage: distinct FrozenReview stages can legitimately share kind, generation and absent source lineage. The `DUPLICATE_CANONICAL_STAGE` blocker is limited to concurrent unfinished Implementation stages with the same explicit source-writer lineage, matching the canonical publishStage collision rule. New tasks receive stable, **proposed** work IDs, never staged actual records. The preview compares account migration phase and project migration case: an account in Preparing/Validated/Activated requires each supplied project to be Prepared, while a Frozen legacy account can be inspected before canonical preparation. Cross-ledger phase mismatches produce `MIGRATION_PHASE_MISMATCH` and `NO_GO`. The independent account/project Git **identity epochs are not compared** or reused as prepared migration epochs. Already-activated accounts are labeled audit-only and never offered fresh Preparing. Suggested Preparing/Prepared labels are report text, not valid prepared receipts or operator approvals. This preview does not invent verified stage generations, provider session IDs, server-witnessed FIFO times, task/role conversion receipts, global atomicity, or authority to release held tokens. ALREADY_PRESENT is only duplicate-screening, not a proof of completed migration.

## Validation

    python3 -m unittest discover -s tools/Wq10MigrationPreview/tests -v
    dotnet build Jarvis.slnx -c Release

The Python stdlib fixtures create disposable bare Git remotes and two clones. They cover ordinary legacy preview, pre-existing stage, pending Role/Task messages, unknown operation receipts, omitted/null versus explicitly empty evidence inventories, incompatible account/project migration phases, already-activated audit, unproven client/Manager gates, competing old-client push, legitimate sequential stages, true duplicate logical stages, working Manager, claimed review, pending integration, held capacity/launch and read-only deterministic output. They do **not** alter a production ledger.

## Safe recovery instructions generated by the report

Before any separately approved mutation, save **exact** account/project heads and all original operation IDs and actors. Inspect uncertainty against original receipts, frozen review SHAs, WIP refs and terminal source jobs, then propose only the smallest evidenced diff with a durable repair note and nonforce Git CAS at the expected head. On a concurrent head change or lost acknowledgement, inspect rather than retry under a new ID or force overwrite. Because the two Git ledgers cannot be atomically committed, any future real cutover needs separate reconciliation, writer fencing, account epoch authorization and validated project proofs. Actual migration, epoch flip, Scheduler activation, Manager retirement, restart and deployment remain disabled.

## Two-project current-main rehearsal (operator report)

The additive `rehearsal.py` consumes **this accepted inspector** plus separately
captured **real WQ08 `scheduler preview` JSON** from two approved disposable
project checkouts sharing one disposable account authority. The WQ08 CLI emits
PascalCase JSON; the harness also accepts camelCase fixture exports.

Build the existing inspector in Release, and capture each read-only Scheduler
host preview from an explicitly trusted **disposable** project clone using
the operator command in `docs/specs/wq08-host-preview.md`. Never point the
rehearsal at a live production ledger. Both read paths report exact Git heads;
the harness checks them against one operator-supplied WQ10 manifest, along
with global cap/held/available, role version, `canDispatch=false`,
provenance on a mandatory selected candidate, its matching task-stage inventory,
and the exact WQ04 work-origin Git blob at the inspected project head.
The wrapper only reads local pinned Git blobs; it never writes or fetches.
All reported WQ08 issues are classified. A missing
or contradictory observation is `NO_GO`, not an empty ledger.

The wrapper takes one `rehearsal.json` (all paths relative to it):

```json
{
  "schemaVersion": 1,
  "cases": [
    {
      "name": "prepared-two-project-clone",
      "manifest": "prepared-manifest.json",
      "schedulerPreviews": {
        "Alpha": "prepared-alpha-scheduler.json",
        "Beta": "prepared-beta-scheduler.json"
      }
    },
    {
      "name": "blocked-original-uncertainty",
      "manifest": "blocked-manifest.json",
      "schedulerPreviews": {
        "Alpha": "blocked-alpha-scheduler.json",
        "Beta": "blocked-beta-scheduler.json"
      }
    }
  ]
}
```

```sh
dotnet build tools/Wq10MigrationPreview/Wq10MigrationPreview.csproj -c Release
python3 tools/Wq10MigrationPreview/rehearsal.py /tmp/wq10/rehearsal.json --format markdown > /tmp/wq10/go-no-go.md
python3 -m unittest discover -s tools/Wq10MigrationPreview/tests -v
```

For a `PREPARED_FOR_REVIEW` rehearsal, both project phases must be
consistent with the account, every original-ID inventory explicit, old Manager
and client writer fences evidenced, and the WQ08 preview verified at **the
same exact heads**. The actual WQ08 host *always* emits `DispatchDisabled`;
the wrapper expects it exactly once, at the inspected project head, only with
`Mode=ReadOnlyDryRun` and `CanDispatch=false`. `AccountNotActivated` is
expected exactly once for a nonactivated account at the inspected account head,
never permission to activate. Missing, duplicated, mismatched or unknown
diagnostics, adverse capacity/identity issues, and noninert previews are blockers.
The output also records
pending Task/Role messages, Manager/agent states, held launch IDs, original
uncertain browser and Git operation IDs, and a manual backup/diff/receipt path.

All source data and Scheduler JSON are **operator-supplied evidence**, not an
authenticated live attestation. The fixture-only tests deliberately synthesize
WQ08-shaped JSON; separately execute the real WQ08 Release test slice and
capture genuine host CLI output for an operator rehearsal. The live WQ03
original browser journal reader and cross-ledger atomic cutover mechanism are
not supplied by this wrapper. **A green rehearsal is not a production GO**;
all cases remain `productionCutover=NOT_AUTHORIZED`.

The `requireCandidate` manifest bypass has been retired. `wouldLaunch=null`
is an explicit non-green `NO_GO` / audit-only observation even if an
untrusted caller supplies `requireCandidate=false`. To report
`PREPARED_FOR_REVIEW`, a candidate must be present in the WQ08 eligible
set and in the pinned canonical queue, agree with the independently inventoried
legacy task stage, generation and source-writer lineage, and match the original
immutable WQ04 source operation/request, source ref/SHA and versioned role.
The recorded WQ04 origin is read from
`records/work-queue/origins/<SHA256(workId)>.json` at the **same pinned Git
commit**, and its real Git blob OID must match `wouldLaunch.originBlobOid`.
An absent origin or mismatch is `SOURCE_PROVENANCE_UNVERIFIED`, not a
positive inference from caller-provided strings.

The unit-test previews remain *source-backed host-contract fixtures*, not live
captured WQ08 outputs: tests assert the actual WQ08 source unconditionally
emits `DispatchDisabled` in `ReadOnlyDryRun` with `CanDispatch=false`,
include the mandatory diagnostic, and exercise deletion, duplication, head
mismatch and adverse issues. Real operator captures still require the separate
WQ08 command and independently trusted disposable account/project bindings.

## Fixture-only prepared-state adapter (WQ10 follow-on)

`prepare.py` is a **disabled-by-default, non-production typed evidence adapter**, not an extension of the read-only inspector. It never edits `state.json`, `ledger.json`, the canonical `records/work-queue/project.json`, any legacy task/agent/Manager record, or the original `jarvis-account-admission` / `jarvis-orchestration` branches. It cannot reserve tokens, launch a role, claim work, send a browser request, start a Scheduler, retire Manager, or activate an epoch.

Only run against **exactly two disposable bare project authorities and one disposable bare account authority**, all beneath a dedicated directory in the OS temporary directory. The fixture repositories must have **no configured remotes**. Create a marker `<disposable-root>/.wq10-disposable-fixture` containing exactly `WQ10_DISPOSABLE_FIXTURE_V1`. The marker and explicit `--apply-disposable` are both mandatory; no production override exists. The root must not be a symlink to a real authority. These guards prevent casual misuse but are not a substitute for operator review or an OS sandbox.

First run the accepted Release inspector against a pinned `manifest.json`, save its **unmodified JSON report**, review it independently, and pin its exact file bytes as SHA-256. The adapter re-runs the inspector and requires the same report, `OFFLINE_READ_ONLY`, `PREVIEW_READY_FOR_REVIEW`, `NOT_AUTHORIZED`, frozen/legacy account phase, no blocked findings and exact project heads. A SHA pin is an **operator-supplied assertion**, not a signed review or authentication receipt.

```sh
dotnet build tools/Wq10MigrationPreview/Wq10MigrationPreview.csproj -c Release
dotnet tools/Wq10MigrationPreview/bin/Release/net11.0/Wq10MigrationPreview.dll \
  /tmp/wq10-disposable/manifest.json > /tmp/wq10-disposable/reviewed-preflight.json
sha256sum /tmp/wq10-disposable/reviewed-preflight.json

python3 tools/Wq10MigrationPreview/prepare.py prepare \
  --disposable-root /tmp/wq10-disposable \
  --manifest /tmp/wq10-disposable/manifest.json \
  --report /tmp/wq10-disposable/reviewed-preflight.json \
  --reviewed-sha256 '<reviewed-SHA-256>' \
  --operation-id '44444444-4444-4444-4444-444444444444' \
  --apply-disposable

python3 tools/Wq10MigrationPreview/prepare.py abort \
  --disposable-root /tmp/wq10-disposable \
  --operation-id '44444444-4444-4444-4444-444444444444' \
  --apply-disposable
```

The adapter backs up each original branch as a verified Git bundle before staging. It writes a minimal **non-runnable migration-sidecar only** on `refs/heads/wq10-prepared/<original-operation-UUID>`, with each new commit parented to its original frozen head. Original refs are never moved. For each authority it saves the before/after Git diff, exact commit, backup SHA-256, report SHA-256 and a resumable local receipt at `<root>/wq10-preparations/<UUID>/receipt.json`. Staging uses an atomic per-repository Git `update-ref --stdin` transaction that verifies the original head and creates the new ref without force. Project sidecars retain old task/attempt/ref/commit, agent, review, integration and message evidence; all proposed stages are **Blocked** with unknown role, origin receipt, dependency graph and generation. No runnable canonical stage is published. The account sidecar is staged last and references the exact two project sidecar commits; this is provenance evidence, **not** an atomic cross-ledger commit.

Reusing the same UUID and same reviewed input returns the same staging commits; changed content, other preparations, missing source refs/SHAs, incomplete original history or a competing head produce `NO_GO`. If a cross-ledger step fails, the local receipt reports partial status, and the operator uses `abort` with the **original** UUID. Abort removes only matching staging refs by exact commit CAS, retains the original bundles/diffs and never rewinds an original authority ref. Inspect an unknown outcome before replay or abort; do not regenerate operation IDs or force an update.

**What this does not prove:** the accepted account ledger has no reviewed `Preparing` migration CAS/receipt operation; the canonical Work project ledger has no safe offline `Prepared` conversion transaction; source-origin receipt identity/role mapping, producer high-water, verified dependency/FIFO state and authenticated review approval are not established by this adapter. Its only successful status is `ADAPTER_ONLY_NOT_PREPARED`; `canonicalPrepared=false`, `schedulerEnabled=false`, and `productionCutover=NOT_AUTHORIZED` remain explicit. A production cutover requires new independently reviewed typed bindings, real provenance proof and separate human authorization, never this script.

Run the hermetic regression suite with:

```sh
python3 -m unittest discover -s tools/Wq10MigrationPreview/tests -v
```
