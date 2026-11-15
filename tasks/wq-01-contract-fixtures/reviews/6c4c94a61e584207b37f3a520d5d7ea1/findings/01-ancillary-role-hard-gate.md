# P1 — ancillary Role-message work bypasses hard dependency gates or becomes permanently stranded

**Frozen exact source:** `ecf7b40f98fddb428e0c73d8659a6522298083e2`  
**Affected code:** `Server/WorkQueueContracts.fs:852-916` (ancillary claim/complete), `:774-800` (primary-terminal ancillary recovery), `:936-963` (new claimed-work hard gate), `:308-312` and `:495-519` (satisfaction/wake), `:1098-1133` (only primary-claim dependency gate resolution).  
**Accepted architecture:** `docs/specs/work-queues-and-ephemeral-executions.md` §§6–7 (every unsatisfied hard edge gates *claimed* work) and §11 (ancillary Role-work claims and recovery).  
**Severity:** **P1** — two reachable branches of the same missing ancillary gate protocol can (a) complete an explicitly gated message stage and wake unsafe downstream work, or (b) strand pending operator work indefinitely. This finding was not detected by the three new corrections; it also existed in the earlier Phase A implementation.

## Concrete transition A: falsely complete a hard-gated ancillary message

1. Publish planning work `Plan` and Role(Lead) message work `W=role-msg:M`, both Available; publish another unfinished ordinary stage `B`, and dependent stage `D` with hard dependency `D -> W` (therefore D is Blocked).
2. Register Lead launch `L`; `claimNext` makes `Plan` its primary work. `claimAncillaryMessage(..., L, M)` makes `W.State=Claimed L` and `M.State=ClaimedAncillary L`. No competing unclaimed credits are needed.
3. `setHardPrerequisite(..., W, B, now)` while B remains unfinished **succeeds**. Its `WorkState.Claimed _` case sets `W.Gate=DependencyGatePending` (§source :951–960).
4. `completeAncillaryMessage(..., L, M, replyId, now)` **still succeeds**. Unlike `completeOrdinary` / primary `completeMessageWork`, it checks only `W.State=Claimed L` and does **not** inspect `W.Gate`, `W.Dependencies`, or `isSatisfied B` (§source :898–916). It commits `M=Handled` and `W.State=Completed`, retaining the pending gate.
5. `isSatisfied W` now returns true solely because W is Completed (it is not integration work). `projectDependenciesChanged` wakes D to Available despite B being unsatisfied and W's mandatory gate never being safely resolved. This is precisely the forbidden `SetHardPrerequisite` wins before `CompleteWork` branch of accepted §7.

**Expected:** a gated ancillary handler's completion is rejected or atomically follows the same versioned, safe dependency-gate resolution/terminalization contract as any other claimed work, with no Handled/Completed or downstream publication while the edge remains unresolved.

## Concrete transition B: primary terminal strands an unfinished gated ancillary message

From the same snapshot immediately after step 3, have L finish `Plan` via `completeOrdinary` instead of handling M. `releaseAncillaryOnTerminal` unconditionally sets `W.State=Available`, a new FIFO time/sequence, and `M.State=Pending` **but retains `W.Gate=DependencyGatePending`** (§source :774–795). `orderAvailable` excludes it because ready requires a Clear gate (§source :325–329). Later completion/waiver of B cannot wake it because `wakeBlockedStages` only processes `State=Blocked && Gate=Clear` (§source :505–508). `resolveDependencyGate` cannot repair it: it requires `State=Claimed L` and a primary `finishClaim` for W; L's primary was Plan and has now terminated (§source :802–819, :1098–1133). The pending operator message therefore has no ordinary future handler.

**Expected:** gated ancillary work stays safely claimed/recovery-blocked until it can be released, or the terminal project CAS converts it to a coherent dependency-Blocked stage with no orphaned claim gate, then wakes once when B is satisfied/waived. A mere requeue must never silently discard the dependency fence.

## Required corrections and regression evidence

- Model ancillary Role-message ownership distinctly enough that **new unsatisfied hard edges fence all completion/ack paths**, not just primary `completeOrdinary` and `completeMessageWork`. Check the canonical gate, graph/claim generation, and current prerequisites atomically in `completeAncillaryMessage`.
- Rework primary-terminal ancillary release to preserve safe handoff and a coherent state: `Blocked` on an unresolved hard edge (with recoverable provenance), or `Available` with fresh witnessed FIFO keys only after the edge is actually satisfied and any gate safely cleared. Do not leave `Available + DependencyGatePending` after its owner launch is terminal.
- Add deterministic tests for both interleavings above, reversed complete-before-edge CAS, two-client stale mutation rejection, lost-reply idempotence, eventual prerequisite satisfaction/waiver wake exactly once, and preservation of compatible outstanding launch credits.
- Run targeted/full .NET 11 Release fixtures against the **new** source SHA and submit a fresh frozen review. Do not modify this review's frozen SHA/ref.

## Independent evidence and boundaries

At this exact SHA, Reviewer 2 independently ran targeted `WorkQueueContractTests` **44/44 PASS** and full Release `Server.Tests` **179/179 PASS**. These passes do not exercise claimed **ancillary** Role work after a hard-edge edit; the code-path proof above is based on the frozen pure state machine, not an asserted live production incident or a new executable regression fixture. A disposable out-of-tree F# reproduction was attempted but the Jarvis client disallowed the shell executable, so no independent dynamic reproduction is claimed. The Phase A contracts remain non-production; this is a blocker to accepting their specified semantics, not evidence of currently deployed scheduling behavior.