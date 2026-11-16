# Persistent Agent retirement: pinned historical manifest and default-off Work preflight

**Status: diagnostic only; production cutover NO-GO.** Task `work-persistent-agent-entity-retirement` (Worker 8). Isolated source base `aa3855c23c76d49e54229d2711c43b9256524f78`. This change adds a bounded, read-only source-of-truth inspection, **not** a new ledger or authorization route. No production Scheduler, live migration, browser send, public Work writer, Manager, service or Agent retirement is activated.

## Authority and semantics

`WorkQueuePersistentAgentRetirement.inspect : LedgerBinding -> Result<WorkFirstRetirementPreflight, LedgerFailure>` accepts an approved, project-specific Git authority binding and retrieves one verified orphan-ledger head. It does **not** accept or discover a legacy Agent ID, invoke `RegisterAgent` / `AgentInbox`, allocate a slot, claim Work, issue credentials, send an execution, or mutate Git.

The new `LedgerStore.listPinnedLedgerBlobsAt` inventories **every blob** in that exact Git tree, not just `agents/` and `tasks/`. It retains original paths and blob OIDs for `ops/` receipts, historical task/attempt/assignment/review/integration contents, notifications, challenges, wait indexes, Agent roles/instruction attachments, response alerts, source journals, and any new record family. Unknown record families are still included and labeled; they are never silently discarded. A reproducible, length-framed SHA-256 digest is computed over the sorted path/OID set. An unexpected Git tree entry, malformed object ID, symlink/other mode, unsafe path, duplicate, or excessive tree fails closed rather than fabricating completeness.

The Work side reads WQ02 and pinned WQ04 role provenance at **that same original verified Git head** and passes them to the existing `WorkQueueToolFacade.inspectAt` projection. Its Work ID, stage/claim generations, blocker/dependency state, and launch ID are non-Agent principals. This is a strictly read-only observation, not a basis for a `ClaimNextWork` or Scheduler action. Missing Work/role coverage yields an explicit UNKNOWN, never a legacy-Agent fallback. A healthy projection likewise does not establish authenticated liveness, account credit or production cutover.

`CutoverAuthorized` is structurally **false**. The report records categories of unattested prerequisites: cross-client old-writer/Manager fencing with original high-water, all WQ10 account/project/uncertain-operation reconciliations, private provider/first-connect/terminal/STOP evidence, and operator activation authority. No mutation method or setter exists.

## Preservation and boundary

| Evidence family | Preflight disposition |
| --- | --- |
| `agents/*.json` and subscriptions | Historical-only, never roster/claim owner |
| `tasks/**` specs, attempts, assignments, reviews, integrations | Historical-only, original immutable author/owner/ref/receipt retained |
| `notifications/**`, `challenges/**`, `challenge-waits/**` | Historical-only, no inherited STOP or launch authority |
| `records/roles/**`, `records/agent-instructions/**`, response alerts and preferences | Historical-only, no role/session authorization from Agent membership |
| `ops/**` | Immutable original operation identity and outcome evidence |
| `records/work-queue/**` | Native Work evidence, never projected from a legacy Agent roster |
| Other `records/**` or future namespaces | Preserve original path/OID and require separate operator/owner reconciliation |

Git OIDs do not prove semantic integrity of a source-effect journal or existence of an old-client fence. The inventory is a prerequisite preservation manifest, not proof that every source side effect was captured or every legacy writer is quarantined.

## Verification

Focused disposable two-clone tests in `WorkQueuePersistentAgentRetirementTests` check stable same-head path/OID manifests and digests, operation receipts and source-journal retention, no preflight write, a Work-only positive read without any Agent entity creation, and a legacy-only negative with no Work fallback. The tests do not invoke production providers, tokens, Scheduler or live migration.

See `docs/work-persistent-agent-retirement-inventory.md` for the legacy mutation-path audit and `docs/work-native-lifecycle-cas.md` for the independently guarded disposable Work transition. This change intentionally avoids Worker 1's UI, Worker 10's migration/credential modules, and other owners' live path modules.

**Still missing before GO:** independently accepted fleet-wide fence, WQ10 head/high-water reconciliation across all participants, authenticated private host execution/terminal and STOP paths, positive Work-first live API/TUI routing, server-restart proof, and explicit operator authorization. Lead-only exact reviewed-SHA integration applies; no hosted GitHub Actions or Codex.
