# WQ-01 independent frozen review #5 — ChangesRequested

**Review ID:** `bef7c832a8f44ed5971bfc55ff628acf`  
**Task:** `wq-01-contract-fixtures`; owner Worker 5  
**Frozen source:** `1d5a2b4daaeef825ad5d14b6dd9facd981adc6f9`  
**Accepted base:** `90ed868d04524d0483ccd979cffb5029d8cf7027`  
**Reviewer:** Reviewer 2  
**Verdict:** **ChangesRequested — one P1 safety issue**

## What is corrected

The new `resolveAncillaryDependencyGate` is a meaningful fix for the previous P1 liveness defect: while an unrelated registered primary Plan remains Claimed, a gated ancillary Role-message W can independently be released by one project CAS. Its launch/claim/gate checks, dependency and external-effect safety, message reset, targeted lease removal, FIFO timestamp/sequence and unclaimed-credit reconciliation follow the accepted Phase A model. The primary Plan claim, lease, account token ID and ongoing launch remain intact. The added five regression tests cover waiver/B completion before Plan terminal, stale project snapshots, lost resolver receipts, source-effect fences, primary terminal races, downstream D, same-launch reclaim and account-credit preservation. Earlier Blocked wake, FIFO time, matching, source-lineage, operation-replay and integration gate contracts remain present.

## Blocking finding

**P1 — An old ancillary handler's reply can complete a later claim on the same launch.** See `findings/01-stale-ancillary-handler-generation.md` for the full sequence and code anchors. The resolver invalidates W's original claim generation, and the same active L may subsequently reclaim M at a higher generation. But `completeAncillaryMessage` accepts only L/M identity and the current project CAS version; it does not require the handler/claim generation that originated the reply. A pre-gate reply retried against the new project head after same-L reclaim can therefore mark M Handled, W Completed and wake D without demonstrating possession of the latest handler generation. This violates the handler-generation fence in the accepted operator-message contract (§Task- and role-addressed operator communication).

**Required:** Bind ancillary completion/ack and operation receipt to the current handler claim generation; reject an old-generation reply even with the latest project version. Test g1 claim → W→B gate → B completion or verified waiver → independent release → same L reclaim at g3 → stale g1 reply rejected, with D still Blocked and Plan/token unchanged; only g3-bound completion can wake D. Add stale two-client and replay variants.

## Independent validation at exact frozen SHA

| Check | Result |
| --- | --- |
| Exact worktree HEAD `Jarvis::worker5-wq01-corrections-4` | `1d5a2b4…`, clean |
| .NET 11 Release focused `ServerWorkQueueContractTests` | **52/52 PASS** |
| .NET 11 Release full `Server.Tests` | **187/187 PASS**, 0 skipped |
| .NET 11 `dotnet build Jarvis.slnx -c Release --no-restore` | **PASS**, 0 errors, 2 compiler warnings |
| Node bridge and ChatGPT launcher tests | **82/82 PASS** |
| Base-to-head `git diff --check` | One non-blocking trailing space at `docs/specs/work-queues-phase-a-contracts.md:52` |

The P1 is a **static code-path counterexample**, not an executed negative test; the existing fixtures do not exercise stale old-generation completion after same-launch reclaim. An inline F# script attempt was rejected by local shell-execution policy, and no passing test is claimed for it. Frozen source was not edited.

## Scope and disposition

WQ-01 remains pure Phase A typed contracts/fixtures, **not** production Scheduler, Git-ledger migration, browser launch, actual account waiver authority, durable source effect verification, or proof of all 80 end-to-end architecture cases. Do not integrate or deploy this SHA. Worker 5 should acknowledge the decision, fix the generation fence on a fresh isolated source commit, rerun validation and request a new independent frozen review. Preserve the current frozen review ref immutably.
