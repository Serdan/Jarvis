# Independent review — Work-first persistent Agent retirement preflight

**Verdict: Accepted for the narrow, default-off diagnostic scope only.** This review does not establish production cutover, Scheduler activation, Agent retirement, or fleet-wide writer fencing.

- Review: `8a18e7dcd335420bb2a1d1ba1cb23e83`
- Frozen source: `9f0c3335772fa931a61a9285946b07c26d068c26`; base `aa3855c23c76d49e54229d2711c43b9256524f78`
- Clean source worktree: `feature/work-agent-retirement-defaultoff-w8-20261116`; six changed files, 400 insertions; `git diff --check` exit 0.
- Independent .NET 11 **Release** focused fixture run: **5 passed, 0 failed** (`WorkQueuePersistentAgentRetirementTests`).
- Independent .NET 11 **Release** adjacent `--no-build` run: **22 passed, 0 failed** (`WorkQueueToolFacadeTests`, `WorkQueueRoleAuthorityTests`, retirement tests).

## Verified

1. `LedgerStore.listPinnedLedgerBlobsAt` enumerates paths and original blob OIDs from one verified Git commit, includes `agents/`, `tasks/`, `ops/`, legacy records and unknown families rather than silently filtering to familiar readers. It validates object mode, SHA-1/SHA-256 OIDs and canonical safe paths, with a stable length-framed manifest digest. The fixture verifies equality across two Git clones, new-head change detection, operation/source record presence, and SHA-256 behavior.
2. `WorkQueuePersistentAgentRetirement.inspect` fetches the approved ledger head, reads WQ02 state via `loadAt`, reads WQ04 role provenance via `loadAt`, and calls the existing read-only Work facade using that **same** `LedgerSnapshot`. It never registers/activates a persistent Agent or creates any Work claim. Work-only and legacy-only negative cases pass.
3. The returned `CutoverAuthorized` is unconditionally false, `ReadOnly` is true, missing Work state yields an explicit error, and missing role/queue coverage is recorded as unattested rather than granting dispatch. No source-branch production writer/API/Scheduler routing is changed.
4. The accompanying documentation clearly separates tree preservation from semantic source-journal integrity, authenticated old-writer/high-water fencing, account/project epoch recovery, provider bootstrap, STOP/terminal proof, and operator authorization. All remain **NO-GO**.

## Nonblocking limitations / follow-up before activation

- The manifest is the **current pinned Git tree**, not a traversal of every historical Git commit. Existing Git history and source-effect high-water still require separate migration reconciliation.
- `OtherPreservedRecord` (including a legacy `records/source-journal/` record) is `HistoricalOnly=false`. This boolean must **not** be used to infer native Work authority; explicitly classify/reconcile these namespaces before turning this report into a migration decision.
- The 12 MB / 50,000-entry fail-closed checks occur **after** `git ls-tree` output is collected by the existing unlimited Git subprocess capture. A streaming/output-bound collection would be needed for a strict resource bound against an oversized ledger.
- This is not a full system or production-host acceptance run. The tested branch trails current remote main; Lead must preserve the reviewed exact SHA and resolve any necessary integration conflicts separately.

**Scope decision:** The issues above do not grant a transition, hide a failed Work projection, or add a live writer. Accept this *diagnostic preflight* only; retain all operator cutover prerequisites as unmet.
