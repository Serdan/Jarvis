# P1 — gated ancillary Role-message lease cannot be resolved while its unrelated primary remains active

**Independent frozen source:** `00a6b6f63d45651f6b619d5b52f71f427afa851e`  
**Task/review:** `wq-01-contract-fixtures` / `d6b52a9cc4c8478e83c4e24f39145a7d`  
**Affected source:** `Server/WorkQueueContracts.fs:919-947` (ancillary completion), `:967-994` (gate insertion), `:502-522` (wake projection), `:1129-1164` plus `:822-839` (primary-only ResolveDependencyGate), `:779-820` (release only when primary terminates).  
**Accepted requirements:** `docs/specs/work-queues-and-ephemeral-executions.md` §§7,11, especially lines 248–250 and 367–375. §11 explicitly permits an existing role execution to answer ancillary Role messages *without waiting to end its primary task activation*.

## Deterministic reachable interleaving

1. Publish `Plan` (Planning), `W=role-msg:M` (Role(Lead) message), `B` (ordinary prerequisite), and `D` with hard prerequisite `D -> W`. Let registered Lead launch `L` claim `Plan` as its one primary activation and then `claimAncillaryMessage(...,L,M)`, making `W.State=Claimed L` and `M.State=ClaimedAncillary L`.
2. While `B` is unfinished, `setHardPrerequisite(...,W,B,...)` commits on the project CAS. It leaves `W.State=Claimed L` but sets `W.Gate=DependencyGatePending` (lines 967–994). The new patch correctly forbids `completeAncillaryMessage` while that gate is present.
3. Another launch now **completes B**, or a canonically verified waiver is recorded for `W -> B`. `hardDependenciesSatisfied project W` is true; **Plan remains an ongoing, independent primary claim**. The projection `wakeBlockedStages` (lines 502–519) only wakes stages with `State=Blocked && Gate=Clear`, so W stays Claimed with DependencyGatePending. `waiveHardPrerequisite` (lines 1014–1027) also leaves that gate intact.
4. The original Lead cannot answer M: `completeAncillaryMessage` checks `work.Gate <> Clear` and returns `DependencyGatePending` (lines 932–946), even though the prerequisite is now satisfied. It cannot release just this ancillary gate using `resolveDependencyGate`: that function calls `finishClaim(..., launchId=L, workId=W,...)` (lines 1129–1162), which *requires* `L.PrimaryWorkId=Some W` (lines 822–825), but `L.PrimaryWorkId=Some Plan`. The result is `InvalidState "stale work claim/launch generation"`; no state mutation commits.
5. M is still `ClaimedAncillary L`, W is still Claimed/Gated, cannot be included in `orderAvailable` or claimed by another Lead, and D cannot wake. `releaseAncillaryOnTerminal` only executes when **Plan itself** finishes or follows another primary terminal path. Plan may remain active for an arbitrarily long period. No independent ancillary terminal/requeue CAS exists.

This is distinct from review #3's successfully corrected *primary-terminal* stranding: here the primary must **not** be forced to finish just to recover a separate operator message. An eventual primary completion may free W, but no bounded or user-directed path exists while Plan remains active.

## Required change

Add a versioned, idempotent **ancillary-handler dependency-gate resolution** under one project CAS, or an equivalent explicit safe ancillary terminal path. Compare the exact launch, ancillary handler/message generation, work claim/gate/graph, current prerequisites, and source/effect safety. Atomically:
- release only W's ancillary claim, returning M to Pending and removing M from `L.AncillaryMessages`;
- make W `Blocked` while prerequisites remain unmet, or `Available` with a fresh witnessed FIFO time/sequence when satisfied/waived; never leave `Available + DependencyGatePending`;
- preserve `L.PrimaryWorkId=Plan`, Plan's claim, L's active registered lease and global token; rematch/fence already admitted unclaimed launch credits without duplicate demand;
- fence stale completion, gate resolution and primary-terminal racing operations through the same versions, and reconcile a lost response by original operation receipt. Do not silently revive the old gated handler claim.

## Missing negative-space regression

Add a two-client immutable-snapshot test in which the **prerequisite finishes before primary Plan does**. Prove that M is independently recoverable/answerable while Plan stays Claimed, and D only wakes after W is legitimately Completed. Repeat for a valid waiver, a stale clone, idempotent original reply, and a race with primary Plan terminal/release; assert no second primary claim and no duplicate account credit.

## Independent evidence / scope

The new correction patch does fix review #3's two published counterexamples: gate and prerequisite checks block unsafe ancillary Handled/Completed; a primary terminal produces coherent Blocked/Available with a fresh FIFO key as appropriate. The 47 targeted WQ-01 NUnit tests and all 182 Release Server.Tests passed independently on this frozen worktree. However, the new tests in `Server.Tests/WorkQueueContractTests.fs:1036-1180` only resolve gated W **after finishing Plan**. None exercises safe recovery with Plan still active. This is a **Phase A contract liveness blocker**, not a claim of a live deployed incident; production scheduling/migration are explicitly outside WQ-01.
