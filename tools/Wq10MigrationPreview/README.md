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

Tasks already represented by canonical work with matching subject IDs are shown as ALREADY_PRESENT rather than converted again. New tasks receive stable, **proposed** work IDs, never staged actual records. The preview compares account migration phase and project migration case: an account in Preparing/Validated/Activated requires each supplied project to be Prepared, while a Frozen legacy account can be inspected before canonical preparation. Cross-ledger phase mismatches produce `MIGRATION_PHASE_MISMATCH` and `NO_GO`. The independent account/project Git **identity epochs are not compared** or reused as prepared migration epochs. Already-activated accounts are labeled audit-only and never offered fresh Preparing. Suggested Preparing/Prepared labels are report text, not valid prepared receipts or operator approvals. This preview does not invent verified stage generations, provider session IDs, server-witnessed FIFO times, task/role conversion receipts, global atomicity, or authority to release held tokens. ALREADY_PRESENT is only duplicate-screening, not a proof of completed migration.

## Validation

    python3 -m unittest discover -s tools/Wq10MigrationPreview/tests -v
    dotnet build Jarvis.slnx -c Release

The Python stdlib fixtures create disposable bare Git remotes and two clones. They cover ordinary legacy preview, pre-existing stage, pending Role/Task messages, unknown operation receipts, omitted/null versus explicitly empty evidence inventories, incompatible account/project migration phases, already-activated audit, unproven client/Manager gates, competing old-client push, multiple stage duplicates, working Manager, claimed review, pending integration, held capacity/launch and read-only deterministic output. They do **not** alter a production ledger.

## Safe recovery instructions generated by the report

Before any separately approved mutation, save **exact** account/project heads and all original operation IDs and actors. Inspect uncertainty against original receipts, frozen review SHAs, WIP refs and terminal source jobs, then propose only the smallest evidenced diff with a durable repair note and nonforce Git CAS at the expected head. On a concurrent head change or lost acknowledgement, inspect rather than retry under a new ID or force overwrite. Because the two Git ledgers cannot be atomically committed, any future real cutover needs separate reconciliation, writer fencing, account epoch authorization and validated project proofs. Actual migration, epoch flip, Scheduler activation, Manager retirement, restart and deployment remain disabled.
